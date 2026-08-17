# -*- coding: utf-8 -*-
"""Answer patch_extension for a game, by reading the world itself.

    py -3.13 tools/audit_patch_extension.py                 # every mapped game
    py -3.13 tools/audit_patch_extension.py mmbn3 smw

An Archipelago world may override the launcher's patch steps in Python
(APPatchExtension). The patch container does not say so -- it lists ordinary
step names -- so a world that reuses a generic name with different behaviour
makes our C# patcher produce a silently corrupt ROM. That is not theoretical:
Pokemon FireRed's rev0/rev1 dispatch nearly shipped exactly that way.

catalog/SCHEMA.md therefore requires every manifest to answer, and the honest
answer when nobody has looked is "unaudited". This tool does the looking.

WHY IT READS BYTECODE
---------------------
Archipelago ships worlds COMPILED: every .apworld holds .pyc and no .py. There
is no source to read locally, and fetching each world's repository would be a
different (and slower) question. A .pyc still carries the class names, the
method names and the string constants -- which is exactly what the answer needs
-- so the code object is loaded and walked directly.

⚠ MUST run on the same Python the worlds were compiled with (3.13 for the
current Archipelago). marshal refuses another version's bytecode, and this tool
says so rather than guessing.
"""

import argparse
import importlib.util
import io
import json
import marshal
import os
import sys
import types
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
GAMES = ROOT / "catalog" / "games"
AP = Path(os.environ.get("ARCHIPELAGO_DIR", r"C:\ProgramData\Archipelago"))
# Bundled worlds live in lib/worlds; anything the player added by hand lands in
# custom_worlds. Searching only the first missed Pokemon FireRed and Crystal --
# the two games we already KNEW carry patch extensions.
WORLD_DIRS = [AP / "lib" / "worlds", AP / "custom_worlds"]

# A world the player has not installed can still be audited: every manifest
# records where its author publishes it. Point --worlds at a folder of
# downloaded .apworld files and they are searched first.
EXTRA_DIR = None

# The step names our own patcher implements. A world that defines a method with
# one of these names has REDEFINED it -- the dangerous class.
GENERIC_STEPS = {
    "apply_bsdiff4", "apply_tokens", "copy", "calc_snes_crc",
    "apply_ips", "apply_xdelta",
}


def load_code(data, where):
    """Unmarshal a .pyc, or explain why not."""
    if len(data) < 16:
        return None, "too short to be a .pyc"
    if data[:4] != importlib.util.MAGIC_NUMBER:
        return None, ("compiled for another Python (%s, this one is %s) -- rerun "
                      "with the matching interpreter"
                      % (data[:4].hex(), importlib.util.MAGIC_NUMBER.hex()))
    try:
        return marshal.loads(data[16:]), None
    except Exception as exc:
        return None, "unreadable: %s" % exc


def walk(code, out):
    """Every nested code object, so class bodies are reached too."""
    out.append(code)
    for c in code.co_consts:
        if isinstance(c, types.CodeType):
            walk(c, out)


def subclasses_of_extension(blocks):
    """Class bodies whose BASE is APPatchExtension.

    ⚠ Not "a class with a method named like a patch step". Castlevania: Circle
    of the Moon has an ordinary helper class `RomData` with its own
    `apply_ips(self, filename)` -- nothing to do with the patch protocol -- and
    a looser rule reported it as a redefined generic step, which would have
    written a false verdict into the manifest.

    A class statement compiles to: push __build_class__, push the class body as
    a code const, push its name, then push each BASE. So a base named
    APPatchExtension appearing in the instructions shortly after a class code
    object is what identifies a real subclass.
    """
    import dis
    found = []
    for code in blocks:
        ins = list(dis.get_instructions(code))
        for i, op in enumerate(ins):
            if not (op.opname == "LOAD_CONST"
                    and isinstance(op.argval, types.CodeType)):
                continue
            # The bases follow within a handful of instructions.
            window = ins[i + 1:i + 8]
            if any(w.opname in ("LOAD_NAME", "LOAD_GLOBAL", "LOAD_ATTR",
                                "LOAD_DEREF")
                   and w.argval == "APPatchExtension" for w in window):
                found.append(op.argval)
    return found


def inspect_source(files):
    """Read .py directly. Unofficial worlds ship source, and source is better
    evidence than bytecode -- the base class is written out in the class
    statement rather than inferred from a name lookup."""
    import re
    hits = []
    for name, data in files:
        text = data.decode("utf-8", "replace")
        if "APPatchExtension" not in text:
            continue
        for cls in re.finditer(
                r"class\s+(\w+)\s*\(([^)]*APPatchExtension[^)]*)\)\s*:", text):
            # Only this class's body: everything up to the next top-level
            # "class " line, so a later class's methods are not attributed here.
            body = text[cls.end():].split("\nclass ")[0]
            methods = re.findall(r"\n\s+def\s+(\w+)", body)
            hits.append((name, cls.group(1), sorted(set(methods))))
    return hits


def inspect_world(path):
    """Return (verdict, detail). Reads a directory or an .apworld zip."""
    files, sources = [], []
    if path.is_dir():
        for p in sorted(path.rglob("*.pyc")):
            files.append((str(p.relative_to(path)), p.read_bytes()))
        for p in sorted(path.rglob("*.py")):
            sources.append((str(p.relative_to(path)), p.read_bytes()))
    else:
        with zipfile.ZipFile(path) as z:
            for n in sorted(z.namelist()):
                if n.endswith(".pyc"):
                    files.append((n, z.read(n)))
                elif n.endswith(".py"):
                    sources.append((n, z.read(n)))

    # Source first: it says outright what the class inherits from.
    if sources:
        hits = inspect_source(sources)
        if not hits:
            return "none", "no APPatchExtension in %d source file(s)" % len(sources)
        return verdict_from(hits)

    if not files:
        return "unaudited", "neither .py nor .pyc inside %s" % path.name

    hits = []          # (file, class name, [method names])
    unreadable = []
    for name, data in files:
        code, err = load_code(data, name)
        if code is None:
            unreadable.append("%s (%s)" % (name, err))
            continue

        blocks = []
        walk(code, blocks)

        for cls_code in subclasses_of_extension(blocks):
            methods = sorted(k.co_name for k in cls_code.co_consts
                             if isinstance(k, types.CodeType))
            hits.append((name, cls_code.co_name, methods))

    if unreadable and not hits:
        return "unaudited", "; ".join(unreadable[:2])

    if not hits:
        return "none", "no APPatchExtension in %d compiled file(s)" % len(files)

    return verdict_from(hits)


def verdict_from(hits):
    """Redefining a generic step is the dangerous class; adding a new name is
    the safe one, because our patcher refuses an unknown step loudly."""
    redefined, added = set(), set()
    for _, _, methods in hits:
        for m in methods:
            if m.startswith("__"):
                continue
            (redefined if m in GENERIC_STEPS else added).add(m)

    where = ", ".join(sorted({"%s:%s" % (f, cls) for f, cls, _ in hits}))
    if redefined:
        return "replicated", ("REDEFINES generic steps %s (also adds %s) in %s "
                              "-- must be mirrored in ApPatch.cs before use"
                              % (sorted(redefined), sorted(added) or "nothing", where))
    return "refused", "adds new steps %s in %s" % (sorted(added), where)


# The world folders are named by their own authors, and the names are neither
# our id nor the display name -- the same trap as lua_module. Guessing from the
# id alone silently reports "no world installed" for a game that is right there,
# which reads as "nothing to audit" instead of "the lookup failed".
ALIASES = {
    "mario_luigi_superstar_saga": "mlss",
    "castlevania_cotm":           "cvcotm",
    "castlevania_hod":            "cvhod",
    "mega_man_2":                 "mm2",
    "super_metroid":              "sm",
    "final_fantasy":              "ff1",
    "legend_of_zelda":            "tloz",
    "yugioh_2006":                "yugioh06",
    "yoshis_island":              "yoshisisland",
    "metroid_zero_mission":       "mzm",
    "minish_cap":                 "tmc",
    "wario_land_4":               "wl4",
    "golden_sun_tla":             "gstla",
    "fire_emblem_sacred_stones":  "fe8",
    "metroid_fusion":             "mf",
}


def find_world(m):
    """Locate the installed world for a manifest, by alias, id or AP name."""
    names = [ALIASES.get(m["id"]), m["id"]]
    guess = m["ap_world_name"].lower().replace(" ", "").replace("'", "")
    names += [guess, guess.replace(":", ""), m["id"].replace("_", "")]

    roots = ([EXTRA_DIR] if EXTRA_DIR else []) + WORLD_DIRS
    for root in roots:
        if not root.exists():
            continue
        for n in [x for x in names if x]:
            for cand in (root / n, root / (n + ".apworld")):
                if cand.exists():
                    return cand
        # Loose match: yugioh_2006 against a file called yugioh06.
        stem = m["id"].replace("_", "")
        for p in sorted(root.iterdir()):
            if p.name.replace("_", "").replace(".apworld", "").lower() == stem:
                return p
    return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("games", nargs="*")
    ap.add_argument("--worlds", metavar="DIR",
                    help="a folder of .apworld files downloaded from their "
                         "authors' own releases, searched before the install")
    ap.add_argument("--write", action="store_true",
                    help="update patch_extension in the manifests")
    args = ap.parse_args()

    global EXTRA_DIR
    if args.worlds:
        EXTRA_DIR = Path(args.worlds)

    if not any(d.exists() for d in WORLD_DIRS) and not EXTRA_DIR:
        print("no Archipelago worlds under %s -- set ARCHIPELAGO_DIR" % AP)
        return 1

    paths = sorted(GAMES.glob("*.json"))
    if args.games:
        paths = [p for p in paths if p.stem in args.games]

    changed = 0
    for p in paths:
        m = json.loads(p.read_text(encoding="utf-8"))
        world = find_world(m)
        if world is None:
            print("  %-28s %-11s no world installed locally" % (m["id"], "-"))
            continue

        verdict, detail = inspect_world(world)
        было = m.get("patch_extension")
        mark = " " if verdict == было else "*"
        print("%s %-28s %-11s %s" % (mark, m["id"], verdict, detail[:96]))

        if args.write and verdict != было:
            # 'replicated' is a CLAIM about our C# side, not something a scan
            # may assert on its own -- it stays unaudited until a human has
            # mirrored the steps and said so.
            if verdict == "replicated":
                continue
            m["patch_extension"] = verdict
            m["_patch_extension_source"] = (
                "read from the compiled world at %s: %s" % (world.name, detail))
            p.write_text(json.dumps(m, indent=2, ensure_ascii=False) + "\n",
                         encoding="utf-8")
            changed += 1

    if args.write:
        print("\n%d manifest(s) updated" % changed)
    return 0


if __name__ == "__main__":
    sys.exit(main())
