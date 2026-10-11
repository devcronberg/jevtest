using JevTest;

namespace Tests;

public sealed class ColorAgentTests
{
    [Theory]
    [InlineData("lyserød", "Rød")]
    [InlineData("gyldengul", "Gul")]
    [InlineData("#000080", "Blå")]
    public async Task UsesOneChoiceCallAndPreservesAnswerMetadata(string input, string selected)
    {
        var expected = new JevChoiceAnswer(selected,
            new Dictionary<string, double> { ["Rød"] = 0.2, ["Gul"] = 0.3, ["Blå"] = 0.5 },
            0.8, new JevUsage(100, 10, 0.00001m, 6.5m), TimeSpan.FromMilliseconds(123));
        var jev = new StubDecisions(expected);
        using var cancellation = new CancellationTokenSource();

        var actual = await new ColorAgent(jev).ClassifyAsync(input, cancellation.Token);

        Assert.Same(expected, actual);
        Assert.Equal(input, jev.State);
        Assert.Equal(["Rød", "Gul", "Blå"], jev.Choices);
        Assert.Equal(cancellation.Token, jev.CancellationToken);
        Assert.Equal(1, jev.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public async Task RejectsEmptyInputBeforeCallingJev(string? input)
    {
        var jev = new StubDecisions();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => new ColorAgent(jev).ClassifyAsync(input!));
        Assert.Equal(0, jev.Calls);
    }

    [Fact]
    public async Task CancellationDoesNotCallJev()
    {
        var jev = new StubDecisions();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ColorAgent(jev).ClassifyAsync("blå", cancellation.Token));
        Assert.Equal(0, jev.Calls);
    }

    [Fact]
    public async Task PropagatesApiFailure()
    {
        var jev = new StubDecisions();
        await Assert.ThrowsAsync<HttpRequestException>(() => new ColorAgent(jev).ClassifyAsync("blå"));
    }

    [Fact]
    public async Task RejectsUnlistedColor()
    {
        var answer = new JevChoiceAnswer("Grøn", new Dictionary<string, double> { ["Grøn"] = 1 },
            1, new JevUsage(0, 0, 0, 6.5m), TimeSpan.Zero);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ColorAgent(new StubDecisions(answer)).ClassifyAsync("grøn"));
    }

    [LiveJevFact]
    [Trait("Category", "LiveApi")]
    public async Task RealJevMapsColorNamesAndHexToPrimaryColors()
    {
        var key = Environment.GetEnvironmentVariable("OpenRouterKey")
            ?? Environment.GetEnvironmentVariable("OpenRouterKey", EnvironmentVariableTarget.User)
            ?? Environment.GetEnvironmentVariable("OpenRouterKey", EnvironmentVariableTarget.Machine);
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("OpenRouterKey skal være sat for at køre API-tests.");

        using var jev = new JevConnector("typesafe/jev-1.13", key);
        var agent = new ColorAgent(jev);
        foreach (var (input, expected) in new[]
        {
            ("lyserød", "Rød"), ("gyldengul", "Gul"), ("#000080", "Blå")
        })
        {
            var answer = await agent.ClassifyAsync(input);
            Assert.Equal(expected, answer.Value);
            Assert.Equal(3, answer.Probabilities.Count);
            Assert.True(answer.Usage.Cost > 0);
        }
    }

    private sealed class StubDecisions(JevChoiceAnswer? answer = null) : IJevDecisions
    {
        public int Calls { get; private set; }
        public string? State { get; private set; }
        public IReadOnlyList<string>? Choices { get; private set; }
        public CancellationToken CancellationToken { get; private set; }

        public Task<JevBooleanAnswer> AskAsync(
            string state, string question, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("ColorAgent må kun bruge choice.");

        public Task<JevChoiceAnswer> ChooseAsync(
            string state, string question, IReadOnlyList<string> choices, CancellationToken cancellationToken = default)
        {
            Calls++;
            State = state;
            Choices = choices;
            CancellationToken = cancellationToken;
            return answer is null
                ? throw new HttpRequestException("Simuleret API-fejl.")
                : Task.FromResult(answer);
        }
    }
}
