# -*- coding: utf-8 -*-
"""Byg et .londonplugin ud af hvert manifest i catalog/games/.

    python tools/build_plugins.py            # alle manifester
    python tools/build_plugins.py pokemon_emerald

Hvert spil faar sit eget lille projekt, der linker den DELTE
plugin/GenericEmulatorPlugin.cs ind og indlejrer sit eget manifest. Derfor er
entryType det samme klassenavn i alle pakker: hver assembly baerer praecis eet
game.json, saa klassen kan ikke komme til at laese et andet spils manifest.

Manifestet indlejres frem for at ligge loest ved siden af, fordi launcherens
pack_plugin.py med vilje kun hvidlister assembly + deps + plugin.json.
"""

import json
import os
import re
import subprocess
import sys
from pathlib import Path
from xml.sax.saxutils import escape as xml_escape

ROOT = Path(__file__).resolve().parent.parent
GAMES = ROOT / "catalog" / "games"
BUILD = ROOT / "build"
DIST = ROOT / "dist"
SHARED = ROOT / "plugin" / "GenericEmulatorPlugin.cs"
LAUNCHER = ROOT.parent / "Multiworld-Launcher" / "LauncherV2.csproj"
PACKER = ROOT.parent / "Multiworld-Launcher" / "Tools" / "pack_plugin.py"

ENTRY = "LauncherV2.Plugins.Catalog.GenericEmulatorPlugin"
AUTHOR = "Solida Games"

CSPROJ = """<Project Sdk="Microsoft.NET.Sdk">

  <!-- Genereret af tools/build_plugins.py ud fra catalog/games/{gid}.json. -->
  <!-- Ret manifestet, ikke denne fil.                                      -->

  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <UseWPF>true</UseWPF>
    <AssemblyName>{asm}</AssemblyName>
    <RootNamespace>LauncherV2.Plugins.Catalog</RootNamespace>
    <EnableDynamicLoading>true</EnableDynamicLoading>
    <Version>{ver}</Version>
    <Company>Solida Games</Company>
    <Product>{name} plugin for Multiworld Launcher</Product>
  </PropertyGroup>

  <ItemGroup>
    <!-- Private=false: launcheren har typerne i forvejen. En kopi her ville  -->
    <!-- gore IGamePlugin i pluginet til en ANDEN type end i vaerten.         -->
    <ProjectReference Include="{launcher}" Private="false" ExcludeAssets="runtime" />
  </ItemGroup>

  <ItemGroup>
    <Compile Include="{shared}" Link="GenericEmulatorPlugin.cs" />
    <EmbeddedResource Include="game.json" />
  </ItemGroup>

</Project>
"""


def asm_name(gid):
    return "Catalog" + "".join(p.title() for p in gid.split("_"))


def check(m, gid):
    """Manifestet skal kunne bygges. Et gaet er vaerre end et nej."""
    problems = []
    for field in ("id", "display_name", "platform"):
        if not m.get(field):
            problems.append("mangler %s" % field)
    if m.get("id") != gid:
        problems.append("id=%r matcher ikke filnavnet %r" % (m.get("id"), gid))
    if not re.match(r"^[a-z0-9][a-z0-9_]{1,63}$", m.get("id") or ""):
        problems.append("id har ikke lovlig form (a-z, 0-9, _)")

    rom = m.get("rom") or {}
    if rom and not rom.get("size") and not rom.get("md5"):
        # Ikke en fejl - men det skal staa i rapporten, ikke skjules.
        problems.append("NOTE: hverken size eller md5 - kan kun advare, ikke afvise")
    return problems


def build_one(path, results):
    gid = path.stem
    m = json.loads(path.read_text(encoding="utf-8"))

    problems = check(m, gid)
    hard = [p for p in problems if not p.startswith("NOTE:")]
    if hard:
        results.append((gid, "AFVIST", "; ".join(hard)))
        return

    asm = asm_name(gid)
    ver = m.get("version", "1.0.0")
    proj = BUILD / gid
    proj.mkdir(parents=True, exist_ok=True)

    # Kun de felter pluginet faktisk laeser. _source-felterne bliver i
    # kartoteket - de er dokumentation for OS, ikke for programmet.
    embedded = {k: m[k] for k in
                ("id", "display_name", "subtitle", "platform", "ap_world_name",
                 "description", "checks_verified")
                if k in m}
    if m.get("rom"):
        embedded["rom"] = {k: m["rom"].get(k)
                           for k in ("description", "size", "md5")
                           if m["rom"].get(k) is not None}
    (proj / "game.json").write_text(
        json.dumps(embedded, indent=2, ensure_ascii=False), encoding="utf-8")

    # ⚠ Spilnavnet gaar ind i XML. "Mario & Luigi" med et bart & er ulovligt
    # XML, og MSBuild doer FOER compileren starter - en fejl der ikke ligner
    # en C#-fejl overhovedet. Alt der stammer fra manifestet escapes.
    (proj / (asm + ".csproj")).write_text(CSPROJ.format(
        gid=gid, asm=asm, ver=ver, name=xml_escape(m["display_name"]),
        launcher=os.path.relpath(LAUNCHER, proj),
        shared=os.path.relpath(SHARED, proj)), encoding="utf-8")

    (proj / "plugin.json").write_text(json.dumps({
        "apiVersion": 2,
        "gameId": gid,
        "displayName": m["display_name"],
        "subtitle": m.get("subtitle", ""),
        "version": ver,
        "author": AUTHOR,
        "authorContact": "https://github.com/solida1987/london-plugin-catalog",
        "assembly": asm + ".dll",
        "entryType": ENTRY,
        "declares": {
            "installsFiles": True,
            "downloadsFrom": [],
            "runsExternalProcess": True,
            "connectsToAp": True,
            "requiresOriginalGame": m.get("requires") == "egen_fil",
        },
        "rulesAcknowledged": True,
    }, indent=2, ensure_ascii=False), encoding="utf-8")

    r = subprocess.run(["dotnet", "build", str(proj), "-c", "Release", "--nologo",
                        "-v", "quiet"], capture_output=True, text=True)
    if r.returncode != 0:
        # De sidste linjer er "Time Elapsed" og reklame for workloads. Find de
        # linjer der faktisk siger hvad der gik galt.
        lines = ((r.stdout or "") + "\n" + (r.stderr or "")).splitlines()
        real = [l.strip() for l in lines
                if " error " in l or l.strip().startswith("error")]
        results.append((gid, "BYG FEJLEDE",
                        " / ".join(real[:2]) if real
                        else " / ".join(l.strip() for l in lines[-4:] if l.strip())))
        return

    r = subprocess.run([sys.executable, str(PACKER), str(proj), "-o", str(DIST)],
                       capture_output=True, text=True)
    if r.returncode != 0:
        results.append((gid, "PAK FEJLEDE", (r.stdout or r.stderr).strip()[-200:]))
        return

    note = [p for p in problems if p.startswith("NOTE:")]
    results.append((gid, "OK", note[0] if note else ""))


def main(argv):
    if not SHARED.is_file():
        print("mangler %s" % SHARED)
        return 1

    wanted = argv[1:]
    paths = sorted(GAMES.glob("*.json"))
    if wanted:
        paths = [p for p in paths if p.stem in wanted]
    if not paths:
        print("ingen manifester at bygge")
        return 1

    DIST.mkdir(exist_ok=True)
    results = []
    for p in paths:
        print("bygger %s ..." % p.stem)
        build_one(p, results)

    print()
    width = max(len(g) for g, _, _ in results)
    for gid, status, note in results:
        print("  %-*s  %-12s %s" % (width, gid, status, note))

    bad = [r for r in results if r[1] != "OK"]
    print("\n%d/%d bygget" % (len(results) - len(bad), len(results)))
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
