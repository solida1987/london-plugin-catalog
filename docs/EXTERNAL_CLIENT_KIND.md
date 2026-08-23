# `client.kind = "world"` — the runtime contract

What the launcher must do at Play for a game whose world ships its own
Archipelago client. This is the spec for the plugin/launcher side; the
catalogue side (manifest fields and build gates) is in `catalog/SCHEMA.md`,
section *`client.kind` — who carries the checks*.

First game in this category: **Burnout 3: Takedown** (`burnout3`, PS2, PINE).

---

## Why the category exists

Every game in the catalogue so far is `kind = "london"`: London starts the
emulator, loads OUR Lua RAM map, reads the game's memory itself and speaks to
the Archipelago server itself. London *is* the client.

A `kind = "world"` game is different in one structural way: **the world ships
its own client and expects it to be used.** Burnout 3's world registers it in
its own `__init__.py`:

```python
components.append(Component("Burnout 3 Client", func=run_client,
                            component_type=Type.CLIENT))
```

and carries its own transport — `burnout3/pine.py` is a PINE client that
talks to PCSX2 directly. If London also read memory it would at best duplicate
the world's client and at worst fight it over the same PINE slot. So for this
kind, London must **not** read memory at all. Its job shrinks to the three
things nobody else does: get the world installed, get the emulator running
with the right disc, and tell the player where the client is.

## Where the flag lives

The embedded `game.json` inside the plugin package. `tools/build_plugins.py`
embeds, for a `world` game:

```jsonc
"client": {
  "protocol": "pine",          // which BRIDGE starts/attaches the emulator
  "kind": "world",             // this contract applies
  "name": "Burnout 3 Client"   // what the player must start, by name
},
"apworld": {
  "url": "https://github.com/Metasura/Burnout3Archipelago/releases",
  "world_version": "0.1.0",
  "minimum_ap_version": "0.6.6"
}
```

- **Absent `kind` means `"london"`.** Old packages predate the field; they are
  all memory-read games and must keep behaving exactly as today.
- Values other than `london`/`world` cannot occur — the build rejects them —
  but the runtime should still refuse loudly rather than default if it ever
  sees one (the same never-guess rule as `requires`).
- The build also guarantees: a `world` package always has `apworld.url`, and
  the manifest's claim was sourced from the world's own code at build time
  (`_kind_source` — never embedded, like every `_source` field).

## What Play must do, in order

1. **File gate — unchanged.** `requires`/`rom` work exactly as for every
   other game: ask for the player's own disc image via `rom.description`,
   verify what can be verified. (Burnout 3 has no md5/size — warn-only, which
   `GetUnmetRomRequirement` already words correctly.)

2. **Install the apworld** from `apworld.url` into the player's Archipelago
   `custom_worlds` — `ApworldSync` already exists for exactly this. Check
   `minimum_ap_version` against the installed Archipelago the same way the
   existing version gate does; the world's client runs inside Archipelago, so
   an AP that is too old fails *there*, later and less readably.

3. **Launch the emulator over the bridge named by `client.protocol`** — for
   `pine`, `PineBridge` starts PCSX2 with the player's disc and verifies the
   attach (BIOS present, PINE answering on the slot). The bridge's
   `GetUnmetRequirement` sentences stay: they fire before the player watches
   a silent boot failure.

4. **Stop there.** Explicitly NOT done for `kind = "world"`:
   - no RAM map is loaded (none exists — the build did not require one),
   - no memory polling, no domain guard, no goal watch,
   - **no connection to the Archipelago server as the game's slot.** The
     world's client owns that slot; a second connection under the same slot
     name is at best a kick and at worst a desync.
   - no per-seed patching (Burnout 3 has no `patch` block at all; its pnach
     is a PCSX2-side setup step the player does once, not something London
     applies).

5. **Tell the player what carries the checks now.** One message at launch,
   naming the client from `client.name`:

   > *This game uses the world's own client. Start **Burnout 3 Client** from
   > the Archipelago Launcher and connect it to your session — London has
   > started the emulator, but the world's client is what sends and receives
   > checks.*

   This message is the load-bearing piece of the whole kind: without it the
   player sees a running game that silently never checks anything, which is
   the exact failure the Lua gate exists to prevent for `london` games.

6. **`checks_verified` keeps its meaning.** It stays `false` until a real
   check has been watched travelling through the world's client end to end,
   and the existing launch warning stays while it is false. For Burnout 3
   specifically: the PINE transport is measured (title, disc id, memory
   round-trip), but no check has ever been carried — transport working is a
   claim about the bridge, not about the game being playable.

## What NOT to infer

- `kind = "world"` says nothing about patching, hashes, or `requires` — those
  fields keep their own meanings and their own gates.
- It does not mean "PC-style install". The emulator, disc gate and bridge
  launch are all still London's job; only the *client role* moves out.
- It does not disable provenance/trust: the plugin's trust level is still
  decided by the launcher, never declared by the plugin.
