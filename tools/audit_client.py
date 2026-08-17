# -*- coding: utf-8 -*-
"""What client does each world actually use? Reported, never rewritten.

    py -3.13 tools/audit_client.py [--worlds DIR] [game ...]

catalog/SCHEMA.md defines client.protocol by the world's own client class --
"the world's client subclasses BizHawkClient" is the cited source. This reads
the world and says what it finds, so a manifest that disagrees with the code is
visible instead of assumed.

⛔ DELIBERATELY DOES NOT WRITE. Unlike patch_extension, a disagreement here is
not automatically a wrong manifest:

  * London does not use Archipelago's client at all. It runs BizHawk with OUR
    generic connector plus a per-game Lua RAM map, so a game can work through
    the bizhawk bridge whether or not its world ships a BizHawkClient.
  * A world with NEITHER base class usually has a standalone client of its own
    (MMBN3 ships ArchipelagoMMBN3Client.exe) -- which says nothing about
    whether our own RAM map reads that game correctly.

So this tool produces a REVIEW LIST. Deciding what a disagreement means is a
judgement about our architecture, and the schema's rule is that a field nobody
can back up stays unanswered rather than being filled in from reasoning.
"""

import argparse
import json
import marshal
import types
import zipfile
from pathlib import Path

import audit_patch_extension as base   # world lookup, aliases, .pyc loading


def strings_and_names(path):
    """Every identifier and string constant in a world, compiled or not."""
    out = set()
    blobs = []
    if path.is_dir():
        for p in sorted(path.rglob("*.pyc")):
            blobs.append(("pyc", p.read_bytes()))
        for p in sorted(path.rglob("*.py")):
            blobs.append(("py", p.read_bytes()))
    else:
        with zipfile.ZipFile(path) as z:
            for n in sorted(z.namelist()):
                if n.endswith(".pyc"):
                    blobs.append(("pyc", z.read(n)))
                elif n.endswith(".py"):
                    blobs.append(("py", z.read(n)))

    for kind, data in blobs:
        if kind == "py":
            out.update(data.decode("utf-8", "replace").split())
            continue
        code, err = base.load_code(data, "")
        if code is None:
            continue
        blocks = []
        base.walk(code, blocks)
        for c in blocks:
            out.update(c.co_names)
            out.update(k for k in c.co_consts if isinstance(k, str))
    return out


def classify(path):
    words = strings_and_names(path)
    joined = " ".join(words)
    sni = any("SNIClient" in w for w in words)
    biz = any("BizHawkClient" in w for w in words) or "_bizhawk" in joined
    if sni and not biz:
        return "sni", "the world's client subclasses SNIClient"
    if biz and not sni:
        return "bizhawk", "the world's client subclasses BizHawkClient"
    if biz and sni:
        return "both", "the world references BOTH SNIClient and BizHawkClient"
    return "neither", ("no SNIClient and no BizHawkClient -- the world most "
                       "likely ships a standalone client of its own")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("games", nargs="*")
    ap.add_argument("--worlds", metavar="DIR")
    ap.add_argument("--fill-blanks", action="store_true",
                    help="write protocol ONLY where the manifest has none and "
                         "the world gives a definite answer")
    args = ap.parse_args()

    if args.worlds:
        base.EXTRA_DIR = Path(args.worlds)

    paths = sorted(base.GAMES.glob("*.json"))
    if args.games:
        paths = [p for p in paths if p.stem in args.games]

    disagree, filled = [], []
    for p in paths:
        m = json.loads(p.read_text(encoding="utf-8"))
        world = base.find_world(m)
        said = (m.get("client") or {}).get("protocol")
        if world is None:
            print("  %-28s %-9s %s" % (m["id"], said or "-", "no world available"))
            continue
        found, why = classify(world)
        agree = (found == said) or (found == "both" and said in ("sni", "bizhawk"))
        mark = " " if agree else "!"
        print("%s %-28s manifest=%-8s world=%-8s %s"
              % (mark, m["id"], said or "-", found, why[:52]))
        if not agree:
            disagree.append((m["id"], said, found))

        # Filling a BLANK from evidence is the safe half of this. Changing a
        # value somebody already established is not, and neither is writing
        # "neither" -- that is a question, not an answer.
        if (args.fill_blanks and said is None
                and found in ("bizhawk", "sni")):
            client = dict(m.get("client") or {})
            client["protocol"] = found
            client["_source"] = why
            m["client"] = client
            p.write_text(json.dumps(m, indent=2, ensure_ascii=False) + "\n",
                         encoding="utf-8")
            filled.append(m["id"])

    if filled:
        print("\nfilled %d blank protocol field(s) from the world: %s"
              % (len(filled), ", ".join(filled)))

    if disagree:
        print("\n%d disagreement(s) -- REVIEW, do not bulk-edit:" % len(disagree))
        for gid, said, found in disagree:
            print("   %-28s manifest says %-8s world says %s" % (gid, said, found))
    else:
        print("\nEvery manifest agrees with its world.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
