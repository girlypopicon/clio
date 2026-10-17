namespace CLIo.Models;

public sealed class Reminder
{
    public int Id { get; init; }
    public required string Title { get; init; }
    public required DateTimeOffset DueAt { get; init; }
    public int? SourceEventId { get; init; }

    public string DisplayDueAt => DueAt.ToString("MMM d, h:mm tt");
}
