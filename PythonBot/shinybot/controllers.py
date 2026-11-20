"""Ways of sending button presses to the Switch.

  sysbotbase - WiFi. Needs a Switch running custom firmware with the
               sys-botbase sysmodule (TCP port 6000).
  nxbt       - Bluetooth. Talks over the network to bridge/nxbt_bridge.py,
               which runs on a Linux machine (Raspberry Pi, Linux VM with a USB
               Bluetooth dongle, ...) and pretends to be a Pro Controller.
               Works on a stock, unmodified Switch.
  dryrun     - Prints the presses only. For testing the vision side.

Button names used everywhere in this bot:
  A B X Y L R ZL ZR PLUS MINUS HOME CAPTURE DUP DDOWN DLEFT DRIGHT LSTICK RSTICK
"""
from __future__ import annotations

import json
import select
import socket
import threading
import time

VALID_BUTTONS = {
    "A", "B", "X", "Y", "L", "R", "ZL", "ZR", "PLUS", "MINUS", "HOME",
    "CAPTURE", "DUP", "DDOWN", "DLEFT", "DRIGHT", "LSTICK", "RSTICK",
}


def _check(buttons):
    buttons = [b.upper() for b in buttons]
    bad = [b for b in buttons if b not in VALID_BUTTONS]
    if bad:
        raise ValueError(f"Unknown button(s): {bad}. Valid: {sorted(VALID_BUTTONS)}")
    return buttons


class Controller:
    def connect(self):
        pass

    def press(self, buttons, hold: float = 0.1):
        """Press all `buttons` at the same time, hold them, then release."""
        raise NotImplementedError

    def tap(self, button: str, hold: float = 0.1):
        self.press([button], hold)

    def close(self):
        pass


class DryRunController(Controller):
    def press(self, buttons, hold=0.1):
        buttons = _check(buttons)
        print(f"[dryrun] press {'+'.join(buttons)} for {hold:.2f}s")
        time.sleep(hold)


class _LineSocket:
    """Small TCP helper with automatic reconnect. `init` is sent after every (re)connect."""

    def __init__(self, host, port, timeout=5.0, init: bytes = b""):
        self.host, self.port, self.timeout, self.init = host, port, timeout, init
        self.sock = None
        self.buf = b""
        self.lock = threading.RLock()

    def open(self):
        """Connects. sys-botbase-cpp rebuilds its listening socket every time a client leaves (ShinyForge's game launcher just
        before the hunt starts); a connection made in that moment is reset at once - watched for 150 ms and made again."""
        with self.lock:
            for attempt in range(3):
                self.close()
                self.sock = socket.create_connection((self.host, self.port), timeout=self.timeout)
                self.sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
                time.sleep(0.15)
                if not self._reset_at_once() or attempt == 2:
                    break
                time.sleep(0.5)
            if self.init:
                self.sock.sendall(self.init)

    def _reset_at_once(self) -> bool:
        ready, _, _ = select.select([self.sock], [], [], 0)
        if not ready:
            return False
        try:
            return self.sock.recv(1, socket.MSG_PEEK) == b""
        except OSError:
            return True

    def send(self, data: bytes):
        with self.lock:
            for attempt in range(3):
                try:
                    if self.sock is None:
                        self.open()
                    self.sock.sendall(data)
                    return
                except OSError as e:
                    print(f"[controller] connection problem ({e}); reconnecting...")
                    self.sock = None
                    time.sleep(1 + attempt)
            raise ConnectionError(f"Could not reach {self.host}:{self.port}")

    def readline(self) -> str:
        while b"\n" not in self.buf:
            chunk = self.sock.recv(1 << 16)
            if not chunk:
                raise ConnectionError("connection closed")
            self.buf += chunk
        line, self.buf = self.buf.split(b"\n", 1)
        return line.decode(errors="replace").strip()

    def request(self, text: str, timeout: float = 15.0) -> str:
        """Sends one command and returns its answer line. Whatever arrived unasked before (error lines after button
        presses, leftovers of a failed read) is dropped first, so answers never get mixed up."""
        with self.lock:
            if self.sock is None:
                self.open()
            self.buf = b""
            self.sock.setblocking(False)
            try:
                while self.sock.recv(1 << 16):
                    pass
            except OSError:
                pass  # nothing more waiting
            self.sock.settimeout(timeout)
            try:
                self.sock.sendall((text + "\r\n").encode())
                return self.readline()
            finally:
                self.sock.settimeout(self.timeout)

    def close(self):
        with self.lock:
            if self.sock:
                try:
                    self.sock.close()
                except OSError:
                    pass
            self.sock = None
            self.buf = b""


_SHARED: dict = {}


def sysbot_socket(host, port=6000) -> _LineSocket:
    """THE connection to sys-botbase on this Switch, shared by button presses, memory reads and screenshots.
    sys-botbase-cpp (3.x) serves only one connection at a time - a second one is never answered (sys-botbase 2.x serves
    several, and works the same way through one)."""
    key = (host, int(port))
    if key not in _SHARED:
        # (no replies) no echo; hex answers on - sys-botbase-cpp may still have them off (2.x ignores the option)
        _SHARED[key] = _LineSocket(host, int(port), init=b"configure echoCommands 0\r\nconfigure enableBackwardsCompat 1\r\n")
    return _SHARED[key]


class SysBotBaseController(Controller):
    """WiFi control through sys-botbase (CFW Switch). Protocol: text lines ending in \\r\\n."""

    # sys-botbase only reads the socket every ~50 ms and WiFi can bunch packets
    # together, so a "press" and "release" sent from the PC 80 ms apart can land
    # in the same tick = a zero-length press the game never sees. Single taps
    # therefore use sys-botbase's own "click", which holds the button for
    # buttonClickSleepTime ms ON THE SWITCH, independent of network timing.
    MIN_HOLD = 0.15

    def __init__(self, host, port=6000):
        self.conn = sysbot_socket(host, port)
        self._click_ms = None

    def _cmd(self, text):
        self.conn.send((text + "\r\n").encode())

    def connect(self):
        self.conn.open()
        self._click_ms = None
        # If a previous bot was closed abruptly its virtual controller can stay half-attached and the game then
        # ignores every input: release everything and detach it first, then attach a fresh one.
        for b in ("A", "B", "X", "Y", "L", "R", "PLUS", "MINUS"):
            self._cmd(f"release {b}")
        self._cmd("detachController")
        time.sleep(0.5)
        # The first input after connecting only attaches sys-botbase's virtual
        # controller and is lost. Attach it now with a neutral stick input.
        self._cmd("setStick LEFT 0 0")
        time.sleep(1.0)
        print(f"[sysbotbase] connected to {self.conn.host}:{self.conn.port}")

    def press(self, buttons, hold=0.1):
        buttons = _check(buttons)
        hold = max(hold, self.MIN_HOLD)
        if len(buttons) == 1:
            ms = int(hold * 1000)
            if ms != self._click_ms:
                self._cmd(f"configure buttonClickSleepTime {ms}")
                self._click_ms = ms
            self._cmd(f"click {buttons[0]}")
            time.sleep(hold + 0.05)  # click runs on the Switch; wait it out
            return
        for b in buttons:
            self._cmd(f"press {b}")
        time.sleep(hold)
        for b in buttons:
            self._cmd(f"release {b}")
        time.sleep(0.1)

    def close(self):
        try:
            for b in ("A", "B", "X", "Y"):
                self._cmd(f"release {b}")
            # Hand control back to the real controllers.
            self._cmd("detachController")
        except Exception:
            pass
        self.conn.close()


class NxbtBridgeController(Controller):
    """Bluetooth control through bridge/nxbt_bridge.py (JSON lines over TCP)."""

    def __init__(self, host, port=7000):
        self.conn = _LineSocket(host, port, timeout=60)

    def _request(self, payload: dict) -> dict:
        self.conn.send((json.dumps(payload) + "\n").encode())
        reply = json.loads(self.conn.readline())
        if not reply.get("ok"):
            raise RuntimeError(f"bridge error: {reply.get('error')}")
        return reply

    def connect(self):
        self.conn.open()
        info = self._request({"cmd": "status"})
        print(f"[nxbt] bridge at {self.conn.host}:{self.conn.port}: {info.get('state')}")

    def press(self, buttons, hold=0.1):
        buttons = _check(buttons)
        self._request({"cmd": "press", "buttons": buttons, "down": hold})

    def close(self):
        self.conn.close()


def make_controller(cfg: dict) -> Controller:
    kind = cfg.get("type", "dryrun").lower()
    if kind == "sysbotbase":
        return SysBotBaseController(cfg["host"], int(cfg.get("port", 6000)))
    if kind == "nxbt":
        return NxbtBridgeController(cfg["host"], int(cfg.get("port", 7000)))
    if kind == "dryrun":
        return DryRunController()
    raise ValueError(f"Unknown controller type '{kind}' (use sysbotbase, nxbt or dryrun)")
