# JevTest — beslutningsmodeller i C#

Et lille .NET 10-projekt, der demonstrerer beslutningsmodeller med **JEV via OpenRouter**.

**BogføringsAgent** kører det oprindelige konsol-eksempel. Et fiktivt regnskabsbilag med tekst, dato og beløb vurderes på tre måder:

| Type | Metode | Eksempel |
|---|---|---|
| Noul | `AskAsync()` | Er købet kontorartikler? |
| Choice | `ChooseAsync()` | Hvilken udgiftskategori passer bedst? |
| Score | `ScoreAsync()` | Hvor tydelig er beskrivelsen? |

**BookingServiceAgent** er en separat, tilstandsfuld chatagent til campingbooking. Den har ingen konsolafhængighed og kan bruges fra tests, en konsol eller en anden brugerflade.

**ColorAgent** er et enkelt, tilstandsløst eksempel: ét JEV-choice-kald placerer en farve i den nærmeste af de traditionelle primærfarver **rød, gul og blå**.

## Kom i gang

Du skal bruge **.NET 10 SDK** og en **OpenRouter API-nøgle** med adgang til modellen og tilstrækkelig saldo. Ingen ekstra NuGet-pakker er nødvendige.

Kør i projektmappen med PowerShell:

```powershell
$env:OpenRouterKey = "DIN_OPENROUTER_API_NØGLE"
dotnet run
```

Nøglen gælder for den aktuelle terminalsession. Gem aldrig en rigtig nøgle i kildekoden eller versionsstyring.

`dotnet run` starter nu farveeksemplet med **"Hvilken farve vil du klassificere?"**. Svar fx `lyserød`; programmet viser primærfarve, sandsynligheder, tid og API-pris.

Vælg eksempel ved at aktivere ét af kaldene i [Program.cs](Program.cs):

```csharp
//return await BogføringsAgent.RunAsync();
//return await BookingConsole.RunAsync();
return await ColorAgent.RunAsync();
```

Bookingeksemplet starter med **"Hvad vil du booke: en hytte eller en plads til campingvogn?"** og afslutter automatisk med JSON. Skriv `/exit` for at afbryde bookingchatten.

## Kode og konfiguration

- [Program.cs](Program.cs): ét aktivt kald til det valgte eksempel.
- [BookingConsole.cs](BookingConsole.cs): konsol-loop, JSON-resultatet `BookingSummary`, API-konfiguration og synlig fejlhåndtering.
- [BogføringsAgent.cs](BogføringsAgent.cs): de tre eksempler, udskrift af forbrug og enkel fejlhåndtering.
- [BookingServiceAgent.cs](BookingServiceAgent.cs): bookingdialog, typede svar og validering.
- [BookingDateParser.cs](BookingDateParser.cs): lokal normalisering af danske datoer til ISO.
- [ColorAgent.cs](ColorAgent.cs): klassifikation af farver med ét choice-kald.
- [IJevDecisions.cs](IJevDecisions.cs): det udskiftelige interface, bookingagenten bruger i tests og drift.
- [JevConnector.cs](JevConnector.cs): HTTP-kald, typede svar og forbrugsoplysninger.
- [Tests](Tests): offline-tests og særskilt tilvalgte API-tests.

Constructoren kræver model og API-nøgle. Endpoint, `HttpClient` og USD/DKK-kurs er valgfrie:

```csharp
using var jev = new JevConnector(model, apiKey, usdToDkkRate: 6.5m);
```

Standardmodellen i programmet er `typesafe/jev-1.13`. Standardkursen er **6,5 DKK pr. USD**, ikke en livekurs.

De oprindelige metoder med `State`, `Question` og `Choices` er bevaret. Bookingagenten bruger nye overloads, hvor state, spørgsmål og svarmuligheder gives som argumenter uden at ændre connectorens properties.

## ColorAgent

Agenten bruger de traditionelle **RYB-primærfarver (rød, gul, blå)**, ikke RGB-skærmfarver. Den modtager et farvenavn, en beskrivelse eller en hex-kode og returnerer `JevChoiceAnswer`, inklusive sandsynligheder, tidsforbrug og API-pris.

```csharp
using var jev = new JevConnector("typesafe/jev-1.13", apiKey);
var agent = new ColorAgent(jev);

var answer = await agent.ClassifyAsync("lyserød");
Console.WriteLine(answer.Value); // Forventet: Rød
Console.WriteLine($"API-pris: {answer.Usage.CostDkk:0.000000000} DKK");
```

Andre eksempler er `gyldengul` → `Gul` og `#000080` → `Blå`. Klassifikationen er en modelvurdering, ikke en matematisk farveafstand. Blandingsfarver tvinges ind i én af de tre kategorier; prompten angiver rækkefølgen rød, gul, blå ved lige vægt. Eksemplet forudsætter en faktisk farve og har ingen særskilt kategori for ukendte input eller neutrale farver.

`IJevDecisions` kan udskiftes i tests. Tomme input afvises, og API-fejl sendes videre til kalderen. `ColorAgent.RunAsync()` pakker eksemplet ind med API-konfiguration, konsolinput, udskrift og fejlhåndtering på samme måde som bogføringseksemplet. Det er nu det aktive kald i [Program.cs](Program.cs).

Kør kun farvetestene uden API-kald:

```powershell
dotnet test .\Tests\Tests.csproj --filter "FullyQualifiedName~ColorAgentTests&Category!=LiveApi"
```

Med `JEV_LIVE_TESTS=1` og `OpenRouterKey` kan API-testen køres med filteret `FullyQualifiedName~ColorAgentTests&Category=LiveApi`.

## Bookingdialog

Opret **én agent pr. samtale**, og afvent hvert `SendAsync` før næste besked. Agenten gemmer oplysningerne i hukommelsen; en ny instans starter en ny samtale.

Dialogen er et **styret C#-flow**, ikke en fri LLM-chat. C# vælger spørgsmålene i denne rækkefølge:

1. Hytte eller plads til campingvogn.
2. Ankomstdato.
3. Afrejsedato.
4. Antal voksne.
5. Antal børn.

Kun manglende eller ugyldige oplysninger efterspørges. Hvis brugeren allerede har oplyst flere felter i samme besked, bevares de, og de tilhørende spørgsmål springes over. JEV bruges til at klassificere og fortolke fritekst — ikke til at generere spørgsmål eller det afsluttende JSON.

```csharp
using var jev = new JevConnector("typesafe/jev-1.13", apiKey);
var agent = new BookingServiceAgent(jev);

var reply = agent.GetNextReply();
// reply.Message: "Hvad vil du booke: en hytte eller en plads til campingvogn?"
reply = await agent.SendAsync("hytte");
reply = await agent.SendAsync("10/7/2027");
reply = await agent.SendAsync("17. juli 2027");
reply = await agent.SendAsync("2");
reply = await agent.SendAsync("0");
// reply.Status == BookingStatus.Ready
// reply.Details indeholder datoer, antal og AccommodationType.Cabin.
// reply.Message er tom, da det færdige resultat er data, ikke en fritekstopsummering.
```

Datoerne i eksemplet skal ligge i fremtiden, og afrejse skal være inden for ét kalenderår fra dagsdatoen, når det køres.

Beslutninger og validering:

1. Startspørgsmålet kommer fra `GetNextReply()` uden et API-kald. Ved fritekst vurderer JEV, om beskeden handler om booking eller besvarer et tidligere bookingspørgsmål. Andre emner giver `NotBooking` uden at ændre indsamlede oplysninger.
2. C# finder og normaliserer datoer lokalt. Ved booking foretages fem **sekventielle** choice-kald: ankomst, afrejse, voksne, børn og opholdstype. Svarmulighederne bygges dynamisk fra de normaliserede datoer og tal i beskeden samt mulighederne hytte/campingvogn. JEV kan også vælge, at et felt mangler eller er uklart.
3. Udtræk ser kun den nye besked og det felt, brugeren blev spurgt om. Gamle feltværdier og valideringsregler holdes ude af udtrækket, så de ikke påvirker fortolkning af rettelser.
4. C# gemmer nye oplysninger, bevarer ikke-omtalte felter og validerer hele resultatet. `NeedsInformation` indeholder ét konkret spørgsmål om det første manglende eller ugyldige felt i dialogrækkefølgen: opholdstype, datoer, voksne og børn. Brugeren må gerne besvare flere felter eller rette tidligere oplysninger i samme besked.
5. Entydige, korte svar på det aktuelle spørgsmål (`10/7`, `10. juli 2027`, `2027-07-10`, `2`, `0`, `hytte` eller `plads til campingvogn`) behandles direkte uden nye API-kald. En ugyldig dato eller et negativt antal er stadig et bookingsvar, ikke et andet emne.
6. `Ready` returneres først, når alle felter er gyldige. Det betyder **ikke**, at tilgængelighed er kontrolleret, eller at en reservation er oprettet.

Regler og afgrænsning:

- Datoer kan skrives som fx `10/7/2027`, `10.7.2027`, `10-7-2027`, `10. juli 2027` eller ISO `2027-07-10`. Numeriske danske datoer læses altid som **dag/måned**, uanset maskinens sprog. Det gælder både korte svar og datoer inde i en længere besked.
- Uden årstal (`10/7` eller `10. juli`) vælges den første gyldige forekomst **på eller efter dagsdatoen**, også hen over nytår og skudår. Et udtrykkeligt årstal ændres aldrig. Umulige datoer som `31/2` kræver rettelse. Ankomst og afrejse fortolkes hver for sig ud fra dagsdatoen; der gættes ikke på en anden afrejse for at få perioden til at passe.
- Ankomst skal være **efter i dag**, og afrejse **efter ankomst** og **senest dagsdatoen plus ét kalenderår**, inklusive grænsedatoen (`AddYears(1)`, ikke 365 dage). Ankomst skal derfor være før denne grænse. Disse regler gælder også datoer uden årstal. Dagsdatoen tages fra den injicerede `TimeProvider` i dens lokale tidszone (som standard maskinens). Der bruges datoer, ikke klokkeslæt.
- Mindst **1 voksen**, **0 eller flere børn**, **højst 12 personer i alt** og **højst 4 børn pr. voksen**, angivet som heltal. Manglende antal børn bliver ikke automatisk til 0. Med fx 1 voksen tillades højst 4 børn; med 3 voksne højst 9 børn på grund af totalgrænsen. Agenten beder om en rettelse ved overskridelse og kontrollerer også fordelingen igen, hvis antallet af voksne ændres.
- Opholdstype: **hytte** eller **plads til campingvogn**.
- Ingen fri datooversættelse som "næste fredag" og ingen ekstra generativ sprogmodel. Brug numeriske datoer eller danske månedsnavne skrevet helt ud; datoer med årstal skal bruge fire cifre til året. Ved uklare værdier skal brugeren præcisere.
- Maksimalt 4000 tegn og 253 forskellige dato- eller talkandidater pr. besked, så JEV's grænse på 255 svarmuligheder overholdes.
- JEV-fortolkning er probabilistisk, ikke en garanti. Faste regler valideres altid i C#. Vis de indsamlede oplysninger til brugeren før en eventuel rigtig reservation.
- Ved API-fejl eller cancellation kastes fejlen videre til brugerfladen; en delvist behandlet besked gemmes ikke. Ugyldigt input til metoden giver `ArgumentException`.

### Bookingkonsollen

[BookingConsole.cs](BookingConsole.cs) kobler agenten på `Console.In`, `Console.Out` og `Console.Error`. Under dialogen vises **kun det konkrete spørgsmål** — ingen velkomsttekst, vejledning eller `Agent:`/`Dig:`-labels. Ved ugyldige værdier indgår de nødvendige grænser i spørgsmålet; uvedkommende beskeder får det aktuelle spørgsmål igen.

Samme agent genbruges gennem samtalen. Når alle oplysninger er gyldige (`Ready`), serialiseres `BookingSummary` med `System.Text.Json`. Det afsluttende svar er **JSON uden en tekstopsummering bagefter**, og programmet afslutter uden at læse flere beskeder. Tomme linjer ignoreres; `/exit` eller slut på input afbryder uden yderligere tekst eller et færdigt bookingresultat. Tekniske fejl vises kort på `Console.Error`, så de ikke skjules, og du kan prøve igen uden at miste tidligere oplysninger.

Eksempel på afsluttende JSON fra en dialog med kun entydige svar (derfor ingen API-omkostninger):

```json
{
  "booking": {
    "arrival": "2027-07-10",
    "departure": "2027-07-17",
    "adults": 2,
    "children": 0,
    "accommodation": "cabin"
  },
  "totalCostUsd": 0,
  "totalCostDkk": 0
}
```

`accommodation` er `"cabin"` eller `"caravanPitch"`. Datoer i JSON er altid ISO, selv om brugeren skrev danske datoer. Priser er JSON-tal, ikke formaterede tekststrenge. JSON beskriver indsamlede bookingønsker; der er stadig **ikke oprettet en reservation**.

Prisen summeres i agentens `TotalCostUsd` og `TotalCostDkk` for hele samtalen, inklusive afklaringer, uvedkommende spørgsmål og rettelser. Entydige korte svar uden API-kald koster ikke ekstra. Modtaget forbrug fra en senere fejlet eller annulleret besked tælles stadig med, selv om beskedens bookingændringer ikke gemmes. Kald, hvor der ikke modtages forbrugsoplysninger, kan ikke medregnes; oversigten er derfor den registrerede API-pris, ikke en garanti for udbyderens endelige fakturering. DKK-beløbet bruger connectorens faste valutakurs.

Konsollaget kan også testes uden en terminal eller API-kald ved at give det en agent med en falsk `IJevDecisions` samt `StringReader`/`StringWriter`:

```csharp
await BookingConsole.RunAsync(agent, input, output, error);
```

Bogføringseksemplet er bevaret i [BogføringsAgent.cs](BogføringsAgent.cs). For at køre det i stedet kan kaldet i [Program.cs](Program.cs) ændres til `return await BogføringsAgent.RunAsync();`.

## Tests

Offline-tests bruger en falsk `IJevDecisions` og et fast tidspunkt. De foretager ingen API-kald:

```powershell
dotnet test .\Tests\Tests.csproj --filter "Category!=LiveApi"
```

API-tests bruger den rigtige connector, modellen og **fiktive bookingdata**. De koster penge og er sprunget over som standard. Med `OpenRouterKey` sat:

```powershell
$env:JEV_LIVE_TESTS = "1"
dotnet test .\Tests\Tests.csproj --filter "Category=LiveApi"
Remove-Item Env:\JEV_LIVE_TESTS
```

Testene dækker komplette beskeder, dialog over flere ture, uvedkommende spørgsmål, manglende oplysninger, rettelser, ugyldige datoer/antal, korte svar og fejlhåndtering. API-testenes datoer beregnes relativt til dagsdatoen.

## Hastighed og pris

Bogføringseksemplet viser vurdering, svartid, tokens, pris i USD/DKK og estimeret antal tilsvarende kald for 1 DKK.

I en eksempelkørsel tog hvert kald **401–819 ms** og kostede omkring **0,0001 DKK** — cirka **10.000 tilsvarende kald pr. krone**. Svar, tider og priser kan variere. Svartiden inkluderer netværk, og prisestimatet forudsætter samme pris pr. kald.

JEV er ét eksempel på en beslutningsmodel. Markedet ændrer sig hurtigt; se [OpenRouters aktuelle beslutningsmodeller](https://openrouter.ai/models?output_modalities=decisions&order=most-popular), og kontrollér API-kompatibilitet før et modelskift.

Brug fiktive bilag til øvelser: data sendes til OpenRouter og modeludbyderen. Modellens vurdering er et forslag, ikke en automatisk godkendelse af bilaget.

Det samme gælder bookingchatten: nye beskeder og samtalens bookingkontekst sendes til OpenRouter og modeludbyderen ved JEV-kald. Brug kun fiktive oplysninger i demoen og testene.
