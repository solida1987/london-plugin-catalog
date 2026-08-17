"""Hent det offentlige apworld-indeks og byg vores eget kartotek.

Kilden er `Eijebong/Archipelago-index` — et offentligt, maskinlæsbart indeks
over uofficielle apworlds. Vi kopierer det ikke; vi læser det og bygger vores
egen tabel med de felter et London-plugin skal bruge.

⚠ Vi henter ALDRIG selve apworld-filerne. Kartoteket indeholder adresser og
metadata — aldrig andres indhold. Samme regel som alt andet vi bygger.
"""

import json
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "catalog" / "index.json"

REPO = "Eijebong/Archipelago-index"


def gh(path):
    run = subprocess.run(["gh", "api", path], capture_output=True, text=True,
                         encoding="utf-8")
    if run.returncode != 0:
        raise SystemExit(f"gh api {path} fejlede:\n{run.stderr[-800:]}")
    return json.loads(run.stdout)


def parse_toml(text):
    """Lille TOML-læser til netop dette format.

    ⚠ Bevidst minimal: indekset bruger kun nøgle=værdi og én [versions]-tabel.
    En fuld TOML-parser ville være en afhængighed vi ikke har brug for — men
    hvis formatet udvides, skal det her erstattes, ikke lappes."""
    out = {"versions": []}
    section = None
    for line in text.splitlines():
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        if line.startswith("["):
            section = line.strip("[]")
            continue
        m = re.match(r'^(\S+)\s*=\s*(.*)$', line)
        if not m:
            continue
        key, value = m.group(1), m.group(2).strip()
        if section == "versions":
            out["versions"].append(key.strip('"'))
        else:
            out[key] = value.strip('"')
    return out


def classify(entry):
    """Hvad kræver spillet af spilleren?

    Kan ikke afgøres af indekset alene — det siger intet om selve spillet.
    Derfor: alt starter som 'ukendt', og hver post skal vurderes én gang og
    skrives ned. Et gæt her ville blive til en plugin der henter noget den
    ikke må."""
    return "ukendt"


def main():
    print(f"henter indeks fra {REPO} …")
    files = gh(f"repos/{REPO}/contents/index")
    print(f"  {len(files)} poster")

    entries = []
    for i, f in enumerate(files, 1):
        if not f["name"].endswith(".toml"):
            continue
        blob = gh(f"repos/{REPO}/contents/{f['path']}")
        import base64
        text = base64.b64decode(blob["content"]).decode("utf-8", "replace")
        t = parse_toml(text)

        entries.append({
            "id": t.get("name", f["name"][:-5]),
            "display_name": t.get("display_name", ""),
            "home": t.get("home", ""),
            "apworld_url": t.get("default_url", ""),
            "versions": t["versions"],
            "requires": classify(t),
            "plugin": None,
        })
        if i % 50 == 0:
            print(f"  {i}/{len(files)} …")

    entries.sort(key=lambda e: e["id"].lower())
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(json.dumps({
        "source": f"https://github.com/{REPO}",
        "note": "Adresser og metadata. Aldrig andres indhold.",
        "count": len(entries),
        "entries": entries,
    }, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")

    with_url = sum(1 for e in entries if e["apworld_url"])
    print(f"\nskrevet: {OUT.relative_to(ROOT)}")
    print(f"  {len(entries)} spil, {with_url} med direkte apworld-adresse")
    print(f"  {len(entries) - with_url} uden — de skal slås op i hånden")
    return 0


if __name__ == "__main__":
    sys.exit(main())
