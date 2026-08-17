# Hvordan plugins udgives

Målet: en spiller finder sit spil på få sekunder, henter ét plugin, og
launcheren klarer resten.

---

## GitHub er hele forsiden

Launcheren har ingen browsende spilliste, og skal ikke have en: spilleren
tilføjer et plugin, og spillet dukker op i listen til venstre. Jo flere
plugins, jo længere liste — kataloget hører til på GitHub, ikke inde i
programmet.

⚠ `CatalogRepo/catalog.json` i launcheren bruges **ikke** af det her. Den
skal alligevel bygges om.

Det betyder at **releases-siden ER opslagsværket**, og den skal kunne læses
af et menneske der leder efter ét bestemt spil.

---

## Grupperne

Én release pr. platform, med et **fast tag** der opdateres i stedet for at
blive erstattet:

| Tag | Titel |
|---|---|
| `gba` | Game Boy Advance |
| `snes` | Super Nintendo |
| `n64` | Nintendo 64 |
| `gb-gbc` | Game Boy og Game Boy Color |
| `nes` | Nintendo Entertainment System |
| `gc-wii` | GameCube og Wii |
| `ds` | Nintendo DS |
| `playstation` | PS1, PS2, PS3 |
| `sega` | Mega Drive og Master System |
| `pc` | PC |
| `web` | Web og PICO-8 |

Taggene skifter aldrig navn. Kommer der et nyt GBA-plugin, opdateres
`gba`-releasen — der laves ikke `gba-2`.

## ⭐ Problemet med "nyeste release"

Du pegede selv på det: med grupperede udgivelser bliver den øverste release
den du sidst rørte ved, ikke den vigtigste. GitHub fremhæver altid én som
**Latest**, og så står der `Sega` øverst fordi det var det sidste du opdaterede.

**Løsningen: en indeks-release der altid er Latest.**

Tag `index`. Den indeholder ingen plugins — kun:

- en indholdsfortegnelse i beskrivelsen: hver platform, hvor mange spil, og
  et link til gruppen
- `plugins.json` — hele listen i maskinlæsbar form, hvis London eller en
  tracker på et tidspunkt skal kunne slå op i den

Hver gang en gruppe opdateres, opdateres `index` bagefter. Så er den altid
nyest, og **Latest** peger altid på oversigten frem for på en tilfældig
platform.

Det er også den rigtige side at lande på fra en README eller en Discord-besked:
én adresse, der altid viser hele billedet.

---

## Hvad der står i en gruppe-release

```
Game Boy Advance — 18 spil

| Spil | Version | Kræver |
|---|---|---|
| Pokémon Emerald | 1.0.0 | din egen ROM |
| Metroid Fusion  | 1.0.0 | din egen ROM |
| …

Hvert plugin tilføjes i launcheren med Add plugin.
Alle GBA-spil kræver at du selv ejer spillet.
```

Assets: ét `.londonplugin` pr. spil, plus en `SHA256SUMS.txt` så en fil kan
kontrolleres uden at installere den.

---

## Rækkefølgen for en gruppe

1. **Kortlæg** hvert spil i gruppen — manifest med kilde på hvert felt,
   se `catalog/SKEMA.md`
2. **Dobbelttjek gruppen samlet**: peger to spil på samme klient? bruger de
   samme emulator-version? mangler nogen en hash?
3. **Byg** pluginet pr. spil og kør PluginCheck på hver enkelt
4. **Udgiv** gruppen, og opdatér `index` bagefter så den er nyest

⚠ Trin 2 er ikke en formalitet. Fejl i den slags data er ens på tværs af en
gruppe — rammer man forkert på ét GBA-spil, er der god chance for at de
17 andre har samme fejl.

---

## Når repoet åbnes

Ligger privat indtil der er plugins der virker. Når det åbnes:

- README får en tabel over alle grupper med antal
- `index`-releasen er landingssiden
- Hvert plugin bærer sit tillidsniveau, som London selv slår op — se
  `Core/Plugins/PluginProvenance.cs`. Et plugin kan ikke skrive sig selv op
  som betroet
