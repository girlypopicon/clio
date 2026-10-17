using Avalonia.Threading;
using CLIo.Models;

namespace CLIo.Services;

/// <summary>
/// Polls for due reminders (explicit @@ reminders and upcoming calendar events) and raises
/// <see cref="ReminderDue"/> on the UI thread. Runs for the life of the app, including while
/// the main window is hidden in the system tray.
/// </summary>
public sealed class ReminderService
{
    private const string LeadMinutesSetting = "reminder_lead_minutes";
    private const int DefaultLeadMinutes = 10;

    private readonly JournalDatabaseService database;
    private readonly ICalendarService calendar;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(15) };
    private bool checking;

    public ReminderService(JournalDatabaseService database, ICalendarService calendar)
    {
        this.database = database;
        this.calendar = calendar;
        timer.Tick += async (_, _) => await CheckAsync();
    }

    public event Action<Reminder>? ReminderDue;

    public void Start()
    {
        if (!timer.IsEnabled)
        {
            timer.Start();
            _ = CheckAsync();
        }
    }

    public Task<int> AddAsync(string title, DateTimeOffset dueAt) => database.AddReminderAsync(title, dueAt);

    public Task<IReadOnlyList<Reminder>> GetPendingAsync() => database.GetPendingRemindersAsync();

    public Task DismissAsync(int id) => database.SetReminderStatusAsync(id, "done");

    public Task SnoozeAsync(int id, TimeSpan delay) => database.SnoozeReminderAsync(id, DateTimeOffset.Now + delay);

    private async Task CheckAsync()
    {
        if (checking)
        {
            return;
        }

        checking = true;
        try
        {
            var now = DateTimeOffset.Now;
            await QueueUpcomingEventRemindersAsync(now);

            var due = (await database.GetPendingRemindersAsync()).Where(r => r.DueAt <= now).ToList();
            foreach (var reminder in due)
            {
                // Mark fired before showing so a slow popup can't cause a repeat on the next tick.
                await database.SetReminderStatusAsync(reminder.Id, "fired");
                ReminderDue?.Invoke(reminder);
            }
        }
        catch
        {
            // Never let a transient DB error kill the timer.
        }
        finally
        {
            checking = false;
        }
    }

    private async Task QueueUpcomingEventRemindersAsync(DateTimeOffset now)
    {
        var lead = int.TryParse(await database.GetSettingAsync(LeadMinutesSetting, DefaultLeadMinutes.ToString()), out var m) && m > 0
            ? m
            : DefaultLeadMinutes;
        if (lead <= 0)
        {
            return;
        }

        var events = await calendar.GetAllEventsAsync();
        foreach (var item in events.Where(e => e.StartsAt > now && e.StartsAt <= now.AddMinutes(lead)))
        {
            if (await database.ReminderExistsForEventAsync(item.Id))
            {
                continue;
            }

            await database.AddReminderAsync($"{item.Title} at {item.StartsAt:h:mm tt}", now, item.Id);
        }
    }
}
