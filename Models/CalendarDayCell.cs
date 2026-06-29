namespace CLIo.Models;

public sealed class CalendarDayCell
{
    public required DateTime Date { get; init; }
    public required bool IsCurrentMonth { get; init; }
    public required bool IsToday { get; init; }
    public required bool IsSelected { get; init; }
    public required bool HasEvents { get; init; }
    public required IReadOnlyList<CalendarEvent> Events { get; init; }

    public int DayNumber => Date.Day;
    public string DayLabel => Date.ToString("ddd d");
    public IReadOnlyList<CalendarEvent> VisibleEvents => Events.Take(3).ToList();
    public int OverflowCount => Math.Max(0, Events.Count - 3);
    public bool HasOverflow => OverflowCount > 0;
    public string OverflowLabel => $"+{OverflowCount} more";
}
