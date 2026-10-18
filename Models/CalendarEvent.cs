namespace CLIo.Models;

public sealed class CalendarEvent
{
    public int Id { get; init; }
    public required string Title { get; init; }
    public required DateTimeOffset StartsAt { get; init; }
    public required string Details { get; init; }

    /// <summary>"manual" (set by the user in the UI) or "chat" (filed automatically).</summary>
    public string Source { get; init; } = "chat";

    public string DisplayStartsAt => StartsAt.ToString("MMM d, h:mm tt");
}
