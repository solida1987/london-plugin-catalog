# Emulator bridges as extensions

A game plugin says which game. An **extension** says how the launcher talks to
the emulator.

London can drive BizHawk because that dialect is compiled in. Every other
protocol is a different conversation with a different program, and baking each
one into the launcher would mean a release every time somebody needs one more.
So a bridge ships as a package the player installs — the same shape as a game
plugin, with the same consent dialog and the same scrutiny.

## The rule that does not bend

**An extension carries protocol, never an emulator.**

The player installs their own emulator into `Emulators/<backend>/`. Fetching
somebody else's emulator would make us its distributor, which is the exact
category the moderation case was about. `Tools/lint_no_emulator_download.py`
covers `Core/Extensions` from the day that folder was created — negative-tested:

```
Core\Extensions\BridgeRegistry.cs:171  [HttpClient]  var probe = new HttpClient();
The launcher must not download emulators.
```

## Why this exists at all

Every SNES world in Archipelago subclasses `SNIClient`; GBA worlds subclass
`BizHawkClient`. A SNES plugin built on the BizHawk-only generic plugin would
install cleanly, launch an emulator, and **never connect** — no error, no
checks, just a player waiting. See `catalog/GROUP_SNES.md`.

The registry exists to make that failure impossible: a game whose protocol has
no working bridge is refused *before* anything starts, with a reason the player
can act on.

## The three answers

`BridgeRegistry` gives one of three, and all three are tested by
`tools/ExtensionCheck`:

| Situation | `CanServe` | What the player is told |
|---|---|---|
| Built-in protocol (`bizhawk`) | true | nothing — it just runs |
| Extension installed, not ready | **false** | what is missing, and where to get it |
| No extension for that protocol | **false** | which protocol the game needs |

The negative answers are the point. Silence here is the bug.

## What an extension is

```
sni_bridge/
  extension.json          the manifest, validated before any code runs
  CatalogSniBridge.dll     one class implementing IEmulatorBridge
  CatalogSniBridge.deps.json
```

`extension.json` mirrors `plugin.json`: `apiVersion`, `extensionId`,
`protocol`, `assembly`, `entryType`, `author`, `rulesAcknowledged`.

Two things the loader refuses outright:

- **Claiming a built-in protocol.** An extension declaring `bizhawk` would
  quietly displace the bridge we have proven, so it is rejected.
- **A manifest that disagrees with its code.** The manifest is what the consent
  dialog showed the player; if `bridge.Protocol` says something else, the
  extension does not load.

## Status

| Protocol | Bridge | Ready |
|---|---|---|
| `bizhawk` | built into the launcher | yes — proven with Pokemon Emerald |
| `sni` | `extensions/sni` | **no** — reachability works, memory transport is not implemented |
| `nwa` | not written | — |

The SNI extension can tell you whether SNI is running. It cannot yet read or
write memory, so `IsReady` is false and the launcher refuses to start a game
with it. That is deliberate: shipping a bridge that half-works would put a
player in a multiworld sending nothing, which is worse than saying no.

Finishing the SNI transport unblocks all 31 SNES games — they are already
mapped in `catalog/games/`.

## Building them

```
python tools/build_extensions.py --stage <launcher-build-folder>
```

Builds every extension, packs each as a `.londonextension` in `dist/`, and
**installs the BizHawk one into the launcher's `Extensions/` folder**.

That last part is not optional. The launcher drives no emulator of its own, so a
package without `Extensions/bizhawk_bridge/` cannot start a single game — and it
looks completely healthy until somebody presses Play. `pack_launcher.py` refuses
such a package; this is what puts the file there. Verified both ways:

```
without the bridge : exit 1  "zip mangler Extensions/bizhawk_bridge/"
with the bridge    : exit 0
```

## Installing one

The player picks the `.londonextension` with the launcher's **Add plugin…**
button — one button takes both kinds, because nobody should have to know
whether the file they were handed is a game or the bridge that game needs.

`ExtensionInstallFlow` runs the same order as the plugin side: look inside
without running anything, show what it declares and who wrote it, install, then
reload the registry so its emulator folders appear without a restart.

If the bridge is added but cannot run yet, **it says so right then** rather than
letting the player find out when a game sends no checks.

## Checking one

`ExtensionCheck <extensions-dir|package> <protocol>` takes either a folder or a
`.londonextension`. Given a package it installs it the way a player would, then
verifies the registry picks it up. It fails if a bridge that is not ready
reports itself servable, if a missing bridge produces no explanation, or if a
native runner cannot resolve its own program.
