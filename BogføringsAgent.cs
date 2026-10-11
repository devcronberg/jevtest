namespace JevTest;

public static class BogføringsAgent
{
    public static async Task<int> RunAsync()
    {
        try
        {
            // Programmet henter API-nøglen fra miljøvariablen, så den ikke gemmes i koden.
            var apiKey = Environment.GetEnvironmentVariable("OpenRouterKey")
                ?? Environment.GetEnvironmentVariable("OpenRouterKey", EnvironmentVariableTarget.User)
                ?? Environment.GetEnvironmentVariable("OpenRouterKey", EnvironmentVariableTarget.Machine);

            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("Set the OpenRouterKey environment variable before running the example.");

            // Et fiktivt bilag. decimal bruges til beløb, og DateOnly til en dato uden klokkeslæt.
            var tekst = "Køb af printerpapir og kuglepenne.";
            var dato = new DateOnly(2026, 10, 10);
            var beløb = 349.95m;

            // Model og API-nøgle gives til constructoren og bruges til alle kald.
            var model = "typesafe/jev-1.13";
            // Endpoint, HttpClient og valutakurs er valgfrie; standardkursen er 6,5 DKK pr. USD.
            // using sørger for at frigive connectorens egen HttpClient efter eksemplerne.
            // State er teksten, som JEV skal vurdere. Vi genbruger den i alle tre eksempler.
            using var jev = new JevConnector(model, apiKey)
            {
                State = FormattableString.Invariant($"""
            Tekst: {tekst}
            Dato: {dato:yyyy-MM-dd}
            Beløb: {beløb:F2} DKK
            """)
            };

            // Dato og beløb sendes i et entydigt format, uanset computerens sprogindstillinger.
            Console.WriteLine($"Bilag:\n{jev.State}\n");
            // Vurderingerne er forslag til regnskabssystemet, ikke en automatisk godkendelse.

            // NOUL: Stil et ja/nej-spørgsmål. JEV returnerer sandsynligheden for ja.
            // Hvert await nedenfor laver et separat API-kald, som kan koste penge.
            // Kaldet sender den aktuelle State og Question; der er ingen chathistorik.
            Console.WriteLine("1. True/false (noul)");
            jev.Question = "Beskriver bilaget et køb af kontorartikler til virksomheden?";
            var booleanAnswer = await jev.AskAsync();
            // Value er true ved en sandsynlighed på mindst 0,5 - ikke en garanti for ja.
            // Probability ligger mellem 0 og 1. Formatet P1 viser procent med en decimal.
            Console.WriteLine($"Svar: {booleanAnswer.Value}");
            Console.WriteLine($"Sandsynlighed for ja: {booleanAnswer.Probability:P1}");
            PrintUsage(booleanAnswer.Usage, booleanAnswer.Duration);

            // CHOICE: JEV vælger blandt de svarmuligheder, vi angiver i Choices.
            // Probabilities indeholder sandsynligheden for hver mulighed, ikke kun vinderen.
            Console.WriteLine("\n2. Valg fra en liste (choice)");
            jev.Question = "Hvilken udgiftskategori passer bedst til bilaget?";
            jev.Choices = ["Kontorartikler", "Transport", "Forplejning", "Andet"];
            var choiceAnswer = await jev.ChooseAsync();
            Console.WriteLine($"Svar: {choiceAnswer.Value}");
            foreach (var option in choiceAnswer.Probabilities)
                Console.WriteLine($"  {option.Key}: {option.Value:P1}");
            PrintUsage(choiceAnswer.Usage, choiceAnswer.Duration);

            // SCORE: Levels beskriver en ordnet skala fra lavest til højest.
            // Tre niveauer giver skalaen 0-2. Scoren er et sandsynlighedsvægtet
            // gennemsnit og kan derfor fx være 1,8; den er ikke en procent.
            // ScoreAsync bruger Levels, så Choices fra det forrige eksempel ignoreres.
            Console.WriteLine("\n3. Vurdering på en skala (score)");
            jev.Question = "Hvor tydeligt beskriver bilaget, hvad virksomheden har købt?";
            jev.Levels = ["Uklar beskrivelse", "Delvist tydelig beskrivelse", "Tydelig og konkret beskrivelse"];
            var scoreAnswer = await jev.ScoreAsync();
            // Legend forbinder hvert niveaus nummer med beskrivelsen i Levels.
            Console.WriteLine($"Score: {scoreAnswer.Value:F2} på skalaen 0-{scoreAnswer.Legend.Count - 1}");
            foreach (var level in scoreAnswer.Probabilities)
                Console.WriteLine($"  {level.Key}: {scoreAnswer.Legend[level.Key]}: {level.Value:P1}");
            PrintUsage(scoreAnswer.Usage, scoreAnswer.Duration);

            return 0;
        }
        catch (Exception ex)
        {
            // Ved en fejl stopper resten af eksemplerne. Exitkode 1 betyder fejl;
            // return 0 ovenfor betyder, at alle tre kald blev gennemført.
            Console.Error.WriteLine($"Fejl: {ex.Message}");
            return 1;
        }
    }

    static void PrintUsage(JevUsage usage, TimeSpan duration)
    {
        // Svartiden inkluderer netværk og læsning af svaret, ikke kun modellens beregning.
        // Cost er API-prisen i USD, ikke bilagets beløb i DKK. Bevar de små decimaler.
        Console.WriteLine($"Tid: {duration.TotalMilliseconds:F0} ms");
        Console.WriteLine($"Tokens: {usage.InputTokens} ind / {usage.OutputTokens} ud");
        Console.WriteLine($"API-pris: {usage.Cost:0.000000000} USD");
        // Connectoren omregner med den faste kurs fra constructoren, ikke en livekurs.
        Console.WriteLine($"API-pris: {usage.CostDkk:0.000000000} DKK (kurs {usage.UsdToDkkRate})");
        // Estimatet gælder kun, hvis de næste kald har samme pris som dette.
        Console.WriteLine(usage.CallsPerDkk is { } calls
            ? $"Hele kald for 1 DKK ved samme pris: {calls:N0}"
            : "Kald for 1 DKK: kan ikke beregnes, fordi API-prisen er 0 DKK.");
    }
}
