# -*- coding: utf-8 -*-
"""Build every emulator-bridge extension, and pack each one.

    python tools/build_extensions.py                 # build + pack all
    python tools/build_extensions.py --stage <dir>   # also install bizhawk there

The --stage step is the one that used to be missing. The launcher drives no
emulator of its own any more, so a package WITHOUT Extensions/bizhawk_bridge/
is a launcher that cannot start a single game -- and it looks completely
healthy until somebody presses Play. pack_launcher.py refuses such a package;
this is what puts the file there in the first place.

Everything else (SNI, Ship of Harkinian) is packed as a .londonextension for
the player to install themselves.
"""

import argparse
import hashlib
import io
import json
import os
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
EXTS = ROOT / "extensions"
DIST = ROOT / "dist"

# The one that ships pre-installed with the launcher, because without it a
# fresh copy cannot launch anything at all.
BUNDLED = "bizhawk"


def discover():
    """Every extensions/<name>/ that has a manifest and exactly one project."""
    found = []
    for d in sorted(p for p in EXTS.iterdir() if p.is_dir()):
        manifest = d / "extension.json"
        projects = list(d.glob("*.csproj"))
        if not manifest.is_file():
            print("  %-10s no extension.json - skipped" % d.name)
            continue
        if len(projects) != 1:
            print("  %-10s expected one .csproj, found %d - skipped"
                  % (d.name, len(projects)))
            continue
        found.append((d, projects[0], json.loads(manifest.read_text(encoding="utf-8"))))
    return found


def build(project):
    r = subprocess.run(["dotnet", "build", str(project), "-c", "Release",
                        "--nologo", "-v", "quiet"], capture_output=True, text=True)
    if r.returncode != 0:
        lines = ((r.stdout or "") + (r.stderr or "")).splitlines()
        real = [l.strip() for l in lines if " error " in l]
        return " / ".join(real[:2]) or "build failed"
    return None


def payload(folder, m):
    """The three files BridgeRegistry reads. Nothing else goes in the package.

    Same whitelist discipline as pack_plugin.py: building against the launcher
    drops a copy of the launcher's own assembly beside the output, and shipping
    that would make IEmulatorBridge in the extension a DIFFERENT type from the
    host's -- the cast in BridgeRegistry would then fail on a class that
    visibly implements the interface.
    """
    asm = m["assembly"]
    stem = os.path.splitext(asm)[0]
    out = None
    for cand in (folder / "bin" / "Release").rglob(asm):
        out = cand.parent
        break
    if out is None:
        return None, "no build output"

    files = [(out / asm, asm), (folder / "extension.json", "extension.json")]
    deps = out / (stem + ".deps.json")
    if deps.is_file():
        files.append((deps, stem + ".deps.json"))
    return files, None


def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument("--stage", help="launcher folder to install the bundled "
                                    "bridge into (its Extensions/ subfolder)")
    args = ap.parse_args(argv[1:])

    DIST.mkdir(exist_ok=True)
    results = []
    staged = None

    for folder, project, m in discover():
        err = build(project)
        if err:
            results.append((folder.name, "BUILD FAILED", err))
            continue

        files, err = payload(folder, m)
        if err:
            results.append((folder.name, "NO OUTPUT", err))
            continue

        dst = DIST / ("%s-%s.londonextension" % (m["extensionId"], m["version"]))
        with zipfile.ZipFile(dst, "w", zipfile.ZIP_DEFLATED) as z:
            for src, name in files:
                z.write(src, name)

        note = ""
        if folder.name == BUNDLED and args.stage:
            # Installed, not just copied: the folder name must be the
            # extensionId, because that is what BridgeRegistry walks.
            target = Path(args.stage) / "Extensions" / m["extensionId"]
            if target.exists():
                shutil.rmtree(target)
            target.mkdir(parents=True)
            for src, name in files:
                shutil.copy2(src, target / name)
            staged = target
            note = "staged into the launcher"

        results.append((folder.name, "OK", note or dst.name))

    print()
    width = max((len(r[0]) for r in results), default=8)
    for name, status, note in results:
        print("  %-*s  %-13s %s" % (width, name, status, note))

    bad = [r for r in results if r[1] != "OK"]
    print("\n%d/%d built" % (len(results) - len(bad), len(results)))

    if args.stage:
        if staged is None:
            print("\nthe bundled \"%s\" bridge was NOT staged -- a launcher "
                  "package built now could not start any game" % BUNDLED)
            return 1
        print("\nstaged: %s" % staged)
        for f in sorted(staged.iterdir()):
            print("   %-34s %8d bytes" % (f.name, f.stat().st_size))

    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
