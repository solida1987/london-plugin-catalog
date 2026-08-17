# Game Boy Advance

18 games on the list. **16 built**, 2 cannot be built.

## The method changed here: ask the code, not the guide

The first five manifests were written from the setup guides on archipelago.gg.
That turned out to be the weaker source. Every Archipelago world declares, in
its own Python, the exact dumps it accepts and the patch extension it produces
— and **the code is what runs**.

Reading the code instead found three things the guides did not give us:

1. **All five bundled games have an MD5 the guide never mentioned.** The gap
   that said "we can only warn, not reject" was never real; we were reading the
   wrong file. 15 of 16 games can now reject a wrong ROM outright.
2. **Harmony of Dissonance's guide states the wrong patch extension** —
   `.apcvcotm`, which belongs to Circle of the Moon. The same author wrote both
   worlds, so it looks like a copy-paste slip. The code says `.apcvhodis`.
3. **Metroid Fusion contradicts itself** — see its manifest note.

---

## Built

| Game | AP name | Patch | Dumps accepted |
|---|---|---|---|
| Castlevania: Circle of the Moon | `Castlevania - Circle of the Moon` | `.apcvcotm` | 2 |
| Castlevania: Harmony of Dissonance | `Castlevania: Harmony of Dissonance` | `.apcvhodis` | 2 |
| Final Fantasy Tactics Advance | `Final Fantasy Tactics Advance` | `.apffta` | 1 |
| Fire Emblem: The Sacred Stones | `Fire Emblem Sacred Stones` | `.apfe8` | 1 |
| Golden Sun: The Lost Age | `Golden Sun The Lost Age` | `.apgstla` | 1 |
| KINGDOM HEARTS Chain of Memories | `Kingdom Hearts Chain of Memories` | — | **0** |
| Mario & Luigi: Superstar Saga | `Mario & Luigi Superstar Saga` | `.apmlss` | 1 |
| Metroid Fusion | `Metroid Fusion` | `.apmetfus` | 2 |
| Metroid: Zero Mission | `Metroid: Zero Mission` | `.apmzm` | 2 |
| Mega Man Battle Network 3 Blue | `MegaMan Battle Network 3` | `.apbn3` | 1 |
| Pokemon Emerald | `Pokemon Emerald` | `.apemerald` | 1 |
| Pokemon FireRed and LeafGreen | `Pokemon FireRed and LeafGreen` | `.apfirered` / `.apleafgreen` | 4 |
| The Legend of Zelda: The Minish Cap | `The Minish Cap` | `.aptmc` | 1 |
| Wario Land 4 | `Wario Land 4` | `.apwl4` | 1 |
| Yu-Gi-Oh! Ultimate Masters: WCT 2006 | `Yu-Gi-Oh! 2006` | `.apygo06` | 1 |
| Yu-Gi-Oh! Dungeon Dice Monsters | `Yu-Gi-Oh! Dungeon Dice Monsters` | `.apygoddm` | 1 |

"Dumps accepted" is how many distinct MD5s the world takes. Several games
legitimately accept more than one — an original cartridge dump and a re-release
rip are different files of the same game.

**The group's BizHawk floor is 2.9.1.** Requirements differ per game (2.7 to
2.9.1) and six worlds state none at all; the highest wins for anyone installing
a single emulator.

---

## Size is not measured — and that is fine

No manifest carries a file size. London reads size `0` as **"size unknown"** and
matches on MD5 alone, which is the stronger check.

That behaviour had to be built: `ValidateBaseRom` filtered on size *first*, so
an entry with a hash and no size would have rejected every ROM while telling the
player we expected a 0-byte file. `tools/rom_gate_test.py` now proves both
directions against a throwaway plugin whose accepted hash is a file we just
created — the right file is accepted, any other is refused.

---

## The gaps

### KINGDOM HEARTS Chain of Memories declares no ROM at all

Its world has no `rom.py`, no patch extension and no base-ROM hash — it does not
patch a ROM. London therefore **cannot verify the player's file** for this one
game, and says so rather than pretending.

### `checks_verified` is `false` everywhere

No RAM map has been measured in-game yet, so London warns at launch. That is the
correct state, not a shortcoming to hide.

### Two games cannot be built

- **Sonic Battle** — `Happyhappyism/Archipelago` has only a `main` branch and no
  Sonic world in the tree; the apworld ships as a release asset only, so there is
  no source to read.
- **Yu-Gi-Oh GX Duel Academy** — the source lives only in a Discord thread we
  have no access to.

---

## Where each world lives

Five ship with Archipelago. The other eleven live in other people's
repositories, and **four are on a branch rather than main**:

| Game | Repository | Branch |
|---|---|---|
| Harmony of Dissonance | `LiquidCat64/LiquidCatipelago` | `CVHoDis` |
| Final Fantasy Tactics Advance | `spicynun/Archipelago` | `ffta` |
| Fire Emblem: The Sacred Stones | `CT075/Archipelago` | `fe8/stable` |
| Golden Sun: The Lost Age | `cjmang/Archipelago` | `gstla` |
| The Minish Cap | `eternalcode0/Archipelago` | `feat/new-game-minish-cap` |
| Metroid Fusion | `StalledStorm/ArchipelagoMine` | `metroidfusion` |
| Pokemon FireRed/LeafGreen | `vyneras/Archipelago` | `frlg-stable` |
| KH Chain of Memories | `gaithernOrg/ArchipelagoKHCOM` | `main` |
| Yu-Gi-Oh! DDM | `JustinMarshall98/Archipelago` | `main` |
| Metroid: Zero Mission | `lilDavid/Archipelago-Metroid-Zero-Mission` | `main` |
| Wario Land 4 | `lilDavid/Archipelago-Wario-Land-4` | `main` |

Trust level is London's own lookup, never the package's claim — see
`Multiworld-Launcher/Core/Plugins/PluginProvenance.cs`.

---

## How to find a fork's world

Forks of Archipelago contain all ~89 upstream worlds plus the one they add.
Diffing the fork's `worlds/` against upstream's isolates it in one step.

Guessing by keyword does not work: searching `JustinMarshall98/Archipelago` for
"dice" matched **Yacht Dice**, not Dungeon Dice Monsters. The diff gave
`worlds/yugiohddm` with no ambiguity.

---

## How they were built

```
python tools/group.py GBA           # what is in the group
python tools/group_check.py GBA     # step 2: mistakes shared by the WHOLE group
python tools/build_plugins.py       # build and pack each one
python tools/rom_gate_test.py       # prove the ROM gate accepts and refuses
python tools/checksums.py           # SHA256SUMS.txt, verified against itself
```

There is no game-specific C#. Each plugin is the same shared
`plugin/GenericEmulatorPlugin.cs` plus the game's own manifest, embedded in the
assembly. A new game is a data file.
