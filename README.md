# JevTest — beslutningsmodeller i C#

Et lille .NET 10-konsolprogram, der demonstrerer beslutningsmodeller med **JEV via OpenRouter**. Et fiktivt regnskabsbilag med tekst, dato og beløb vurderes på tre måder:

| Type | Metode | Eksempel |
|---|---|---|
| Noul | `AskAsync()` | Er købet kontorartikler? |
| Choice | `ChooseAsync()` | Hvilken udgiftskategori passer bedst? |
| Score | `ScoreAsync()` | Hvor tydelig er beskrivelsen? |

## Kom i gang

Du skal bruge **.NET 10 SDK** og en **OpenRouter API-nøgle** med adgang til modellen og tilstrækkelig saldo. Ingen ekstra NuGet-pakker er nødvendige.

Kør i projektmappen med PowerShell:

```powershell
$env:OpenRouterKey = "DIN_OPENROUTER_API_NØGLE"
dotnet run
```

Nøglen gælder for den aktuelle terminalsession. Gem aldrig en rigtig nøgle i kildekoden eller versionsstyring.

## Kode og konfiguration

- [Program.cs](Program.cs): de tre eksempler og enkel fejlhåndtering.
- [JevConnector.cs](JevConnector.cs): HTTP-kald, typede svar og forbrugsoplysninger.

Constructoren kræver model og API-nøgle. Endpoint, `HttpClient` og USD/DKK-kurs er valgfrie:

```csharp
using var jev = new JevConnector(model, apiKey, usdToDkkRate: 6.5m);
```

Standardmodellen i programmet er `typesafe/jev-1.13`. Standardkursen er **6,5 DKK pr. USD**, ikke en livekurs.

## Hastighed og pris

Programmet viser vurdering, svartid, tokens, pris i USD/DKK og estimeret antal tilsvarende kald for 1 DKK.

I en eksempelkørsel tog hvert kald **401–819 ms** og kostede omkring **0,0001 DKK** — cirka **10.000 tilsvarende kald pr. krone**. Svar, tider og priser kan variere. Svartiden inkluderer netværk, og prisestimatet forudsætter samme pris pr. kald.

JEV er ét eksempel på en beslutningsmodel. Markedet ændrer sig hurtigt; se [OpenRouters aktuelle beslutningsmodeller](https://openrouter.ai/models?output_modalities=decisions&order=most-popular), og kontrollér API-kompatibilitet før et modelskift.

Brug fiktive bilag til øvelser: data sendes til OpenRouter og modeludbyderen. Modellens vurdering er et forslag, ikke en automatisk godkendelse af bilaget.
