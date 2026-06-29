using CLIo.Models;

namespace CLIo.Services;

public interface ICalendarService
{
    Task<IReadOnlyList<CalendarEvent>> GetAllEventsAsync();
    Task AddEventAsync(string title, DateTimeOffset startsAt, string details);
    Task DeleteEventAsync(int id);
    Task UpdateEventStartAsync(int id, DateTimeOffset startsAt);
    Task UpdateEventAsync(int id, string title, DateTimeOffset startsAt, string details);
    IReadOnlyList<CalendarEvent> FindMatchingEvents(string title, DateTimeOffset? when, IReadOnlyList<CalendarEvent> events);
}
