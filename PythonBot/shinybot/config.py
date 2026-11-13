import json
from pathlib import Path

import cv2
import yaml

ROOT = Path(__file__).resolve().parent.parent
CONFIG_PATH = ROOT / "config.yaml"
CAL_PATH = ROOT / "calibration.json"
REF_DIR = ROOT / "references"
STATS_PATH = ROOT / "stats.json"


def load_config(path=CONFIG_PATH) -> dict:
    with open(path, "r", encoding="utf-8") as f:
        return yaml.safe_load(f)


def load_calibration() -> dict:
    if not CAL_PATH.exists():
        return {}
    return json.loads(CAL_PATH.read_text())


def save_calibration(cal: dict):
    # numpy numbers -> plain Python numbers
    CAL_PATH.write_text(json.dumps(cal, indent=2, default=lambda o: o.item() if hasattr(o, "item") else str(o)))


def load_ref(name):
    p = REF_DIR / name
    return cv2.imread(str(p)) if p.exists() else None


def save_ref(name, img):
    REF_DIR.mkdir(exist_ok=True)
    cv2.imwrite(str(REF_DIR / name), img)


def sprite_path(cfg) -> Path:
    p = Path(cfg.get("sprite", "assets/shiny_deoxys.png"))
    return p if p.is_absolute() else ROOT / p
