# The game manifest

One JSON manifest per game. The generic plugin reads it and behaves
accordingly — so a new game is a data file, not a new C# class.

## The rule for every single field

> **No field is filled in from memory or from judgement.**

Every field carries a `_source` — where the number or the text came from. If a
field cannot be backed up, it stays `null` with a note. **An empty field is
usable; an invented field is dangerous**, because it becomes a plugin that
asks for the wrong file or points at the wrong client.

Accepted sources, in order of rank:

1. The game's own setup guide (archipelago.gg or the developer's repo)
2. The developer's `README` or release notes
3. A measurement taken from a file we hold (hash, header, file size)
4. BizHawk's `gamedb` — an offline hash database of known dumps

**Not a source:** what "is usually the case", or what another game on the same
console does.

---

## Fields

```jsonc
{
  "id": "pokemon_emerald",              // our gameId, lowercase
  "display_name": "Pokémon Emerald",
  "platform": "GBA",

  "requires": "own_copy",               // own_copy | mod | free | unknown
  "_requires_source": "GBA title - the game lives on a cartridge the player owns",

  "ap_world_name": "Pokemon Emerald",   // what the AP server knows the slot as
  "_ap_world_name_source": "confirmed word for word against archipelago.gg/games",

  "checks_verified": false,             // true only once the RAM map is MEASURED
  "_checks_verified_note": "London warns at launch until this is true",

  "rom": {
    "description": "An English Pokémon Emerald ROM",
    "_description_source": "archipelago.gg setup guide, word for word",
    "md5": null,                        // the guide states NO checksum
    "size": null,
    "_hash_note": "Must be measured on a verified dump and cross-checked against gamedb"
  },

  "emulator": {
    "name": "BizHawk",
    "min_version": "2.7",
    "_source": "setup guide: 'BizHawk 2.7 or later'"
  },

  "client": {
    "name": "Archipelago's BizHawk Client",
    "lua": "data/lua/connector_bizhawk_generic.lua",
    "_source": "setup guide"
  },

  "apworld": {
    "bundled": true,                    // ships with Archipelago
    "url": null,
    "_source": "game list: 'Bundled with Archipelago'"
  },

  "patch": {
    "extension": ".apemerald",
    "produced_by": "Archipelago Launcher - Open Patch",
    "_source": "setup guide"
  }
}
```

## What the plugin does with it

| Field | What London uses it for |
|---|---|
| `requires` | Whether to ask for the player's own file |
| `rom.md5` / `rom.size` | `AcceptableBaseRoms` — rejecting a wrong dump |
| `emulator` | Which backend, and whether the version is high enough |
| `client.lua` | Which connector script gets loaded |
| `patch` | Whether a ROM has to be built before launch |
| `apworld` | Whether it ships with AP, or comes from the publisher |
| `checks_verified` | Whether the launcher warns that no checks will arrive |

## When a field is `null`

The plugin **must not** guess. With no `rom.md5` it falls back to checking
**size** and says so plainly in the dialog: *"this edition cannot be checked
exactly; make sure yourself that it is the right dump."*

That is more honest than rejecting a valid file, and safer than accepting a
wrong one in silence.

## `requires` — the four values

| Value | Meaning | Plugin may |
|---|---|---|
| `own_copy` | Console title; the player owns a cartridge or disc | only ask for the player's own file |
| `mod` | The player owns the game; the plugin installs the AP developer's mod | write into an existing install |
| `free` | The game itself is free | fetch the whole game |
| `unknown` | Not classified yet | nothing automatic |

`tools/build_plugins.py` **refuses to build** a manifest whose `requires` is
outside this set. A value it does not recognise must never fall through to a
default: if the name were ever changed and one caller missed, the plugin would
quietly stop asking the player for their own file.
