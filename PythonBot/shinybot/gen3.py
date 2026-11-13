"""Decode a Generation 3 (Ruby/Sapphire/Emerald/FireRed/LeafGreen) Pokemon record.

A party/battle Pokemon is 100 bytes:
  0  PID            4  OT ID (TID | SID << 16)   8 nickname[10]   20 OT name[7]
  28 checksum       32 48 bytes of encrypted data (4 x 12-byte substructures)
  80 status         84 level   86 current HP   88 max HP
  90 Atk  92 Def  94 Spe  96 SpA  98 SpD
The 48 data bytes are XORed with (PID ^ OTID) and shuffled by PID % 24.
"""
from __future__ import annotations

import struct
from dataclasses import dataclass

NATURES = ["Hardy", "Lonely", "Brave", "Adamant", "Naughty", "Bold", "Docile", "Relaxed",
           "Impish", "Lax", "Timid", "Hasty", "Serious", "Jolly", "Naive", "Modest", "Mild",
           "Quiet", "Bashful", "Rash", "Calm", "Gentle", "Sassy", "Careful", "Quirky"]
# +10% / -10% stat for each nature (index into Atk, Def, Spe, SpA, SpD), None = neutral
_NAT_STATS = ["Atk", "Def", "Spe", "SpA", "SpD"]

ORDERS = ["GAEM", "GAME", "GEAM", "GEMA", "GMAE", "GMEA", "AGEM", "AGME", "AEGM", "AEMG",
          "AMGE", "AMEG", "EGAM", "EGMA", "EAGM", "EAMG", "EMGA", "EMAG", "MGAE", "MGEA",
          "MAGE", "MAEG", "MEGA", "MEAG"]

HIDDEN_POWER_TYPES = ["Fighting", "Flying", "Poison", "Ground", "Rock", "Bug", "Ghost", "Steel",
                      "Fire", "Water", "Grass", "Electric", "Psychic", "Ice", "Dragon", "Dark"]

STAT_ORDER = ["HP", "Atk", "Def", "SpA", "SpD", "Spe"]  # display order


@dataclass
class Pokemon:
    pid: int
    otid: int
    species: int
    level: int
    hp: int
    max_hp: int
    stats: dict            # Atk, Def, Spe, SpA, SpD (actual values)
    ivs: dict              # HP, Atk, Def, Spe, SpA, SpD
    evs: dict
    nature: str
    ability_slot: int
    checksum_ok: bool

    @property
    def tid(self):
        return self.otid & 0xFFFF

    @property
    def sid(self):
        return self.otid >> 16

    @property
    def shiny_value(self):
        return (self.tid ^ self.sid ^ (self.pid >> 16) ^ (self.pid & 0xFFFF))

    @property
    def is_shiny(self):
        return self.shiny_value < 8

    @property
    def species_name(self):
        from .species import name
        return name(self.species)

    @property
    def iv_total(self):
        return sum(self.ivs.values())

    @property
    def nature_effect(self):
        i = NATURES.index(self.nature)
        up, down = _NAT_STATS[i // 5], _NAT_STATS[i % 5]
        return "neutral" if up == down else f"+{up} -{down}"

    @property
    def hidden_power(self):
        iv = [self.ivs[k] for k in ("HP", "Atk", "Def", "Spe", "SpA", "SpD")]
        t = sum(((v & 1) << i) for i, v in enumerate(iv)) * 15 // 63
        p = sum((((v >> 1) & 1) << i) for i, v in enumerate(iv)) * 40 // 63 + 30
        return HIDDEN_POWER_TYPES[t], p

    def summary(self) -> str:
        iv = " ".join(f"{k} {self.ivs[k]:>2}" for k in STAT_ORDER)
        ev = " ".join(f"{k} {self.evs[k]}" for k in STAT_ORDER)
        st = " ".join(f"{k} {self.stats[k]}" for k in STAT_ORDER[1:])
        hp_type, hp_pow = self.hidden_power
        return (f"{self.species_name} Lv.{self.level}  HP {self.hp}/{self.max_hp}  "
                f"{self.nature} ({self.nature_effect})  "
                f"{'SHINY' if self.is_shiny else 'not shiny'} (SV {self.shiny_value})\n"
                f"      Stats: {st}\n"
                f"      IVs:   {iv}   (total {self.iv_total}/186)\n"
                f"      EVs:   {ev}\n"
                f"      Ability slot {self.ability_slot}  HiddenPower {hp_type} {hp_pow}  "
                f"PID {self.pid:08X}")


def decode(raw: bytes) -> Pokemon | None:
    """Decode a 100-byte party/battle record. Returns None if it's empty/garbage."""
    if len(raw) < 100:
        return None
    pid, otid = struct.unpack_from("<II", raw, 0)
    if pid == 0 and otid == 0:
        return None
    key = pid ^ otid
    data = bytearray(raw[32:80])
    for i in range(0, 48, 4):
        v = struct.unpack_from("<I", data, i)[0] ^ key
        struct.pack_into("<I", data, i, v)
    checksum = sum(struct.unpack_from("<24H", data)) & 0xFFFF
    stored = struct.unpack_from("<H", raw, 28)[0]

    order = ORDERS[pid % 24]
    sub = {c: bytes(data[i * 12:(i + 1) * 12]) for i, c in enumerate(order)}
    species = struct.unpack_from("<H", sub["G"], 0)[0]
    e = sub["E"]
    evs = {"HP": e[0], "Atk": e[1], "Def": e[2], "Spe": e[3], "SpA": e[4], "SpD": e[5]}
    ivw = struct.unpack_from("<I", sub["M"], 4)[0]
    ivs = {"HP": ivw & 31, "Atk": (ivw >> 5) & 31, "Def": (ivw >> 10) & 31,
           "Spe": (ivw >> 15) & 31, "SpA": (ivw >> 20) & 31, "SpD": (ivw >> 25) & 31}

    level = raw[84]
    hp, max_hp, atk, df, spe, spa, spd = struct.unpack_from("<7H", raw, 86)
    return Pokemon(
        pid=pid, otid=otid, species=species,
        level=level, hp=hp, max_hp=max_hp,
        stats={"Atk": atk, "Def": df, "Spe": spe, "SpA": spa, "SpD": spd},
        ivs=ivs, evs=evs, nature=NATURES[pid % 25], ability_slot=(ivw >> 31) & 1,
        checksum_ok=(checksum == stored))
