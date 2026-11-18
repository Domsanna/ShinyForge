"""The hunting loop: reset -> title (random delay) -> mash A -> check Deoxys -> repeat."""
from __future__ import annotations

import csv
import json
import random
import time
from datetime import datetime

import cv2

from .capture import make_source
from .config import ROOT, STATS_PATH, load_calibration, load_ref, sprite_path
from .controllers import make_controller
from .detector import ShinyDetector, match_in_region
from .extras import EMBEDDED, PREVIEW, ask_keep_hunting, remove_file


class Preview:
    def __init__(self, enabled):
        self.enabled = enabled
        self.next_publish = 0.0

    def publish(self, view, force=False):
        """Inside ShinyForge: the picture goes to a file its Hunter output card shows (4 times a second is plenty)."""
        now = time.time()
        if not force and now < self.next_publish:
            return
        self.next_publish = now + 0.25
        ok, jpg = cv2.imencode(".jpg", view, [cv2.IMWRITE_JPEG_QUALITY, 80])
        if not ok:
            return
        try:
            tmp = PREVIEW.with_suffix(".tmp")
            tmp.write_bytes(jpg.tobytes())
            tmp.replace(PREVIEW)
        except OSError:
            pass  # ShinyForge is reading the last picture: the next one replaces it

    def show(self, img, status=""):
        if not self.enabled:
            return
        h, w = img.shape[:2]
        f = min(1.0, 960 / w)
        view = cv2.resize(img, None, fx=f, fy=f) if f < 1 else img.copy()
        if status:
            cv2.putText(view, status, (10, view.shape[0] - 12), cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 0, 0), 4)
            cv2.putText(view, status, (10, view.shape[0] - 12), cv2.FONT_HERSHEY_SIMPLEX, 0.6, (255, 255, 255), 1)
        if EMBEDDED:
            self.publish(view, force=status.startswith("STOPPED"))
            return
        cv2.imshow("Deoxys shiny bot (Ctrl+C in the console to stop)", view)
        cv2.waitKey(1)


def load_stats():
    if STATS_PATH.exists():
        return json.loads(STATS_PATH.read_text())
    return {"encounters": 0, "unknown": 0, "shinies": []}


def save_stats(stats):
    STATS_PATH.write_text(json.dumps(stats, indent=2))


def save_shot(folder, frame, tag):
    d = ROOT / folder
    d.mkdir(exist_ok=True)
    p = d / f"{tag}_{datetime.now():%Y%m%d_%H%M%S}.png"
    cv2.imwrite(str(p), frame)
    return p


CSV_COLS = ["encounter", "time", "verdict", "species", "level", "hp", "max_hp",
            "atk", "def", "spa", "spd", "spe",
            "iv_hp", "iv_atk", "iv_def", "iv_spa", "iv_spd", "iv_spe", "iv_total",
            "ev_hp", "ev_atk", "ev_def", "ev_spa", "ev_spd", "ev_spe",
            "nature", "ability_slot", "hidden_power", "shiny", "shiny_value", "pid", "note"]


def log_encounter_csv(n, verdict, p, note):
    path = ROOT / "encounters.csv"
    new = not path.exists()
    row = {"encounter": n, "time": f"{datetime.now():%Y-%m-%d %H:%M:%S}", "verdict": verdict, "note": note}
    if p is not None:
        hp_t, hp_p = p.hidden_power
        row.update({"species": p.species_name, "level": p.level, "hp": p.hp, "max_hp": p.max_hp,
                    **{k.lower(): v for k, v in p.stats.items()},
                    **{f"iv_{k.lower()}": v for k, v in p.ivs.items()}, "iv_total": p.iv_total,
                    **{f"ev_{k.lower()}": v for k, v in p.evs.items()},
                    "nature": p.nature, "ability_slot": p.ability_slot,
                    "hidden_power": f"{hp_t} {hp_p}", "shiny": p.is_shiny,
                    "shiny_value": p.shiny_value, "pid": f"{p.pid:08X}"})
    with open(path, "a", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=CSV_COLS)
        if new:
            w.writeheader()
        w.writerow(row)


def alert_shiny(beep=True):
    if not beep:
        return
    try:
        import winsound
        for _ in range(3):
            for freq in (880, 1175, 1568):
                winsound.Beep(freq, 150)
            time.sleep(0.2)
    except Exception:
        print("\a")


class Hunter:
    def __init__(self, cfg):
        self.cfg = cfg
        self.t = cfg["timing"]
        self.d = cfg["detection"]
        self.cal = load_calibration()
        if "roi" not in self.cal:
            raise SystemExit("Not calibrated yet. Run:  python -m shinybot calibrate")
        self.normal_ref = load_ref("normal_deoxys.png")
        self.battle_ref = load_ref("battle.png")
        self.title_ref = load_ref("title.png")
        self.det = ShinyDetector(sprite_path(cfg), self.cal, self.d, self.normal_ref)
        self.src = make_source(cfg["capture"])
        self.ctrl = make_controller(cfg["controller"])
        self.preview = Preview(cfg.get("show_preview", True))
        self.stats = load_stats()
        # Reading the Pokemon's real stats from RAM needs sys-botbase.
        mem_cfg = cfg.get("memory", {}) or {}
        self.memory = None
        if mem_cfg.get("enabled", True) and cfg["controller"].get("type") == "sysbotbase":
            from .memory import GameMemory
            self.memory = GameMemory(cfg["controller"], mem_cfg)

    def read_stats(self, n, verdict):
        """Print + log the wild Pokemon's stats. Returns the decoded Pokemon or None."""
        if self.memory is None:
            log_encounter_csv(n, verdict, None, "stats disabled")
            return None
        try:
            p, note = self.memory.read_enemy()
        except Exception as e:
            p, note = None, f"stats error: {e}"
        if p is not None:
            print("    " + p.summary().replace("\n", "\n    ") + (f"\n    NOTE: {note}" if note else ""))
        else:
            print(f"    stats unavailable: {note}")
        log_encounter_csv(n, verdict, p, note)
        return p

    # ------------------------------------------------------------- steps
    def soft_reset(self):
        buttons = self.t.get("reset_buttons", ["A", "B", "X", "Y"])
        print(f"  soft reset ({'+'.join(buttons)})")
        self.ctrl.press(buttons, hold=max(float(self.t.get("reset_hold", 1.5)), 1.0))

    def wait_for_title(self):
        if self.title_ref is None:
            wait = float(self.t.get("reset_to_title", 10))
            print(f"  waiting {wait:.1f}s for the title screen (no title reference calibrated)")
            end = time.time() + wait
            while time.time() < end:
                self.preview.show(self.src.read(), "waiting for title screen")
                time.sleep(0.05)
            return True
        t0 = time.time()
        hits = 0
        thr = self.d.get("title_threshold", 0.8)
        # Skip the intro movie: tap A until the title logo shows, then stop
        # immediately (one more A would leave the title screen and skip the
        # random delay). A press during the intro/logo animation only jumps ahead.
        skip = bool(self.t.get("skip_intro", True))
        next_a = t0 + float(self.t.get("intro_skip_delay", 2.0))
        interval = float(self.t.get("intro_skip_interval", 1.0))
        hold = float(self.t.get("press_hold", 0.15))
        while time.time() - t0 < float(self.t.get("title_timeout", 45)):
            frame = self.src.read()
            s = match_in_region(frame, self.title_ref, self.cal.get("title_box"))
            hits = hits + 1 if s >= thr else 0
            self.preview.show(frame, f"{'skipping intro' if skip else 'waiting for title'}  match={s:.2f}")
            if hits >= 2:
                print(f"  title screen detected after {time.time() - t0:.1f}s")
                return True
            if skip and hits == 0 and s < thr * 0.6 and time.time() >= next_a:
                self.ctrl.tap("A", hold)
                next_a = time.time() + interval
            time.sleep(0.05)
        print("  title screen not detected (timeout) - continuing anyway")
        return False

    def title_delay(self):
        lo, hi = float(self.t.get("title_delay_min", 1)), float(self.t.get("title_delay_max", 5))
        delay = random.uniform(lo, hi)
        print(f"  random title-screen delay: {delay:.2f}s")
        end = time.time() + delay
        while time.time() < end:
            self.preview.show(self.src.read(), f"title delay {end - time.time():.1f}s")
            time.sleep(0.03)

    def mash_until_decided(self):
        """Press A through the menus/text until Deoxys can be judged.

        Returns ('shiny' | 'normal' | 'unknown', frame, result).
        """
        hold = float(self.t.get("press_hold", 0.08))
        interval = float(self.t.get("a_interval", 0.45))
        need = int(self.d.get("confirm_frames", 3))
        bthr = self.d.get("battle_threshold", 0.8)
        t0 = time.time()
        next_a = 0.0
        mashing = True
        shiny_hits = normal_hits = 0
        frame = r = None
        while time.time() - t0 < float(self.t.get("battle_timeout", 40)):
            frame = self.src.read()
            r = self.det.analyze(frame)

            if self.battle_ref is not None:
                bscore = match_in_region(frame, self.battle_ref, self.cal.get("battle_box"))
                in_battle = bscore >= bthr
            else:
                bscore = -1
                in_battle = time.time() - t0 >= float(self.t.get("min_check_delay", 4))

            # Deoxys visible -> stop pressing A right away so we never pick a move.
            if in_battle and (self.battle_ref is not None or r.present or r.is_shiny):
                mashing = False

            if in_battle:
                shiny_hits = shiny_hits + 1 if r.is_shiny else 0
                normal_hits = normal_hits + 1 if r.is_normal else 0
                if shiny_hits >= need:
                    return "shiny", frame, r
                if normal_hits >= need:
                    return "normal", frame, r

            self.preview.show(self.det.annotate(frame, r),
                              f"{'mashing A' if mashing else 'judging'}  battle={bscore:.2f}")

            if mashing and time.time() >= next_a:
                self.ctrl.tap("A", hold)
                next_a = time.time() + interval
            else:
                time.sleep(0.03)
        return "unknown", frame, r

    # ------------------------------------------------------------- main loop
    def run(self):
        print(f"Connecting controller ({self.cfg['controller']['type']})...")
        self.ctrl.connect()
        print(f"Hunting! {self.stats['encounters']} encounters so far. Ctrl+C to stop.\n")
        try:
            while True:
                n = self.stats["encounters"] + 1
                print(f"[attempt {n}] {datetime.now():%H:%M:%S}")
                self.soft_reset()
                self.wait_for_title()
                self.title_delay()
                verdict, frame, r = self.mash_until_decided()
                print(f"  -> {verdict.upper()}  ({r})")
                mon = self.read_stats(n, verdict) if verdict != "unknown" else None
                # The game's own data is the final word: if RAM says shiny, stop
                # even when the picture check said normal.
                if mon is not None and mon.is_shiny and verdict != "shiny":
                    print("  !! The game's memory says this Pokemon IS SHINY - overriding the screen check.")
                    verdict = "shiny"

                if verdict == "normal":
                    self.stats["encounters"] = n
                    save_stats(self.stats)
                    continue

                if verdict == "unknown":
                    self.stats["unknown"] += 1
                    save_stats(self.stats)
                    p = save_shot("unknown", frame, "unknown")
                    print(f"  could not tell what happened - screenshot saved to {p}")
                    if not self.cfg["on_shiny"].get("stop_on_unknown", False):
                        continue

                # Shiny (or stop_on_unknown): stop everything and wait for the user.
                if verdict == "shiny":
                    self.stats["encounters"] = n
                    p = save_shot("shinies", self.det.annotate(frame, r), "SHINY_DEOXYS")
                    self.stats["shinies"].append({"encounter": n, "time": f"{datetime.now():%Y-%m-%d %H:%M:%S}",
                                                  "screenshot": str(p)})
                    save_stats(self.stats)
                    print("\n" + "*" * 60)
                    print(f"  SHINY DEOXYS FOUND after {n} encounters!  Screenshot: {p}")
                    print("  The bot has stopped pressing buttons. Take over with your controller.")
                    print("*" * 60)
                self.preview.show(self.det.annotate(frame, r), "STOPPED - waiting for you")
                alert_shiny(self.cfg["on_shiny"].get("beep", True))
                if not ask_keep_hunting("\nType 'c' + Enter to keep hunting (this RESETS the game!), "
                                        "or just Enter to quit: "):
                    break
        except KeyboardInterrupt:
            print("\nStopped by user.")
        finally:
            save_stats(self.stats)
            if self.memory:
                self.memory.close()
            self.ctrl.close()
            self.src.close()
            if EMBEDDED:
                remove_file(PREVIEW)
            else:
                cv2.destroyAllWindows()
