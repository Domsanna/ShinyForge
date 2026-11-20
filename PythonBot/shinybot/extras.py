"""Extra features shared by the hunters:

- shinyforge.json (written by the ShinyForge app) overrides config.yaml: mode, stop rules, notifications, auto-catch, RNG
- phone / Discord notifications (ntfy.sh and Discord webhooks)
- pause.flag  (ShinyForge / the phone remote creates it -> the bot pauses between attempts)
- bot_status.json (what the bot is doing right now - read by ShinyForge's overlay / phone remote)
- stop rules: stop on species, natures, IVs - not only on shinies
- a screenshot of the Switch when something is found (sys-botbase pixelPeek, or the capture card with sys-botbase-cpp)
"""
from __future__ import annotations

import atexit
import json
import os
import subprocess
import threading
import time
import urllib.request
import uuid
from datetime import datetime

from .config import ROOT

OVERRIDES = ROOT / "shinyforge.json"
PAUSE_FLAG = ROOT / "pause.flag"
STATUS = ROOT / "bot_status.json"

# Started by ShinyForge, which shows the bot's output on the game page: no console to type in, no OpenCV window.
EMBEDDED = bool(os.environ.get("SHINYFORGE_EMBEDDED"))
WAITING_FLAG = ROOT / "hunt_waiting.flag"  # the bot waits for the Keep hunting / Stop hunter buttons
ANSWER = ROOT / "hunt_answer.txt"          # ShinyForge writes "keep" or "stop" here
PREVIEW = ROOT / "hunt_preview.jpg"        # vision mode: the picture the bot sees
# Shiny failsafe: written the moment something is found, removed only when the user chooses "keep hunting" (or presses
# Start hunting in ShinyForge). While it exists, a hunter that gets started again (ShinyForge reconnecting after the Switch
# went to sleep, start_hunt.bat ...) presses NOTHING until the user answers - the found Pokemon stays in the game.
HOLD_FLAG = ROOT / "shiny_hold.flag"


def apply_overrides(cfg: dict) -> dict:
    """Merge shinyforge.json (set from the ShinyForge app) into the config."""
    try:
        o = json.loads(OVERRIDES.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return cfg
    hunt = cfg.setdefault("hunt", {}) or {}
    cfg["hunt"] = hunt
    for k in ("mode", "stop_rules", "auto_catch", "rng", "target_species"):
        if k in o and o[k] is not None:
            hunt[k] = o[k]
    if "notify" in o:
        cfg["notify"] = o["notify"]
    if "controller" in o:
        cfg["controller"].update(o["controller"])
    return cfg


# ---------------------------------------------------------------------------- status / pause
def write_status(**fields):
    fields["updated"] = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    try:
        tmp = STATUS.with_suffix(".tmp")
        tmp.write_text(json.dumps(fields), encoding="utf-8")
        tmp.replace(STATUS)
    except OSError:
        pass


def wait_while_paused(on_pause=None):
    """Blocks while pause.flag exists. Returns True if it paused."""
    if not PAUSE_FLAG.exists():
        return False
    print("  Paused (from ShinyForge / the phone remote) - waiting for Resume...")
    if on_pause:
        on_pause()
    while PAUSE_FLAG.exists():
        time.sleep(1)
    print("  Resumed.")
    return True


def remove_file(path):
    try:
        path.unlink()
    except OSError:
        pass


def ask_keep_hunting(prompt: str, found: str = "") -> bool:
    """After a find: True = keep hunting. Asked in the console, or with ShinyForge's buttons when it runs the bot.
    The find is held (shiny_hold.flag) until the answer is "keep hunting"."""
    if not HOLD_FLAG.exists():
        try:
            HOLD_FLAG.write_text(f"{datetime.now():%Y-%m-%d %H:%M:%S}\t{found or 'Something was found'}", encoding="utf-8")
        except OSError:
            pass
    keep = _ask_keep_hunting(prompt)
    if keep:
        remove_file(HOLD_FLAG)
    return keep


def held_find() -> str | None:
    """What the failsafe is holding ("SHINY Deoxys (PID ...)"), or None."""
    try:
        text = HOLD_FLAG.read_text(encoding="utf-8").strip()
    except OSError:
        return None
    return (text.split("\t", 1)[1] if "\t" in text else text) or "Something was found"


def respect_shiny_hold():
    """Called before a hunter touches the Switch: if a find is still held, wait for the user instead of resetting the game.
    Raises SystemExit when the user chooses to stop."""
    what = held_find()
    if what is None:
        return
    print("\n" + "*" * 60)
    print(f"  FAILSAFE: {what} was found and the hunt was never told to go on.")
    print("  It may still be in the game, so NO button is pressed (no reset) until you answer.")
    print("  Catch it and save first - keep hunting RESETS the game.")
    print("*" * 60)
    write_status(state="shiny-held", last=None)
    if not ask_keep_hunting("\nType 'c' + Enter to keep hunting (this RESETS the game!), or just Enter to quit: ", what):
        write_status(state="stopped", last=None)
        raise SystemExit("Stopped: the found Pokemon is kept - nothing was pressed.")
    print("  Going on with the hunt (you chose keep hunting).\n")


def _ask_keep_hunting(prompt: str) -> bool:
    if not EMBEDDED:
        return input(prompt).strip().lower() == "c"
    remove_file(ANSWER)
    WAITING_FLAG.write_text(datetime.now().strftime("%Y-%m-%d %H:%M:%S"), encoding="utf-8")
    print("\nWaiting for you: press Keep hunting or Stop hunter in ShinyForge.")
    try:
        while True:
            try:
                answer = ANSWER.read_text(encoding="utf-8").strip().lower()
            except OSError:
                answer = ""
            if answer:
                return answer == "keep"
            time.sleep(0.5)
    finally:
        remove_file(ANSWER)
        remove_file(WAITING_FLAG)


# ---------------------------------------------------------------------------- stop rules
def gen3_gender(pid: int, ratio: int) -> int:
    """0 = male, 1 = female, 2 = genderless (Gen 3: the PID's low byte against the species' gender ratio)."""
    if ratio == 255:
        return 2
    if ratio == 254:
        return 1
    if ratio == 0:
        return 0
    return 1 if (pid & 0xFF) < ratio else 0


def gen3_unown_form(pid: int) -> int:
    """Unown's letter (0 = A ... 25 = Z, 26 = !, 27 = ?) from its PID."""
    return (((pid >> 24) & 3) << 6 | ((pid >> 16) & 3) << 4 | ((pid >> 8) & 3) << 2 | (pid & 3)) % 28


UNOWN_INDEX = 201  # internal index = National Dex number below 252


def target_matches(p, t: dict) -> bool:
    """A stop target from ShinyForge: {"species_index", "gender_ratio", "form" (-1 any), "gender", "shiny", "name"}."""
    if p.species != int(t.get("species_index", -1)):
        return False
    form = int(t.get("form", -1))
    if form >= 0 and p.species == UNOWN_INDEX and gen3_unown_form(p.pid) != form:
        return False
    gender = (t.get("gender") or "any").lower()
    actual = gen3_gender(p.pid, int(t.get("gender_ratio", 127)))
    if gender == "male" and actual != 0 or gender == "female" and actual != 1:
        return False
    shiny = (t.get("shiny") or "any").lower()
    if shiny in ("shiny", "star", "square") and not p.is_shiny or shiny == "not" and p.is_shiny:
        return False
    return True


def check_rules(p, rules: dict | None) -> str | None:
    """Returns why the bot should stop for Pokemon `p` (besides being shiny), or None.

    rules = {"targets": [{...stop target from ShinyForge...}], "natures": ["Adamant", "Jolly"], "min_iv_total": 150,
             "min_ivs": {"Spe": 31, "Atk": 25}, "require_all": false}
    require_all = true: every given condition must match; false: any one is enough."""
    if not rules:
        return None
    checks = []
    if rules.get("species"):
        checks.append(("species " + p.species_name, p.species_name.lower() in [s.lower() for s in rules["species"]]))
    if rules.get("targets"):
        hit = next((t for t in rules["targets"] if target_matches(p, t)), None)
        checks.append((hit["name"] if hit else "a stop target", hit is not None))
    if rules.get("natures"):
        checks.append(("nature " + p.nature, p.nature.lower() in [n.lower() for n in rules["natures"]]))
    if rules.get("min_iv_total"):
        checks.append((f"IV total {p.iv_total}", p.iv_total >= int(rules["min_iv_total"])))
    for stat, v in (rules.get("min_ivs") or {}).items():
        if v:
            checks.append((f"{stat} IV {p.ivs.get(stat, 0)}", p.ivs.get(stat, 0) >= int(v)))
    if not checks:
        return None
    hits = [name for name, ok in checks if ok]
    if rules.get("require_all", False):
        return "matches your stop rules (" + ", ".join(hits) + ")" if len(hits) == len(checks) else None
    return "matches a stop rule (" + ", ".join(hits) + ")" if hits else None


# ---------------------------------------------------------------------------- screenshot
def switch_screenshot(host: str, port: int = 6000) -> bytes | None:
    """The Switch's screen as JPEG bytes, or None.
    sys-botbase 2.x: its own screenshot (pixelPeek). sys-botbase-cpp (3.x): the capture card ShinyForge named in
    shinyforge.json - with a game running, sys-botbase-cpp's fixed 3 MB of memory can't hold a screenshot (std::bad_alloc in
    pixelPeek, then it answers nothing until the Switch restarts). The hunter always has the game open, so without a capture
    card there's no screenshot with sys-botbase-cpp."""
    from .controllers import sysbot_socket  # the hunt's one connection (sys-botbase-cpp answers only one)
    link = sysbot_socket(host, port)
    try:
        if _is_cpp(link.request("getVersion", timeout=5)):
            return _CARD.grab()
        return bytes.fromhex(link.request("pixelPeek", timeout=20)) or None
    except (OSError, ValueError):
        return None


def _is_cpp(version: str) -> bool:
    try:
        return int(version.split("/")[-1].split(".")[0]) >= 3
    except ValueError:
        return False


class _CaptureCard:
    """Reads the capture card with one FFmpeg (5 JPEG pictures a second, 1280x720 like sys-botbase's screenshots) while
    screenshots are wanted; stops 30 s after the last one. FFmpeg and the device come from shinyforge.json ("switch_screen")."""

    def __init__(self):
        self.proc = None
        self.frame, self.frame_at, self.wanted_at = None, 0.0, 0.0
        self.lock = threading.Lock()

    def grab(self, timeout: float = 6.0) -> bytes | None:
        asked = time.time()
        with self.lock:
            self.wanted_at = asked
            if self.proc is None or self.proc.poll() is not None:
                if not self._start():
                    return None
        while time.time() - asked < timeout:  # a picture that left the card after the request (~0.1-0.2 s delay)
            with self.lock:
                if self.frame and self.frame_at >= asked + 0.25:
                    return self.frame
                if self.proc is None or self.proc.poll() is not None:
                    break
            time.sleep(0.04)
        return None

    def _start(self) -> bool:
        try:
            screen = json.loads(OVERRIDES.read_text(encoding="utf-8")).get("switch_screen") or {}
        except (OSError, ValueError):
            screen = {}
        if not screen.get("ffmpeg") or not screen.get("args"):
            print("  No capture card is set for screenshots (ShinyForge names it) - sys-botbase-cpp can't take them in-game.")
            return False
        try:
            self.proc = subprocess.Popen([screen["ffmpeg"], "-hide_banner", "-loglevel", "error", *screen["args"]],
                                         stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                                         creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        except OSError as e:
            print(f"  The capture card couldn't be read for a screenshot: {e}")
            return False
        self.frame = None
        threading.Thread(target=self._read, args=(self.proc,), daemon=True).start()
        threading.Thread(target=self._stop_when_idle, args=(self.proc,), daemon=True).start()
        return True

    def _read(self, proc):
        """Splits FFmpeg's stream into pictures: FFmpeg's JPEGs end at the first FF D9 (no thumbnails or restart markers,
        and the compressed data never holds FF D9)."""
        buf = b""
        try:
            while True:
                chunk = proc.stdout.read1(1 << 16)
                if not chunk:
                    return  # FFmpeg stopped
                buf += chunk
                while (start := buf.find(b"\xff\xd8")) >= 0 and (end := buf.find(b"\xff\xd9", start + 2)) >= 0:
                    jpg, buf = buf[start:end + 2], buf[end + 2:]
                    with self.lock:
                        if proc is not self.proc:
                            return
                        self.frame, self.frame_at = jpg, time.time()
                if len(buf) > 0x400000:
                    buf = b""
        except (OSError, ValueError):
            pass

    def _stop_when_idle(self, proc):
        while proc.poll() is None:
            time.sleep(5)
            with self.lock:
                if proc is self.proc and time.time() - self.wanted_at > 30:
                    self.stop()

    def stop(self):
        p, self.proc, self.frame = self.proc, None, None
        if p and p.poll() is None:
            try:
                p.stdin.write(b"q")
                p.stdin.flush()
                p.wait(1.5)
            except (OSError, subprocess.TimeoutExpired):
                p.kill()


_CARD = _CaptureCard()
atexit.register(lambda: _CARD.stop())


def save_screenshot(host: str, port: int, tag: str):
    data = switch_screenshot(host, port)
    if not data:
        return None
    d = ROOT / "shiny_screens"
    d.mkdir(exist_ok=True)
    path = d / f"{tag}_{datetime.now():%Y%m%d_%H%M%S}.jpg"
    path.write_bytes(data)
    return path


# ---------------------------------------------------------------------------- notifications
def notify(cfg: dict, title: str, message: str, important=False, image=None):
    """Discord webhook and/or ntfy.sh push - whatever is set in ShinyForge (Settings -> Notifications)."""
    n = cfg.get("notify") or {}
    sent = []
    hook = (n.get("discord_webhook") or "").strip()
    if hook:
        try:
            content = ("@everyone " if important and n.get("mention_everyone") else "") + f"**{title}**\n{message}"
            payload = json.dumps({"username": "ShinyForge", "content": content}).encode()
            if image and image.exists():
                b = uuid.uuid4().hex
                body = (f"--{b}\r\nContent-Disposition: form-data; name=\"payload_json\"\r\nContent-Type: application/json\r\n\r\n").encode() + payload + \
                       (f"\r\n--{b}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"{image.name}\"\r\nContent-Type: image/jpeg\r\n\r\n").encode() + \
                       image.read_bytes() + f"\r\n--{b}--\r\n".encode()
                req = urllib.request.Request(hook, data=body, headers={"Content-Type": f"multipart/form-data; boundary={b}", "User-Agent": "ShinyForge"})
            else:
                req = urllib.request.Request(hook, data=payload, headers={"Content-Type": "application/json", "User-Agent": "ShinyForge"})
            urllib.request.urlopen(req, timeout=15).read()
            sent.append("Discord")
        except Exception as e:  # never let a notification stop the hunt
            print(f"  [notify] Discord failed: {e}")
    topic = (n.get("ntfy_topic") or "").strip()
    if topic:
        try:
            server = (n.get("ntfy_server") or "https://ntfy.sh").rstrip("/")
            req = urllib.request.Request(f"{server}/{topic}", data=message.encode(), headers={
                "Title": title.encode("ascii", "replace").decode(), "Priority": "urgent" if important else "default",
                "Tags": "sparkles" if important else "warning", "User-Agent": "ShinyForge"})
            urllib.request.urlopen(req, timeout=15).read()
            sent.append("ntfy")
        except Exception as e:
            print(f"  [notify] ntfy failed: {e}")
    if sent:
        print(f"  [notify] sent to {', '.join(sent)}")
