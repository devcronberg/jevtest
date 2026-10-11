namespace JevTest;

public interface IJevDecisions
{
    Task<JevBooleanAnswer> AskAsync(
        string state, string question, CancellationToken cancellationToken = default);

    Task<JevChoiceAnswer> ChooseAsync(
        string state, string question, IReadOnlyList<string> choices,
        CancellationToken cancellationToken = default);
}
