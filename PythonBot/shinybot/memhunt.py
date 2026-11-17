"""Memory-based hunting for ANY FireRed/LeafGreen encounter (needs sys-botbase).

Modes (config.yaml -> hunt: mode):
  auto    - reads the game version and where the player stands: at a legendary/static spot
            (Birth Island, Navel Rock, Seafoam B4F, Power Plant, Mt. Ember, Cerulean Cave,
            Snorlax routes) -> static, anywhere else -> wild.
  static  - soft reset (A+B+X+Y) in front of any static Pokemon (legendaries, Snorlax,
            Deoxys...), skip the intro, mash A from the title screen into the battle (no title-screen
            delay is needed: tested on the Switch, every reset already gives a different Pokemon).
  wild    - walk back and forth in grass / a cave / on water until a wild battle starts.
  fishing - use the rod registered to the item button until something bites.
  rng     - OPTIONAL (never chosen automatically): shiny RNG manipulation for static encounters,
            see rng.py. Only runs when you pick it yourself.
Every encounter's stats are printed and logged to encounters.csv. Shiny (read from the
game's own data) -> the bot stops pressing buttons and waits for you. Otherwise: static
mode resets, wild/fishing modes run from the battle - but never from a legendary or
mythical Pokemon: a static legendary switches the bot to soft resets, a roaming beast stops it.
"""
from __future__ import annotations

import csv
import random
import time
from collections import deque
from datetime import datetime

from .config import ROOT
from .controllers import make_controller
from .gen3 import decode
from .hunt import alert_shiny, load_stats, log_encounter_csv, save_stats


def load_recent_pids(n=500):
    """PIDs of the last real encounters in encounters.csv (consecutive duplicates = stuck resets are skipped)."""
    try:
        with open(ROOT / "encounters.csv", encoding="utf-8") as fh:
            rows = list(csv.DictReader(fh))[-n:]
    except (OSError, csv.Error):
        return []
    out = []
    for r in rows:
        try:
            pid = int(r.get("pid") or "", 16)
        except ValueError:
            continue
        if not out or out[-1] != pid:
            out.append(pid)
    return out


from .extras import check_rules, notify, save_screenshot, switch_screenshot, wait_while_paused, write_status
from .memory import (BALL_NAMES, BATTLE_MENU, GBA_ANCHOR, IN_BATTLE, INITIAL_SEED, LEGENDARY_OR_MYTHICAL, ROAMERS, STATIC_SPOTS,
                     GameMemory, current_seed_offset)
from . import rng


class MemoryHunter:
    def __init__(self, cfg):
        if cfg["controller"].get("type") != "sysbotbase":
            raise SystemExit("static/wild/fishing modes read the game's memory and need controller type sysbotbase.")
        self.cfg = cfg
        self.h = cfg.get("hunt", {}) or {}
        self.t = cfg["timing"]
        self.mode = self.h.get("mode", "auto")
        self.ctrl = make_controller(cfg["controller"])
        self.game = GameMemory(cfg["controller"])
        self.mem = self.game.mem
        self.stats = load_stats()
        self.hold = float(self.t.get("press_hold", 0.15))
        self.last_pid = None    # PID of the last logged encounter
        # ---- same-Pokemon guard
        self.recent = deque(load_recent_pids(), maxlen=500)  # PIDs of recent real encounters
        self.extra_jitter = float(self.h.get("extra_random_delay", 0.0))  # grows when RNG repeats are seen
        self.stale_in_a_row = 0
        self.session = 0                     # encounters in this run
        self.started = datetime.now()
        self.rules = self.h.get("stop_rules") or {}
        self.rng_timer = None                # created when the optional RNG mode is used

    # ---------------------------------------------------------------- status for ShinyForge (overlay / phone remote)
    def status(self, state: str, p=None):
        last = None
        if p is not None:
            last = {"species": p.species_name, "nature": p.nature, "iv_total": p.iv_total, "shiny": p.is_shiny,
                    "pid": f"{p.pid:08X}"}
        write_status(state=state, mode=self.mode, encounters=self.stats["encounters"], session=self.session,
                     started=f"{self.started:%Y-%m-%d %H:%M:%S}", last=last)

    # ---------------------------------------------------------------- same-Pokemon guard
    def warn(self, text: str):
        """Loud warning in the console + a line in bot_warnings.log (ShinyForge shows these to the user)."""
        print("\n" + "!" * 60 + f"\n  WARNING: {text}\n" + "!" * 60)
        try:
            with open(ROOT / "bot_warnings.log", "a", encoding="utf-8") as fh:
                fh.write(f"{datetime.now():%Y-%m-%d %H:%M:%S}\t{text}\n")
        except OSError:
            pass
        if (self.cfg.get("notify") or {}).get("problems"):
            notify(self.cfg, "ShinyForge bot warning", text)
        try:
            import winsound
            winsound.Beep(440, 250)
        except Exception:
            pass

    def is_repeat(self, p) -> bool:
        """True if `p` must NOT be counted. Handles both ways the same Pokemon can come back:
        1. exactly the last one again  -> the game didn't really reset / old battle data -> fix the reset
        2. one seen earlier in the hunt -> two resets landed on the same RNG frame -> widen the timing randomness"""
        if p.pid == self.last_pid:
            self.stale_in_a_row += 1
            self.warn(f"The same {p.species_name} (PID {p.pid:08X}) came back {self.stale_in_a_row}x in a row - the game "
                      "did not reset. Fixing: checking the reset and retrying it with a longer hold.")
            if self.mode == "static":
                self.t["reset_hold"] = float(self.t.get("reset_hold", 1.5)) + 0.5
                self.ctrl.connect()  # re-attach the virtual controller
            if self.stale_in_a_row >= 5:
                raise RuntimeError("The same Pokemon keeps coming back even after the automatic fixes.")
            return True
        self.stale_in_a_row = 0
        if p.pid in self.recent and self.mode == "static":
            self.extra_jitter = min(self.extra_jitter + 0.5, 4.0)
            self.warn(f"{p.species_name} PID {p.pid:08X} was already found earlier in this hunt (same RNG frame). "
                      f"Fixed automatically: up to {self.extra_jitter:.1f}s of extra random timing per reset.")
        self.recent.append(p.pid)
        return False

    # ---------------------------------------------------------------- memory helpers
    def in_battle(self) -> bool:
        return self.mem.peek(IN_BATTLE, 1)[0] == 2

    def on_battle_menu(self) -> bool:
        return self.mem.peek(BATTLE_MENU, 1)[0] == 2

    def read_wild(self, tries=40):
        for _ in range(tries):
            p = decode(self.mem.peek(self.game.offsets[0], 100))
            if p and p.checksum_ok:
                return p
            time.sleep(0.1)
        return None

    # ---------------------------------------------------------------- actions
    def tap(self, b, wait):
        self.ctrl.tap(b, self.hold)
        time.sleep(wait)

    def flee(self) -> bool:
        t0 = time.time()
        while self.in_battle():
            if time.time() - t0 > float(self.h.get("flee_timeout", 60)):
                return False
            lead = decode(self.mem.peek(self.game.offsets[1], 100))
            if lead and lead.checksum_ok and lead.hp == 0:
                print("  Your lead Pokemon fainted while trying to run (escapes kept failing against faster wild "
                      "Pokemon). Lead with a faster / higher-level Pokemon, or give it a Smoke Ball.")
                return False
            if self.on_battle_menu():
                # B first: harmless on the FIGHT/BAG/POKeMON/RUN menu, but backs out of any
                # sub-menu (the menu flag stays set inside the party/bag screens).
                time.sleep(0.4)  # let the menu finish appearing (e.g. after "Can't escape!")
                self.tap("B", 0.35)
                self.tap("B", 0.35)
                self.tap("DRIGHT", 0.25)
                self.tap("DDOWN", 0.25)
                self.tap("A", 0.8)
            else:
                self.tap("B", 0.4)
        time.sleep(0.8)
        return True

    # ---------------------------------------------------------------- encounter handling
    def judge(self, p) -> bool:
        """Log the encounter. Returns True when the bot should stop."""
        self.last_pid = p.pid
        n = self.stats["encounters"] + 1
        self.stats["encounters"] = n
        self.session += 1
        print(f"[encounter {n}] {datetime.now():%H:%M:%S}")
        print("    " + p.summary().replace("\n", "\n    "))
        verdict = "shiny" if p.is_shiny else "normal"
        targets = [s.lower() for s in self.h.get("target_species", []) or []]
        target_hit = bool(targets) and p.species_name.lower() in targets
        rule = check_rules(p, self.rules)
        log_encounter_csv(n, verdict, p, rule or "")
        save_stats(self.stats)
        self.status("found" if (p.is_shiny or target_hit or rule) else "hunting", p)
        if not (p.is_shiny or target_hit or rule):
            return False
        why = "SHINY " + p.species_name if p.is_shiny else f"{p.species_name} - " + (rule or "target species")
        shot = save_screenshot(self.cfg["controller"]["host"], int(self.cfg["controller"].get("port", 6000)),
                               ("shiny_" if p.is_shiny else "found_") + p.species_name)
        if shot:
            print(f"  Screenshot saved: {shot}")
        n_cfg = self.cfg.get("notify") or {}
        if n_cfg.get("shiny", True):
            notify(self.cfg, ("✨ " if p.is_shiny else "") + f"{why} after {n} encounters!",
                   f"{p.species_name} Lv.{p.level} {p.nature}, IVs {p.iv_total}/186, PID {p.pid:08X}. "
                   "The bot stopped - the game is waiting in the battle.", important=True, image=shot)
        self.stats["shinies"].append({"encounter": n, "time": f"{datetime.now():%Y-%m-%d %H:%M:%S}",
                                      "species": p.species_name, "pid": f"{p.pid:08X}"})
        save_stats(self.stats)
        print("\n" + "*" * 60)
        print(f"  {why.upper()} - FOUND after {n} encounters!")
        print("  The bot has stopped pressing buttons. Take over with your controller.")
        print("*" * 60)
        return True

    def after_find(self, p) -> bool:
        """Something was found: optional auto-catch, then wait for the user. True = keep hunting."""
        self.status("found", p)
        if self.auto_catch(p):
            notify(self.cfg, f"Caught {p.species_name}!", "Auto-catch caught it. SAVE THE GAME before you continue hunting!",
                   important=True)
            print("\n  CAUGHT! Save the game in the START menu before continuing to hunt (a reset would lose it).")
        return self.wait_for_user()

    # ---------------------------------------------------------------- optional auto-catch
    def auto_catch(self, p) -> bool:
        """OPTIONAL (off by default): throw Poke Balls until it's caught. Settings (ShinyForge -> hunt options):
        auto_catch.mode = off | non_legendary | all,  ball_slot = position in the POKe BALLS pocket (1 = top),
        max_balls. Stops (and leaves the battle to you) the moment anything unexpected happens."""
        ac = self.h.get("auto_catch") or {}
        mode = (ac.get("mode") or "off").lower()
        if mode == "off":
            return False
        if p.species_name in LEGENDARY_OR_MYTHICAL and mode != "all":
            print("  Auto-catch is set to non-legendary Pokemon only - leaving this one to you.")
            return False
        max_balls = int(ac.get("max_balls", 25))
        ball = int(ac.get("ball_item", 0))
        pocket = self.game.ball_pocket()
        if ball:
            if pocket is None:
                self.warn("Auto-catch: the bag couldn't be read - stopped, the battle is yours.")
                return False
            items = [item for item, _ in pocket]
            if ball not in items:
                self.warn(f"Auto-catch: there's no {BALL_NAMES.get(ball, ball)} in your bag - stopped, the battle is yours.")
                return False
            index, count = items.index(ball), pocket[items.index(ball)][1]
            max_balls = min(max_balls, count)
            what = BALL_NAMES.get(ball, f"ball {ball}")
        else:  # older settings: a position in the pocket
            index, what = max(1, int(ac.get("ball_slot", 1))) - 1, f"ball #{ac.get('ball_slot', 1)} in the POKe BALLS pocket"
        print(f"  Auto-catch: throwing up to {max_balls} x {what}...")
        self.status("catching", p)
        for n in range(1, max_balls + 1):
            t0 = time.time()
            while not self.on_battle_menu():
                if not self.in_battle():
                    return True
                if time.time() - t0 > 40:
                    self.warn("Auto-catch: the battle menu didn't come back - stopped, the battle is yours.")
                    return False
                self.tap("B", 1.2)   # B only advances battle text ("Wild X appeared!", "X used Y!")
            lead = decode(self.mem.peek(self.game.offsets[1], 100))
            if lead and lead.checksum_ok and lead.hp == 0:
                self.warn("Auto-catch: your lead Pokemon fainted - stopped, the battle is yours.")
                return False
            time.sleep(0.5)
            # FIGHT is top-left: go there first (the menu remembers the last choice), then BAG (top-right)
            self.tap("DUP", 0.2); self.tap("DLEFT", 0.2); self.tap("DRIGHT", 0.25); self.tap("A", 1.6)
            if not self.select_ball(index):
                self.warn(f"Auto-catch: the {what} couldn't be selected in the bag (a button press got lost?) - "
                          "nothing was thrown, the battle is yours.")
                return False
            self.tap("A", 0.8)    # the ball -> USE / CANCEL
            self.tap("A", 1.0)    # USE -> throw
            print(f"    ball {n} thrown")
            # The battle flags don't change during a throw (and the bag screen briefly clears the in-battle flag), so:
            # wait out the throw, then press B through the text ("Gotcha!", nickname -> No, Pokedex entry, or
            # "Oh no! It broke free!" + the wild Pokemon's move) until the battle has ended or ~30 s have passed.
            time.sleep(5.0)
            t0 = time.time()
            caught = False
            while time.time() - t0 < 30:
                if not self.in_battle():
                    time.sleep(1.0)
                    if not self.in_battle():   # really over (not a screen transition)
                        caught = True
                        break
                self.tap("B", 1.5)
            if caught:
                # "Gotcha!" -> nickname prompt (B = no) -> Pokedex entry
                for _ in range(12):
                    self.tap("B", 0.8)
                return True
        print("  Auto-catch: out of throws - the battle is yours.")
        return False

    # ---------------------------------------------------------------- picking the ball (checked on screenshots)
    def screen(self):
        """The Switch's screen, grey and shrunk (for "did it change?"), or None."""
        import cv2
        import numpy as np
        data = switch_screenshot(self.cfg["controller"]["host"], int(self.cfg["controller"].get("port", 6000)))
        if not data:
            return None
        img = cv2.imdecode(np.frombuffer(data, np.uint8), cv2.IMREAD_GRAYSCALE)
        return None if img is None else cv2.resize(img, (320, 180), interpolation=cv2.INTER_AREA).astype("int16")

    def press_and_check(self, button: str, tries: int = 3) -> bool | None:
        """Presses `button` and checks on screenshots that the picture really changed (a dropped press is repeated).
        Parts that change on their own (blinking scroll arrows) are measured first and ignored.
        True = it moved, False = nothing moves (end of the list / first pocket), None = no screenshot."""
        import numpy as np
        for _ in range(tries):
            a, b = self.screen(), None
            time.sleep(0.25)
            b = self.screen()
            if a is None or b is None:
                return None
            idle = np.abs(a - b) > 12                      # what blinks by itself
            self.tap(button, 0.55)
            c = self.screen()
            if c is None:
                return None
            changed = (np.abs(c - b) > 12) & ~idle
            if changed.mean() > 0.002:                    # a cursor row / pocket really changed
                return True
        return False

    def select_ball(self, index: int) -> bool:
        """In the battle bag: the POKe BALLS pocket (ITEMS, KEY ITEMS, POKe BALLS - the first pocket, then two to the
        right), then the top of its list, then `index` rows down - every step checked on the screen, so a lost press
        can't make it throw a different ball. False = something didn't add up (nothing is thrown)."""
        time.sleep(0.6)
        for _ in range(4):                                # to the first pocket (LEFT stops moving there)
            moved = self.press_and_check("DLEFT", tries=2)
            if moved is None:
                return False
            if not moved:
                break
        for _ in range(2):                                # two pockets to the right
            if not self.press_and_check("DRIGHT"):
                return False
        for _ in range(16):                               # to the top of the list (UP stops moving there)
            moved = self.press_and_check("DUP", tries=2)
            if moved is None:
                return False
            if not moved:
                break
        for _ in range(index):                            # down to the ball
            if not self.press_and_check("DDOWN"):
                return False
        return True

    def wait_for_user(self) -> bool:
        alert_shiny(self.cfg["on_shiny"].get("beep", True))
        ans = input("\nType 'c' + Enter to keep hunting (static mode RESETS the game!), "
                    "or just Enter to quit: ").strip().lower()
        return ans == "c"

    # ---------------------------------------------------------------- modes
    def game_was_reset(self, timeout=4.0) -> bool:
        """True once the game has really restarted: the battle flag drops and the old wild Pokemon is gone."""
        t0 = time.time()
        while time.time() - t0 < timeout:
            try:
                if not self.in_battle():
                    return True
                w = decode(self.mem.peek(self.game.offsets[0], 100))
                if not (w and w.checksum_ok) or (self.last_pid is not None and w.pid != self.last_pid):
                    return True
            except IOError:
                return True  # memory is being re-initialised = the game is restarting
            time.sleep(0.2)
        return False

    def soft_reset(self) -> bool:
        """Press the reset combo and CHECK that the game restarted (a missed reset would otherwise just
        re-read the same Pokemon over and over). Retries with longer holds; False if it never worked."""
        buttons = self.t.get("reset_buttons", ["A", "B", "X", "Y"])
        hold = max(float(self.t.get("reset_hold", 1.5)), 1.0)
        for attempt in range(4):
            self.ctrl.press(buttons, hold)
            if self.game_was_reset():
                return True
            hold += 0.75
            print(f"  soft reset didn't register - retrying with a {hold:.2f}s hold")
            if attempt == 1:
                # re-attach sys-botbase's virtual controller in case it was dropped
                self.ctrl.connect()
        return False

    def static_attempt(self):
        if not self.soft_reset():
            raise RuntimeError("The soft reset (" + "+".join(self.t.get("reset_buttons", ["A", "B", "X", "Y"])) +
                               ") isn't working - the game didn't restart after 4 tries.")
        time.sleep(float(self.h.get("intro_skip_delay", 2.5)))
        for _ in range(int(self.h.get("intro_skip_presses", 3))):
            self.tap("A", float(self.h.get("intro_skip_interval", 1.2)))
        # No fixed title-screen delay: tested on the Switch, back-to-back resets with identical timing still land on
        # different RNG frames. Random timing is only added after the same-Pokemon guard has seen a repeat.
        if self.extra_jitter > 0:
            delay = random.uniform(0, self.extra_jitter)
            print(f"  title screen: extra random delay {delay:.2f}s (a repeat was seen earlier)")
            time.sleep(delay)
        t0 = time.time()
        warned = False
        a_interval = float(self.t.get("a_interval", 0.6))
        while time.time() - t0 < float(self.t.get("battle_timeout", 40)):
            # The wild slot is empty after a reset, so a valid Pokemon here is the new encounter.
            if self.in_battle():
                p = self.read_wild(tries=1)
                if p and p.pid != self.last_pid:
                    return p
                if p and not warned:
                    # Same Pokemon as last time = still the old data, never a new encounter.
                    print("  (same Pokemon as before - waiting for the new encounter)")
                    warned = time.time()
                elif p and time.time() - warned > 8:
                    return p  # still the old one after 8 s: let the same-Pokemon guard warn and fix it
            # A little randomness in the mashing too: the RNG keeps advancing every frame until the battle.
            self.tap("A", a_interval * random.uniform(0.85, 1.2))
        return None

    # ---------------------------------------------------------------- optional RNG mode (see rng.py)
    def rng_state(self) -> int:
        return int.from_bytes(self.mem.peek(current_seed_offset(self.game.title), 4), "little")

    def trainer_ids(self):
        ptr = int.from_bytes(self.mem.peek(current_seed_offset(self.game.title) + 0xC, 4), "little")  # SaveBlock2
        if not GBA_ANCHOR <= ptr <= 0x0203FFFF:
            raise RuntimeError("Couldn't read your trainer ID (save not loaded).")
        d = self.mem.peek(INITIAL_SEED + (ptr - GBA_ANCHOR) + 10, 4)
        return int.from_bytes(d[0:2], "little"), int.from_bytes(d[2:4], "little")

    def to_overworld(self):
        """After a reset: press A through the intro / title / CONTINUE until the save is loading, then B through the recap."""
        ow = self.game.offsets[2]
        t0 = time.time()
        while self.mem.peek(ow, 4) == b"\0\0\0\0":
            self.tap("A", 0.8)
            if time.time() - t0 > 45:
                raise RuntimeError("RNG mode: the game never got past the title screen.")
        for _ in range(4):
            time.sleep(0.8)
            self.tap("B", 0.15)
        time.sleep(1.0)

    def rng_attempt(self):
        cfg = self.h.get("rng") or {}
        if not self.soft_reset():
            raise RuntimeError("The soft reset isn't working - stopping the RNG mode.")
        time.sleep(2.0)
        self.to_overworld()
        tid, sid = self.trainer_ids()
        if self.rng_timer is None:
            self.rng_timer = rng.RngTimer(self.rng_state, lambda: self.ctrl.tap("A", 0.15),
                                          delay=int(cfg["delay"]) if cfg.get("delay") else None)
        step = self.rng_timer.measure_step()
        base = self.rng_state()
        if self.rng_timer.delay is None:
            # First attempt: measure how many frames after the A press the Pokemon is generated.
            print("  RNG: measuring the delay between pressing A and the encounter (first attempt only)...")
            self.ctrl.tap("A", 0.15)
            sent, target = 0, None
        else:
            found = rng.find_target(base, self.rng_timer.delay + 90 * step, tid, sid,
                                    natures=cfg.get("natures"), min_ivs=cfg.get("min_ivs"),
                                    step=step, delay=self.rng_timer.delay)
            if not found:
                print("  RNG: no matching shiny frame within reach after this reset - resetting.")
                return None
            target, pid, ivs = found
            wait_s = (target - self.rng_timer.delay) / step / 60
            if wait_s > float(cfg.get("max_wait_minutes", 20)) * 60:
                print(f"  RNG: the next matching shiny frame is {wait_s / 60:.0f} min away - resetting for a closer one.")
                return None
            print(f"  RNG: shiny frame in {target} frames (press A in {wait_s:.1f}s): PID {pid:08X} "
                  f"{rng.NATURES[pid % 25]}, IVs {'/'.join(str(ivs[k]) for k in ('HP', 'Atk', 'Def', 'SpA', 'SpD', 'Spe'))}")
            self.status("rng-waiting")
            sent = self.rng_timer.press_at(base, target)
            if sent is None:
                print(f"  RNG: couldn't press at the right moment ({self.rng_timer.last_problem}) - resetting.")
                return None
        t0 = time.time()
        while not self.in_battle():
            if time.time() - t0 > 40:
                return None
            time.sleep(0.05)
        p = self.read_wild()
        if p is None:
            return None
        hit = rng.find_index_of_pid(base, p.pid)
        if hit is not None:
            self.rng_timer.learn(hit - sent)
            if target is None:
                print(f"  RNG: the encounter comes {hit - sent} frames after the press - next resets will aim for a shiny frame.")
            else:
                diff = hit - target
                print(f"  RNG: {'HIT the target frame!' if diff == 0 else f'missed by {diff:+d} RNG advance(s) (WiFi timing) - adjusting'}")
        else:
            print("  RNG: couldn't place this Pokemon in the RNG (not a Method-1 encounter?) - RNG mode may not work here.")
        return p

    def trigger_wild(self, fishing: bool):
        flip, t0, steps = False, time.time(), 0
        axis = self.h.get("walk_axis", "horizontal").lower()
        walk = float(self.h.get("walk_hold", 0.3))
        while not self.in_battle():
            if time.time() - t0 > 600:
                raise SystemExit("No wild encounter for 10 minutes - check where the player is standing.")
            if fishing:
                self.tap(self.h.get("fishing_button", "Y"), 1.0)
                self.tap("B", 0.4)
            else:
                d = ("DLEFT" if flip else "DRIGHT") if axis.startswith("h") else ("DUP" if flip else "DDOWN")
                self.ctrl.press([d], walk)
                flip = not flip
                steps += 1
                # Clear any overworld message (e.g. "REPEL's effect wore off...") that would stop the walking.
                if steps % 8 == 0 and not self.in_battle():
                    self.tap("B", 0.3)

    def resolve_mode(self) -> str:
        """Log where the player is; in auto mode pick static at a legendary spot, wild elsewhere."""
        loc = self.game.location()
        spot = STATIC_SPOTS.get(loc[:2]) if loc else None
        if loc:
            where = f"{spot[0]} (static {spot[1]} spot)" if spot else "not a static-encounter spot"
            print(f"Player location: map {loc[0]}:{loc[1]} x{loc[2]} y{loc[3]} - {where}")
        if self.mode != "auto":
            return self.mode
        if spot:
            return "static"
        if self.in_battle():
            p = self.read_wild(tries=3)
            if p and p.species_name in LEGENDARY_OR_MYTHICAL:
                return "static"
        return "wild"

    def run(self):
        print("Connecting controller (sysbotbase)...")
        self.ctrl.connect()
        if not self.game.locate():
            return
        self.mode = self.resolve_mode()
        ac = self.h.get("auto_catch") or {}
        if (ac.get("mode") or "off") != "off" and int(ac.get("ball_item", 0)):
            ball = int(ac["ball_item"])
            if ball not in [item for item, _ in (self.game.ball_pocket() or [])]:
                self.warn(f"Auto-catch is on, but there's no {BALL_NAMES.get(ball, ball)} in your bag - it won't throw anything.")
        print(f"Mode: {self.mode}.  {self.stats['encounters']} encounters so far. Ctrl+C to stop.\n")
        if self.mode == "rng":
            print("RNG mode (chosen by you): the bot times the A press to land on a shiny frame. "
                  "Turn it off any time in ShinyForge (hunt options) to go back to normal soft resets.\n")
        self.status("hunting")
        try:
            while True:
                if wait_while_paused(lambda: self.status("paused")):
                    self.status("hunting")
                if self.mode == "static":
                    try:
                        p = self.static_attempt()
                    except RuntimeError as e:
                        self.warn(f"{e} Check that the game is running and no other controller/bot is in use, "
                                  "then start the hunt again.")
                        break
                    if p is None:
                        print("  no encounter before the timeout - resetting")
                        continue
                    if self.is_repeat(p):
                        continue
                    if self.judge(p) and not self.after_find(p):
                        break
                    continue

                if self.mode == "rng":
                    try:
                        p = self.rng_attempt()
                    except RuntimeError as e:
                        self.warn(f"{e}")
                        break
                    if p is None:
                        continue
                    if self.is_repeat(p):
                        continue
                    if self.judge(p) and not self.after_find(p):
                        break
                    continue

                # wild / fishing
                if not self.in_battle():
                    self.trigger_wild(fishing=self.mode == "fishing")
                p = self.read_wild()
                # The game writes the new wild Pokemon a moment after the battle flag: give it 3 s to change.
                t_new = time.time()
                while p is not None and p.pid == self.last_pid and time.time() - t_new < 3:
                    time.sleep(0.2)
                    p = self.read_wild(tries=5) or p
                if p is None:
                    print("  battle without wild Pokemon data (trainer battle?) - stopping so you can check.")
                    break
                if self.is_repeat(p):
                    # Old battle data (the last battle didn't end): get out of it and look for a new one.
                    if not self.flee():
                        self.tap("B", 0.5)
                    continue
                if self.judge(p) and not self.after_find(p):
                    break
                if p.species_name in LEGENDARY_OR_MYTHICAL:
                    # Never run from a legendary/mythical Pokemon.
                    if p.species_name in ROAMERS:
                        print(f"  {p.species_name} is a roaming legendary: resetting can't change it and running "
                              "lets it flee - stopping so you can decide.")
                        self.wait_for_user()
                        break
                    print(f"  {p.species_name} is legendary - switching to soft resets in front of it "
                          "(save in front of it first!).")
                    self.mode = "static"
                    continue
                if not self.flee():
                    print("Could not run from the battle - stopping.")
                    break
        except RuntimeError as e:
            self.warn(f"{e} The bot stopped - check the game and the connection, then start the hunt again.")
        except KeyboardInterrupt:
            print("\nStopped by user.")
        finally:
            save_stats(self.stats)
            self.status("stopped")
            self.game.close()
            self.ctrl.close()
