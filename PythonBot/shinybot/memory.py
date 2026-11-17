"""Read the wild Pokemon's real data (IVs, EVs, nature, stats...) from the Switch's RAM.

Pokemon FireRed/LeafGreen on Switch keeps the GBA game's RAM at fixed heap offsets.
Offsets come from Lusamine's SysBot.NET "moarencounterbots" FRLG research (also used
by LegoFigure11's GRASS): https://github.com/Lusamine/SysBot.NET/tree/moarencounterbots
Verified on English LeafGreen (010034D02340E000) with sys-botbase 2.5.

Uses its own sys-botbase connection (sys-botbase serves several clients at once).
"""
from __future__ import annotations

import socket

from .gen3 import decode

SLOT = 100  # party-format Pokemon record

# title ID -> (wild Pokemon, first party slot, overworld flag), all heap-relative
OFFSETS = {
    # Japanese
    "01006FA0233F8000": (0x120BF88, 0x120C1E0, 0x1222B54),  # FireRed
    "0100F1E0233FA000": (0x120BF88, 0x120C1E0, 0x1222B54),  # LeafGreen
    # English
    "0100554023408000": (0x120C028, 0x120C280, 0x1222BDC),
    "010034D02340E000": (0x120C028, 0x120C280, 0x1222BDC),
    # French
    "01004B3023412000": (0x120C028, 0x120C280, 0x1222BDC),
    "010087C02342E000": (0x120C028, 0x120C280, 0x1222BDC),
    # Italian
    "010092302342A000": (0x120C028, 0x120C280, 0x1222BDC),
    "01005C7023432000": (0x120C028, 0x120C280, 0x1222BDC),
    # German
    "01007F8023416000": (0x120C028, 0x120C280, 0x1222BDC),
    "0100FD6023430000": (0x120C028, 0x120C280, 0x1222BDC),
    # Spanish
    "0100EB702342C000": (0x120C028, 0x120C280, 0x1222BDC),
    "01002B5023434000": (0x120C028, 0x120C280, 0x1222BDC),
}
IN_BATTLE = 0x6D9C91    # 02 in battle, 01 outside (not cleared by a soft reset)
BATTLE_MENU = 0x11E805C  # 02 on the FIGHT/BAG/POKeMON/RUN menu, 00 otherwise

# Player location: gSaveBlock1Ptr (a GBA address) sits 8 bytes after the current-seed value.
# GBA 0x02020000 == heap 0x1208000. SaveBlock1: s16 x, s16 y, s8 mapGroup, s8 mapNum.
INITIAL_SEED = 0x1208000
GBA_ANCHOR = 0x02020000


def current_seed_offset(title: str) -> int:
    if title in ("01006FA0233F8000", "0100F1E0233FA000"):
        return 0xBD68D230  # Japanese
    if title in ("0100554023408000", "010034D02340E000"):
        return 0xBD68D2D0  # English
    return 0xBD68D220      # French / Italian / German / Spanish


GAME_NAMES = {
    "01006FA0233F8000": "FireRed (JPN)", "0100F1E0233FA000": "LeafGreen (JPN)",
    "0100554023408000": "FireRed (ENG)", "010034D02340E000": "LeafGreen (ENG)",
    "01004B3023412000": "FireRed (FRA)", "010087C02342E000": "LeafGreen (FRA)",
    "010092302342A000": "FireRed (ITA)", "01005C7023432000": "LeafGreen (ITA)",
    "01007F8023416000": "FireRed (GER)", "0100FD6023430000": "LeafGreen (GER)",
    "0100EB702342C000": "FireRed (SPA)", "01002B5023434000": "LeafGreen (SPA)",
}

# Static encounters you soft reset for, by (mapGroup, mapNum) from pret/pokefirered map_groups.json.
STATIC_SPOTS = {
    (2, 56): ("Birth Island", "Deoxys"),
    (2, 37): ("Navel Rock Summit", "Ho-Oh"),
    (2, 38): ("Navel Rock Base", "Lugia"),
    (1, 87): ("Seafoam Islands B4F", "Articuno"),
    (1, 95): ("Power Plant", "Zapdos"),
    (1, 101): ("Mt. Ember Summit", "Moltres"),
    (1, 74): ("Cerulean Cave B1F", "Mewtwo"),
    (3, 30): ("Route 12", "Snorlax"),
    (3, 34): ("Route 16", "Snorlax"),
}

LEGENDARY_OR_MYTHICAL = {
    "Articuno", "Zapdos", "Moltres", "Mewtwo", "Mew", "Raikou", "Entei", "Suicune", "Lugia", "Ho-Oh",
    "Celebi", "Regirock", "Regice", "Registeel", "Latias", "Latios", "Kyogre", "Groudon", "Rayquaza",
    "Jirachi", "Deoxys",
}
ROAMERS = {"Raikou", "Entei", "Suicune"}


class SysBotMemory:
    """Minimal sys-botbase reader that stays in sync after failed reads."""

    def __init__(self, host, port=6000):
        self.host, self.port = host, port
        self.sock = self.f = None

    def _open(self):
        self.sock = socket.create_connection((self.host, self.port), timeout=15)
        self.f = self.sock.makefile("rb")
        self.sock.sendall(b"configure echoCommands 0\r\n")  # (no reply)

    def cmd(self, text: str) -> str:
        """One request, one hex answer. A failed request raises IOError and leaves the connection in sync:
        sys-botbase 2.x answers several error lines ("pmdmnt...", "svc...") plus a blank line - the connection is
        reopened instead of guessing how many lines are left; sys-botbase-cpp 3.x answers "0"."""
        line = None
        for attempt in range(2):
            try:
                if self.sock is None:
                    self._open()
                self.sock.sendall((text + "\r\n").encode())
                line = self.f.readline().decode(errors="replace").strip()
                break
            except OSError:  # connection lost: reconnect once
                self.close()
                if attempt:
                    raise
        if line and line != "0" and len(line) % 2 == 0 and all(c in "0123456789abcdefABCDEF" for c in line):
            return line
        if line != "0":
            self.close()  # 2.x error lines (and a blank line) may still be on the way
        raise IOError(f"sys-botbase: {text.split()[0]} failed ({line or 'no answer'})")

    def peek(self, heap_off: int, size: int) -> bytes:
        data = bytes.fromhex(self.cmd(f"peek 0x{heap_off:X} {size}"))
        if len(data) != size:
            raise IOError(f"short read ({len(data)}/{size})")
        return data

    def close(self):
        try:
            if self.sock:
                self.sock.close()
        except OSError:
            pass
        self.sock = self.f = None


# Gen 3 Poke Ball item IDs (the POKe BALLS pocket holds these)
BALL_NAMES = {1: "Master Ball", 2: "Ultra Ball", 3: "Great Ball", 4: "Poke Ball", 5: "Safari Ball", 6: "Net Ball",
              7: "Dive Ball", 8: "Nest Ball", 9: "Repeat Ball", 10: "Timer Ball", 11: "Luxury Ball", 12: "Premier Ball"}
BALL_POCKET = 0x430      # SaveBlock1: 13 slots of (u16 item, u16 quantity ^ key) - the order the bag shows them in
ITEM_KEY = 0xF20         # SaveBlock2: the key quantities are XORed with (low 16 bits)


class GameMemory:
    def __init__(self, ctrl_cfg: dict, mem_cfg: dict | None = None):
        self.mem = SysBotMemory(ctrl_cfg["host"], int(ctrl_cfg.get("port", 6000)))
        self.offsets = None
        self.last_pid = None

    def locate(self) -> bool:
        if self.offsets:
            return True
        try:
            title = self.mem.cmd("getTitleID").upper()
        except OSError:
            print("  [stats] no game is running on the Switch - stats disabled")
            return False
        if title not in OFFSETS:
            print(f"  [stats] unknown game title ID {title} - stats disabled")
            return False
        self.offsets = OFFSETS[title]
        self.title = title
        party = decode(self.mem.peek(self.offsets[1], SLOT))
        if party and party.checksum_ok:
            print(f"  [stats] game: {GAME_NAMES[title]} - lead Pokemon {party.species_name} Lv{party.level}")
        else:
            print(f"  [stats] title {title}: party not readable yet (game not loaded?) - will retry")
        return True

    def in_battle(self) -> bool:
        return self.mem.peek(IN_BATTLE, 1)[0] == 2

    def party_loaded(self) -> bool:
        """True once a save is really loaded (a valid Pokemon in the first party slot). On the title screen and in a
        NEW GAME's intro there is none - the location then reads map 0:0 and must not be used to pick a mode."""
        if not self.locate():
            return False
        p = decode(self.mem.peek(self.offsets[1], SLOT))
        return bool(p and p.checksum_ok)

    def location(self):
        """Return (mapGroup, mapNum, x, y) of the player, or None if not readable."""
        if not self.locate():
            return None
        ptr = int.from_bytes(self.mem.peek(current_seed_offset(self.title) + 8, 4), "little")
        if not GBA_ANCHOR <= ptr <= 0x0203FFFF:
            return None
        d = self.mem.peek(INITIAL_SEED + (ptr - GBA_ANCHOR), 6)
        to_s8 = lambda b: b - 256 if b > 127 else b
        return (to_s8(d[4]), to_s8(d[5]), int.from_bytes(d[0:2], "little", signed=True),
                int.from_bytes(d[2:4], "little", signed=True))

    def ball_pocket(self):
        """The POKe BALLS pocket as [(item id, count), ...] in the order the bag lists it, or None if unreadable."""
        if not self.locate():
            return None
        ptrs = self.mem.peek(current_seed_offset(self.title) + 8, 8)
        sb1, sb2 = int.from_bytes(ptrs[0:4], "little"), int.from_bytes(ptrs[4:8], "little")
        if not (GBA_ANCHOR <= sb1 <= 0x0203FFFF and GBA_ANCHOR <= sb2 <= 0x0203FFFF):
            return None
        key = int.from_bytes(self.mem.peek(INITIAL_SEED + (sb2 - GBA_ANCHOR) + ITEM_KEY, 4), "little") & 0xFFFF
        raw = self.mem.peek(INITIAL_SEED + (sb1 - GBA_ANCHOR) + BALL_POCKET, 13 * 4)
        pocket = []
        for i in range(13):
            item = int.from_bytes(raw[i * 4:i * 4 + 2], "little")
            if item:
                pocket.append((item, int.from_bytes(raw[i * 4 + 2:i * 4 + 4], "little") ^ key))
        return pocket

    def read_enemy(self):
        """Return (Pokemon | None, note)."""
        if not self.locate():
            return None, "game not supported"
        p = decode(self.mem.peek(self.offsets[0], SLOT))
        if p is None or not p.checksum_ok:
            return None, "no valid wild Pokemon data"
        note = "same PID as last encounter (data may be stale)" if p.pid == self.last_pid else ""
        self.last_pid = p.pid
        return p, note

    def close(self):
        self.mem.close()
