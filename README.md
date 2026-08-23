# London Plugin Catalog

Game plugins for the [Multiworld Launcher](https://github.com/solida1987/Multiworld-Launcher).
Pick your game, download its plugin, add it to the launcher.

**[→ Download plugins](https://github.com/solida1987/london-plugin-catalog/releases/tag/index)**

---

## What a plugin is

A plugin is a small file that teaches the launcher one game: which copy of the
game to look for, which emulator plays it, and how to read your progress so it
reaches the multiworld.

**It is not the game and it is not the randomiser.** Both of those are other
people's work, and both are named for every game in the list below.

| You need | Where it comes from |
|---|---|
| The game | Your own copy. Nothing here contains one, and none is ever downloaded. |
| The randomiser ("Archipelago world") | Its author — credited and linked per game below. |
| An emulator | The emulator's own project. The launcher can fetch it for you after showing you whose it is and under which licence, or you install it yourself. |
| The plugin | solida1987 (this repository), MIT. |

---

## Getting started

1. Install the [Multiworld Launcher](https://github.com/solida1987/Multiworld-Launcher/releases/latest).
2. Open the [plugin list](https://github.com/solida1987/london-plugin-catalog/releases/tag/index)
   and download the `.londonplugin` for your game.
3. In the launcher: **Add plugin** → pick the file → read what it declares → accept.
4. Open the game's page and point it at your own copy of the game.
5. Join a multiworld. The first time you join a given seed the launcher asks for
   that seed's patch (or its randomised game file) — once per seed, then never again.

---

## Finding a game

The catalogue is too long to keep by hand here, and a list that is copied by
hand goes stale. Two places carry it and stay current on their own:

- **[The plugin list](https://github.com/solida1987/london-plugin-catalog/releases/tag/index)**
  — every game, grouped by platform, generated from the catalogue itself.
- **The launcher's Plugin Library** — searchable, filterable by system and
  genre, and it installs with one press.

Each game is marked **tested** or **untested**. Tested means somebody has played
it against a real multiworld and watched the progress arrive. Untested means
everything is in place and nobody has tried it yet — if you play one, please say
how it went.

---

## Credits

Every randomiser in this catalogue was written by somebody else. They are the
reason these games can be played in a multiworld at all, and each plugin names
its game's author and links to where they publish — in the plugin list, in the
launcher, and on the game's own page.

Worlds that ship with Archipelago itself are released under
[the project's](https://github.com/ArchipelagoMW/Archipelago) licence, and the
people credited in each world's own source are named alongside it.

Licences are read from each author's own repository. Where an author has
published none, that is what it says rather than a guess.

---

## What is not here

- No game files, ever
- No randomiser code — only the address where its author publishes it
- No emulators

Nothing is downloaded without you agreeing to it first, and the launcher shows
you what it would download before it downloads anything.

---

*341 games in the catalogue, 8 of them confirmed playable. The rest are built and waiting for somebody to try them.*

---

## If you would rather not be here

The Archipelago worlds this catalogue supports are other people's work. If you
wrote one and would rather it were not supported here, **open an issue and it
will be removed.** No argument, no conditions, and it will not come back later.

That stands whether or not a licence would let us carry on. Someone who does
not want their work associated with this project has a better claim on that
decision than we do, and a permission granted years ago to nobody in particular
is not the same as agreement.

Removal means everything: the plugin, its entry here, the published download,
and the module in the launcher that reads that game's memory.

### Removed on request

Neither the people who asked nor the worlds they asked about are named here.
Two of them asked not to be associated with this project, and a page of ours
listing their names — or their games — would be that association all over
again. A removal log that keeps the titles searchable removes nothing.

As of 23 August 2026, **ten worlds** across five platforms have been withdrawn
at their authors' request. Where anything had been published, all of it went:
the plugin, the catalogue entry, the release asset, and the module in the
launcher that read that game's memory. Where nothing had been published, the
title is blocked from ever entering the build.

That block is enforced in code, not from memory. Every tool that can add a
game — the sheet importer, both plugin builders and the shop-window
generator — checks each title against the withdrawal list before it does
anything, and refuses. The list itself is kept out of this repository for the
same reason this section names nothing.

**If you are an author and want your world out of here, open an issue or say
so on Discord and it will go.** You do not have to explain why, and you will
not have to ask twice — that happened once, and this process exists because
of it.

---
