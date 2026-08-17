# Game Boy Advance

18 games on the list. **5 built**, 12 still to map, 1 cannot be built.

Everything below is copied from the game's own setup guide on archipelago.gg.
Nothing is guessed — what the guide did not state is written down as a gap
below rather than invented.

---

## Built

| Game | AP name | Patch | BizHawk |
|---|---|---|---|
| Castlevania: Circle of the Moon | `Castlevania - Circle of the Moon` | `.apcvcotm` | 2.7+ |
| Mario & Luigi: Superstar Saga | `Mario & Luigi Superstar Saga` | `.apmlss` | 2.9.1 recommended |
| Mega Man Battle Network 3 Blue | `MegaMan Battle Network 3` | `.apbn3` | 2.7.0+ |
| Pokémon Emerald | `Pokemon Emerald` | `.apemerald` | 2.7+ |
| Yu-Gi-Oh! Ultimate Masters: WCT 2006 | `Yu-Gi-Oh! 2006` | `.apygo06` | 2.7.0+ |

The AP names are **confirmed word for word** against the game list on
archipelago.gg/games (2026-08-17). They were derived from the tutorial URL
first and then cross-checked — a wrong AP name is the mistake that first shows
up when the player is holding a slot the server does not recognise.

**The group's BizHawk floor is 2.9.1**, not 2.7. The requirements differ per
game, and the highest one wins for anyone installing a single emulator.

---

## The gaps — what we do NOT know

### 1. None of the setup guides state a checksum

This is true for all five, which is why it is recorded here as a property of
the group rather than as five separate footnotes. The consequence is exact:

> The plugin can **warn** about a wrong ROM. It cannot **reject** one.

The plugin says so plainly in the dialog rather than pretending the file was
approved. The field gets closed once we can measure a dump and cross-check it
against BizHawk's own `gamedb_gba.txt`.

### 2. `checks_verified` is `false` on all five

The RAM map has not been measured in-game yet. London **warns at launch**, so
nobody sits in a multiworld for an hour wondering why no checks arrive. That
is the correct state until the map is measured — not a shortcoming to hide.

### 3. Yu-Gi-Oh GX Duel Academy cannot be built

The source lives **only in a Discord thread** we have no access to. It is
recorded as a known limitation rather than quietly dropped.

---

## The 12 still to do

All have a public address, so they can be mapped the same way:

Castlevania: Harmony of Dissonance · Final Fantasy Tactics Advance ·
Fire Emblem: The Sacred Stones · Golden Sun: The Lost Age ·
KINGDOM HEARTS Chain of Memories · Metroid Fusion · Metroid: Zero Mission ·
Pokémon FireRed and LeafGreen · Sonic Battle ·
The Legend of Zelda: The Minish Cap · Wario Land 4 ·
Yu-Gi-Oh! Dungeon Dice Monsters

These live in **other people's** repositories, not in Archipelago itself, so
the trust level is different. London looks that up itself — see
`Multiworld-Launcher/Core/Plugins/PluginProvenance.cs`. A plugin cannot
declare itself trusted.

---

## How they were built

```
python tools/group.py GBA           # what is in the group
python tools/group_check.py GBA     # step 2: mistakes shared by the WHOLE group
python tools/build_plugins.py       # build and pack each one
```

There is no game-specific C#. Each plugin is the same shared
`plugin/GenericEmulatorPlugin.cs` plus the game's own manifest, embedded in
the assembly. A new game is a data file.

The manifest lives **inside** the DLL rather than beside it: the launcher's
`pack_plugin.py` deliberately whitelists only assembly + deps + `plugin.json`,
so a loose `game.json` would never reach the package.
