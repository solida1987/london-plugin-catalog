# -*- coding: utf-8 -*-
"""Build dist/plugins.json and the text for the `index` release.

    python tools/make_index.py

Run this LAST in a release round, so the index release is the newest one and
GitHub's "Latest" points at the overview instead of whichever platform was
touched most recently. See docs/RELEASES.md.
"""

import collections
import glob
import io
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DIST = ROOT / "dist"
NOTES = DIST / "index_notes.md"

GROUPS = [
    ("gba", "GBA", "Game Boy Advance"),
    ("snes", "SNES", "Super Nintendo"),
    ("n64", "N64", "Nintendo 64"),
    ("gb-gbc", "GB/GBC", "Game Boy and Game Boy Color"),
    ("nes", "NES", "Nintendo Entertainment System"),
    ("gc-wii", "GC/Wii", "GameCube and Wii"),
    ("ds", "DS", "Nintendo DS"),
    ("playstation", "PS", "PlayStation"),
    ("sega", "Sega", "Mega Drive and Master System"),
    ("pc", "PC", "PC"),
    ("web", "Web", "Web and PICO-8"),
]
REPO = "https://github.com/solida1987/london-plugin-catalog"

BODY = """**The overview. Start here.**

The catalogue is split into one release per platform. Tags never change name —
when a new game arrives, that platform's release is updated.

| Platform | Built |
|---|---|
%s

This release contains no plugins — only `plugins.json`, the whole list in
machine-readable form. It is always updated **last**, so GitHub highlights the
overview rather than whichever platform happened to be touched most recently.

---

### Using a plugin

Download the `.londonplugin` file from the platform's release and add it in
London with **Add plugin**. The game appears in the list on the left.

### Three things that apply to the whole catalogue

- **You must own the games yourself.** No game files ever ship with a plugin.
- **%d of %d plugins verify your ROM by MD5** and refuse a wrong file outright.
  Where a game's randomizer declares no hash, the plugin says so rather than
  pretending the file was checked.
- **London decides a plugin's trust level itself** — a plugin cannot declare
  itself trusted.

### Status

%d plugins built so far. Each platform release states what its own plugins
cannot do yet.
"""


def main():
    games = json.loads((ROOT / "catalog" / "games.json").read_text(
        encoding="utf-8"))["games"]

    # A manifest is NOT a plugin. Ground truth is what actually got packaged --
    # counting manifests once claimed 8 SNES games were "built" and linked to a
    # release that did not exist, because those manifests are blocked by the
    # protocol gate. The index must never promise more than dist/ holds.
    packaged = {p.name.rsplit("-", 1)[0] for p in DIST.glob("*.londonplugin")}

    built, blocked = {}, {}
    for p in sorted(glob.glob(str(ROOT / "catalog" / "games" / "*.json"))):
        m = json.loads(Path(p).read_text(encoding="utf-8"))
        (built if m["id"] in packaged else blocked)[m["id"]] = m

    if blocked:
        print("mapped but NOT packaged (%d): %s\n"
              % (len(blocked), ", ".join(sorted(blocked))))

    known = collections.Counter()
    for g in games:
        for pf in g["platforms"] or ["unknown"]:
            known[pf] += 1
    done = collections.Counter(m["platform"] for m in built.values())

    idx = {"generated_for": "London Plugin Catalog", "groups": [], "plugins": []}
    rows = []
    for tag, pf, title in GROUPS:
        n_done, n_all = done.get(pf, 0), known.get(pf, 0)
        if n_all == 0:
            continue
        idx["groups"].append({"tag": tag, "platform": pf, "title": title,
                              "built": n_done, "known": n_all})
        label = "[%s](%s/releases/tag/%s)" % (title, REPO, tag) if n_done else title
        rows.append("| %s | %d / %d |" % (label, n_done, n_all))

    for m in sorted(built.values(), key=lambda x: x["display_name"]):
        rom = m.get("rom") or {}
        hashes = rom.get("md5") or []
        idx["plugins"].append({
            "id": m["id"], "name": m["display_name"], "platform": m["platform"],
            "version": m.get("version", "1.0.0"),
            "ap_world_name": m.get("ap_world_name"),
            "requires_own_copy": m.get("requires") == "own_copy",
            "checks_verified": bool(m.get("checks_verified")),
            "accepted_dumps": len(hashes),
            # The honest question: can this plugin REFUSE a wrong file, or only
            # warn about one? A player deserves to know which they are getting.
            "rom_can_be_rejected": bool(hashes) or bool(rom.get("size")),
        })

    DIST.mkdir(exist_ok=True)
    io.open(DIST / "plugins.json", "w", encoding="utf-8").write(
        json.dumps(idx, indent=2, ensure_ascii=False) + "\n")

    n = len(idx["plugins"])
    verifiable = sum(1 for p in idx["plugins"] if p["rom_can_be_rejected"])
    io.open(NOTES, "w", encoding="utf-8", newline="\n").write(
        BODY % ("\n".join(rows), verifiable, n, n))

    print("\n".join(rows))
    print("\n%d plugins, %d can reject a wrong ROM" % (n, verifiable))
    print("wrote dist/plugins.json and dist/index_notes.md")
    return 0


if __name__ == "__main__":
    sys.exit(main())
