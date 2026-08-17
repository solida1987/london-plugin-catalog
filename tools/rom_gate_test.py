# -*- coding: utf-8 -*-
"""Prove the ROM gate accepts the right file and refuses the wrong one.

    python tools/rom_gate_test.py

We cannot test with a real ROM -- we do not have one, and would not ship one.
So the test builds a THROWAWAY plugin whose accepted MD5 is the hash of a file
we just created. That makes both directions testable for real:

    the file we hashed        -> must be ACCEPTED
    any other file            -> must be REJECTED

This is the only place the "size unknown, MD5 known" path runs, and that path
decides whether a player can start the game at all. Before it existed, a
manifest with a hash and no measured size would have rejected every ROM while
telling the player we expected a 0-byte file.
"""

import hashlib
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
GAMES = ROOT / "catalog" / "games"
PROBE = "rom_gate_probe"

RUNNER = ROOT / "tools" / "RomCheck"


def run(cmd, **kw):
    return subprocess.run(cmd, capture_output=True, text=True, **kw)


def main():
    tmp = ROOT / "build" / "_romgate"
    tmp.mkdir(parents=True, exist_ok=True)

    good = tmp / "the-right-dump.gba"
    bad = tmp / "some-other-file.gba"
    good.write_bytes(b"pretend cartridge dump " * 4096)
    bad.write_bytes(b"a different file entirely " * 4096)

    md5 = hashlib.md5(good.read_bytes()).hexdigest()
    print("probe hash: %s\n" % md5)

    manifest = GAMES / (PROBE + ".json")
    manifest.write_text(json.dumps({
        "id": PROBE,
        "display_name": "ROM gate probe",
        "subtitle": "test",
        "platform": "GBA",
        "version": "0.0.0",
        "ap_world_name": "ROM gate probe",
        "_ap_world_name_source": "throwaway test fixture",
        "description": "Throwaway fixture for tools/rom_gate_test.py.",
        "_description_source": "throwaway test fixture",
        "checks_verified": False,
        "requires": "own_copy",
        "_requires_source": "throwaway test fixture",
        # The point of the whole test: a hash, and NO measured size.
        "rom": {"description": "the right dump", "md5": [md5], "size": None},
    }, indent=2), encoding="utf-8")

    failures = 0
    try:
        r = run([sys.executable, str(ROOT / "tools" / "build_plugins.py"), PROBE])
        if r.returncode != 0:
            print(r.stdout + r.stderr)
            return 1

        r = run(["dotnet", "build", str(RUNNER), "-c", "Release", "--nologo",
                 "-v", "quiet"])
        if r.returncode != 0:
            print("RomCheck build failed:\n" + (r.stdout or r.stderr)[-800:])
            return 1

        exe = next((RUNNER / "bin" / "Release").rglob("RomCheck.exe"), None)
        dll = next((ROOT / "build" / PROBE / "bin" / "Release").rglob(
            "CatalogRomGateProbe.dll"), None)
        if exe is None or dll is None:
            print("could not find RomCheck.exe or the probe plugin")
            return 1

        for path, expect in ((good, "accept"), (bad, "reject")):
            print("--- offering %s, expecting %s" % (path.name, expect))
            r = run([str(exe), str(dll), str(path), "--expect", expect])
            print(r.stdout.rstrip() or r.stderr.rstrip())
            failures += r.returncode != 0
            print()
    finally:
        manifest.unlink(missing_ok=True)
        shutil.rmtree(ROOT / "build" / PROBE, ignore_errors=True)
        for f in (ROOT / "dist").glob(PROBE + "-*.londonplugin"):
            f.unlink()

    print("%d failures" % failures)
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
