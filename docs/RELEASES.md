# How plugins are released

The goal: a player finds their game in seconds, downloads one plugin, and the
launcher handles the rest.

---

## GitHub is the front page

The launcher has no browsable game list, and should not have one: the player
adds a plugin and the game appears in the list on the left. The more plugins,
the longer that list — the catalogue belongs on GitHub, not inside the
program.

`CatalogRepo/catalog.json` in the launcher is **not** used for this. It needs
rebuilding regardless.

That means **the releases page IS the reference work**, and it has to be
readable by a human looking for one particular game.

---

## The groups

One release per platform, with a **fixed tag** that gets updated rather than
replaced:

| Tag | Title |
|---|---|
| `gba` | Game Boy Advance |
| `snes` | Super Nintendo |
| `n64` | Nintendo 64 |
| `gb-gbc` | Game Boy and Game Boy Color |
| `nes` | Nintendo Entertainment System |
| `gc-wii` | GameCube and Wii |
| `ds` | Nintendo DS |
| `playstation` | PS1, PS2, PS3 |
| `sega` | Mega Drive and Master System |
| `pc` | PC |
| `web` | Web and PICO-8 |

Tags never change name. A new GBA plugin updates the `gba` release; there is
no `gba-2`.

## The problem with "latest release"

With grouped releases, the top release becomes whichever one you touched last,
not the most important one. GitHub always highlights one as **Latest**, so
`Sega` ends up at the top purely because it was updated most recently.

**The fix: an index release that is always Latest.**

Tag `index`. It contains no plugins — only:

- a table of contents in the description: each platform, how many games, and a
  link to the group
- `plugins.json` — the whole list in machine-readable form, in case London or
  a tracker ever needs to look it up

Every time a group is updated, `index` is updated afterwards. That keeps it
newest, and **Latest** always points at the overview rather than at an
arbitrary platform.

It is also the right page to land on from a README or a message: one address
that always shows the whole picture.

---

## What a group release says

```
Game Boy Advance — 18 games

| Game | Version | Requires |
|---|---|---|
| Pokémon Emerald | 1.0.0 | your own ROM |
| Metroid Fusion  | 1.0.0 | your own ROM |
| …

Each plugin is added in the launcher with Add plugin.
Every GBA game requires you to own the game yourself.
```

Assets: one `.londonplugin` per game, plus a `SHA256SUMS.txt` so a file can be
checked without installing it.

---

## The order for a group

0. **Read the world's own code, not its setup guide.** Every Archipelago world
   declares the dumps it accepts and the patch extension it produces in its own
   Python (`rom.py` / `client.py` / `__init__.py`). The guide is a summary that
   can be stale or wrong — Harmony of Dissonance's guide states Circle of the
   Moon's patch extension — and the code is what runs. For a fork, diff its
   `worlds/` against upstream's to find which world it adds; keyword guessing
   matched *Yacht Dice* when we wanted *Dungeon Dice Monsters*.
1. **Map** every game in the group — a manifest with a source on every field,
   see `catalog/SCHEMA.md`
2. **Double-check the group as a whole**: do two games point at the same
   client? do they need the same emulator version? is anyone missing a hash?
3. **Build** the plugin for each game and run PluginCheck on every one, then
   `tools/rom_gate_test.py` to prove the ROM gate still accepts the right file
   and refuses everything else
4. **Checksums** with `tools/checksums.py` — never by hand. Writing the file
   with PowerShell produced CRLF line endings, which made `sha256sum -c`
   report "No such file or directory" for a file sitting right beside it. A
   checksums file that does not verify is worse than none, because it tells
   the reader the download was checked when it was not.
5. **Release** the group, then update `index` afterwards so it is newest

Step 2 is not a formality. Mistakes in this kind of data are the same across a
group — get one GBA game wrong and there is a good chance the other 17 carry
the same mistake. `tools/group_check.py` exists for exactly this.

---

## When the repo opens

Private until there are plugins that work. When it opens:

- the README gets a table of every group with counts
- the `index` release is the landing page
- each plugin carries its trust level, which London looks up itself — see
  `Multiworld-Launcher/Core/Plugins/PluginProvenance.cs`. A plugin cannot
  declare itself trusted

---

## Language

Everything in this repository is written in **English** — documentation, code
comments, manifest `_source` notes, field values and release notes. It matches
the three public repositories.
