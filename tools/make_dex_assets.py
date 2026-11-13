"""Builds ShinyForge's Pokédex sprite set from PokeAPI's open data (github.com/PokeAPI):
- data/v2/csv: pokemon.csv, pokemon_forms.csv, pokemon_species.csv (gender differences)
- sprites/pokemon/*.png (96x96 game sprites) for every species, variety (regional forms...), cosmetic form, and female variant.
Output: ShinyForge/Assets/Dex/<file>.png + ShinyForge/Assets/Dex/dex_index.json
"""
import csv, io, json, os, urllib.request
from concurrent.futures import ThreadPoolExecutor

ROOT = os.path.join(os.path.dirname(__file__), "..", "ShinyForge", "Assets", "Dex")
os.makedirs(ROOT, exist_ok=True)
CSV = "https://raw.githubusercontent.com/PokeAPI/pokeapi/master/data/v2/csv/{}.csv"
SPR = "https://raw.githubusercontent.com/PokeAPI/sprites/master/sprites/pokemon/{}"


def get(url, binary=False):
    with urllib.request.urlopen(url, timeout=60) as r:
        data = r.read()
    return data if binary else data.decode("utf-8")


def table(name):
    return list(csv.DictReader(io.StringIO(get(CSV.format(name)))))


pokemon = {r["id"]: r for r in table("pokemon")}            # id, identifier, species_id, is_default
forms = table("pokemon_forms")                                # id, identifier, form_identifier, pokemon_id, is_default, is_battle_only, form_order, order
species = {r["id"]: r for r in table("pokemon_species")}    # id, identifier, has_gender_differences, evolves_from_species_id
print(len(pokemon), "pokemon,", len(forms), "forms,", len(species), "species")

index = {}  # species -> {"name": identifier, "female": bool, "forms": [{"id": form identifier, "file": png, "battle": bool, "default": bool}]}
jobs = []
for f in sorted(forms, key=lambda r: (int(pokemon[r["pokemon_id"]]["species_id"]), int(r["order"] or 0))):
    p = pokemon[f["pokemon_id"]]
    sid = int(p["species_id"])
    if sid > 1025:
        continue
    sp = species[str(sid)]
    entry = index.setdefault(sid, {"name": sp["identifier"], "female": sp["has_gender_differences"] == "1", "forms": []})
    fid = f["form_identifier"] or ""
    # Sprite file naming in PokeAPI/sprites: varieties use the pokemon id; cosmetic forms use "<species>-<form>".
    if f["is_default"] == "1":
        remote = f"{p['id']}.png"
    else:
        remote = f"{sid}-{fid}.png"
    local = f"{sid}{('-' + fid) if fid else ''}{'' if f['is_default'] == '1' or not fid else ''}.png"
    if p["is_default"] != "1" and f["is_default"] == "1":
        local = f"{sid}-{p['identifier'].split('-', 1)[1] if '-' in p['identifier'] else p['id']}.png"
        fid = p["identifier"].split("-", 1)[1] if "-" in p["identifier"] else fid
    entry["forms"].append({"id": fid, "file": local, "battle": f["is_battle_only"] == "1",
                           "default": p["is_default"] == "1" and f["is_default"] == "1"})
    jobs.append((SPR.format(remote), os.path.join(ROOT, local)))
for sid, e in index.items():
    if e["female"]:
        jobs.append((SPR.format(f"female/{sid}.png"), os.path.join(ROOT, f"{sid}-female.png")))

seen = set()
jobs = [j for j in jobs if not (j[1] in seen or seen.add(j[1]))]


def fetch(job):
    url, out = job
    if os.path.exists(out):
        return True
    try:
        data = get(url, binary=True)
        with open(out, "wb") as fh:
            fh.write(data)
        return True
    except Exception:
        return False


with ThreadPoolExecutor(16) as ex:
    results = list(ex.map(fetch, jobs))
missing = {os.path.basename(j[1]) for j, ok in zip(jobs, results) if not ok}
print(f"sprites: {sum(results)} downloaded/present, {len(missing)} not available")

# Drop entries whose sprite doesn't exist (fall back to the species' default sprite at runtime).
for e in index.values():
    for f in e["forms"]:
        f["has"] = f["file"] not in missing
    if e["female"] and f"{list(index.keys())[0]}" == "":
        pass
for sid, e in index.items():
    e["femaleFile"] = f"{sid}-female.png" if e["female"] and f"{sid}-female.png" not in missing else None

with open(os.path.join(ROOT, "dex_index.json"), "w", encoding="utf-8") as fh:
    json.dump({str(k): v for k, v in sorted(index.items())}, fh, separators=(",", ":"))
total = sum(os.path.getsize(os.path.join(ROOT, n)) for n in os.listdir(ROOT))
print(f"index: {len(index)} species; folder size {total / 1e6:.1f} MB")
