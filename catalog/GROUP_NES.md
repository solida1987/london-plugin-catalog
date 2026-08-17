# Nintendo Entertainment System

9 games on the list. **2 built**, 2 mapped but blocked, 4 not yet mapped,
1 cannot be built.

## Protocol first, mapping second

The SNES group taught this: check how a world talks to Archipelago **before**
mapping it, or the research lands nowhere. Every world here was read for its
client class first.

| World | Client | Buildable |
|---|---|---|
| `ff1` | `BizHawkClient` | yes |
| `mm2` | `BizHawkClient` | yes |
| `faxanadu` | none found | **no** |
| `tloz` | none found | **no** |

---

## Built

| Game | AP name | Patch | Dumps accepted |
|---|---|---|---|
| Final Fantasy | `Final Fantasy` | — | **0** |
| Mega Man 2 | `Mega Man 2` | `.apmm2` | 4 |

**Mega Man 2 accepts four dumps**: the cartridge rip, the Virtual Console
release, the copy inside *Mega Man Legacy Collection* on Steam, and one more the
source calls `PROTEUSHASH`. The setup guide names the Legacy Collection
explicitly as an alternative to owning a cartridge — worth knowing, because it
means a player with the Steam version is already covered.

**Final Fantasy has no base-ROM hash and no patch extension.** Its randomised
ROM comes from the external Final Fantasy Randomizer, not from Archipelago, so
there is nothing for London to verify. The plugin says so rather than implying
the file was checked — the same situation as KH Chain of Memories (GBA) and
Final Fantasy Mystic Quest (SNES).

---

## Blocked: two worlds whose protocol is not established

Neither is a data gap; both are honest unknowns.

- **Faxanadu** has no `Client.py` and no `rom.py`, and its setup guide mentions
  only Archipelago's Text Client. How checks reach the server is not visible in
  the source we can read.
- **The Legend of Zelda** has `Rom.py` and a base patch, but no client class we
  could find.

Both are recorded with `client.protocol: "unknown"`, and the build refuses them:

```
faxanadu         REJECTED   client.protocol='unknown' - GenericEmulatorPlugin
legend_of_zelda  REJECTED   only drives BizHawk, so this would install and
                            then never connect
```

Guessing "probably BizHawk" would have produced two plugins that install
cleanly and never send a check. The refusal is the feature.

---

## Not yet mapped

Four live in other people's repositories:

Crystalis (`Ars-Ignis/Archipelago`) · Dragon Warrior 1
(`Serpikmin/Archipelago-DragonWarrior`) · Mega Man 3 (`Silvris/Archipelago`,
tag `mm3_0`) · Zelda II: The Adventure of Link (`PinkSwitch/Archipelago`)

**Spelunker** cannot be built — the source lives only in a Discord thread we
have no access to.

---

## How they were built

```
python tools/group.py NES
python tools/group_check.py NES
python tools/build_plugins.py
python tools/rom_gate_test.py
python tools/checksums.py
```
