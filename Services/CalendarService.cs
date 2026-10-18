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

    // Repeating series are shown as computed occurrences from a month back to four months ahead; the stored
    // row is the series (its own start is the first occurrence). Every occurrence carries the series' Id.
    private const int OccurrenceDaysBack = 30;
    private const int OccurrenceDaysAhead = 120;

    public async Task<IReadOnlyList<CalendarEvent>> GetAllEventsAsync()
    {
        var stored = await database.GetEventsAsync();
        if (stored.All(e => e.Recurrence is null))
        {
            return stored;
        }

        var from = DateTimeOffset.Now.AddDays(-OccurrenceDaysBack);
        var to = DateTimeOffset.Now.AddDays(OccurrenceDaysAhead);
        var all = new List<CalendarEvent>();
        foreach (var item in stored)
        {
            if (item.Recurrence is null || !RecurrenceRule.TryParse(item.Recurrence, out var rule))
            {
                all.Add(item);
                continue;
            }

            foreach (var start in rule.Expand(item.StartsAt, from, to))
            {
                all.Add(new CalendarEvent
                {
                    Id = item.Id,
                    Title = item.Title,
                    StartsAt = start,
                    Details = item.Details,
                    Source = item.Source,
                    Recurrence = item.Recurrence
                });
            }
        }

        return all.OrderBy(e => e.StartsAt).ToList();
    }

    public async Task SetRecurrenceAsync(int id, RecurrenceRule? rule)
    {
        await database.SetEventRecurrenceAsync(id, rule?.ToString());
        runtimeLog?.Record("calendar", "repeat", "ok", $"event #{id} {(rule is null ? "stops repeating" : rule.Describe())}");
    }

    // A repeating series moves as a whole and keeps its first date: callers pass a date taken from one
    // occurrence, so only the time of day is applied to the series start.
    private async Task<DateTimeOffset> SeriesStartAsync(int id, DateTimeOffset requested)
    {
        var stored = (await database.GetEventsAsync()).FirstOrDefault(e => e.Id == id);
        if (stored?.Recurrence is null)
        {
            return requested;
        }

        var wall = stored.StartsAt.Date + requested.TimeOfDay;
        return new DateTimeOffset(wall, TimeZoneInfo.Local.GetUtcOffset(wall));
    }

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
        startsAt = await SeriesStartAsync(id, startsAt);
        await database.UpdateEventStartAsync(id, startsAt);
        runtimeLog?.Record("calendar", "move", "ok", $"event #{id} to {startsAt:yyyy-MM-dd HH:mm}");
    }

    public async Task UpdateEventAsync(int id, string title, DateTimeOffset startsAt, string details, string source = "chat")
    {
        startsAt = await SeriesStartAsync(id, startsAt);
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
