namespace CLIo.Models;

public sealed class CalendarEvent
{
    public int Id { get; init; }
    public required string Title { get; init; }
    public required DateTimeOffset StartsAt { get; init; }
    public required string Details { get; init; }

    /// <summary>"manual" (set by the user in the UI) or "chat" (filed automatically).</summary>
    public string Source { get; init; } = "chat";

    /// <summary>RRULE string when this event repeats; null otherwise. Occurrences share the series' Id.</summary>
    public string? Recurrence { get; init; }

    /// <summary>True for a computed occurrence of a repeating series (every occurrence of a series carries the series' Id).</summary>
    public bool IsOccurrence => Recurrence is not null;

    public string DisplayStartsAt => StartsAt.ToString("MMM d, h:mm tt") + (Recurrence is null ? string.Empty : " ↻");
}
