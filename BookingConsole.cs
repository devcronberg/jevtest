using System.Text.Json;
using System.Text.Json.Serialization;

namespace JevTest;

public sealed record BookingSummary(BookingDetails Booking, decimal TotalCostUsd, decimal TotalCostDkk);

public static class BookingConsole
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static async Task<int> RunAsync()
    {
        try
        {
            var apiKey = Environment.GetEnvironmentVariable("OpenRouterKey")
                ?? Environment.GetEnvironmentVariable("OpenRouterKey", EnvironmentVariableTarget.User)
                ?? Environment.GetEnvironmentVariable("OpenRouterKey", EnvironmentVariableTarget.Machine);
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("Sæt miljøvariablen OpenRouterKey, før bookingchatten startes.");

            using var jev = new JevConnector("typesafe/jev-1.13", apiKey);
            var agent = new BookingServiceAgent(jev);
            await RunAsync(agent, Console.In, Console.Out, Console.Error);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fejl: {ex.Message}");
            return 1;
        }
    }

    public static async Task RunAsync(
        BookingServiceAgent agent, TextReader input, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var next = agent.GetNextReply();
        if (next.Status == BookingStatus.Ready)
        {
            await WriteSummaryAsync(next.Details, agent, output);
            return;
        }
        await output.WriteLineAsync(next.Message);

        while (true)
        {
            await output.FlushAsync();
            var message = await input.ReadLineAsync();
            if (message is null || message.Trim().Equals("/exit", StringComparison.OrdinalIgnoreCase))
                break;
            if (string.IsNullOrWhiteSpace(message))
                continue;

            try
            {
                var reply = await agent.SendAsync(message);
                if (reply.Status == BookingStatus.Ready)
                {
                    await WriteSummaryAsync(reply.Details, agent, output);
                    return;
                }
                await output.WriteLineAsync(reply.Message);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or ArgumentException
                or InvalidOperationException or OperationCanceledException)
            {
                await error.WriteLineAsync($"Bookingfejl: {ex.Message}");
            }
        }
    }

    private static Task WriteSummaryAsync(BookingDetails details, BookingServiceAgent agent, TextWriter output)
    {
        var summary = new BookingSummary(details, agent.TotalCostUsd, agent.TotalCostDkk);
        return output.WriteLineAsync(JsonSerializer.Serialize(summary, JsonOptions));
    }
}
