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
    "kind": "london",                   // london | world — who carries the checks, see below
    "protocol": "bizhawk",              // bizhawk | sni | unknown
    "lua": "data/lua/connector_bizhawk_generic.lua",
    "_source": "the world's client subclasses BizHawkClient"
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
| `client.kind` | Whether London reads memory itself, or only launches and defers to the world's own client |
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

## `client.protocol` — which BRIDGE London drives

The field answers **which of our bridge extensions carries this game's checks**,
not which client Archipelago ships. London never runs Archipelago's client: it
starts the emulator itself and reads memory through a bridge.

| Value | Bridge | Buildable |
|---|---|---|
| `bizhawk` | BizHawk + our generic Lua connector | yes |
| `sni` | SNI | yes |
| `unknown` | not established | **no** — refused by the build |

Two kinds of evidence answer it, and either is enough:

1. **The world's own client class.** `SNIClient` means the game is driven over
   SNI; `BizHawkClient` means it is driven by reading memory in BizHawk, which
   is what our connector does too; the value is read from the world itself.
2. **A Lua RAM map of ours exists** in `Plugins/Scripts/games/`. Somebody found
   this game's addresses in a running game, which is the whole substance of
   driving it through the BizHawk bridge.

⚠ The second rule exists because a dozen games have **neither** base class:
Archipelago ships them a standalone client of their own (Adventure, Ocarina of
Time, MMBN3, Zelda 1 and Zillion each have their own `Archipelago*Client.exe`).
That says nothing about whether OUR RAM map reads them correctly — it only means
question 1 has no answer for them, so question 2 decides.

Neither rule claims the checks actually arrive. That is `checks_verified`, which
stays false until somebody has watched it happen, and the launcher warns at
launch while it is false.

`GenericEmulatorPlugin` resolves the protocol in `BridgeRegistry`. A plugin
built for a protocol nothing carries would install cleanly, launch an
emulator, and never connect — no error, just a player waiting. So the build
**refuses** it. See `catalog/GROUP_SNES.md`.

The build **refuses** a manifest whose `requires` is
outside this set. A value it does not recognise must never fall through to a
default: if the name were ever changed and one caller missed, the plugin would
quietly stop asking the player for their own file.

## `client.kind` — who carries the checks

Two kinds of game exist in this catalogue, and the difference decides what
London *is* for that game:

| Value | Meaning |
|---|---|
| `london` (default) | London's own connector reads the game's memory over the bridge. This is every BizHawk and SNI game: our Lua RAM map under `Plugins/Scripts/games/` is the whole substance of the connection, so the build **requires** that the map exists. |
| `world` | The world ships its **own** Archipelago client — it registers a `Component(..., component_type=Type.CLIENT)` in its own `__init__.py`, and its client speaks to the emulator itself (Burnout 3's `pine.py` talks PINE to PCSX2 directly). London must **not** read memory for such a game. |

For a `world` game, London's job is exactly three things:

1. install the apworld into the player's Archipelago (`custom_worlds`),
2. start the emulator over the bridge with the right disc,
3. tell the player to start the world's own client from the Archipelago
   Launcher — by the name in `client.name`.

The build gates follow from that:

- `"kind": "world"` **requires `_kind_source`** in the client block: the
  world's own `components.append(...)` line, quoted. The claim must be the
  world's words, not our impression — without the quote the manifest is
  **rejected**. (This is deliberate: a wrongly-classified game launches and
  then no code anywhere carries its checks.)
- `"kind": "world"` **requires `apworld.url`**: the world's client is the only
  thing that will ever carry checks, so it must be fetchable.
- `"kind": "world"` **skips the Lua-map requirement** — no code of ours reads
  the game, so demanding our map would hold every self-clienting world out of
  the catalogue forever. Every other gate (protocol bridge, requires,
  patch_extension, removed-games) applies unchanged.
- An unrecognised value is rejected, never defaulted — the same rule as
  `requires`.

`checks_verified` keeps its meaning: it stays **false** until a real check has
been watched travelling through the world's client, and London warns at launch
while it is false. "The transport works" is not "a check arrived".

The runtime contract — what the launcher must actually do at Play for a
`world` game — is written out in `docs/EXTERNAL_CLIENT_KIND.md`.

## patch_extension (required)

Whether the game's world overrides patch steps in Python (`APPatchExtension`
in the world's `rom.py`). The patch container does not reveal this — the
manifest looks ordinary either way — so it must be answered by READING the
world source, once, when the manifest is written:

| value | meaning |
|---|---|
| `none` | world audited; no extension. Generic steps are correct. |
| `replicated` | extension REUSES generic step names with changed behaviour; the logic is replicated in `Core/Patching/ApPatch.cs` with the source cited. |
| `refused` | extension adds NEW step names; the launcher's patcher refuses them loudly and the game plays unpatched. |
| `unaudited` | world source not locally available; flagged in every build until audited. |

Why this exists: Pokémon FRLG ships rev0 AND rev1 patch files in one container
and switches on the ROM's revision byte inside a Python override of
`apply_bsdiff4`. Generic code applies the rev0 patch to a rev1 ROM — the
checksum guard passes (rev1 is an accepted dump) and the output is silently
corrupt. The reused-generic-name class is the dangerous one; new step names
fail loudly on their own.
