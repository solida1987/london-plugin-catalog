# -*- coding: utf-8 -*-
"""Show one platform group from the game list.

    python tools/group.py GBA

Writes nothing -- it shows the group so it can be read through before anything
is built.
"""

import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
GAMES = ROOT / "catalog" / "games.json"

WHERE = {
    "bundled":      "ships with Archipelago",
    "discord_only": "only in a Discord thread",
}


def main(platform):
    data = json.loads(GAMES.read_text(encoding="utf-8"))
    hits = [g for g in data["games"] if platform in g["platforms"]]

    print("%s: %d games\n" % (platform, len(hits)))
    for g in hits:
        print("  %s" % g["name"])
        print("      %s" % WHERE.get(g["source"], g["url"]))

    kinds = {}
    for g in hits:
        kinds[g["source"]] = kinds.get(g["source"], 0) + 1
    print("\nsources: " + ", ".join("%s=%d" % kv for kv in sorted(kinds.items())))

    blocked = [g["name"] for g in hits if g["source"] == "discord_only"]
    if blocked:
        print("\n%d cannot be built -- the source lives only in a Discord "
              "thread we have no access to:" % len(blocked))
        for b in blocked:
            print("    %s" % b)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1] if len(sys.argv) > 1 else "GBA"))
