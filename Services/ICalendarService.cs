using CLIo.Models;

namespace CLIo.Services;

public interface ICalendarService
{
    Task<IReadOnlyList<CalendarEvent>> GetAllEventsAsync();
    Task AddEventAsync(string title, DateTimeOffset startsAt, string details, string source = "chat");
    Task DeleteEventAsync(int id);
    Task UpdateEventStartAsync(int id, DateTimeOffset startsAt);
    Task UpdateEventAsync(int id, string title, DateTimeOffset startsAt, string details, string source = "chat");
    /// <summary>Makes an event repeat (or stop repeating when <paramref name="rule"/> is null).</summary>
    Task SetRecurrenceAsync(int id, RecurrenceRule? rule);
    IReadOnlyList<CalendarEvent> FindMatchingEvents(string title, DateTimeOffset? when, IReadOnlyList<CalendarEvent> events);
}
