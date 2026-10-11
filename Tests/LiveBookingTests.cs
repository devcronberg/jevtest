using JevTest;
using Xunit.Abstractions;

namespace Tests;

public sealed class LiveBookingTests(ITestOutputHelper output)
{
    [LiveJevFact]
    [Trait("Category", "LiveApi")]
    public async Task RealJevHandlesConversationValidationAndCorrections()
    {
        using var jev = CreateConnector();
        var agent = new BookingServiceAgent(jev);
        var today = DateOnly.FromDateTime(DateTime.Now);
        var arrival = today.AddDays(30);
        var departure = today.AddDays(37);

        var unrelated = await Send(agent, "Hvordan bliver vejret i morgen?");
        Assert.Equal(BookingStatus.NotBooking, unrelated.Status);
        Assert.Equal(BookingField.Accommodation, (await Send(agent, "Jeg vil gerne booke et ophold på campingpladsen.")).NextField);
        Assert.Equal(BookingField.Arrival, (await Send(agent, "Plads til campingvogn")).NextField);
        Assert.Equal(BookingField.Arrival, (await Send(agent, $"{today:yyyy-MM-dd}")).NextField);
        Assert.Equal(BookingField.Departure, (await Send(agent, $"{arrival:yyyy-MM-dd}")).NextField);
        Assert.Equal(BookingField.Departure, (await Send(agent, $"{arrival:yyyy-MM-dd}")).NextField);
        Assert.Equal(BookingField.Adults, (await Send(agent, $"{departure:yyyy-MM-dd}")).NextField);
        Assert.Equal(BookingField.Adults, (await Send(agent, "0")).NextField);
        Assert.Equal(BookingField.Children, (await Send(agent, "2")).NextField);
        var ready = await Send(agent, "0");
        Assert.Equal(BookingStatus.Ready, ready.Status);
        Assert.Equal(new BookingDetails(arrival, departure, 2, 0, AccommodationType.CaravanPitch), ready.Details);

        var correction = await Send(agent, "Ret det til 3 voksne og 1 barn, og vi vil hellere have en hytte.");
        Assert.Equal(BookingStatus.Ready, correction.Status);
        Assert.Equal(new BookingDetails(arrival, departure, 3, 1, AccommodationType.Cabin), correction.Details);
        var invalidDate = await Send(agent, $"Ret ankomsten til {departure.AddDays(1):yyyy-MM-dd}.");
        Assert.Equal(BookingField.Departure, invalidDate.NextField);
    }

    [LiveJevFact]
    [Trait("Category", "LiveApi")]
    public async Task RealJevExtractsCompleteMessageAndDoesNotInventMissingChildren()
    {
        using var jev = CreateConnector();
        var arrival = DateOnly.FromDateTime(DateTime.Now).AddDays(50);
        var departure = arrival.AddDays(7);
        var agent = new BookingServiceAgent(jev);
        var reply = await Send(agent,
            $"Book en hytte fra {arrival:yyyy-MM-dd} til {departure:yyyy-MM-dd} til 2 voksne og 0 børn.");
        Assert.Equal(BookingStatus.Ready, reply.Status);
        Assert.Equal(new BookingDetails(arrival, departure, 2, 0, AccommodationType.Cabin), reply.Details);

        var partial = new BookingServiceAgent(jev);
        var missing = await Send(partial,
            $"Jeg vil booke en plads til campingvogn fra {arrival:yyyy-MM-dd} til {departure:yyyy-MM-dd}. Vi er 2 voksne.");
        Assert.Equal(BookingField.Children, missing.NextField);
        Assert.Null(missing.Details.Children);
        var completed = await Send(partial, "1");
        Assert.Equal(BookingStatus.Ready, completed.Status);
        Assert.Equal(new BookingDetails(arrival, departure, 2, 1, AccommodationType.CaravanPitch), completed.Details);
    }

    [LiveJevFact]
    [Trait("Category", "LiveApi")]
    public async Task RealJevKeepsInvalidInitialValuesInBookingFlow()
    {
        using var jev = CreateConnector();
        var today = DateOnly.FromDateTime(DateTime.Now);
        var departure = today.AddDays(7);
        var agent = new BookingServiceAgent(jev);
        var reply = await Send(agent,
            $"Book en hytte fra {today:yyyy-MM-dd} til {departure:yyyy-MM-dd}, 0 voksne og -1 børn.");
        Assert.Equal(BookingStatus.NeedsInformation, reply.Status);
        Assert.Equal(BookingField.Arrival, reply.NextField);
        Assert.Equal(today, reply.Details.Arrival);
        Assert.Equal(0, reply.Details.Adults);
        Assert.Equal(-1, reply.Details.Children);
        Assert.Equal(BookingField.Adults, (await Send(agent, $"{today.AddDays(1):yyyy-MM-dd}")).NextField);
        Assert.Equal(BookingField.Children, (await Send(agent, "2")).NextField);
        Assert.Equal(BookingStatus.Ready, (await Send(agent, "0")).Status);
    }

    [LiveJevFact]
    [Trait("Category", "LiveApi")]
    public async Task RealJevUnderstandsLocallyNormalizedDanishDates()
    {
        using var jev = CreateConnector();
        var today = DateOnly.FromDateTime(DateTime.Now);
        var arrival = today.AddDays(30);
        var departure = today.AddDays(37);
        var danish = System.Globalization.CultureInfo.GetCultureInfo("da-DK");
        var agent = new BookingServiceAgent(jev);
        var reply = await Send(agent,
            $"Book en hytte fra {arrival.ToString("d. MMMM", danish)} til {departure:dd/MM/yyyy}, 2 voksne og 0 børn.");
        Assert.Equal(BookingStatus.Ready, reply.Status);
        Assert.Equal(new BookingDetails(arrival, departure, 2, 0, AccommodationType.Cabin), reply.Details);
        Assert.True(agent.TotalCostUsd > 0);
        Assert.Equal(agent.TotalCostUsd * 6.5m, agent.TotalCostDkk);
    }

    [LiveJevFact]
    [Trait("Category", "LiveApi")]
    public async Task RealJevHandlesDeadlineCapacityAndRatioCorrections()
    {
        using var jev = CreateConnector();
        var today = DateOnly.FromDateTime(DateTime.Now);
        var arrival = today.AddDays(30);
        var deadline = today.AddYears(1);
        var agent = new BookingServiceAgent(jev);

        var late = await Send(agent,
            $"Book en hytte fra {arrival:yyyy-MM-dd} til {deadline.AddDays(1):yyyy-MM-dd}, 3 voksne og 10 børn.");
        Assert.Equal(BookingField.Departure, late.NextField);
        Assert.Contains($"{deadline:yyyy-MM-dd}", late.Message);
        var tooMany = await Send(agent, $"{deadline:yyyy-MM-dd}");
        Assert.Equal(BookingField.Children, tooMany.NextField);
        Assert.Contains("fra 0 til 9 børn", tooMany.Message);
        Assert.Equal(BookingStatus.Ready, (await Send(agent, "9")).Status);

        var ratio = await Send(agent, "Ret antallet af voksne til 1.");
        Assert.Equal(BookingField.Children, ratio.NextField);
        Assert.Contains("fra 0 til 4 børn", ratio.Message);
        var ready = await Send(agent, "4");
        Assert.Equal(BookingStatus.Ready, ready.Status);
        Assert.Equal(new BookingDetails(arrival, deadline, 1, 4, AccommodationType.Cabin), ready.Details);
    }

    private async Task<BookingReply> Send(BookingServiceAgent agent, string message)
    {
        output.WriteLine($"Bruger: {message}");
        var reply = await agent.SendAsync(message);
        output.WriteLine($"Agent ({reply.Status}, {reply.NextField}): {reply.Message}");
        return reply;
    }

    private static JevConnector CreateConnector()
    {
        var key = Environment.GetEnvironmentVariable("OpenRouterKey")
            ?? Environment.GetEnvironmentVariable("OpenRouterKey", EnvironmentVariableTarget.User)
            ?? Environment.GetEnvironmentVariable("OpenRouterKey", EnvironmentVariableTarget.Machine);
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("OpenRouterKey skal være sat for at køre API-tests.");
        return new JevConnector("typesafe/jev-1.13", key);
    }
}

public sealed class LiveJevFactAttribute : FactAttribute
{
    public LiveJevFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("JEV_LIVE_TESTS") != "1")
            Skip = "Sæt JEV_LIVE_TESTS=1 for at køre betalte API-tests med fiktive data.";
    }
}
