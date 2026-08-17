# London Plugin Catalog

Ét sted med plugins til Multiworld Launcher — så en spiller kan hente pluginet
til lige det spil han vil spille, og launcheren klarer resten.

> **Privat indtil videre.** Ligger her mens der bygges.

---

## Hvad kartoteket er

`catalog/index.json` holder **454 uofficielle apworlds**, hentet fra det
offentlige indeks [Eijebong/Archipelago-index](https://github.com/Eijebong/Archipelago-index)
med `tools/harvest_index.py`.

⚠ **Vi kopierer ikke andres filer.** Kartoteket indeholder navne, adresser og
versionsnumre — aldrig indhold. Skal en spiller bruge en apworld, henter
launcheren den fra udgiverens egen adresse, ligesom han selv ville have gjort.

| | |
|---|---|
| Spil i indekset | 454 |
| Med direkte apworld-adresse | 245 |
| Uden — skal slås op i hånden | 209 |

---

## Spillisten — 472 spil med platform

`catalog/games.json` er den vigtigste fil. Den kommer fra Marcos egen
sammenskrivning af kanalen (`catalog/gamelist_raw.txt`), som er **rigere end
det maskinlæsbare indeks**: den siger hvilken platform hvert spil kører på.

⭐ Og det er platformen der afgør hvad et plugin må.

| Kilde | Antal |
|---|---|
| Direkte URL | 382 |
| Følger med Archipelago | 76 |
| Kun i en Discord-tråd | 14 |

## ⚠ Tre kategorier, ikke to

| Spillet er | Pluginet må | Antal |
|---|---|---|
| **Konsolspil** — SNES, N64, GBA, PSX … | kun bede om spillerens egen ROM | **166** |
| **Frit** — Web, PICO-8 | hente hele spillet | **12** |
| **PC** — Steam og gratis ser ens ud | skal vurderes enkeltvis | **294** |

De 166 er klassificeret **automatisk**: står der `(SNES)`, ligger spillet på
en kassette spilleren ejer, og så er sagen afgjort uden at nogen skal
skønne.

De 294 er næsten alle `(PC)`. Her er der oftest en tredje mulighed: spillet
ejes på Steam, og pluginet installerer **AP-udviklerens mod** ind i den
installation spilleren allerede har. Det er hverken at hente spillet eller at
bede om en ROM — og det er lovligt, fordi mod'en er udviklerens eget arbejde.

⚠ Derfor bliver `requires` stående som `ukendt` for de 294 indtil hver enkelt
er set efter. Et gæt her ville blive til et plugin der henter noget det ikke
måtte.

Maskineriet til kolonne 1 findes allerede: `RomRequirement` og
`AcceptableBaseRoms` i launcherens `EmulatorPlugin` beder om spillerens egen
fil og verificerer den på hash. Samme model som Pokémon-projektet.

---

## ⭐ Hvorfor der ikke skal skrives 454 plugins

Det ville være et års arbejde, og det meste ville være det samme kode 454
gange.

De fleste emulerede spil kører **samme opskrift**: BizHawk, den generiske
Lua-connector, en apworld, og et RAM-kort der siger hvor checks ligger. Kun
det sidste er spilspecifikt.

Så planen er **ét datadrevet plugin** frem for mange håndskrevne:

```
PluginManifest (JSON pr. spil)      →  et fælles GenericEmulatorPlugin
  spilnavn, system, apworld-adresse
  spillet: frit eller egen fil
  RAM-kort: adresse → lokations-id
```

Håndskrevne plugins bygges kun til spil der ikke passer i formen — som
Diablo II og OpenTTD, der begge har deres eget.

---

## Faser

**1 — kartoteket** ✅ 454 apworlds fra indekset + 472 spil med platform

**2 — klassificering.** ✅ 166 konsolspil og 12 frie afgjort automatisk.
⏳ 294 PC-titler tilbage: ejes spillet på Steam (pluginet installerer en mod),
eller er det frit (pluginet må hente det)? Manuelt, én gang, skrevet ned.

**3 — det generiske plugin.** `GenericEmulatorPlugin : EmulatorPlugin` drevet
af et manifest. Bevises på ét spil før det bruges på flere.

**4 — RAM-kort pr. spil.** Det eneste der ikke kan automatiseres. Kommer
først, når vi ved hvilke spil folk faktisk vil have.

**5 — udgivelse.** Repoet gøres offentligt når der ligger plugins der virker.

---

## Regler

Gælder `REGELBOG.md` i projektroden. Kort:

- Vi hoster aldrig andres apworlds eller spilfiler — kun adresser til dem
- Et plugin henter kun et spil hvis spillet er frit
- Kommercielle spil: spilleren leverer selv, og filen verificeres på hash
- Ingen automatisk hentning uden at brugeren siger ja
