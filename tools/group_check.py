# -*- coding: utf-8 -*-
"""Dobbelttjek en HEL gruppe, ikke ét spil ad gangen.

    python tools/group_check.py GBA

Trin 2 i docs/UDGIVELSER.md. Den leder efter fejl der er FÆLLES for gruppen,
fordi det er dem der gør mest skade: rammer man forkert på ét GBA-spil, har de
17 andre efter al sandsynlighed samme fejl. Enkeltfejl fanger build_plugins.py.

Historikken bag: fire gange har et opfundet øvre loft stille skåret data væk
(kort > 120, tegn < 0xA1, trænerklasse > 100). Ingen af dem fejlede — de
returnerede bare mindre. Derfor kigger den her efter TAVSHED: felter uden
kilde, værdier der er ens overalt, huller ingen har nævnt.
"""

import json
import sys
from collections import Counter, defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
GAMES = ROOT / "catalog" / "games"

# Felter der SKAL kunne spores. Et tal uden kilde er et gæt der ser ud som
# en måling.
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
        print("ingen manifester for %s" % platform)
        return 1

    print("Gruppe-tjek: %s, %d manifester\n" % (platform, len(games)))

    # 1. Kolliderer noget? To spil med samme id/apworld-navn/patch-endelse
    #    ville stille overskrive hinanden.
    for field, path in (("id", ("id",)),
                        ("ap_world_name", ("ap_world_name",)),
                        ("patch-endelse", ("patch", "extension"))):
        seen = defaultdict(list)
        for gid, m in games.items():
            v = m
            for k in path:
                v = (v or {}).get(k)
            if v:
                seen[v].append(gid)
        for v, who in seen.items():
            if len(who) > 1:
                note("FEJL", "%s %r deles af: %s" % (field, v, ", ".join(who)))

    # 2. Har hvert sporbart felt en kilde?
    for gid, m in games.items():
        for f in NEEDS_SOURCE:
            if m.get(f) and not m.get("_%s_source" % f):
                note("FEJL", "%s: %s har ingen _%s_source" % (gid, f, f))

    # 3. Er en værdi ens ALLE steder? Så er den enten strukturel eller
    #    copy-paste. Den skal ses efter, ikke antages.
    for field in ("subtitle", "requires", "description"):
        vals = Counter(m.get(field) for m in games.values() if m.get(field))
        if len(vals) == 1 and len(games) > 1:
            v = list(vals)[0]
            level = "OK" if field != "description" else "FEJL"
            note(level, "%s er %r i alle %d - %s"
                 % (field, v[:40], len(games),
                    "ens med vilje" if level == "OK"
                    else "beskrivelser maa ikke vaere ens"))

    # 4. Hullerne. Ikke fejl, men de skal STAA der - ikke opdages af en
    #    spiller hvis ROM bliver accepteret og ikke virker.
    nohash = [g for g, m in games.items()
              if not (m.get("rom") or {}).get("md5")
              and not (m.get("rom") or {}).get("size")]
    if nohash:
        note("HUL", "%d/%d har hverken md5 eller stoerrelse - de kan kun "
                    "ADVARE om en forkert ROM, ikke afvise den: %s"
             % (len(nohash), len(games), ", ".join(sorted(nohash))))

    unver = [g for g, m in games.items() if not m.get("checks_verified")]
    if unver:
        note("HUL", "%d/%d har checks_verified=false - London advarer ved "
                    "start, og det er korrekt indtil RAM-kortet er maalt"
             % (len(unver), len(games)))

    unconfirmed = [g for g, m in games.items()
                   if "SKAL bekraeftes" in (m.get("_ap_world_name_source") or "")]
    if unconfirmed:
        note("HUL", "%d ap_world_name er AFLEDT af tutorial-adressen og ikke "
                    "bekraeftet mod apworldet: %s"
             % (len(unconfirmed), ", ".join(sorted(unconfirmed))))

    # 5. Emulator-gulvet for gruppen. Forskellige krav er ikke en fejl, men
    #    gruppens tekst skal naevne det HOEJESTE, ellers installerer en
    #    spiller en for gammel BizHawk og faar en uforklarlig fejl.
    vers = {g: (m.get("emulator") or {}).get("min_version")
            for g, m in games.items()}
    known = sorted({v for v in vers.values() if v},
                   key=lambda s: [int(x) for x in s.split(".")])
    if known:
        note("OK", "BizHawk-krav i gruppen: %s -> gruppeteksten skal sige "
                   "mindst %s" % (", ".join(known), known[-1]))

    order = {"FEJL": 0, "HUL": 1, "OK": 2}
    for level, msg in sorted(findings, key=lambda f: order[f[0]]):
        print("  [%-4s] %s" % (level, msg))

    bad = [f for f in findings if f[0] == "FEJL"]
    print("\n%d fejl, %d huller" % (len(bad), len([f for f in findings if f[0] == "HUL"])))
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1] if len(sys.argv) > 1 else "GBA"))
