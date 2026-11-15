"""Builds the Living Dex's Pokémon HOME pictures (ShinyForge/Assets/Home) from the HOME renders.

Steps:
  1. SelfTest <work> homedex <Serebii depositablepokemon.shtml> <"...\\[HOME] Pokémon Renders\\Detailed">
     maps every boxable slot of Serebii's Pokémon HOME list (https://www.serebii.net/pokemonhome/depositablepokemon.shtml)
     to PKHeX's species / form / gender / Alcremie sweet and to its normal + shiny render -> <work>\\home_dex.json
  2. python make_home_sprites.py <work>\\home_dex.json <"...\\Detailed">
     crops each render to the Pokémon, shrinks it to 128 px (full colour) and writes
     Assets/Home/n/<key>.png (normal), Assets/Home/s/<key>.png (shiny) and Assets/Home/home_dex.json (the Living Dex list,
     in HOME's box order).
"""
import json, os, sys
from concurrent.futures import ThreadPoolExecutor
from PIL import Image

SIDE = 128
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "ShinyForge", "Assets", "Home")


def key(e):
    k = f"{e['species']}_{e['form']}" + ("f" if e["female"] else "")
    return k + (f"_{e['sweet']}" if e["species"] == 869 else "")


def shrink(src, dst):
    im = Image.open(src).convert("RGBA")
    box = im.getbbox()
    if box:
        im = im.crop(box)
    k = SIDE / max(im.size)
    if k < 1:
        im = im.resize((max(1, round(im.width * k)), max(1, round(im.height * k))), Image.LANCZOS)
    im.save(dst, "PNG", optimize=True)


def main(mapping, renders):
    entries = json.load(open(mapping, encoding="utf-8"))
    for sub in ("n", "s"):
        os.makedirs(os.path.join(OUT, sub), exist_ok=True)
    jobs, out = [], []
    for e in entries:
        k = key(e)
        jobs.append((os.path.join(renders, "Normal", e["normal"]), os.path.join(OUT, "n", k + ".png")))
        jobs.append((os.path.join(renders, "Shiny", e["shiny"]), os.path.join(OUT, "s", k + ".png")))
        out.append({"species": e["species"], "form": e["form"], "female": e["female"], "sweet": e["sweet"],
                    "name": e["name"], "formName": e["formName"], "file": k + ".png"})
    with ThreadPoolExecutor(8) as pool:
        list(pool.map(lambda j: shrink(*j), jobs))
    with open(os.path.join(OUT, "home_dex.json"), "w", encoding="utf-8") as fh:
        json.dump(out, fh, ensure_ascii=False, separators=(",", ":"))
    print(f"{len(out)} Living Dex entries, {len(jobs)} pictures -> {os.path.normpath(OUT)}")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
