# -*- coding: utf-8 -*-
"""Sort a folder of ROMs by whether Archipelago supports them.

    python tools/scan_roms.py D:\\roms                 # report only
    python tools/scan_roms.py D:\\roms --organise      # then move them

Three answers, and keeping them apart is the whole point:

  MATCHED    the file's MD5 is one this game's randomizer accepts.
             Proven, by content. The filename was not consulted.

  NAMED      a game with a similar NAME has an Archipelago world, but we hold
             no hash for it, so this is a GUESS. It may be the wrong region,
             the wrong revision, or a hack that kept the name.

  NONE       no Archipelago world found for anything resembling this name.

⚠ ROMs are the player's own files. This reads and moves them inside the folder
  it was pointed at; it never copies them anywhere else, never uploads, and the
  report it writes stays in that folder. Nothing about ROMs belongs in this
  repository.
"""

import argparse
import hashlib
import json
import re
import shutil
import sys
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
GAMES = ROOT / "catalog" / "games"
LIST = ROOT / "catalog" / "games.json"

EXT_PLATFORM = {
    ".gba": "GBA", ".gb": "GB", ".gbc": "GBC",
    ".nes": "NES", ".sfc": "SNES", ".smc": "SNES",
    ".z64": "N64", ".n64": "N64", ".v64": "N64",
    ".md": "GEN", ".gen": "GEN", ".sms": "SMS",
}


def md5(path, chunk=1 << 20):
    h = hashlib.md5()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(chunk), b""):
            h.update(block)
    return h.hexdigest()


def normalise(name):
    """A comparable form of a title: lowercase words, no punctuation, no
    region/language tags. 'Banjo-Tooie (USA)' and 'Banjo Tooie' must meet."""
    name = re.sub(r"\([^)]*\)|\[[^\]]*\]", " ", name)      # (USA) [!] tags
    name = re.sub(r"\.[a-z0-9]{2,4}$", "", name, flags=re.I)
    return set(re.findall(r"[a-z0-9]+", name.lower()))


def load_catalogue():
    """Hash -> game, and every known AP game name with its platform."""
    by_hash, mapped = {}, []
    for p in sorted(GAMES.glob("*.json")):
        m = json.loads(p.read_text(encoding="utf-8"))
        for h in (m.get("rom") or {}).get("md5") or []:
            by_hash[h.lower()] = m
        mapped.append(m)

    known = []
    for g in json.loads(LIST.read_text(encoding="utf-8"))["games"]:
        known.append((normalise(g["name"]), g))
    return by_hash, mapped, known


# Words too common to identify anything on their own. Without this, an AP title
# like "Castlevania 64" and a file called "Castlevania - Legacy of Darkness"
# share enough to look like a match when they are two different games.
NOISE = {"the", "of", "and", "a", "usa", "europe", "japan", "en", "fr", "de",
         "es", "it", "nl", "ja", "rev", "beta", "proto", "v1", "v2"}


# Games whose release name and Archipelago name differ enough that scoring
# cannot bridge them. Each entry is a decision somebody made, not a fuzzy tweak
# -- loosening the threshold instead would drag in wrong games everywhere.
#
#   AP name -> the words to match on INSTEAD of the parsed title.
#
# Replacing, not adding: "Castlevania 64" fails against a file called
# "Castlevania (USA).z64" because of a "64" the cartridge never carried, and
# adding a word the title already has changes nothing. The replacement drops
# the part that does not exist in the wild.
ALIASES = {
    # The N64 game is simply "Castlevania" on the cartridge; AP disambiguates
    # it from the series with "64". Verified against this ROM set: the folder
    # holds "Castlevania (USA).z64" and no other N64 Castlevania except
    # Legacy of Darkness, which has its own AP world.
    "Castlevania 64": {"castlevania"},
    # Written "Star Fox 64" on the box, "Starfox 64" in the AP game list.
    "Starfox 64": {"star", "fox", "64"},
}


def best_name_match(stem, known, platform):
    """The AP game whose name best matches this filename, on this platform.

    Scored BOTH ways on purpose. Only asking "how much of the AP title appears
    in the filename" lets a short title win inside a longer, unrelated one --
    "Castlevania" would swallow "Castlevania - Legacy of Darkness". Requiring
    the filename to be mostly accounted for as well keeps them apart.
    """
    words = normalise(stem) - NOISE
    if not words:
        return None, 0.0, None

    scored = []
    for gw, g in known:
        if platform and g["platforms"] and platform not in g["platforms"]:
            continue
        gw = (ALIASES.get(g["name"]) or gw) - NOISE
        if not gw:
            continue
        shared = len(words & gw)
        if shared == 0:
            continue
        # Harmonic mean: both directions have to be good, not just one.
        a, b = shared / len(gw), shared / len(words)
        scored.append((2 * a * b / (a + b), g))

    if not scored:
        return None, 0.0, None
    scored.sort(key=lambda t: -t[0])
    runner = scored[1][1]["name"] if len(scored) > 1 else None
    return scored[0][1], scored[0][0], runner


def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument("folder")
    ap.add_argument("--organise", action="store_true",
                    help="move files into subfolders (writes a log first)")
    ap.add_argument("--threshold", type=float, default=0.8,
                    help="name overlap needed to call it NAMED (default 0.8)")
    args = ap.parse_args(argv[1:])

    folder = Path(args.folder)
    if not folder.is_dir():
        print("not a folder: %s" % folder)
        return 1

    by_hash, mapped, known = load_catalogue()
    print("catalogue: %d games mapped, %d accepted dumps with a hash\n"
          % (len(mapped), len(by_hash)))

    files = [p for p in sorted(folder.rglob("*"))
             if p.is_file() and p.suffix.lower() in EXT_PLATFORM]
    print("scanning %d ROMs ...\n" % len(files))

    rows = []
    for i, p in enumerate(files, 1):
        platform = EXT_PLATFORM[p.suffix.lower()]
        h = md5(p)
        hit = by_hash.get(h)
        if hit:
            rows.append((p, platform, "MATCHED", hit["display_name"], h, 1.0))
        else:
            g, score, _runner = best_name_match(p.stem, known, platform)
            if g and score >= args.threshold:
                rows.append((p, platform, "NAMED", g["name"], h, score))
            else:
                rows.append((p, platform, "NONE", "", h, score))
        if i % 50 == 0:
            print("  %d/%d" % (i, len(files)))

    counts = Counter(r[2] for r in rows)
    per_platform = Counter((r[1], r[2]) for r in rows)

    print("\n%-8s %6s %6s %6s" % ("", "MATCH", "NAMED", "NONE"))
    for pf in sorted({r[1] for r in rows}):
        print("%-8s %6d %6d %6d"
              % (pf, per_platform[(pf, "MATCHED")],
                 per_platform[(pf, "NAMED")], per_platform[(pf, "NONE")]))
    print("%-8s %6d %6d %6d"
          % ("total", counts["MATCHED"], counts["NAMED"], counts["NONE"]))

    print("\nMATCHED — proven by content, these are the ones to test with:")
    for p, pf, kind, name, h, score in rows:
        if kind == "MATCHED":
            print("  %-5s %-34s %s" % (pf, name, p.name))

    report = folder / "AP-SUPPORT.txt"
    with open(report, "w", encoding="utf-8", newline="\n") as f:
        f.write("Archipelago support for the ROMs in this folder.\n")
        f.write("MATCHED = the file's MD5 is one the randomizer accepts.\n")
        f.write("NAMED   = a game with this name has an AP world, but we hold\n")
        f.write("          no hash, so this is a GUESS from the filename.\n")
        f.write("NONE    = no AP world found.\n\n")
        # Pipe-separated, because game names contain spaces and a fixed-width
        # column silently ran into the next one — the first version of this
        # report was unreadable for exactly that reason.
        f.write("verdict | platform | archipelago game | confidence | md5 | file\n")
        f.write("-" * 100 + "\n")
        for p, pf, kind, name, h, score in sorted(
                rows, key=lambda r: (r[2], r[1], r[0].name)):
            # A guess carries its score so it can be argued with; a hash match
            # says "proven", because that is not an opinion.
            conf = ("proven" if kind == "MATCHED"
                    else "%.0f%%" % (score * 100) if name else "")
            f.write("%s | %s | %s | %s | %s | %s\n"
                    % (kind, pf, name, conf, h, p.name))
    print("\nwrote %s" % report)

    if not args.organise:
        print("\n(report only — pass --organise to move the files)")
        return 0

    # Move, never copy: same volume, so this is a rename and cannot half-finish
    # a 5 GB file. The log is written BEFORE anything moves.
    log = folder / "AP-SUPPORT-moves.txt"
    with open(log, "w", encoding="utf-8", newline="\n") as f:
        for p, pf, kind, name, h, score in rows:
            f.write("%s\t%s\n" % (p.name, "%s/%s" % (kind.title(), pf)))

    moved = 0
    for p, pf, kind, name, h, score in rows:
        dest = folder / kind.title() / pf
        dest.mkdir(parents=True, exist_ok=True)
        target = dest / p.name
        if target.exists():
            continue
        shutil.move(str(p), str(target))
        moved += 1
    print("\nmoved %d files; %s lists where each one went" % (moved, log.name))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
