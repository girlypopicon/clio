namespace CLIo.Models;

public sealed class CalendarCandidate
{
    public int Id { get; init; }
    public required string Timestamp { get; init; }
    public required string Title { get; init; }
    public required string StartsAt { get; init; }
    public required string Details { get; init; }
    public required string Status { get; init; }
    public required string Source { get; init; }
    public required string Error { get; init; }

    public bool HasValidStartsAt => DateTimeOffset.TryParse(StartsAt, out _);
}
