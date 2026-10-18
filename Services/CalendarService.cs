using CLIo.Models;

namespace CLIo.Services;

public sealed class CalendarService : ICalendarService
{
    private readonly JournalDatabaseService database;

    public CalendarService(JournalDatabaseService database)
    {
        this.database = database;
    }

    public Task<IReadOnlyList<CalendarEvent>> GetAllEventsAsync() => database.GetEventsAsync();

    public Task AddEventAsync(string title, DateTimeOffset startsAt, string details, string source = "chat") =>
        database.AddEventAsync(title, startsAt, details, source);

    public Task DeleteEventAsync(int id) => database.DeleteEventAsync(id);

    public Task UpdateEventStartAsync(int id, DateTimeOffset startsAt) =>
        database.UpdateEventStartAsync(id, startsAt);

    public Task UpdateEventAsync(int id, string title, DateTimeOffset startsAt, string details, string source = "chat") =>
        database.UpdateEventAsync(id, title, startsAt, details, source);

    public IReadOnlyList<CalendarEvent> FindMatchingEvents(string title, DateTimeOffset? when, IReadOnlyList<CalendarEvent> events)
    {
        var matches = events
            .Where(e => e.Title.Contains(title, StringComparison.OrdinalIgnoreCase)
                || title.Contains(e.Title, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (when.HasValue && matches.Count > 1)
        {
            var dateMatches = matches.Where(e => e.StartsAt.Date == when.Value.Date).ToList();
            if (dateMatches.Count > 0)
                matches = dateMatches;
        }

        return matches;
    }
}
