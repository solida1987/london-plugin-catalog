# -*- coding: utf-8 -*-
"""Draft a game manifest for every RAM map that has none yet.

    py -3.13 tools/draft_manifests.py --worlds C:/tmp/worlds        # dry run
    py -3.13 tools/draft_manifests.py --worlds C:/tmp/worlds --write

The expensive part of adding a game is the Lua RAM map -- somebody has to find
the addresses in a running game. Sixty-two of those exist. This finds the ones
with no manifest and fills in what can be SOURCED:

    platform, display_name   the harvested game list (catalog/games.json)
    client.protocol          read from the world (audit_client)
    patch_extension          read from the world (audit_patch_extension)
    lua_module               the RAM map file that prompted the draft
    requires                 the harvested list's own classification

Everything else stays null. catalog/SCHEMA.md: an empty field is usable, an
invented one is dangerous.

⚠ NAME MATCHING IS STRICT ON PURPOSE. A first attempt matched loosely and
produced two silent errors: "oot" matched *Minishoot' Adventures* (substring),
and "golden_sun" matched *Golden Sun: The Lost Age*, which is a DIFFERENT game
that already has a manifest. A wrong match here becomes a plugin that demands
the wrong ROM, so a module is either exact, explicitly aliased below, or left
alone with a note.
"""

import argparse
import json
import os
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import audit_patch_extension as base
import audit_client

ROOT = Path(__file__).resolve().parent.parent
SCRIPTS = ROOT.parent / "Multiworld-Launcher" / "Plugins" / "Scripts" / "games"

# RAM-map file -> the game's name EXACTLY as the harvested list writes it.
# Every entry here was read off that list, not recalled.
ALIAS = {
    "Donkey Kong Country 2":        "Donkey Kong Country 2: Diddy's Kong Quest",
    "Donkey Kong Country 3":        "Donkey Kong Country 3: Dixie Kong's Double Trouble!",
    "ctjot":                        "Chrono Trigger",
    "cv64":                         "Castlevania 64",
    "dk64":                         "Donkey Kong 64",
    "ff4fe":                        "Final Fantasy IV",
    "ff6wc":                        "Final Fantasy VI",
    "ladx":                         "The Legend of Zelda: Link's Awakening DX",
    "mk64":                         "Mario Kart 64",
    "mm3":                          "Mega Man 3",
    "oot":                          "The Legend of Zelda: Ocarina of Time",
    "smrpg":                        "Super Mario RPG",
    "sonic1":                       "Sonic the Hedgehog",
    "sotn":                         "Castlevania: Symphony of the Night",
    "tloz_ooa":                     "The Legend of Zelda: Oracle of Ages",
    "tloz_oos":                     "The Legend of Zelda: Oracle of Seasons",
    "zelda2":                       "The Legend of Zelda II: The Adventure of Link",
    "golden_sun":                   "Golden Sun",       # NOT The Lost Age
    "mmx4":                         "Mega Man X4",
    "pokemon_rb":                   "Pokemon Red and Blue",
    "pokemon_bw":                   "Pokemon Black and White",
    "pokemon_platinum":             "Pokemon Platinum",
}

# Our id for a module, where the module name is not a legal id.
ID_FOR = {
    "Diddy Kong Racing":            "diddy_kong_racing",
    "Donkey Kong Country":          "donkey_kong_country",
    "Donkey Kong Country 2":        "donkey_kong_country_2",
    "Donkey Kong Country 3":        "donkey_kong_country_3",
    "Mega Man X":                   "mega_man_x",
    "Mega Man X2":                  "mega_man_x2",
    "Mega Man X3":                  "mega_man_x3",
    "kirby_64_-_the_crystal_shards": "kirby_64",
}

PLATFORM_OK = {"GBA", "SNES", "NES", "GBC", "GB", "N64", "GEN", "SMS", "2600"}


def norm(s):
    return re.sub(r"[^a-z0-9]", "", s.lower())


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--worlds", metavar="DIR")
    ap.add_argument("--write", action="store_true")
    args = ap.parse_args()
    if args.worlds:
        base.EXTRA_DIR = Path(args.worlds)

    games = json.loads((ROOT / "catalog" / "games.json")
                       .read_text(encoding="utf-8"))["games"]
    by_name = {norm(g["name"]): g for g in games}

    taken_ids, taken_modules = set(), set()
    for f in (ROOT / "catalog" / "games").glob("*.json"):
        m = json.loads(f.read_text(encoding="utf-8"))
        taken_ids.add(m["id"])
        taken_modules.add(m.get("lua_module") or m["id"])

    modules = sorted(os.path.splitext(f)[0] for f in os.listdir(SCRIPTS))
    drafted, skipped = [], []

    for mod in modules:
        if mod in taken_modules:
            continue

        wanted = ALIAS.get(mod, mod)
        g = by_name.get(norm(wanted))
        if g is None:
            skipped.append((mod, "not in the harvested list as %r" % wanted))
            continue

        plat = (g["platforms"] or ["unknown"])[0]
        if plat not in PLATFORM_OK:
            skipped.append((mod, "platform %s has no bridge yet" % plat))
            continue

        gid = ID_FOR.get(mod, mod if re.match(r"^[a-z0-9][a-z0-9_]{1,63}$", mod)
                         else re.sub(r"[^a-z0-9]+", "_", mod.lower()).strip("_"))
        if gid in taken_ids:
            skipped.append((mod, "id %s already used" % gid))
            continue

        m = {
            "id": gid,
            "display_name": g["name"],
            "subtitle": "",
            "platform": plat,
            "_platform_source": "harvested game list: platform_raw=%r"
                                % g.get("platform_raw"),
            "version": "1.0.0",
            "ap_world_name": g["name"],
            "_ap_world_name_source": "harvested game list, name as written there",
            # ⚠ Assembled from fields we HOLD, never a claim about the game.
            # The launcher refuses an empty description, and the hand-written
            # ones say what gets shuffled -- which nobody here has established.
            # So this says only what is true of every catalog entry, and a
            # human replaces it when they know the game.
            "description": "%s as a multiworld. Items and progression are "
                           "shuffled across the session." % g["name"],
            "_description_source": "placeholder assembled from display_name; "
                                   "replace with what this game actually shuffles",
            "checks_verified": False,
            "requires": g.get("requires") or "unknown",
            "_requires_source": "harvested game list: requires=%r"
                                % g.get("requires"),
            "lua_module": mod,
            "_lua_module_source": "Plugins/Scripts/games/%s.lua exists" % mod,
            "apworld": {
                "bundled": g.get("source") != "url",
                "url": g.get("url"),
                "_source": "harvested game list: source=%r" % g.get("source"),
            },
        }

        world = base.find_world(m)
        if world is None:
            m["client"] = {"protocol": "unknown",
                           "_source": "world not available to read"}
            m["patch_extension"] = "unaudited"
        else:
            proto, why = audit_client.classify(world)
            m["client"] = ({"protocol": proto, "_source": why}
                           if proto in ("bizhawk", "sni")
                           else {"protocol": "unknown", "_source": why})
            verdict, detail = base.inspect_world(world)
            m["patch_extension"] = ("unaudited" if verdict == "replicated"
                                    else verdict)
            m["_patch_extension_source"] = "read from %s: %s" % (world.name, detail)

        drafted.append(m)
        state = m["client"]["protocol"]
        print("  %-30s %-6s %-8s %-11s %s"
              % (gid, plat, state, m["patch_extension"], g["name"][:40]))

        if args.write:
            (ROOT / "catalog" / "games" / (gid + ".json")).write_text(
                json.dumps(m, indent=2, ensure_ascii=False) + "\n",
                encoding="utf-8")

    print("\n%d drafted%s" % (len(drafted), " and written" if args.write else ""))
    if skipped:
        print("\n%d left alone:" % len(skipped))
        for mod, why in skipped:
            print("   %-32s %s" % (mod, why))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
