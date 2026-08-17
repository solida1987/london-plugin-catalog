# -*- coding: utf-8 -*-
"""Read the combined game list and classify every game.

The list is far richer than the machine-readable index, because it states
WHICH PLATFORM each game runs on -- and that is what decides whether a plugin
may fetch the game or has to ask the player for their own file.

The format is `Name (PLATFORM): source`, with section headings in between.
"""

import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "catalog" / "games.json"

# Consoles: the game lives on a cartridge or disc the player owns.
# A plugin must NEVER fetch these.
CONSOLE = {
    "2600", "NES", "SNES", "N64", "GC", "Wii", "Wii U", "GB", "GBC", "GBA",
    "DS", "3DS", "PSX", "PS1", "PS2", "PS3", "PSP", "GEN", "SMS",
}
# Platforms where the game is usually free or browser-based.
OPEN_ISH = {"Web", "PICO-8"}
# PC and VR say nothing by themselves -- Steam titles and free games look the
# same from here.
AMBIGUOUS = {"PC", "VR", "Android", "PC/Web", "PC/Mobile", "N64/PC", "PC / Physical"}

BUNDLED = "bundled with archipelago"
DISCORD_ONLY = ("discord thread only", "discord channel only")

# The lines before the first "Games" heading are tools and clients, not games.
# They do not belong in the catalogue.
SECTION = re.compile(r"^(Games\b.*|Hint Games:)\s*$")
ENTRY = re.compile(r"^(.*?)\s*\(([^)]*)\):\s*(.*)$")


def classify(platforms):
    """What does the game require from the player?"""
    if any(p in CONSOLE for p in platforms):
        return "own_copy"          # the player supplies their own ROM/disc
    if platforms and all(p in OPEN_ISH for p in platforms):
        return "free"
    return "unknown"               # PC/VR has to be judged one at a time


def source_kind(rest):
    low = rest.lower()
    if low.startswith(BUNDLED):
        return "bundled", ""
    if any(low.startswith(d) for d in DISCORD_ONLY):
        return "discord_only", ""
    if rest.startswith("http"):
        return "url", rest.strip()
    return "other", rest.strip()


def main(path):
    lines = Path(path).read_text(encoding="utf-8").splitlines()
    games, in_games = [], False

    for raw in lines:
        line = raw.strip()
        if not line:
            continue
        if SECTION.match(line):
            in_games = True
            continue
        if not in_games:
            continue                       # the tools section at the top

        m = ENTRY.match(line)
        if not m:
            continue
        name, plat, rest = m.group(1).strip(), m.group(2).strip(), m.group(3)
        platforms = [p.strip() for p in re.split(r"[/,]", plat) if p.strip()]
        # "N64/PC" and friends must stay together, not be split into two
        # platforms neither of which is recognised.
        if plat in AMBIGUOUS:
            platforms = [plat]
        kind, url = source_kind(rest)

        games.append({
            "name": name,
            "platform_raw": plat,
            "platforms": platforms,
            "source": kind,
            "url": url,
            "requires": classify(platforms),
        })

    by_req = {}
    by_src = {}
    for g in games:
        by_req[g["requires"]] = by_req.get(g["requires"], 0) + 1
        by_src[g["source"]] = by_src.get(g["source"], 0) + 1

    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(json.dumps({
        "note": ("Names, platforms and addresses - never anyone else's "
                 "content. 'platforms' drives what a plugin is allowed to do; "
                 "'requires' stays 'unknown' until a title has been looked at "
                 "individually."),
        "count": len(games),
        "by_requires": by_req,
        "by_source": by_src,
        "games": sorted(games, key=lambda g: g["name"].lower()),
    }, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")

    print("%d games\n" % len(games))
    print("what they require from the player:")
    for k, v in sorted(by_req.items(), key=lambda x: -x[1]):
        print("  %-13s %4d" % (k, v))
    print("\nwhere they come from:")
    for k, v in sorted(by_src.items(), key=lambda x: -x[1]):
        print("  %-13s %4d" % (k, v))

    plats = {}
    for g in games:
        plats[g["platform_raw"]] = plats.get(g["platform_raw"], 0) + 1
    print("\nplatforms (top 12):")
    for k, v in sorted(plats.items(), key=lambda x: -x[1])[:12]:
        print("  %-14s %4d" % (k, v))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1]))
