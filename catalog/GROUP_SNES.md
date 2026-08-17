# Super Nintendo — BLOCKED

31 games on the list. **0 built, and that is the correct number for now.**

## Why

Every SNES world in Archipelago subclasses **`SNIClient`**. GBA worlds subclass
`BizHawkClient`. Checked, not assumed:

| World | Client base class |
|---|---|
| `smw`, `kdl3`, `yoshisisland`, `sm`, `alttp`, `lufia2ac`, `smz3` | `SNIClient` |
| `pokemon_emerald` (for contrast) | `BizHawkClient` |

`SNIClient` talks to **SNI**, a separate program that bridges to snes9x, bsnes
or real hardware. It is not a Lua script running inside an emulator.

`GenericEmulatorPlugin` drives BizHawk with `connector_bizhawk_generic.lua` and
nothing else. A SNES plugin built on it would install cleanly, launch an
emulator, and **never connect** — no error, no checks, just a player waiting.
That is worse than shipping nothing, so `tools/build_plugins.py` now refuses
any manifest whose `client.protocol` is not `bizhawk`:

```
alttp   REJECTED   client.protocol='sni' - GenericEmulatorPlugin only drives
                   BizHawk, so this would install and then never connect
```

London has no SNI bridge. It does have a snes9x backend over NWA TCP
(`Core/EmulatorBackends.cs`), which is a different protocol again.

## What has to happen first

One of:

1. **An SNI bridge in London** — start/track SNI, speak its protocol, and give
   `GenericEmulatorPlugin` a second transport alongside the BizHawk one.
2. **Reuse the existing snes9x NWA backend** — London already has
   `NwaClient` / `Snes9xLuaBridge`. That is closer to hand, but it is not what
   the AP worlds speak, so the world's own client would still be bypassed.

Option 1 is the one that makes the 31 games work as their authors intended.

---

## The mapping is done anyway

Eight worlds are fully mapped and written to `catalog/games/`. They are refused
by the gate, not lost — when a bridge exists, they build without more research.

| Game | AP name | Patch | Dumps |
|---|---|---|---|
| Final Fantasy Mystic Quest | `Final Fantasy Mystic Quest` | `.apmq` | 0 |
| Kirby's Dream Land 3 | `Kirby's Dream Land 3` | `.apkdl3` | 2 |
| Lufia II: Rise of the Sinistrals | `Lufia II Ancient Cave` | `.apl2ac` | 1 |
| Secret of Evermore | `Secret of Evermore` | `.apsoe` | 1 |
| Super Mario World | `Super Mario World` | `.apsmw` | 1 |
| Super Mario World 2: Yoshi's Island | `Yoshi's Island` | `.apyi` | 1 |
| Super Metroid | `Super Metroid` | `.apsm` / `.apm3` | 1 |
| The Legend of Zelda: A Link to the Past | `A Link to the Past` | `.aplttp` / `.apz3` | 1 |

### Three names that would have broken a YAML

The AP game name is not the retail title in three cases. Getting these wrong
produces a slot the server does not recognise:

- **Lufia II** → `Lufia II Ancient Cave`. The world randomises the Ancient Cave
  mode specifically, not the retail game.
- **Yoshi's Island** → `Yoshi's Island`, without "Super Mario World 2:".
- **A Link to the Past** → `A Link to the Past`, without "The Legend of Zelda:".

### A Link to the Past wants the JAPANESE ROM

The accepted dump is the **Japanese 1.0** ROM (`LTTPJPN10HASH`,
`03a63945398191337e896e5771f77173`), not a US one. Anyone assuming "US, like
everything else" would hand over a valid ROM and be refused.

### Final Fantasy Mystic Quest has no base-ROM hash

Its patch is produced through an external randomizer API rather than from a
local base ROM, so there is nothing to verify against — the same situation as
KH Chain of Memories in the GBA group.

---

## Two more findings

**Donkey Kong Country 3 is not bundled.** The game list says "Bundled with
Archipelago", but there is no `worlds/dkc3` upstream. It lives in
`TheLX5/Archipelago` on the `dkc2` branch. The list is stale here.

**SMZ3 needs TWO base ROMs** — Super Metroid *and* A Link to the Past. The
manifest schema holds one `rom` block, so it cannot express that game honestly.
It is left unmapped rather than described wrongly. (The same shape as the
Pokémon FireRed + Ruby problem.)

**Two sources are not repositories at all:** Chrono Trigger points at a wiki
(`wiki.ctjot.com`) and Final Fantasy VI at a Google Doc. Neither can be read the
way a repo can, so both need a different approach.

**Super Metroid Map Rando** lives only in a Discord thread we have no access to.

---

## The 20 third-party games

Not yet mapped — blocked behind the same protocol problem, so mapping them now
would be research with nowhere to land. They are listed in
`catalog/games.json` with their addresses.
