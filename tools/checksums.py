# -*- coding: utf-8 -*-
"""Write dist/SHA256SUMS.txt in the format `sha256sum -c` actually accepts.

    python tools/checksums.py

This exists because writing it by hand on Windows produced a file that looked
right and was useless: PowerShell's Out-File writes CRLF, so `sha256sum -c`
reads every filename with a trailing \\r and reports "No such file or
directory" for a file sitting right next to it.

A checksums file that does not verify is worse than none -- it tells the
reader the download was checked when it was not. So the newline is written
explicitly, and this script verifies its own output before returning.
"""

import hashlib
import io
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DIST = ROOT / "dist"
OUT = DIST / "SHA256SUMS.txt"


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def main():
    files = sorted(DIST.glob("*.londonplugin"))
    if not files:
        print("no packages in dist/ -- run tools/build_plugins.py first")
        return 1

    # newline="" keeps Python from translating \n to \r\n on Windows. Two
    # spaces between hash and name is the coreutils format.
    with io.open(OUT, "w", encoding="ascii", newline="") as f:
        for p in files:
            f.write("%s  %s\n" % (sha256(p), p.name))

    # Read it back the way the verifying tool will, and re-check every line.
    # A gate that has never been run against its own output is not a gate.
    raw = io.open(OUT, "rb").read()
    if b"\r" in raw:
        print("SHA256SUMS.txt contains CR -- sha256sum -c would reject it")
        return 1

    bad = 0
    for line in raw.decode("ascii").splitlines():
        want, name = line.split("  ", 1)
        got = sha256(DIST / name)
        ok = got == want
        print("  [%s] %s" % ("ok  " if ok else "FAIL", name))
        bad += not ok

    print("\n%d files, %d mismatches" % (len(files), bad))
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
