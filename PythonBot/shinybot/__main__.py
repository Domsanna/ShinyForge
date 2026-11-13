"""Command line entry point:  python -m shinybot <command>

  hunt                     start shiny hunting
  calibrate [--image F]    one-time setup (live from Streamlabs, or from a screenshot)
  cameras                  list camera indexes (find the Streamlabs Virtual Camera)
  snapshot                 save one captured frame to snapshot.png
  test IMAGE [IMAGE ...]   run the shiny detector on screenshots
  press BUTTONS            send a press to the Switch, e.g.  press A   or  press A+B+X+Y
"""
import argparse
import sys
import time

import cv2

from .config import load_calibration, load_config, load_ref, sprite_path


def main(argv=None):
    ap = argparse.ArgumentParser(prog="shinybot", description="Deoxys shiny hunting bot")
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("hunt")
    c = sub.add_parser("calibrate")
    c.add_argument("--image")
    sub.add_parser("cameras")
    sub.add_parser("snapshot")
    t = sub.add_parser("test")
    t.add_argument("images", nargs="+")
    p = sub.add_parser("press")
    p.add_argument("buttons")
    p.add_argument("--hold", type=float, default=0.15)
    p.add_argument("--repeat", type=int, default=1)
    args = ap.parse_args(argv)
    cfg = load_config()
    from .extras import apply_overrides
    cfg = apply_overrides(cfg)  # settings chosen in the ShinyForge app (shinyforge.json)

    if args.cmd == "hunt":
        mode = (cfg.get("hunt", {}) or {}).get("mode", "auto")
        if mode == "vision":
            from .hunt import Hunter
            Hunter(cfg).run()
        else:
            from .memhunt import MemoryHunter
            MemoryHunter(cfg).run()

    elif args.cmd == "calibrate":
        from .calibrate import run_calibration
        run_calibration(cfg, args.image)

    elif args.cmd == "cameras":
        from .capture import list_cameras
        cams = list_cameras()
        if not cams:
            print("No cameras found. In Streamlabs, click 'Start Virtual Camera' first.")
        for i, size in cams:
            print(f"camera_index {i}: {'%dx%d' % tuple(size) if size else 'opened, no frame'}")

    elif args.cmd == "snapshot":
        from .capture import make_source
        src = make_source(cfg["capture"])
        time.sleep(0.5)
        cv2.imwrite("snapshot.png", src.read())
        src.close()
        print("Saved snapshot.png")

    elif args.cmd == "test":
        from .detector import ShinyDetector
        cal = load_calibration()
        if "roi" not in cal:
            sys.exit("Calibrate first (python -m shinybot calibrate --image <battle screenshot>).")
        det = ShinyDetector(sprite_path(cfg), cal, cfg["detection"], load_ref("normal_deoxys.png"))
        for path in args.images:
            frame = cv2.imread(path)
            if frame is None:
                print(f"{path}: cannot read")
                continue
            print(f"{path}: {det.analyze(frame)}")

    elif args.cmd == "press":
        from .controllers import make_controller
        ctrl = make_controller(cfg["controller"])
        ctrl.connect()
        for _ in range(args.repeat):
            ctrl.press(args.buttons.split("+"), hold=args.hold)
            time.sleep(0.3)
        ctrl.close()


if __name__ == "__main__":
    main()
