# Spil-manifestet

Ét JSON-manifest pr. spil. Det generiske plugin læser det og opfører sig
derefter — så et nyt spil er en datafil, ikke en ny C#-klasse.

## ⭐ Grundreglen for hvert eneste felt

> **Intet felt udfyldes af hukommelsen eller af et skøn.**

Hvert felt bærer `_source` — hvor tallet eller teksten kommer fra. Kan et felt
ikke belægges, står det som `null` med en note. **Et tomt felt er brugbart;
et opdigtet felt er farligt**, for det bliver til en plugin der beder om den
forkerte fil eller peger på den forkerte klient.

Godkendte kilder, i rangorden:

1. Spillets egen opsætningsvejledning (archipelago.gg eller udviklerens repo)
2. Udviklerens `README` eller release-noter
3. Måling i en fil vi selv har (hash, header, filstørrelse)
4. BizHawks `gamedb` — en offline hash-database over kendte dumps

⛔ Ikke en kilde: hvad der "plejer at være tilfældet", eller hvad et andet
spil på samme konsol gør.

---

## Felter

```jsonc
{
  "id": "pokemon_emerald",              // vores gameId, lowercase
  "display_name": "Pokémon Emerald",
  "platform": "GBA",

  "requires": "egen_fil",               // egen_fil | mod | frit
  "_requires_source": "platform GBA ⇒ kassette spilleren ejer",

  "rom": {
    "description": "An English Pokémon Emerald ROM",
    "_source": "archipelago.gg opsætningsvejledning, ordret",
    "sha1": null,                       // ⚠ vejledningen angiver INGEN hash
    "_sha1_note": "Skal måles på en verificeret dump og krydstjekkes mod gamedb",
    "size": null
  },

  "emulator": {
    "name": "BizHawk",
    "min_version": "2.7",
    "_source": "opsætningsvejledningen: 'BizHawk 2.7 or later'"
  },

  "client": {
    "name": "Archipelago's BizHawk Client",
    "lua": "data/lua/connector_bizhawk_generic.lua",
    "_source": "opsætningsvejledningen"
  },

  "apworld": {
    "bundled": true,                    // følger med Archipelago
    "url": null,
    "_source": "spillisten: 'Bundled with Archipelago'"
  },

  "patch": {
    "extension": ".apemerald",
    "produced_by": "Archipelago Launcher, Open Patch",
    "_source": "opsætningsvejledningen"
  },

  "steps": [ /* ordret fra vejledningen */ ],
  "_steps_source": "archipelago.gg opsætningsvejledning"
}
```

## Hvad pluginet gør med det

| Felt | Hvad London bruger det til |
|---|---|
| `requires` | Om der skal spørges efter spillerens egen fil |
| `rom.sha1` | `AcceptableBaseRoms` — afvisning af forkert dump |
| `emulator` | Hvilken backend, og om versionen er høj nok |
| `client.lua` | Hvilket connector-script der loades |
| `patch` | Om der skal bygges et ROM før start |
| `apworld` | Om den følger med, eller skal hentes fra udgiveren |

## ⚠ Når et felt er `null`

Pluginet må **ikke** gætte. Mangler `rom.sha1`, falder det tilbage til at
tjekke **størrelse** og sige det ligeud i dialogen: *"Denne udgave kan ikke
verificeres præcist — kontrollér selv at din fil er den rigtige."*

Det er ærligere end at afvise en gyldig fil, og sikrere end at acceptere en
forkert i stilhed.
