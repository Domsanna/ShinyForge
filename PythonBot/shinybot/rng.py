"""OPTIONAL shiny RNG manipulation for FireRed/LeafGreen static encounters (Deoxys, Mewtwo, the birds, Lugia, Ho-Oh...).

Only used when you choose it yourself (ShinyForge: "Shiny RNG (optional)", or config.yaml hunt.mode: rng).
It is never selected automatically.

How it works (verified on the Switch release):
- The game's random number (gRngValue) advances once per frame (60/s) in the overworld; sys-botbase reads it live.
- Static encounters use Gen 3 "Method 1": PID = (rng(n+2) >> 16) << 16 | (rng(n+1) >> 16), IVs from the next two calls.
- The Pokemon is generated a FIXED number of frames after you press A (Deoxys: ~1006 frames from the moment the
  bot reads the RNG and sends the press). The bot measures that delay itself on the first attempt.
- So: after a reset, read the RNG, look ahead for the next frame whose Method-1 PID is shiny for YOUR trainer ID/SID,
  wait until "that frame minus the delay", press A. A miss (WiFi jitter of 1-2 frames) just means one more soft reset.
"""
from __future__ import annotations

import statistics
import time

M = 0xFFFFFFFF
MULT, ADD = 0x41C64E6D, 0x6073
NATURES = ["Hardy", "Lonely", "Brave", "Adamant", "Naughty", "Bold", "Docile", "Relaxed", "Impish", "Lax", "Timid", "Hasty",
           "Serious", "Jolly", "Naive", "Modest", "Mild", "Quiet", "Bashful", "Rash", "Calm", "Gentle", "Sassy", "Careful", "Quirky"]


def nxt(s: int) -> int:
    return (s * MULT + ADD) & M


def method1_at(s: int):
    """The Pokemon generated from state `s` (index i): PID from the next two calls, IVs from the two after."""
    a = nxt(s); b = nxt(a); c = nxt(b); d = nxt(c)
    pid = ((b >> 16) << 16) | (a >> 16)
    iv1, iv2 = c >> 16, d >> 16
    ivs = {"HP": iv1 & 31, "Atk": (iv1 >> 5) & 31, "Def": (iv1 >> 10) & 31,
           "Spe": iv2 & 31, "SpA": (iv2 >> 5) & 31, "SpD": (iv2 >> 10) & 31}
    return pid, ivs


def is_shiny(pid: int, tid: int, sid: int) -> bool:
    return (tid ^ sid ^ (pid >> 16) ^ (pid & 0xFFFF)) < 8


def index_of(base: int, target: int, limit: int = 2_000_000):
    s = base
    for i in range(limit):
        if s == target:
            return i
        s = nxt(s)
    return None


def find_index_of_pid(base: int, pid: int, limit: int = 200_000):
    s = base
    for i in range(limit):
        if method1_at(s)[0] == pid:
            return i
        s = nxt(s)
    return None


def find_target(base: int, start: int, tid: int, sid: int, natures=None, min_ivs=None, horizon=60 * 60 * 60,
                step: int = 1, delay: int = 0):
    """First index >= start (advances after `base`) whose Method-1 Pokemon is shiny (and matches the optional filters).
    `step` = RNG advances per frame where the player stands (Birth Island: 2) - only indices a button press can reach
    ((index - delay) divisible by step) are returned."""
    s = base
    for _ in range(start):
        s = nxt(s)
    natures = [n.lower() for n in natures or []]
    for i in range(start, start + horizon):
        pid, ivs = method1_at(s)
        if (i - delay) % step == 0 and is_shiny(pid, tid, sid):
            ok = not natures or NATURES[pid % 25].lower() in natures
            if ok and min_ivs:
                ok = all(ivs.get(k, 0) >= int(v) for k, v in min_ivs.items() if v)
            if ok:
                return i, pid, ivs
        s = nxt(s)
    return None


class RngTimer:
    """Hits a target frame: tracks the live RNG index and presses A `delay` frames before it."""

    def __init__(self, read_state, press_a, delay=None):
        self.read_state = read_state      # () -> current gRngValue
        self.press_a = press_a            # () -> sends the A press (returns quickly)
        self.delays = [delay] if delay else []
        self.debug = False
        self.last_problem = ""
        self.step = 1

    def measure_step(self, reads: int = 12) -> int:
        """RNG advances per frame here (1 normally, 2 on Birth Island): the gcd of the steps between fast reads."""
        from math import gcd
        prev, g = self.read_state(), 0
        for _ in range(reads):
            s = self.read_state()
            d = index_of(prev, s, 10_000)
            if d:
                g = gcd(g, d)
            prev = s
        self.step = g if g in (1, 2, 3, 4) else 1
        return self.step

    @property
    def delay(self):
        return int(statistics.median(self.delays)) if self.delays else None

    def learn(self, measured: int):
        self.delays.append(measured)
        self.delays = self.delays[-7:]

    def press_at(self, base: int, target_index: int):
        """Waits until frame `target_index - delay` (counted from `base`) and presses A.
        Returns the index (from `base`) at which the press was sent, or None if the moment had already passed."""
        delay = self.delay
        prev, idx = base, 0
        while True:
            state = self.read_state()
            t_read = time.perf_counter()
            step = index_of(prev, state, limit=200_000)
            if step is None:
                self.last_problem = "the RNG jumped (game reset?)"
                return None
            idx += step
            prev = state
            remaining = target_index - delay - idx
            if self.debug:
                print(f"    [rng] idx {idx} remaining {remaining}")
            if remaining < 0:
                self.last_problem = f"the moment passed ({-remaining} advances late)"
                return None
            frames = remaining / self.step
            if frames > 40:
                time.sleep(min((frames - 30) / 60, 2.0))
                continue
            if frames > 6:
                # get close with short sleeps and fresh reads (each read is only a frame or two)
                time.sleep(max(0.0, (frames - 5) / 60 - 0.02))
                continue
            # last few frames: busy-wait (time.sleep on Windows is only accurate to ~16 ms = a whole frame)
            deadline = t_read + frames / 60
            while time.perf_counter() < deadline:
                pass
            self.press_a()
            return idx + remaining
