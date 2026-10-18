namespace CLIo.Models;

/// <summary>
/// One thing the app actually did: an LLM call, a skill run, something filed, an error. Written as it
/// happens, so CLIo can answer "did that happen?" from facts instead of guessing.
/// </summary>
public sealed record RuntimeEvent(
    DateTimeOffset Time,
    string Component,
    string Type,
    string Outcome,
    string Detail,
    int? LatencyMs = null,
    string? Model = null,
    int? TokensIn = null,
    int? TokensOut = null)
{
    public long Id { get; init; }
}

/// <summary>Filters for reading the log back. Everything is optional.</summary>
public sealed record RuntimeQuery(
    DateTimeOffset? Since = null,
    string? Component = null,
    string? Type = null,
    string? Outcome = null,
    string? Contains = null,
    int Limit = 25);

public sealed record RuntimeQueryResult(
    IReadOnlyList<RuntimeEvent> Events,
    int Total,
    int Errors,
    IReadOnlyDictionary<string, int> ByComponent,
    int LlmCalls,
    long TokensIn,
    long TokensOut,
    double AverageLlmMs,
    IReadOnlyList<string> Models,
    DateTimeOffset? Earliest,
    DateTimeOffset? Latest);
