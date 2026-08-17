# London Plugin Catalog

One place for Multiworld Launcher plugins — so a player can download the
plugin for the game they actually want to play, and the launcher handles the
rest.

> **Private for now.** It lives here while it is being built.

**Start at the [index release](https://github.com/solida1987/london-plugin-catalog/releases/tag/index)** —
it is always the newest and lists every group.

---

## What the catalogue is

`catalog/index.json` holds **454 unofficial apworlds**, harvested from the
public index [Eijebong/Archipelago-index](https://github.com/Eijebong/Archipelago-index)
with `tools/harvest_index.py`.

**We do not copy anyone else's files.** The catalogue holds names, addresses
and version numbers — never content. If a player needs an apworld, the
launcher fetches it from the publisher's own address, exactly as the player
would have done.

| | |
|---|---|
| Games in the index | 454 |
| With a direct apworld address | 245 |
| Without — needs a manual lookup | 209 |

---

## The game list — 472 games with platform

`catalog/games.json` is the important file. It comes from the combined list
(`catalog/gamelist_raw.txt`), which is **richer than the machine-readable
index**: it says which platform each game runs on.

And the platform is what decides what a plugin is allowed to do.

| Source | Count |
|---|---|
| Direct URL | 382 |
| Ships with Archipelago | 76 |
| Only in a Discord thread | 14 |

## Three categories, not two

| The game is | The plugin may | Count |
|---|---|---|
| **Console** — SNES, N64, GBA, PSX … | only ask for the player's own ROM | **166** |
| **Free** — Web, PICO-8 | fetch the whole game | **12** |
| **PC** — Steam and free look identical | must be judged one at a time | **294** |

The 166 are classified **automatically**: if it says `(SNES)`, the game lives
on a cartridge the player owns, and the matter is settled without anyone
having to make a judgement call.

The 294 are nearly all `(PC)`. There is usually a third option here: the game
is owned on Steam, and the plugin installs **the AP developer's mod** into the
installation the player already has. That is neither fetching the game nor
asking for a ROM — and it is legitimate, because the mod is the developer's
own work.

That is why `requires` stays `unknown` for those 294 until each one has been
looked at. A guess here would become a plugin that fetches something it is not
allowed to.

The machinery for column 1 already exists: `RomRequirement` and
`AcceptableBaseRoms` in the launcher's `EmulatorPlugin` ask for the player's
own file and verify it by hash.

---

## Why we are not writing 454 plugins

That would be a year of work, and most of it would be the same code 454 times.

Most emulated games follow the **same recipe**: BizHawk, the generic Lua
connector, an apworld, and a RAM map saying where the checks are. Only the
last part is game-specific.

So the plan is **one data-driven plugin** rather than many hand-written ones:

```
game manifest (JSON per game)   ->   one shared GenericEmulatorPlugin
  name, system, apworld address
  the game: free or player's own file
  RAM map: address -> location id
```

Hand-written plugins are built only for games that do not fit the shape — like
Diablo II and OpenTTD, which each have their own.

See `catalog/SCHEMA.md` for the manifest, and `docs/RELEASES.md` for how
groups are built and published.

---

## Phases

**1 — the catalogue.** Done. 454 apworlds from the index, 472 games with
platform.

**2 — classification.** Done for 166 console games and 12 free ones, decided
automatically. Remaining: 294 PC titles — is the game owned on Steam (the
plugin installs a mod), or is it free (the plugin may fetch it)? Manual, once,
written down.

**3 — the generic plugin.** Done. `GenericEmulatorPlugin : EmulatorPlugin`
driven by a manifest, proven end-to-end on the GBA group.

**4 — RAM map per game.** The one thing that cannot be automated. It comes
once we know which games people actually want.

**5 — going public.** The repo opens when there are plugins that work.

---

## Progress

| Group | Built |
|---|---|
| [Game Boy Advance](https://github.com/solida1987/london-plugin-catalog/releases/tag/gba) | 5 / 18 |

---

## Rules

`REGELBOG.md` in the project root applies. In short:

- We never host anyone else's apworlds or game files — only addresses to them
- A plugin fetches a game only if the game is free
- Commercial games: the player supplies the file, and it is verified by hash
- No automatic download without the user agreeing to it

## Language

Everything in this repository is written in **English** — documentation, code
comments, manifest notes, field values and release notes.
