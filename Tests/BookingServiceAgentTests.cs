using System.Text.Json;
using JevTest;

namespace Tests;

public sealed class BookingServiceAgentTests
{
    private static readonly BookingDetails Complete = new(
        new(2027, 7, 10), new(2027, 7, 17), 2, 0, AccommodationType.Cabin);
    private const string CompleteMessage =
        "Jeg vil booke en hytte fra 2027-07-10 til 2027-07-17 til 2 voksne og 0 børn.";

    [Fact]
    public void OpeningQuestionAsksForAccommodationWithoutApiCalls()
    {
        var jev = new ScriptedDecisions();
        var agent = Create(jev);

        var reply = agent.GetNextReply();

        Assert.Equal(BookingStatus.NeedsInformation, reply.Status);
        Assert.Equal(BookingField.Accommodation, reply.NextField);
        Assert.Equal("Hvad vil du booke: en hytte eller en plads til campingvogn?", reply.Message);
        Assert.Equal(new BookingDetails(), reply.Details);
        Assert.Empty(jev.Calls);
        Assert.Equal(0m, agent.TotalCostUsd);
    }

    [Fact]
    public async Task DatesAndGuestsGivenEarlyAreKeptButAccommodationIsAskedFirst()
    {
        var jev = new ScriptedDecisions(new Turn(new()
        {
            [BookingField.Arrival] = "2027-07-10",
            [BookingField.Departure] = "2027-07-17",
            [BookingField.Adults] = "2"
        }));
        var agent = Create(jev);
        var first = await agent.SendAsync("Book fra 10/7 til 17/7 for 2 voksne.");
        Assert.Equal(BookingField.Accommodation, first.NextField);

        var next = await agent.SendAsync("hytte");
        Assert.Equal(BookingField.Children, next.NextField);
        Assert.Equal(Complete with { Children = null }, next.Details);
        Assert.Equal(6, jev.Calls.Count);
    }

    [Fact]
    public async Task NonBookingStopsAfterIntent()
    {
        var jev = new ScriptedDecisions(new Turn(IsBooking: false));
        var reply = await Create(jev).SendAsync("Hvordan er vejret?");

        Assert.Equal(BookingStatus.NotBooking, reply.Status);
        Assert.Equal("Hvad vil du booke: en hytte eller en plads til campingvogn?", reply.Message);
        Assert.Equal(new BookingDetails(), reply.Details);
        Assert.Single(jev.Calls);
    }

    [Fact]
    public async Task CostsIncludeOffTopicAndBookingDecisionsButNotLiteralReplies()
    {
        var agent = Create(new ScriptedDecisions(new Turn(IsBooking: false), new Turn()));
        Assert.Equal(0m, agent.TotalCostUsd);
        Assert.Equal(0m, agent.TotalCostDkk);
        await agent.SendAsync("Hvordan er vejret?");
        Assert.Equal(0.00001m, agent.TotalCostUsd);
        await agent.SendAsync("Jeg vil booke.");
        Assert.Equal(0.00007m, agent.TotalCostUsd);
        Assert.Equal(0.000455m, agent.TotalCostDkk);
        await agent.SendAsync("hytte");
        await agent.SendAsync("2027-07-10");
        Assert.Equal(0.00007m, agent.TotalCostUsd);
        Assert.Equal(0.000455m, agent.TotalCostDkk);
    }

    [Fact]
    public async Task CompleteMessageProducesTypedDetailsWithIsolatedExtractionAndBoundedChoices()
    {
        var jev = new ScriptedDecisions(FullTurn());
        var reply = await Create(jev).SendAsync(CompleteMessage);

        Assert.Equal(BookingStatus.Ready, reply.Status);
        Assert.Equal(Complete, reply.Details);
        Assert.Null(reply.NextField);
        Assert.Empty(reply.Message);
        Assert.Equal(6, jev.Calls.Count);
        var departureCall = jev.Calls.Single(call => call.Field == BookingField.Departure);
        using var state = JsonDocument.Parse(departureCall.State);
        Assert.Equal(CompleteMessage, state.RootElement.GetProperty("Message").GetString());
        Assert.False(state.RootElement.TryGetProperty("Collected", out _));
        var adultsCall = jev.Calls.Single(call => call.Field == BookingField.Adults);
        Assert.Equal(["Ikke oplyst", "Uklart eller ikke understøttet", "2", "0"], adultsCall.Choices);
    }

    [Fact]
    public async Task CollectsShortAnswersAcrossTurnsAndPreservesContextAcrossOffTopicMessage()
    {
        var jev = new ScriptedDecisions(
            new Turn(),
            new Turn(IsBooking: false));
        var agent = Create(jev);

        var first = await agent.SendAsync("Jeg vil booke en ferie.");
        Assert.Equal(BookingField.Accommodation, first.NextField);
        Assert.Equal(BookingField.Arrival, (await agent.SendAsync("Plads til campingvogn")).NextField);
        var arrival = await agent.SendAsync("2027-07-10");
        Assert.Equal(BookingField.Departure, arrival.NextField);
        var offTopic = await agent.SendAsync("Hvordan er vejret?");
        Assert.Equal(BookingStatus.NotBooking, offTopic.Status);
        Assert.Equal(arrival.Message, offTopic.Message);
        Assert.Equal(arrival.Details, offTopic.Details);
        Assert.Equal(BookingField.Adults, (await agent.SendAsync("2027-07-17")).NextField);
        Assert.Equal(BookingField.Children, (await agent.SendAsync("2")).NextField);
        var ready = await agent.SendAsync("0");
        Assert.Equal(BookingStatus.Ready, ready.Status);
        Assert.Equal(Complete with { Accommodation = AccommodationType.CaravanPitch }, ready.Details);
        Assert.Null(first.Details.Arrival);

        Assert.Equal(7, jev.Calls.Count);
        using var state = JsonDocument.Parse(jev.Calls[^1].State);
        Assert.Equal(arrival.Message, state.RootElement.GetProperty("PreviousQuestion").GetString());
    }

    [Fact]
    public async Task InvalidLiteralRepliesStayInBookingFlowWithoutModelClassification()
    {
        var jev = new ScriptedDecisions(new Turn());
        var agent = Create(jev);
        await agent.SendAsync("Book et ophold.");
        await agent.SendAsync("hytte");
        Assert.Equal(BookingField.Arrival, (await agent.SendAsync("2026-10-11")).NextField);
        Assert.Equal(BookingField.Arrival, (await agent.SendAsync("2027-02-30")).NextField);
        Assert.Equal(BookingField.Departure, (await agent.SendAsync("2027-07-10")).NextField);
        Assert.Equal(BookingField.Departure, (await agent.SendAsync("2027-07-10")).NextField);
        Assert.Equal(BookingField.Adults, (await agent.SendAsync("2027-07-17")).NextField);
        Assert.Equal(BookingField.Adults, (await agent.SendAsync("0")).NextField);
        Assert.Equal(BookingField.Adults, (await agent.SendAsync("1.5")).NextField);
        Assert.Equal(BookingField.Children, (await agent.SendAsync("2")).NextField);
        Assert.Equal(BookingField.Children, (await agent.SendAsync("-1")).NextField);
        Assert.Equal(BookingField.Children, (await agent.SendAsync("2147483648")).NextField);
        Assert.Equal(BookingStatus.Ready, (await agent.SendAsync("0")).Status);
        Assert.Equal(6, jev.Calls.Count);
    }

    [Fact]
    public async Task PreservesOutOfOrderInformationAndDoesNotDefaultChildrenToZero()
    {
        var jev = new ScriptedDecisions(
            Value(BookingField.Accommodation, "Hytte"),
            new Turn(new()
            {
                [BookingField.Arrival] = "2027-07-10",
                [BookingField.Departure] = "2027-07-17",
                [BookingField.Adults] = "2"
            }));
        var agent = Create(jev);
        Assert.Equal(BookingField.Arrival, (await agent.SendAsync("Book en hytte")).NextField);
        var reply = await agent.SendAsync("Fra 2027-07-10 til 2027-07-17, 2 voksne.");
        Assert.Equal(BookingField.Children, reply.NextField);
        Assert.Null(reply.Details.Children);
        Assert.Equal(AccommodationType.Cabin, reply.Details.Accommodation);
    }

    [Theory]
    [InlineData("10/7/2027")]
    [InlineData("10.7.2027")]
    [InlineData("10-7-2027")]
    [InlineData("10. juli 2027")]
    [InlineData("10 JULI 2027")]
    [InlineData("10/7")]
    [InlineData("10.7")]
    [InlineData("10-7")]
    [InlineData("10. juli")]
    [InlineData("2027-7-10")]
    [InlineData("2027-07-10")]
    public async Task DanishLiteralDatesAreParsedLocallyWithoutApiCalls(string text)
    {
        var jev = new ScriptedDecisions(new Turn());
        var agent = Create(jev);
        await agent.SendAsync("Jeg vil booke.");
        await agent.SendAsync("hytte");

        var reply = await agent.SendAsync(text);
        Assert.Equal(BookingField.Departure, reply.NextField);
        Assert.Equal(new DateOnly(2027, 7, 10), reply.Details.Arrival);
        var departure = await agent.SendAsync("17. juli");
        Assert.Equal(BookingField.Adults, departure.NextField);
        Assert.Equal(new DateOnly(2027, 7, 17), departure.Details.Departure);
        Assert.Equal(6, jev.Calls.Count);
        Assert.Equal(0.00006m, agent.TotalCostUsd);
    }

    [Theory]
    [InlineData("31/2/2027")]
    [InlineData("31. februar")]
    [InlineData("29/2/2027")]
    [InlineData("10/7/2026")]
    [InlineData("11/10")]
    public async Task InvalidOrPastDanishDatesAreNotSilentlyMovedToValidDates(string text)
    {
        var jev = new ScriptedDecisions(new Turn());
        var agent = Create(jev);
        await agent.SendAsync("Jeg vil booke.");
        await agent.SendAsync("hytte");

        var reply = await agent.SendAsync(text);
        Assert.Equal(BookingField.Arrival, reply.NextField);
        Assert.Equal(BookingStatus.NeedsInformation, reply.Status);
        Assert.Equal(6, jev.Calls.Count);
    }

    [Fact]
    public async Task YearlessLeapDayUsesNextActualOccurrence()
    {
        var clock = new FixedTimeProvider { Now = new(2027, 3, 1, 12, 0, 0, TimeSpan.Zero) };
        var agent = new BookingServiceAgent(new ScriptedDecisions(new Turn()), clock);
        await agent.SendAsync("Jeg vil booke.");
        await agent.SendAsync("hytte");
        var reply = await agent.SendAsync("29. februar");
        Assert.Equal(new DateOnly(2028, 2, 29), reply.Details.Arrival);
        Assert.Equal(BookingField.Departure, reply.NextField);
    }

    [Fact]
    public async Task YearlessDatesDoNotBypassOneYearBookingLimit()
    {
        var agent = Create(new ScriptedDecisions(new Turn()));
        await agent.SendAsync("Jeg vil booke.");
        await agent.SendAsync("hytte");
        var reply = await agent.SendAsync("29/2");
        Assert.Equal(new DateOnly(2028, 2, 29), reply.Details.Arrival);
        Assert.Equal(BookingField.Arrival, reply.NextField);
    }

    [Fact]
    public async Task DanishDatesInsideFullMessageAreNormalizedBeforeJevAndDoNotBecomeGuestCounts()
    {
        var jev = new ScriptedDecisions(FullTurn());
        var reply = await Create(jev).SendAsync(
            "Book en hytte fra 10. juli til 17/7/2027, 2 voksne og 0 børn.");

        Assert.Equal(BookingStatus.Ready, reply.Status);
        Assert.Equal(Complete, reply.Details);
        using var state = JsonDocument.Parse(jev.Calls[1].State);
        Assert.Equal("Book en hytte fra 2027-07-10 til 2027-07-17, 2 voksne og 0 børn.",
            state.RootElement.GetProperty("Message").GetString());
        Assert.Equal(["Ikke oplyst", "Uklart eller ikke understøttet", "2", "0"],
            jev.Calls.Single(call => call.Field == BookingField.Adults).Choices);
    }

    [Fact]
    public async Task DanishDateParsingDoesNotDependOnMachineCulture()
    {
        var previousCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
            var agent = Create(new ScriptedDecisions(new Turn()));
            await agent.SendAsync("Jeg vil booke.");
            await agent.SendAsync("hytte");
            var reply = await agent.SendAsync("10/7");
            Assert.Equal(new DateOnly(2027, 7, 10), reply.Details.Arrival);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Theory]
    [InlineData("2026-10-10", "2026-10-20", BookingField.Arrival)]
    [InlineData("2026-10-11", "2026-10-20", BookingField.Arrival)]
    [InlineData("2026-10-12", "2026-10-12", BookingField.Departure)]
    [InlineData("2026-10-13", "2026-10-12", BookingField.Departure)]
    [InlineData("2027-02-30", "2027-03-10", BookingField.Arrival)]
    [InlineData("2027-02-20", "2027-02-30", BookingField.Departure)]
    public async Task RejectsInvalidDates(string arrival, string departure, BookingField expected)
    {
        var turn = FullTurn();
        turn.Values![BookingField.Arrival] = arrival;
        turn.Values[BookingField.Departure] = departure;
        var reply = await Create(new ScriptedDecisions(turn))
            .SendAsync($"Book hytte fra {arrival} til {departure}, 2 voksne, 0 børn.");
        Assert.Equal(BookingStatus.NeedsInformation, reply.Status);
        Assert.Equal(expected, reply.NextField);
    }

    [Fact]
    public async Task TomorrowAndFollowingDayAreValid()
    {
        var turn = FullTurn();
        turn.Values![BookingField.Arrival] = "2026-10-12";
        turn.Values[BookingField.Departure] = "2026-10-13";
        var reply = await Create(new ScriptedDecisions(turn))
            .SendAsync("Book hytte fra 2026-10-12 til 2026-10-13, 2 voksne og 0 børn.");
        Assert.Equal(BookingStatus.Ready, reply.Status);
    }

    [Theory]
    [InlineData(BookingField.Adults, "0")]
    [InlineData(BookingField.Adults, "-1")]
    [InlineData(BookingField.Adults, "1.5")]
    [InlineData(BookingField.Adults, "2147483648")]
    [InlineData(BookingField.Children, "-1")]
    [InlineData(BookingField.Children, "1,5")]
    public async Task RejectsInvalidCounts(BookingField field, string count)
    {
        var turn = FullTurn();
        turn.Values![field] = count;
        var adults = turn.Values[BookingField.Adults];
        var children = turn.Values[BookingField.Children];
        var reply = await Create(new ScriptedDecisions(turn)).SendAsync(
            $"Book hytte fra 2027-07-10 til 2027-07-17, {adults} voksne og {children} børn.");
        Assert.Equal(BookingStatus.NeedsInformation, reply.Status);
        Assert.Equal(field, reply.NextField);
    }

    [Theory]
    [InlineData("2027-10-10", "2027-10-11", null)]
    [InlineData("2027-10-10", "2027-10-12", BookingField.Departure)]
    [InlineData("2027-10-11", "2027-10-12", BookingField.Arrival)]
    [InlineData("2027-10-12", "2027-10-13", BookingField.Arrival)]
    public async Task EnforcesInclusiveOneYearDeadline(
        string arrival, string departure, BookingField? expectedField)
    {
        var turn = FullTurn();
        turn.Values![BookingField.Arrival] = arrival;
        turn.Values[BookingField.Departure] = departure;
        var reply = await Create(new ScriptedDecisions(turn))
            .SendAsync($"Book hytte fra {arrival} til {departure}, 2 voksne og 0 børn.");

        Assert.Equal(expectedField is null ? BookingStatus.Ready : BookingStatus.NeedsInformation, reply.Status);
        Assert.Equal(expectedField, reply.NextField);
        if (expectedField is not null)
            Assert.Contains("2027-10-11", reply.Message);
    }

    [Theory]
    [InlineData("2027-03-01", "2028-03-01")]
    [InlineData("2028-02-29", "2029-02-28")]
    public async Task DeadlineUsesCalendarYearIncludingLeapYears(string todayText, string deadlineText)
    {
        var today = DateOnly.ParseExact(todayText, "yyyy-MM-dd");
        var deadline = DateOnly.ParseExact(deadlineText, "yyyy-MM-dd");
        var arrival = deadline.AddDays(-1);
        var turn = FullTurn();
        turn.Values![BookingField.Arrival] = arrival.ToString("yyyy-MM-dd");
        turn.Values[BookingField.Departure] = deadlineText;
        var clock = new FixedTimeProvider
        {
            Now = new DateTimeOffset(today.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero)
        };
        var agent = new BookingServiceAgent(new ScriptedDecisions(turn,
            Value(BookingField.Departure, deadline.AddDays(1).ToString("yyyy-MM-dd"))), clock);

        Assert.Equal(BookingStatus.Ready, (await agent.SendAsync(
            $"Book hytte fra {arrival:yyyy-MM-dd} til {deadlineText}, 2 voksne og 0 børn.")).Status);
        var late = await agent.SendAsync($"Ret afrejsen til {deadline.AddDays(1):yyyy-MM-dd}.");
        Assert.Equal(BookingField.Departure, late.NextField);
        Assert.Contains(deadlineText, late.Message);
        Assert.Equal(BookingStatus.Ready, (await agent.SendAsync(deadlineText)).Status);
    }

    [Theory]
    [InlineData(1, 4, null)]
    [InlineData(1, 5, BookingField.Children)]
    [InlineData(2, 8, null)]
    [InlineData(2, 9, BookingField.Children)]
    [InlineData(2, 10, BookingField.Children)]
    [InlineData(3, 9, null)]
    [InlineData(3, 10, BookingField.Children)]
    [InlineData(12, 0, null)]
    [InlineData(12, 1, BookingField.Children)]
    [InlineData(13, 0, BookingField.Adults)]
    [InlineData(1, int.MaxValue, BookingField.Children)]
    [InlineData(int.MaxValue, int.MaxValue, BookingField.Adults)]
    public async Task EnforcesGroupSizeAndChildRatio(int adults, int children, BookingField? expectedField)
    {
        var turn = FullTurn();
        turn.Values![BookingField.Adults] = adults.ToString();
        turn.Values[BookingField.Children] = children.ToString();
        var reply = await Create(new ScriptedDecisions(turn)).SendAsync(
            $"Book hytte fra 2027-07-10 til 2027-07-17, {adults} voksne og {children} børn.");

        Assert.Equal(expectedField is null ? BookingStatus.Ready : BookingStatus.NeedsInformation, reply.Status);
        Assert.Equal(expectedField, reply.NextField);
        if (expectedField is not null)
            Assert.Contains("12", reply.Message);
        if (expectedField == BookingField.Children)
            Assert.Contains("4 børn pr. voksen", reply.Message);
    }

    [Fact]
    public async Task AdultCorrectionRevalidatesRatioAndLiteralReplyCanFixIt()
    {
        var turn = FullTurn();
        turn.Values![BookingField.Adults] = "3";
        turn.Values[BookingField.Children] = "9";
        var jev = new ScriptedDecisions(turn, Value(BookingField.Adults, "1"));
        var agent = Create(jev);
        Assert.Equal(BookingStatus.Ready, (await agent.SendAsync(
            "Book hytte fra 2027-07-10 til 2027-07-17, 3 voksne og 9 børn.")).Status);

        var invalid = await agent.SendAsync("Ret antallet af voksne til 1.");
        Assert.Equal(BookingField.Children, invalid.NextField);
        Assert.Contains("fra 0 til 4 børn", invalid.Message);
        Assert.Equal(BookingField.Children, (await agent.SendAsync("5")).NextField);
        var corrected = await agent.SendAsync("4");
        Assert.Equal(BookingStatus.Ready, corrected.Status);
        Assert.Equal(Complete with { Adults = 1, Children = 4 }, corrected.Details);
        Assert.Equal(12, jev.Calls.Count);
    }

    [Fact]
    public async Task CorrectingArrivalRevalidatesPreviouslyAcceptedDeparture()
    {
        var agent = Create(new ScriptedDecisions(FullTurn(), Value(BookingField.Arrival, "2027-07-20")));
        var original = await agent.SendAsync(CompleteMessage);
        var corrected = await agent.SendAsync("Ret ankomsten til 2027-07-20.");

        Assert.Equal(BookingField.Departure, corrected.NextField);
        Assert.Equal(Complete with { Arrival = new(2027, 7, 20) }, corrected.Details);
        Assert.Equal(Complete, original.Details);
    }

    [Fact]
    public async Task RetractedValueWithoutNumericCandidatesMustBeAskedAgain()
    {
        var agent = Create(new ScriptedDecisions(FullTurn(),
            Value(BookingField.Arrival, "Uklart eller ikke understøttet")));
        await agent.SendAsync(CompleteMessage);
        var reply = await agent.SendAsync("Jeg ved alligevel ikke, hvornår vi ankommer.");
        Assert.Equal(BookingField.Arrival, reply.NextField);
        Assert.Null(reply.Details.Arrival);
    }

    [Fact]
    public async Task ApiFailurePropagatesAndDoesNotCommitPartialTurn()
    {
        var jev = new ScriptedDecisions(
            FullTurn(),
            new Turn(new() { [BookingField.Arrival] = "2027-07-12" }, FailField: BookingField.Adults),
            new Turn());
        var agent = Create(jev);
        await agent.SendAsync(CompleteMessage);

        await Assert.ThrowsAsync<HttpRequestException>(() => agent.SendAsync("Ret ankomst til 2027-07-12."));
        Assert.Equal(0.00009m, agent.TotalCostUsd);
        Assert.Equal(0.000585m, agent.TotalCostDkk);
        var reply = await agent.SendAsync("Er oplysningerne klar?");
        Assert.Equal(Complete, reply.Details);
        Assert.Equal(0.00015m, agent.TotalCostUsd);
        Assert.Equal(0.000975m, agent.TotalCostDkk);
    }

    [Fact]
    public async Task CancellationDuringDecisionsDoesNotCommitPartialTurn()
    {
        var jev = new ScriptedDecisions(
            FullTurn(), Value(BookingField.Arrival, "2027-07-12"), new Turn());
        var agent = Create(jev);
        await agent.SendAsync(CompleteMessage);
        using var cancellation = new CancellationTokenSource();
        jev.OnChoose = field =>
        {
            if (field == BookingField.Departure)
                cancellation.Cancel();
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            agent.SendAsync("Ret ankomst til 2027-07-12.", cancellation.Token));
        Assert.Equal(0.00009m, agent.TotalCostUsd);
        Assert.Equal(0.000585m, agent.TotalCostDkk);
        jev.OnChoose = null;
        Assert.Equal(Complete, (await agent.SendAsync("Er oplysningerne klar?")).Details);
    }

    [Fact]
    public async Task UnsupportedAccommodationRemainsMissing()
    {
        var turn = FullTurn();
        turn.Values![BookingField.Accommodation] = "Uklart eller ikke understøttet";
        var reply = await Create(new ScriptedDecisions(turn)).SendAsync(
            "Book en teltplads fra 2027-07-10 til 2027-07-17, 2 voksne og 0 børn.");
        Assert.Equal(BookingField.Accommodation, reply.NextField);
        Assert.Null(reply.Details.Accommodation);
    }

    [Fact]
    public async Task ClockIsReadAgainEachTurn()
    {
        var clock = new FixedTimeProvider();
        var agent = new BookingServiceAgent(new ScriptedDecisions(FullTurn(), new Turn()), clock);
        await agent.SendAsync(CompleteMessage);
        clock.Now = new DateTimeOffset(2027, 7, 10, 12, 0, 0, TimeSpan.Zero);
        var reply = await agent.SendAsync("Er oplysningerne klar?");
        Assert.Equal(BookingField.Arrival, reply.NextField);
    }

    [Fact]
    public async Task InvalidInputAndCancellationMakeNoApiCalls()
    {
        var jev = new ScriptedDecisions();
        var agent = Create(jev);
        await Assert.ThrowsAsync<ArgumentException>(() => agent.SendAsync(" "));
        await Assert.ThrowsAsync<ArgumentException>(() => agent.SendAsync(new string('a', 4001)));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            agent.SendAsync(string.Join(" ", Enumerable.Range(0, 254))));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => agent.SendAsync("Book", cancellation.Token));
        Assert.Empty(jev.Calls);
    }

    private static BookingServiceAgent Create(ScriptedDecisions jev) => new(jev, new FixedTimeProvider());
    private static Turn Value(BookingField field, string value) => new(new() { [field] = value });
    private static Turn FullTurn() => new(new()
    {
        [BookingField.Arrival] = "2027-07-10",
        [BookingField.Departure] = "2027-07-17",
        [BookingField.Adults] = "2",
        [BookingField.Children] = "0",
        [BookingField.Accommodation] = "Hytte"
    });

    private sealed record Turn(
        Dictionary<BookingField, string>? Values = null,
        bool IsBooking = true, BookingField? FailField = null);
    private sealed record Call(BookingField? Field, string State, IReadOnlyList<string> Choices);

    private sealed class ScriptedDecisions(params Turn[] turns) : IJevDecisions
    {
        private readonly Queue<Turn> remaining = new(turns);
        private Turn? current;
        public List<Call> Calls { get; } = [];
        public Action<BookingField>? OnChoose { get; set; }
        private static readonly JevUsage Usage = new(1, 1, 0.00001m, 6.5m);

        public Task<JevBooleanAnswer> AskAsync(string state, string question, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            current = remaining.Dequeue();
            Calls.Add(new(null, state, []));
            return Task.FromResult(new JevBooleanAnswer(current.IsBooking ? 1 : 0, Usage, TimeSpan.Zero));
        }

        public Task<JevChoiceAnswer> ChooseAsync(
            string state, string question, IReadOnlyList<string> choices, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var field = Enum.GetValues<BookingField>().Single(field => question.Contains($"({field}", StringComparison.Ordinal));
            Calls.Add(new(field, state, choices));
            OnChoose?.Invoke(field);
            if (current?.FailField == field)
                throw new HttpRequestException("Simuleret API-fejl.");
            var value = current?.Values?.GetValueOrDefault(field) ?? "Ikke oplyst";
            Assert.Contains(value, choices);
            return Task.FromResult(new JevChoiceAnswer(
                value, choices.ToDictionary(choice => choice, choice => choice == value ? 1.0 : 0),
                1, Usage, TimeSpan.Zero));
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
