namespace CLIo.Models;

public sealed class CalendarEvent
{
    public int Id { get; init; }
    public required string Title { get; init; }
    public required DateTimeOffset StartsAt { get; init; }
    public required string Details { get; init; }

    public string DisplayStartsAt => StartsAt.ToString("MMM d, h:mm tt");
}
