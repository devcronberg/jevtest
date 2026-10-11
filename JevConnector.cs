using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace JevTest;

public sealed class JevConnector : IDisposable
{
    private readonly Uri endpoint;
    private readonly HttpClient httpClient;
    private readonly bool ownsHttpClient;
    private readonly string model;
    private readonly string apiKey;
    private readonly decimal usdToDkkRate;
    private bool disposed;

    public string State { get; set; } = "";
    public string Question { get; set; } = "";
    /// <summary>One to 255 unique, non-empty options used by ChooseAsync.</summary>
    public List<string> Choices { get; set; } = [];
    /// <summary>Two to ten non-empty descriptions, ordered from lowest to highest, used by ScoreAsync.</summary>
    public List<string> Levels { get; set; } = [];

    /// <summary>A supplied HttpClient remains owned by the caller; otherwise the connector creates and disposes its own.</summary>
    /// <param name="usdToDkkRate">DKK per USD. A fixed conversion rate, not a live exchange rate.</param>
    public JevConnector(
        string model,
        string apiKey,
        string endpoint = "https://openrouter.ai/api/alpha/decisions",
        HttpClient? httpClient = null,
        decimal usdToDkkRate = 6.5m)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(usdToDkkRate);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) ||
            (endpointUri.Scheme != Uri.UriSchemeHttps && endpointUri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("Endpoint must be an absolute HTTP or HTTPS URL.", nameof(endpoint));
        }

        this.endpoint = endpointUri;
        ownsHttpClient = httpClient is null;
        this.httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        this.model = model;
        this.apiKey = apiKey;
        this.usdToDkkRate = usdToDkkRate;
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        if (ownsHttpClient)
            httpClient.Dispose();
    }

    public async Task<JevBooleanAnswer> AskAsync(CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(State);
        ArgumentException.ThrowIfNullOrWhiteSpace(Question);

        var (result, usage, duration) = await EvaluateWithMetadataAsync(
            JsonSerializer.SerializeToElement(State),
            JsonSerializer.SerializeToElement(new
            {
                answer = new { type = "noul", instructions = Question }
            }),
            cancellationToken: cancellationToken);

        var answer = result.GetProperty("answers").GetProperty("answer");
        if (answer.ValueKind != JsonValueKind.Object ||
            !answer.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String || type.GetString() != "noul")
        {
            throw new JsonException("OpenRouter must return a noul answer.");
        }

        return new JevBooleanAnswer(ReadProbability(answer, "noul"), usage, duration);
    }

    public async Task<JevChoiceAnswer> ChooseAsync(CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(State);
        ArgumentException.ThrowIfNullOrWhiteSpace(Question);
        ArgumentNullException.ThrowIfNull(Choices);
        if (Choices.Count is < 1 or > 255)
            throw new ArgumentException("Provide between 1 and 255 choices.", nameof(Choices));

        var criteria = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var option in Choices)
        {
            if (string.IsNullOrWhiteSpace(option) || !criteria.TryAdd(option, null))
                throw new ArgumentException("Choices must be unique and non-empty.", nameof(Choices));
        }

        var (result, usage, duration) = await EvaluateWithMetadataAsync(
            JsonSerializer.SerializeToElement(State),
            JsonSerializer.SerializeToElement(new
            {
                answer = new { type = "choice", instructions = Question, criteria }
            }),
            cancellationToken: cancellationToken);

        var answer = result.GetProperty("answers").GetProperty("answer");
        if (answer.ValueKind != JsonValueKind.Object ||
            !answer.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String || type.GetString() != "choice" ||
            !answer.TryGetProperty("choice", out var choice) ||
            choice.ValueKind != JsonValueKind.String ||
            choice.GetString() is not { } selectedChoice ||
            !criteria.ContainsKey(selectedChoice) ||
            !answer.TryGetProperty("probabilities", out var distribution) ||
            distribution.ValueKind != JsonValueKind.Object ||
            distribution.EnumerateObject().Count() != criteria.Count)
        {
            throw new JsonException("OpenRouter must return a listed choice and probabilities for all choices.");
        }

        var confidence = ReadProbability(answer, "confidence");
        var probabilities = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var option in criteria.Keys)
            probabilities.Add(option, ReadProbability(distribution, option));

        return new JevChoiceAnswer(selectedChoice, probabilities.AsReadOnly(), confidence, usage, duration);
    }

    public async Task<JevScoreAnswer> ScoreAsync(CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(State);
        ArgumentException.ThrowIfNullOrWhiteSpace(Question);
        ArgumentNullException.ThrowIfNull(Levels);
        var criteria = Levels.ToArray();
        if (criteria.Length is < 2 or > 10 || criteria.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Provide between 2 and 10 non-empty levels, ordered from lowest to highest.", nameof(Levels));

        var (result, usage, duration) = await EvaluateWithMetadataAsync(
            JsonSerializer.SerializeToElement(State),
            JsonSerializer.SerializeToElement(new
            {
                answer = new { type = "score", instructions = Question, criteria }
            }),
            cancellationToken: cancellationToken);

        var answer = result.GetProperty("answers").GetProperty("answer");
        if (answer.ValueKind != JsonValueKind.Object ||
            !answer.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String || type.GetString() != "score" ||
            !answer.TryGetProperty("score", out var score) ||
            score.ValueKind != JsonValueKind.Number ||
            !score.TryGetDouble(out var value) ||
            !double.IsFinite(value) || value < 0 || value > criteria.Length - 1 ||
            !answer.TryGetProperty("probabilities", out var distribution) ||
            distribution.ValueKind != JsonValueKind.Object ||
            distribution.EnumerateObject().Count() != criteria.Length ||
            !answer.TryGetProperty("legend", out var legend) ||
            legend.ValueKind != JsonValueKind.Object ||
            legend.EnumerateObject().Count() != criteria.Length)
        {
            throw new JsonException("OpenRouter must return a score within the scale, with probabilities and a legend for all levels.");
        }

        var confidence = ReadProbability(answer, "confidence");
        var probabilities = new Dictionary<int, double>();
        var descriptions = new Dictionary<int, string>();
        for (var index = 0; index < criteria.Length; index++)
        {
            var key = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            probabilities.Add(index, ReadProbability(distribution, key));
            if (!legend.TryGetProperty(key, out var description) ||
                description.ValueKind != JsonValueKind.String ||
                description.GetString() != criteria[index])
            {
                throw new JsonException($"OpenRouter returned an unexpected description for level {index}.");
            }

            descriptions.Add(index, criteria[index]);
        }

        return new JevScoreAnswer(value, probabilities.AsReadOnly(), descriptions.AsReadOnly(), confidence, usage, duration);
    }

    // Duration includes the HTTP round trip and response parsing, not just model inference.
    private async Task<(JsonElement Result, JevUsage Usage, TimeSpan Duration)> EvaluateWithMetadataAsync(
        JsonElement state,
        JsonElement questions,
        CancellationToken cancellationToken)
    {
        var start = Stopwatch.GetTimestamp();
        var result = await EvaluateAsync(state, questions, cancellationToken);
        if (!result.TryGetProperty("usage", out var usage) ||
            usage.ValueKind != JsonValueKind.Object ||
            !usage.TryGetProperty("input_tokens", out var input) ||
            input.ValueKind != JsonValueKind.Number || !input.TryGetInt64(out var inputTokens) || inputTokens < 0 ||
            !usage.TryGetProperty("output_tokens", out var output) ||
            output.ValueKind != JsonValueKind.Number || !output.TryGetInt64(out var outputTokens) || outputTokens < 0 ||
            !usage.TryGetProperty("cost", out var cost) ||
            cost.ValueKind != JsonValueKind.Number || !cost.TryGetDecimal(out var amount) || amount < 0)
        {
            throw new JsonException("OpenRouter must return usage with non-negative input_tokens, output_tokens and cost.");
        }

        return (result, new JevUsage(inputTokens, outputTokens, amount, usdToDkkRate), Stopwatch.GetElapsedTime(start));
    }

    private static double ReadProbability(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var number) ||
            number.ValueKind != JsonValueKind.Number ||
            !number.TryGetDouble(out var probability) ||
            !double.IsFinite(probability) || probability < 0 || probability > 1)
        {
            throw new JsonException($"OpenRouter must return '{propertyName}' as a number between 0 and 1.");
        }

        return probability;
    }

    public async Task<JsonElement> EvaluateAsync(
        JsonElement state,
        JsonElement questions,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (state.ValueKind is not (JsonValueKind.String or JsonValueKind.Object or JsonValueKind.Array))
            throw new ArgumentException("State must be a JSON string, object or array.", nameof(state));
        if (questions.ValueKind != JsonValueKind.Object || !questions.EnumerateObject().Any())
            throw new ArgumentException("Questions must be a non-empty JSON object.", nameof(questions));

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new { model, state, questions })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"OpenRouter returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}): {body}",
                null,
                response.StatusCode);
        }

        var result = JsonSerializer.Deserialize<JsonElement>(body);
        if (result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("answers", out var answers) ||
            answers.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("OpenRouter returned an unexpected Decisions response: " + body);
        }

        foreach (var question in questions.EnumerateObject())
        {
            if (!answers.TryGetProperty(question.Name, out _))
                throw new JsonException($"OpenRouter did not return an answer for '{question.Name}'.");
        }

        return result;
    }
}

/// <summary>Duration is the measured request time, including network time and response parsing.</summary>
public sealed record JevBooleanAnswer(double Probability, JevUsage Usage, TimeSpan Duration)
{
    /// <summary>True when the probability of yes is at least 50%; this is not a guarantee.</summary>
    public bool Value => Probability >= 0.5;
}

/// <summary>Confidence is the model's certainty measure, distinct from the choice's probability.</summary>
public sealed record JevChoiceAnswer(
    string Value,
    IReadOnlyDictionary<string, double> Probabilities,
    double Confidence,
    JevUsage Usage,
    TimeSpan Duration)
{
    public double Probability => Probabilities[Value];
}

/// <summary>Value is a probability-weighted score from 0 to the number of levels minus one, not a probability.</summary>
public sealed record JevScoreAnswer(
    double Value,
    IReadOnlyDictionary<int, double> Probabilities,
    IReadOnlyDictionary<int, string> Legend,
    double Confidence,
    JevUsage Usage,
    TimeSpan Duration);

/// <summary>Token usage and API cost in USD, as reported by OpenRouter.</summary>
public sealed record JevUsage(long InputTokens, long OutputTokens, decimal Cost, decimal UsdToDkkRate)
{
    /// <summary>API cost converted to DKK using the connector's fixed exchange rate.</summary>
    public decimal CostDkk => Cost * UsdToDkkRate;

    /// <summary>Whole calls within 1 DKK at this call's price, rounded down; null when the DKK cost is zero.</summary>
    public decimal? CallsPerDkk => CostDkk == 0 ? null : decimal.Floor(1m / CostDkk);
}
