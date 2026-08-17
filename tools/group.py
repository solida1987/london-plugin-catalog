"""Træk én platformgruppe ud af spillisten.

    python tools/group.py GBA

Skriver intet — viser gruppen, så den kan gennemgås før der bygges.
"""

import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
GAMES = ROOT / "catalog" / "games.json"


def main(platform):
    data = json.loads(GAMES.read_text(encoding="utf-8"))
    hits = [g for g in data["games"] if platform in g["platforms"]]

    print(f"{platform}: {len(hits)} spil\n")
    for g in hits:
        src = {"bundlet": "følger med AP",
               "kun_discord": "⚠ kun i en Discord-tråd",
               "url": g["url"],
               "andet": g["url"]}[g["source"]]
        print(f"  {g['name']}")
        print(f"      {src}")

    kinds = {}
    for g in hits:
        kinds[g["source"]] = kinds.get(g["source"], 0) + 1
    print("\nkilder:", ", ".join(f"{k}={v}" for k, v in sorted(kinds.items())))

    blocked = [g["name"] for g in hits if g["source"] == "kun_discord"]
    if blocked:
        print(f"\n⚠ {len(blocked)} kan ikke bygges — kilden ligger kun i en "
              f"Discord-tråd vi ikke har adgang til:")
        for b in blocked:
            print(f"    {b}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1] if len(sys.argv) > 1 else "GBA"))
