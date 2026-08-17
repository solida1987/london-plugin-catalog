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

## ⚠ Det der afgør om et plugin må hente noget

Et plugin kan gøre to vidt forskellige ting, og forskellen er juridisk:

| Spillet er | Pluginet må |
|---|---|
| Frit / open source (fx OpenTTD) | **Hente og installere spillet** |
| Kommercielt (fx en GBA-titel) | **Kun bede spilleren om hans egen fil** |

Derfor står `requires` på **`ukendt`** for alle 454 poster indtil videre.
Indekset siger nemlig intet om selve spillet — kun om apworld'en. Et gæt her
ville blive til et plugin der henter noget det ikke måtte, så hver post skal
vurderes én gang og skrives ned.

Maskineriet til den anden kolonne findes allerede: `RomRequirement` og
`AcceptableBaseRoms` i launcherens `EmulatorPlugin` beder om spillerens egen
fil og verificerer den på hash. Det er samme model som Pokémon-projektet.

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

**1 — kartoteket** ✅ 454 poster hentet og struktureret

**2 — klassificering.** Hver post får `requires` sat: frit spil, egen fil,
eller "ingen spilfil" (Manual-verdener). ⚠ Manuelt, én gang, skrevet ned.

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
