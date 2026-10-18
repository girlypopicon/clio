using CLIo.Models;

namespace CLIo.Services;

public sealed class CalendarService : ICalendarService
{
    private readonly JournalDatabaseService database;
    private readonly RuntimeLog? runtimeLog;

    public CalendarService(JournalDatabaseService database, RuntimeLog? runtimeLog = null)
    {
        this.database = database;
        this.runtimeLog = runtimeLog;
    }

    public Task<IReadOnlyList<CalendarEvent>> GetAllEventsAsync() => database.GetEventsAsync();

    public async Task AddEventAsync(string title, DateTimeOffset startsAt, string details, string source = "chat")
    {
        await database.AddEventAsync(title, startsAt, details, source);
        runtimeLog?.Record("calendar", "add", "ok", $"\"{title}\" on {startsAt:yyyy-MM-dd HH:mm} [{source}]");
    }

    public async Task DeleteEventAsync(int id)
    {
        await database.DeleteEventAsync(id);
        runtimeLog?.Record("calendar", "delete", "ok", $"event #{id}");
    }

    public async Task UpdateEventStartAsync(int id, DateTimeOffset startsAt)
    {
        await database.UpdateEventStartAsync(id, startsAt);
        runtimeLog?.Record("calendar", "move", "ok", $"event #{id} to {startsAt:yyyy-MM-dd HH:mm}");
    }

    public async Task UpdateEventAsync(int id, string title, DateTimeOffset startsAt, string details, string source = "chat")
    {
        await database.UpdateEventAsync(id, title, startsAt, details, source);
        runtimeLog?.Record("calendar", "update", "ok", $"event #{id} \"{title}\" on {startsAt:yyyy-MM-dd HH:mm} [{source}]");
    }

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
