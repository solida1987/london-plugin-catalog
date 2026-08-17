# -*- coding: utf-8 -*-
"""Double-check a WHOLE group, not one game at a time.

    python tools/group_check.py GBA

Step 2 in docs/RELEASES.md. It looks for mistakes that are SHARED across the
group, because those do the most damage: get something wrong on one GBA game
and the other 17 very likely have the same mistake. Single-game problems are
caught by build_plugins.py.

The history behind this: four separate times an invented upper bound quietly
cut data away (map > 120, charset < 0xA1, trainer class > 100). None of them
failed -- they just returned less. So this looks for SILENCE: fields with no
source, values that are identical everywhere, gaps nobody wrote down.
"""

import json
import sys
from collections import Counter, defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
GAMES = ROOT / "catalog" / "games"

# Fields that must be traceable. A number with no source is a guess wearing
# the clothes of a measurement.
NEEDS_SOURCE = ["ap_world_name", "description", "requires"]

findings = []


def note(level, msg):
    findings.append((level, msg))


def main(platform):
    games = {}
    for p in sorted(GAMES.glob("*.json")):
        m = json.loads(p.read_text(encoding="utf-8"))
        if m.get("platform") == platform:
            games[p.stem] = m

    if not games:
        print("no manifests for %s" % platform)
        return 1

    print("Group check: %s, %d manifests\n" % (platform, len(games)))

    # 1. Does anything collide? Two games sharing an id / AP name / patch
    #    extension would quietly overwrite each other.
    for field, path in (("id", ("id",)),
                        ("ap_world_name", ("ap_world_name",)),
                        ("patch extension", ("patch", "extension"))):
        seen = defaultdict(list)
        for gid, m in games.items():
            v = m
            for k in path:
                v = (v or {}).get(k)
            if v:
                seen[v].append(gid)
        for v, who in seen.items():
            if len(who) > 1:
                note("FAIL", "%s %r is shared by: %s" % (field, v, ", ".join(who)))

    # 2. Does every traceable field carry its source?
    for gid, m in games.items():
        for f in NEEDS_SOURCE:
            if m.get(f) and not m.get("_%s_source" % f):
                note("FAIL", "%s: %s has no _%s_source" % (gid, f, f))

    # 3. Is a value identical EVERYWHERE? Then it is either structural or a
    #    copy-paste. It gets looked at rather than assumed.
    for field in ("subtitle", "requires", "description"):
        vals = Counter(m.get(field) for m in games.values() if m.get(field))
        if len(vals) == 1 and len(games) > 1:
            v = list(vals)[0]
            deliberate = field != "description"
            note("OK" if deliberate else "FAIL",
                 "%s is %r in all %d - %s"
                 % (field, v[:40], len(games),
                    "deliberate" if deliberate
                    else "descriptions must not be identical"))

    # 4. The gaps. Not errors, but they must be WRITTEN DOWN rather than
    #    discovered by a player whose ROM is accepted and does not work.
    nohash = [g for g, m in games.items()
              if not (m.get("rom") or {}).get("md5")
              and not (m.get("rom") or {}).get("size")]
    if nohash:
        note("GAP", "%d/%d have neither md5 nor size - they can only WARN "
                    "about a wrong ROM, not reject it: %s"
             % (len(nohash), len(games), ", ".join(sorted(nohash))))

    unver = [g for g, m in games.items() if not m.get("checks_verified")]
    if unver:
        note("GAP", "%d/%d have checks_verified=false - London warns at "
                    "launch, which is correct until the RAM map is measured"
             % (len(unver), len(games)))

    unconfirmed = [g for g, m in games.items()
                   if "must be confirmed" in (m.get("_ap_world_name_source") or "")]
    if unconfirmed:
        note("GAP", "%d ap_world_name values are DERIVED from the tutorial "
                    "URL and not confirmed against the apworld: %s"
             % (len(unconfirmed), ", ".join(sorted(unconfirmed))))

    # 5. The group's emulator floor. Different requirements are not an error,
    #    but the group text has to state the HIGHEST one, or somebody installs
    #    a too-old BizHawk and gets an unexplainable failure.
    vers = {g: (m.get("emulator") or {}).get("min_version")
            for g, m in games.items()}
    known = sorted({v for v in vers.values() if v},
                   key=lambda s: [int(x) for x in s.split(".")])
    if known:
        note("OK", "BizHawk requirements in this group: %s -> the group text "
                   "must say at least %s" % (", ".join(known), known[-1]))

    order = {"FAIL": 0, "GAP": 1, "OK": 2}
    for level, msg in sorted(findings, key=lambda f: order[f[0]]):
        print("  [%-4s] %s" % (level, msg))

    bad = [f for f in findings if f[0] == "FAIL"]
    print("\n%d failures, %d gaps"
          % (len(bad), len([f for f in findings if f[0] == "GAP"])))
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1] if len(sys.argv) > 1 else "GBA"))
