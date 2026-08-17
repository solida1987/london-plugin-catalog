# -*- coding: utf-8 -*-
"""Fetch the public apworld index and build our own catalogue.

The source is `Eijebong/Archipelago-index`, a public machine-readable index of
unofficial apworlds. We do not copy it; we read it and build our own table
with the fields a London plugin needs.

We NEVER fetch the apworld files themselves. The catalogue holds addresses and
metadata -- never anyone else's content. Same rule as everything else here.
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
        raise SystemExit(f"gh api {path} failed:\n{run.stderr[-800:]}")
    return json.loads(run.stdout)


def parse_toml(text):
    """A small TOML reader for exactly this format.

    Deliberately minimal: the index uses only key=value and one [versions]
    table. A full TOML parser would be a dependency we do not need -- but if
    the format grows, this gets REPLACED, not patched."""
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
    """What does the game require from the player?

    The index alone cannot answer this -- it says nothing about the game
    itself. So everything starts as 'unknown', and each entry gets judged
    once and written down. A guess here would turn into a plugin that
    fetches something it is not allowed to."""
    return "unknown"


def main():
    print(f"fetching index from {REPO} ...")
    files = gh(f"repos/{REPO}/contents/index")
    print(f"  {len(files)} entries")

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
        "note": "Addresses and metadata. Never anyone else's content.",
        "count": len(entries),
        "entries": entries,
    }, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")

    with_url = sum(1 for e in entries if e["apworld_url"])
    print(f"\nwrote: {OUT.relative_to(ROOT)}")
    print(f"  {len(entries)} games, {with_url} with a direct apworld address")
    print(f"  {len(entries) - with_url} without -- those need a manual lookup")
    return 0


if __name__ == "__main__":
    sys.exit(main())
