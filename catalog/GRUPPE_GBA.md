# Game Boy Advance

18 spil på listen. **5 bygget**, 12 mangler kortlægning, 1 kan ikke bygges.

Alt herunder er skrevet af fra spillets egen opsætningsvejledning på
archipelago.gg. Der er ikke gættet et eneste sted — det der ikke stod, står som
et hul nedenfor i stedet for som en opfundet værdi.

---

## Bygget

| Spil | AP-navn | Patch | BizHawk |
|---|---|---|---|
| Castlevania: Circle of the Moon | `Castlevania - Circle of the Moon` | `.apcvcotm` | 2.7+ |
| Mario & Luigi: Superstar Saga | `Mario & Luigi Superstar Saga` | `.apmlss` | 2.9.1 anbefalet |
| Mega Man Battle Network 3 Blue | `MegaMan Battle Network 3` | `.apbn3` | 2.7.0+ |
| Pokémon Emerald | `Pokemon Emerald` | `.apemerald` | 2.7+ |
| Yu-Gi-Oh! Ultimate Masters: WCT 2006 | `Yu-Gi-Oh! 2006` | `.apygo06` | 2.7.0+ |

AP-navnene er **bekræftet ordret** mod spillelisten på archipelago.gg/games
(17-08-2026). De blev afledt af tutorial-adressen først og derefter
krydstjekket — et forkert AP-navn er den fejl der først viser sig når
spilleren står med et slot serveren ikke kender.

⭐ **Gruppens BizHawk-gulv er 2.9.1**, ikke 2.7. Kravene er forskellige pr.
spil, og det højeste vinder for den der bare installerer én emulator.

---

## Hullerne — hvad vi IKKE ved

### 1. Ingen af vejledningerne oplyser en checksum

Det gælder alle fem, og det er derfor det står her som et gruppevilkår og ikke
som fem enkeltbemærkninger. Konsekvensen er præcis:

> Pluginet kan **advare** om en forkert ROM. Det kan ikke **afvise** den.

Pluginet siger det ligeud i dialogen frem for at lade som om filen er
godkendt. Feltet lukkes når vi kan måle en dump og krydstjekke mod BizHawks
egen `gamedb_gba.txt`.

### 2. `checks_verified` er `false` på alle fem

RAM-kortet er ikke målt i spillet endnu. London **advarer ved start**, så
ingen sidder en time i en multiworld og undrer sig over at der ikke kommer
checks. Det er den rigtige tilstand indtil kortet er målt — ikke en mangel
der skal skjules.

### 3. Yu-Gi-Oh GX Duel Academy kan ikke bygges

Kilden ligger **kun i en Discord-tråd** vi ikke har adgang til. Den står som
kendt begrænsning frem for at blive glemt.

---

## De 12 der mangler

Alle har en offentlig adresse, så de kan kortlægges på samme måde:

Castlevania: Harmony of Dissonance · Final Fantasy Tactics Advance ·
Fire Emblem: The Sacred Stones · Golden Sun: The Lost Age ·
KINGDOM HEARTS Chain of Memories · Metroid Fusion · Metroid: Zero Mission ·
Pokémon FireRed and LeafGreen · Sonic Battle ·
The Legend of Zelda: The Minish Cap · Wario Land 4 ·
Yu-Gi-Oh! Dungeon Dice Monsters

⚠ De ligger i **andres** repoer, ikke i Archipelago selv. Tillidsniveauet er
derfor et andet, og London slår det selv op — se
`Multiworld-Launcher/Core/Plugins/PluginProvenance.cs`. Et plugin kan ikke
skrive sig selv op som betroet.

---

## Sådan blev de bygget

```
python tools/group.py GBA           # hvad er i gruppen
python tools/group_check.py GBA     # trin 2: fælles fejl i HELE gruppen
python tools/build_plugins.py       # byg + pak hver enkelt
```

Der er ingen spil-specifik C#. Hvert plugin er den samme delte
`plugin/GenericEmulatorPlugin.cs` plus spillets eget manifest, indlejret i
assemblyen. Et nyt spil er en datafil.

⚠ Manifestet ligger **inde i** DLL'en, ikke ved siden af: launcherens
`pack_plugin.py` hvidlister med vilje kun assembly + deps + `plugin.json`, så
en løs `game.json` ville aldrig nå med i pakken.
