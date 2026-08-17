"""Læs den samlede spilliste og klassificér hvert spil.

Listen er Marcos egen sammenskrivning fra Archipelagos kanal. Den er langt
rigere end det maskinlæsbare indeks, fordi den siger **hvilken platform**
hvert spil kører på — og det er dét der afgør om et plugin må hente spillet
eller skal bede spilleren om hans egen fil.

Formatet er `Navn (PLATFORM): kilde`, med afsnitsoverskrifter imellem.
"""

import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "catalog" / "games.json"

# Konsoller: spillet ligger på en kassette eller disk spilleren ejer.
# Et plugin må ALDRIG hente dem.
CONSOLE = {
    "2600", "NES", "SNES", "N64", "GC", "Wii", "Wii U", "GB", "GBC", "GBA",
    "DS", "3DS", "PSX", "PS1", "PS2", "PS3", "PSP", "GEN", "SMS",
}
# Platforme hvor spillet oftest er frit eller webbaseret.
OPEN_ISH = {"Web", "PICO-8"}
# PC og VR siger intet i sig selv — Steam-titler og gratis spil ser ens ud.
AMBIGUOUS = {"PC", "VR", "Android", "PC/Web", "PC/Mobile", "N64/PC", "PC / Physical"}

BUNDLED = "bundled with archipelago"
DISCORD_ONLY = ("discord thread only", "discord channel only")

# Linjerne før den første "Games"-overskrift er værktøjer og klienter,
# ikke spil. De skal ikke i kartoteket.
SECTION = re.compile(r"^(Games\b.*|Hint Games:)\s*$")
ENTRY = re.compile(r"^(.*?)\s*\(([^)]*)\):\s*(.*)$")


def classify(platforms):
    """Hvad kræver spillet af spilleren?"""
    if any(p in CONSOLE for p in platforms):
        return "egen_fil"          # spilleren leverer sin egen ROM/disk
    if platforms and all(p in OPEN_ISH for p in platforms):
        return "frit"
    return "ukendt"                # PC/VR skal vurderes enkeltvis


def source_kind(rest):
    low = rest.lower()
    if low.startswith(BUNDLED):
        return "bundlet", ""
    if any(low.startswith(d) for d in DISCORD_ONLY):
        return "kun_discord", ""
    if rest.startswith("http"):
        return "url", rest.strip()
    return "andet", rest.strip()


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
            continue                       # værktøjs-afsnittet øverst

        m = ENTRY.match(line)
        if not m:
            continue
        name, plat, rest = m.group(1).strip(), m.group(2).strip(), m.group(3)
        platforms = [p.strip() for p in re.split(r"[/,]", plat) if p.strip()]
        # "N64/PC" og lignende skal blive sammen, ikke splittes til ukendte
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
        "note": "Navne, platforme og adresser. Aldrig andres indhold.",
        "count": len(games),
        "by_requires": by_req,
        "by_source": by_src,
        "games": sorted(games, key=lambda g: g["name"].lower()),
    }, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")

    print(f"{len(games)} spil\n")
    print("hvad de kræver af spilleren:")
    for k, v in sorted(by_req.items(), key=lambda x: -x[1]):
        print(f"  {k:<12} {v:>4}")
    print("\nhvor de kommer fra:")
    for k, v in sorted(by_src.items(), key=lambda x: -x[1]):
        print(f"  {k:<12} {v:>4}")

    plats = {}
    for g in games:
        plats[g["platform_raw"]] = plats.get(g["platform_raw"], 0) + 1
    print("\nplatforme (top 12):")
    for k, v in sorted(plats.items(), key=lambda x: -x[1])[:12]:
        print(f"  {k:<14} {v:>4}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1]))
