"""Builds per-game Pokédex sprite sets for ShinyForge from a local copy of PokeAPI/sprites (sprites/pokemon/versions).

usage: python make_version_sprites.py <versions dir, e.g. E:\\sprites\\pokemon\\versions>

Output: ShinyForge/Assets/DexVersions/<set>/<file>.png using the SAME file names as Assets/Dex (see make_dex_assets.py:
"<species>.png", "<species>-<form>.png", "<species>-female.png"), so the app just looks for the same name in the game's set
and falls back to the generic sprite. Sprites are cropped to the Pokémon, large ones (Gen 8/9 renders are 512px) are shrunk
to 128px, and animated GIFs (Ultra Sun/Moon, Let's Go) use their first frame.
"""
import csv, os, sys, urllib.request
from PIL import Image

SRC = sys.argv[1]
# Optional 2nd source (e.g. Desktop\New folder): sprites named by Pokémon NAME ("Dugtrio-alolan.png", "poipole.png") or
# PokeAPI pokemon id ("10116.png"). Only used to fill sprites the main source doesn't have.
EXTRA = sys.argv[2] if len(sys.argv) > 2 else None
EXTRA_SETS = {
    "x-y": ["gen 6/xy/x-y"],
    "omega-ruby-alpha-sapphire": ["gen 6/Omega Ruby and Alpha Sapphire/omega-ruby-alpha-sapphire"],
    "ultra-sun-ultra-moon": ["gen 7/Ultra Sun and Ultra Moon/ultra-sun-ultra-moon", "gen 7/Sun and Moon/ultra-sun-ultra-moon"],
    "lets-go-pikachu-lets-go-eevee": ["gen 7/Lets Go Pikachu and Eevee/lets-go-pikachu-lets-go-eevee",
                                      "gen 7/Lets Go Pikachu and Eevee/lets-go-pikachu-lets-go-eevee/Starters"],
    "sword-shield": ["gen 8/Sword and Shield/sword-shield"],
    "brilliant-diamond-shining-pearl": ["gen 8/Brillant Diamond and Shining Pearl/brilliant-diamond-shining-pearl"],
    "legends-arceus": ["gen 8/Legends Arceus/legends-arceus"],
    "scarlet-violet": ["gen 9/scarlet and violet/scarlet-violet"],
}
HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "ShinyForge", "Assets", "DexVersions")
CSV = "https://raw.githubusercontent.com/PokeAPI/pokeapi/master/data/v2/csv/{}.csv"
CACHE = os.path.join(HERE, "_csv_cache")
os.makedirs(CACHE, exist_ok=True)

# set name -> folder inside versions (first existing wins: transparent copies are preferred for Gen 1/2)
SETS = {
    "red-blue": ["generation-1/red-blue/transparent", "generation-1/red-blue"],
    "red-green-japan": ["generation-1/red-green-japan"],
    "yellow": ["generation-1/yellow/transparent", "generation-1/yellow"],
    "gold": ["generation-2/gold/transparent", "generation-2/gold"],
    "silver": ["generation-2/silver/transparent", "generation-2/silver"],
    "crystal": ["generation-2/crystal/transparent", "generation-2/crystal"],
    "ruby-sapphire": ["generation-3/ruby-sapphire"],
    "emerald": ["generation-3/emerald"],
    "firered-leafgreen": ["generation-3/firered-leafgreen"],
    "diamond-pearl": ["generation-4/diamond-pearl"],
    "platinum": ["generation-4/platinum"],
    "heartgold-soulsilver": ["generation-4/heartgold-soulsilver"],
    "black-white": ["generation-5/black-white"],
    "x-y": ["generation-6/x-y"],
    "omega-ruby-alpha-sapphire": ["generation-6/omega-ruby-alpha-sapphire"],
    "ultra-sun-ultra-moon": ["generation-7/ultra-sun-ultra-moon"],
    "lets-go-pikachu-lets-go-eevee": ["generation-7/lets-go-pikachu-lets-go-eevee"],
    "sword-shield": ["generation-8/sword-shield"],
    "brilliant-diamond-shining-pearl": ["generation-8/brilliant-diamond-shining-pearl"],
    "legends-arceus": ["generation-8/legends-arceus"],
    "scarlet-violet": ["generation-9/scarlet-violet"],
    "legends-za": ["generation-9/legends-za"],
}


def table(name):
    path = os.path.join(CACHE, name + ".csv")
    if not os.path.exists(path):
        with urllib.request.urlopen(CSV.format(name), timeout=60) as r, open(path, "wb") as fh:
            fh.write(r.read())
    with open(path, encoding="utf-8") as fh:
        return list(csv.DictReader(fh))


pokemon = {r["id"]: r for r in table("pokemon")}
forms = table("pokemon_forms")
species = {r["id"]: r for r in table("pokemon_species")}

# local file name -> candidate source names (without extension), same naming as make_dex_assets.py
wanted = {}
names_for = {}
same_look = {}
# Scatterbug/Spewpa patterns and Mothim cloaks don't change the look; Battle Bond Greninja, Power Construct Zygarde and
# Own Tempo Rockruff look like the normal Pokémon.
SAME_LOOK_SPECIES = {414, 664, 665}
SAME_LOOK_FILES = {"658-battle-bond.png", "718-10-power-construct.png", "718-50-power-construct.png", "744-own-tempo.png"}
for f in forms:
    p = pokemon[f["pokemon_id"]]
    sid = int(p["species_id"])
    if sid > 1025:
        continue
    fid = f["form_identifier"] or ""
    local = f"{sid}{('-' + fid) if fid else ''}.png"
    cands = []
    if p["is_default"] != "1" and f["is_default"] == "1":  # variety (regional form etc.) -> its pokemon id
        vid = p["identifier"].split("-", 1)[1] if "-" in p["identifier"] else p["id"]
        local = f"{sid}-{vid}.png"
        cands += [p["id"], f"{sid}-{vid}"]
    elif f["is_default"] == "1":  # the species' default look
        cands += [str(sid), f"{sid}-{fid}" if fid else str(sid)]
    else:  # cosmetic form
        cands += [f"{sid}-{fid}"]
        if p["is_default"] != "1":
            cands.append(p["id"])
    wanted.setdefault(local, cands)
    # names the extra source may use: "<pokemon identifier>", "<species>-<form>", plain species name for the default look
    sp_ident = species[str(sid)]["identifier"]
    names = names_for.setdefault(local, [])
    if f["is_default"] == "1":          # the look of a variety (or the species' default look)
        names += [p["identifier"], p["id"]]
        if fid:
            names.append(f"{sp_ident}-{fid}")
        if p["is_default"] == "1":
            names.append(sp_ident)
    else:                               # cosmetic form: only its own name, never the plain species picture
        names.append(f"{sp_ident}-{fid}")
        if p["is_default"] != "1":
            names.append(p["id"])
    # forms that look exactly like the species' normal picture may use it
    if sid in SAME_LOOK_SPECIES or local in SAME_LOOK_FILES:
        same_look[local] = sid
    if species[str(sid)]["has_gender_differences"] == "1":
        wanted.setdefault(f"{sid}-female.png", [f"female/{sid}"])
        names_for.setdefault(f"{sid}-female.png", [f"female/{sp_ident}", f"female/{sid}"])


import unicodedata

SYN = [("alolan", "alola"), ("galarian", "galar"), ("hisuian", "hisui"), ("paldean", "paldea"), ("defence", "defense"),
       ("zenmode", "zen"), ("whitestripe", "whitestriped"), ("bluestripe", "bluestriped"), ("redstripe", "redstriped")]


def norm(s):
    s = unicodedata.normalize("NFKD", s.lower())
    s = "".join(c for c in s if c.isalnum())
    for a, b in SYN:
        s = s.replace(a, b)
    return s


def key(name):  # "female/xyz" keeps its prefix
    return ("female/" + norm(name[7:])) if name.startswith("female/") else norm(name)


def find(folder, name):
    for ext in (".png", ".gif"):
        path = os.path.join(folder, name.replace("/", os.sep) + ext)
        if os.path.exists(path):
            return path
    return None


def convert(src, dst):
    im = Image.open(src)
    im.seek(0)
    im = im.convert("RGBA")
    box = im.getchannel("A").point(lambda a: 255 if a > 16 else 0).getbbox()
    if box:
        im = im.crop(box)
    if max(im.size) > 128:
        im.thumbnail((128, 128), Image.LANCZOS)
    im.save(dst, optimize=True)


summary = {}
for set_name, folders in SETS.items():
    folder = next((os.path.join(SRC, f) for f in folders if os.path.isdir(os.path.join(SRC, f))), None)
    if folder is None:
        print(f"{set_name}: source folder missing")
        continue
    out = os.path.join(OUT, set_name)
    os.makedirs(out, exist_ok=True)
    n = 0
    for local, cands in wanted.items():
        src = next((s for s in (find(folder, c) for c in cands) if s), None)
        if src is None:
            continue
        dst = os.path.join(out, local)
        if not os.path.exists(dst) or os.path.getmtime(dst) < os.path.getmtime(src):
            convert(src, dst)
        n += 1
    added = []
    if EXTRA and set_name in EXTRA_SETS:
        index = {}  # normalized name -> file, for the main folder and its "female" sub-folder
        for rel in EXTRA_SETS[set_name]:
            base = os.path.join(EXTRA, rel)
            for sub, prefix in (("", ""), ("female", "female/")):
                d = os.path.join(base, sub)
                if not os.path.isdir(d):
                    continue
                for fn in os.listdir(d):
                    stem, ext = os.path.splitext(fn)
                    if ext.lower() in (".png", ".gif"):
                        index.setdefault(prefix + norm(stem), os.path.join(d, fn))
        for local, names in names_for.items():
            dst = os.path.join(out, local)
            if os.path.exists(dst):
                continue
            src = next((index[k] for k in (key(nm) for nm in names) if k in index), None)
            if src:
                convert(src, dst)
                added.append(local)
                n += 1
    import shutil
    for local, sid in same_look.items():
        dst = os.path.join(out, local)
        if os.path.exists(dst):
            continue
        base = {"718-10-power-construct.png": "718-10.png", "718-50-power-construct.png": "718-50.png"}.get(local)
        base = next((os.path.join(out, b) for b in ([base] if base else []) + [f"{sid}.png", f"{sid}-plant.png"]
                     if os.path.exists(os.path.join(out, b))), None)
        if base:
            shutil.copyfile(base, dst)
            added.append(local)
            n += 1
    summary[set_name] = n
    print(f"{set_name}: {n} sprites" + (f" (+{len(added)} from extra: {', '.join(sorted(added)[:40])}{'...' if len(added) > 40 else ''})" if added else ""))

total = sum(os.path.getsize(os.path.join(dp, f)) for dp, _, fs in os.walk(OUT) for f in fs)
print(f"total {sum(summary.values())} sprites, {total / 1e6:.1f} MB")
