"""Grabbing frames of the game from Streamlabs.

  virtualcam - Streamlabs Desktop "Virtual Camera" read like a webcam
               (best option: works even if the Streamlabs window is covered).
  window     - Screenshots of the Streamlabs window (must be visible, not minimized).
  screen     - Screenshots of a whole monitor.
"""
from __future__ import annotations

import ctypes
import threading
import time
from ctypes import wintypes

import cv2
import numpy as np

try:  # make window coordinates match real pixels on high-DPI screens
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    pass


class VirtualCamSource:
    def __init__(self, index=0, width=1280, height=720):
        self.cap = cv2.VideoCapture(int(index), cv2.CAP_DSHOW)
        if not self.cap.isOpened():
            raise RuntimeError(
                f"Could not open camera #{index}. In Streamlabs click 'Start Virtual Camera' "
                "and try another camera index (python -m shinybot cameras lists them).")
        self.cap.set(cv2.CAP_PROP_FRAME_WIDTH, width)
        self.cap.set(cv2.CAP_PROP_FRAME_HEIGHT, height)
        self.frame = None
        self.running = True
        self.lock = threading.Lock()
        # Read continuously in the background so we always get the newest frame.
        threading.Thread(target=self._loop, daemon=True).start()
        t0 = time.time()
        while self.frame is None and time.time() - t0 < 10:
            time.sleep(0.05)
        if self.frame is None:
            raise RuntimeError("Virtual camera opened but sent no frames.")

    def _loop(self):
        while self.running:
            ok, f = self.cap.read()
            if ok:
                with self.lock:
                    self.frame = f
            else:
                time.sleep(0.01)

    def read(self) -> np.ndarray:
        with self.lock:
            return self.frame.copy()

    def close(self):
        self.running = False
        time.sleep(0.05)
        self.cap.release()


def find_window(title_part: str):
    """Return (hwnd, title) of the first visible window whose title contains title_part."""
    user32 = ctypes.windll.user32
    found = []

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def cb(hwnd, _):
        if user32.IsWindowVisible(hwnd):
            n = user32.GetWindowTextLengthW(hwnd)
            if n:
                buf = ctypes.create_unicode_buffer(n + 1)
                user32.GetWindowTextW(hwnd, buf, n + 1)
                if title_part.lower() in buf.value.lower():
                    found.append((hwnd, buf.value))
        return True

    user32.EnumWindows(cb, 0)
    return found[0] if found else (None, None)


class WindowSource:
    def __init__(self, title="Streamlabs"):
        import mss
        self.sct = mss.mss()
        self.title = title
        self.hwnd, name = find_window(title)
        if not self.hwnd:
            raise RuntimeError(f"No visible window with '{title}' in its title. Is Streamlabs open?")
        print(f"[capture] using window: {name}")

    def _client_rect(self):
        user32 = ctypes.windll.user32
        r = wintypes.RECT()
        user32.GetClientRect(self.hwnd, ctypes.byref(r))
        pt = wintypes.POINT(0, 0)
        user32.ClientToScreen(self.hwnd, ctypes.byref(pt))
        return {"left": pt.x, "top": pt.y, "width": r.right, "height": r.bottom}

    def read(self) -> np.ndarray:
        if ctypes.windll.user32.IsIconic(self.hwnd):
            raise RuntimeError("Streamlabs window is minimized - restore it.")
        img = np.array(self.sct.grab(self._client_rect()))
        return cv2.cvtColor(img, cv2.COLOR_BGRA2BGR)

    def close(self):
        self.sct.close()


class ScreenSource:
    def __init__(self, monitor=1):
        import mss
        self.sct = mss.mss()
        self.mon = self.sct.monitors[int(monitor)]

    def read(self) -> np.ndarray:
        return cv2.cvtColor(np.array(self.sct.grab(self.mon)), cv2.COLOR_BGRA2BGR)

    def close(self):
        self.sct.close()


def make_source(cfg: dict):
    kind = cfg.get("type", "virtualcam").lower()
    if kind == "virtualcam":
        return VirtualCamSource(cfg.get("camera_index", 0), cfg.get("width", 1280), cfg.get("height", 720))
    if kind == "window":
        return WindowSource(cfg.get("window_title", "Streamlabs"))
    if kind == "screen":
        return ScreenSource(cfg.get("monitor", 1))
    raise ValueError(f"Unknown capture type '{kind}' (use virtualcam, window or screen)")


def list_cameras(max_index=8):
    out = []
    for i in range(max_index):
        cap = cv2.VideoCapture(i, cv2.CAP_DSHOW)
        if cap.isOpened():
            ok, f = cap.read()
            out.append((i, f.shape[1::-1] if ok else None))
        cap.release()
    return out
