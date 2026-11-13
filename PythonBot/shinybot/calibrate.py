"""One-time setup: tell the bot where Deoxys, the battle HP box and the title screen are."""
from __future__ import annotations

import cv2

from .capture import make_source
from .config import load_calibration, save_calibration, save_ref, sprite_path
from .detector import ShinyDetector

MAX_W, MAX_H = 1400, 850


def _fit(img):
    h, w = img.shape[:2]
    f = min(1.0, MAX_W / w, MAX_H / h)
    return (cv2.resize(img, None, fx=f, fy=f) if f < 1 else img), f


def select_box(frame, prompt):
    """Let the user drag a rectangle. Returns (x, y, w, h) in full-frame pixels or None."""
    print(f"\n>>> {prompt}\n    Drag a box with the mouse, then press ENTER (or C to skip).")
    view, f = _fit(frame)
    win = "Calibrate - drag a box, ENTER to accept, C to skip"
    x, y, w, h = cv2.selectROI(win, view, showCrosshair=True, fromCenter=False)
    cv2.destroyWindow(win)
    if w == 0 or h == 0:
        return None
    return [int(x / f), int(y / f), int(w / f), int(h / f)]


def crop(frame, box):
    x, y, w, h = box
    return frame[y:y + h, x:x + w].copy()


def wait_for_key(src, message, keys):
    """Show live video until one of `keys` is pressed. Returns (key, frame)."""
    print(f"\n>>> {message}")
    win = "Calibrate - live view"
    while True:
        frame = src.read()
        view, _ = _fit(frame)
        cv2.putText(view, message[:90], (10, 25), cv2.FONT_HERSHEY_SIMPLEX, 0.55, (0, 0, 0), 4)
        cv2.putText(view, message[:90], (10, 25), cv2.FONT_HERSHEY_SIMPLEX, 0.55, (0, 255, 255), 1)
        cv2.imshow(win, view)
        k = cv2.waitKey(30) & 0xFF
        for key in keys:
            if k == ord(key) or (key == " " and k == 32):
                cv2.destroyWindow(win)
                return key, frame


def calibrate_battle(frame, cfg, cal):
    roi = select_box(frame, "STEP 1/3: box the AREA where Deoxys stands in battle "
                            "(make it a bit bigger than Deoxys, but don't include the HP boxes)")
    if roi is None:
        raise SystemExit("Calibration cancelled.")
    cal["roi"] = roi

    tight = select_box(frame, "STEP 2/3: draw a TIGHT box around Deoxys itself")
    if tight is None:
        raise SystemExit("Calibration cancelled.")
    save_ref("normal_deoxys.png", crop(frame, tight))
    cal["normal_box"] = tight

    battle = select_box(frame, "STEP 3/3: box the enemy's name/HP box (says DEOXYS Lv...). "
                               "Press C to skip (not recommended)")
    if battle:
        save_ref("battle.png", crop(frame, battle))
        cal["battle_box"] = battle

    # Sprite scale: compare the tight box with the PNG's visible size.
    det = ShinyDetector(sprite_path(cfg), {"roi": roi, "sprite_scale": 1}, cfg["detection"])
    th, tw = det.tmpl.shape[:2]
    scale = (tight[2] / tw + tight[3] / th) / 2
    cal["sprite_scale"] = round(scale, 3)

    det = ShinyDetector(sprite_path(cfg), cal, cfg["detection"], crop(frame, tight))
    r = det.analyze(frame)
    cal["normal_yellow_frac"] = round(r.yellow_frac, 4)
    cal["normal_shiny_score"] = round(r.shiny_score, 4)
    print(f"\nSprite scale ~{scale:.2f}x. On this NORMAL Deoxys: {r}")
    if r.is_shiny:
        print("WARNING: the normal Deoxys already looks shiny to the detector. "
              "Raise detection.shiny_threshold in config.yaml above "
              f"{r.shiny_score:.3f} and/or yellow_min_frac above {r.yellow_frac:.3f}.")
    return cal


def calibrate_title(frame, cal):
    box = select_box(frame, "Box the big POKEMON logo on the TITLE SCREEN "
                            "(not 'PRESS START' - it blinks)")
    if box:
        save_ref("title.png", crop(frame, box))
        cal["title_box"] = box
        print("Title screen reference saved.")


def run_calibration(cfg, image_path=None):
    cal = load_calibration()
    if image_path:  # calibrate from a saved screenshot
        frame = cv2.imread(image_path)
        if frame is None:
            raise SystemExit(f"Could not read {image_path}")
        cal = calibrate_battle(frame, cfg, cal)
        save_calibration(cal)
        print("\nSaved calibration.json")
        return

    src = make_source(cfg["capture"])
    try:
        print("Get the game to the battle with a NORMAL Deoxys on screen "
              "(after the HP box has appeared), using your real controller.")
        _, frame = wait_for_key(src, "Deoxys battle on screen? Press SPACE to freeze the frame (Q quits)",
                                [" ", "q"])
        if _ == "q":
            return
        cal = calibrate_battle(frame, cfg, cal)
        save_calibration(cal)
        print("Saved battle calibration.")

        key, frame = wait_for_key(
            src, "Optional: soft reset, and when the TITLE SCREEN shows press T (or Q to finish)",
            ["t", "q"])
        if key == "t":
            calibrate_title(frame, cal)
            save_calibration(cal)
        print("\nCalibration complete -> calibration.json")
    finally:
        src.close()
        cv2.destroyAllWindows()
