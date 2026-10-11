using JevTest;
using System.Text.Json;

namespace Tests;

public sealed class BookingConsoleTests
{
    [Fact]
    public async Task ConsoleStopsWhenReadyAndShowsCostForEntireConversation()
    {
        var jev = new StubDecisions();
        var agent = new BookingServiceAgent(jev, new FixedTimeProvider());
        using var input = new StringReader(
            "\nJeg vil booke.\nhytte\n10/7\n17. juli\n2\n0\nDenne linje må ikke læses.");
        using var output = new StringWriter();
        using var error = new StringWriter();

        await BookingConsole.RunAsync(agent, input, output, error);

        AssertPromptOrder(output.ToString());
        AssertOnlyQuestionsBeforeJson(output.ToString());
        Assert.DoesNotContain("Alle oplysninger er klar", output.ToString());
        Assert.DoesNotContain("Bookingchat afsluttet.", output.ToString());
        using var summary = ReadSummary(output.ToString());
        var booking = summary.RootElement.GetProperty("booking");
        Assert.Equal("2027-07-10", booking.GetProperty("arrival").GetString());
        Assert.Equal("2027-07-17", booking.GetProperty("departure").GetString());
        Assert.Equal(2, booking.GetProperty("adults").GetInt32());
        Assert.Equal(0, booking.GetProperty("children").GetInt32());
        Assert.Equal("cabin", booking.GetProperty("accommodation").GetString());
        Assert.Empty(error.ToString());
        Assert.Equal(1, jev.IntentCalls);
        Assert.Equal(5, jev.ChoiceCalls);
        Assert.Equal(0.000006m, agent.TotalCostUsd);
        Assert.Equal(0.000039m, agent.TotalCostDkk);
        Assert.Equal(0.000039m, summary.RootElement.GetProperty("totalCostDkk").GetDecimal());
        Assert.Equal(0.000006m, summary.RootElement.GetProperty("totalCostUsd").GetDecimal());
        Assert.Equal(3, summary.RootElement.EnumerateObject().Count());
        Assert.Equal("Denne linje må ikke læses.", await input.ReadLineAsync());
    }

    [Theory]
    [InlineData("hytte", "cabin")]
    [InlineData("plads til campingvogn", "caravanPitch")]
    public async Task GuidedFlowStartsWithAccommodationAndNeedsNoApiForExactAnswers(string answer, string expectedType)
    {
        var jev = new StubDecisions();
        var agent = new BookingServiceAgent(jev, new FixedTimeProvider());
        using var input = new StringReader($"{answer}\n10/7\n17. juli\n2\n0\nMå ikke læses.");
        using var output = new StringWriter();
        using var error = new StringWriter();

        await BookingConsole.RunAsync(agent, input, output, error);

        AssertPromptOrder(output.ToString());
        AssertOnlyQuestionsBeforeJson(output.ToString());
        Assert.Equal(5, output.ToString()[..output.ToString().IndexOf('{')].Split(
            Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal(0, jev.IntentCalls);
        Assert.Equal(0, jev.ChoiceCalls);
        using var summary = ReadSummary(output.ToString());
        Assert.Equal(expectedType, summary.RootElement.GetProperty("booking").GetProperty("accommodation").GetString());
        Assert.Equal(0m, summary.RootElement.GetProperty("totalCostUsd").GetDecimal());
        Assert.Equal(0m, summary.RootElement.GetProperty("totalCostDkk").GetDecimal());
        Assert.Empty(error.ToString());
        Assert.Equal("Må ikke læses.", await input.ReadLineAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" /EXIT \n")]
    public async Task EofOrExitStopsWithoutApiCalls(string text)
    {
        var jev = new StubDecisions();
        using var input = new StringReader(text);
        using var output = new StringWriter();
        using var error = new StringWriter();

        await BookingConsole.RunAsync(new BookingServiceAgent(jev), input, output, error);

        Assert.Equal(0, jev.IntentCalls);
        Assert.Equal("Hvad vil du booke: en hytte eller en plads til campingvogn?" + Environment.NewLine, output.ToString());
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task ApiFailureIsDisplayedAndUserCanRetry()
    {
        var jev = new StubDecisions { FailNextCall = true };
        using var input = new StringReader("Jeg vil booke.\nJeg vil booke.\n/exit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();

        await BookingConsole.RunAsync(new BookingServiceAgent(jev), input, output, error);

        Assert.Contains("Bookingfejl: Simuleret API-fejl.", error.ToString());
        Assert.Equal("Bookingfejl: Simuleret API-fejl." + Environment.NewLine, error.ToString());
        Assert.Contains("Hvad vil du booke:", output.ToString());
        Assert.Equal(2, jev.IntentCalls);
    }

    private static void AssertPromptOrder(string output)
    {
        Assert.StartsWith("Hvad vil du booke: en hytte eller en plads til campingvogn?", output);
        string[] prompts =
        [
            "Hvad vil du booke:",
            "Hvilken dato vil I ankomme?",
            "Hvilken dato vil I rejse hjem?",
            "Hvor mange voksne kommer I?",
            "Hvor mange børn kommer I?"
        ];
        var previous = -1;
        foreach (var prompt in prompts)
        {
            var index = output.IndexOf(prompt, StringComparison.Ordinal);
            Assert.True(index > previous, $"Spørgsmålet '{prompt}' mangler eller står i forkert rækkefølge.");
            previous = index;
        }
    }

    private static void AssertOnlyQuestionsBeforeJson(string output)
    {
        var start = output.IndexOf('{');
        Assert.True(start >= 0);
        var lines = output[..start].Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.All(lines, line =>
        {
            Assert.EndsWith("?", line);
            Assert.DoesNotContain("Agent:", line);
            Assert.DoesNotContain("Dig:", line);
            Assert.DoesNotContain("Skriv", line);
        });
    }

    private static JsonDocument ReadSummary(string output)
    {
        var start = output.IndexOf('{');
        Assert.True(start >= 0, "JSON-resultatet mangler.");
        return JsonDocument.Parse(output[start..]);
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class StubDecisions : IJevDecisions
    {
        private static readonly JevUsage Usage = new(0, 0, 0.000001m, 6.5m);
        public int IntentCalls { get; private set; }
        public int ChoiceCalls { get; private set; }
        public bool FailNextCall { get; set; }

        public Task<JevBooleanAnswer> AskAsync(
            string state, string question, CancellationToken cancellationToken = default)
        {
            IntentCalls++;
            if (FailNextCall)
            {
                FailNextCall = false;
                throw new HttpRequestException("Simuleret API-fejl.");
            }
            return Task.FromResult(new JevBooleanAnswer(1, Usage, TimeSpan.Zero));
        }

        public Task<JevChoiceAnswer> ChooseAsync(
            string state, string question, IReadOnlyList<string> choices, CancellationToken cancellationToken = default)
        {
            ChoiceCalls++;
            return Task.FromResult(new JevChoiceAnswer(
                choices[0], choices.ToDictionary(choice => choice, choice => choice == choices[0] ? 1.0 : 0),
                1, Usage, TimeSpan.Zero));
        }
    }
}
