"""Extra features shared by the hunters:

- shinyforge.json (written by the ShinyForge app) overrides config.yaml: mode, stop rules, notifications, auto-catch, RNG
- phone / Discord notifications (ntfy.sh and Discord webhooks)
- pause.flag  (ShinyForge / the phone remote creates it -> the bot pauses between attempts)
- bot_status.json (what the bot is doing right now - read by ShinyForge's overlay / phone remote)
- stop rules: stop on species, natures, IVs - not only on shinies
- a screenshot straight from the Switch (sys-botbase pixelPeek) when something is found
"""
from __future__ import annotations

import json
import socket
import time
import urllib.request
import uuid
from datetime import datetime

from .config import ROOT

OVERRIDES = ROOT / "shinyforge.json"
PAUSE_FLAG = ROOT / "pause.flag"
STATUS = ROOT / "bot_status.json"


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
    try:
        s = socket.create_connection((host, port), timeout=15)
        s.sendall(b"configure echoCommands 0\r\npixelPeek\r\n")
        f = s.makefile("rb")
        line = f.readline().strip()
        s.close()
        return bytes.fromhex(line.decode()) if line else None
    except (OSError, ValueError):
        return None


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
