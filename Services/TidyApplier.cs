using CLIo.Models;

namespace CLIo.Services;

/// <summary>
/// Carries out a tidy proposal the user accepted in Review. Each action re-checks that it still applies (the data
/// may have changed since the scan), refuses rather than guesses, and leaves a trail in the memory transaction log.
/// </summary>
public sealed class TidyApplier
{
    private readonly JournalDatabaseService database;
    private readonly IMemoryService memory;
    private readonly ICalendarService calendar;
    private readonly ITaskService tasks;

    public TidyApplier(JournalDatabaseService database, IMemoryService memory, ICalendarService calendar, ITaskService tasks)
    {
        this.database = database;
        this.memory = memory;
        this.calendar = calendar;
        this.tasks = tasks;
    }

    /// <returns>True if something was changed; false if the proposal no longer applied (so nothing was touched).</returns>
    public async Task<bool> ApplyAsync(ReviewItem item)
    {
        switch (item.Kind)
        {
            case "merge_memory":
            {
                // TargetKey is kept, ProposedValue is the key to remove. Both must still exist.
                var remove = item.ProposedValue;
                var keptValue = await database.GetMemoryValueAsync(item.TargetKey);
                var removedValue = await database.GetMemoryValueAsync(remove);
                if (keptValue is null || removedValue is null)
                {
                    return false;
                }

                await memory.DeleteAsync(remove);
                await database.AddMemoryTransactionAsync("delete", remove, removedValue, "success", "tidy:merge", type: memory.MemoryTypeForKey(remove));
                return true;
            }

            case "rename_memory":
            {
                // TargetKey becomes ProposedValue, unless the new name is already taken.
                var value = await database.GetMemoryValueAsync(item.TargetKey);
                if (value is null || await database.GetMemoryValueAsync(item.ProposedValue) is not null)
                {
                    return false;
                }

                await memory.RenameKeyAsync(item.TargetKey, item.ProposedValue);
                await database.AddMemoryTransactionAsync("write", item.ProposedValue, value, "success", "tidy:rename", type: memory.MemoryTypeForKey(item.ProposedValue));
                return true;
            }

            case "duplicate_event":
            {
                // TargetKey is the duplicate to remove, ProposedValue the id that stays. Both must still exist.
                if (!int.TryParse(item.TargetKey, out var removeId) || !int.TryParse(item.ProposedValue, out var keepId))
                {
                    return false;
                }

                var events = await calendar.GetAllEventsAsync();
                if (!events.Any(e => e.Id == removeId) || !events.Any(e => e.Id == keepId))
                {
                    return false;
                }

                await calendar.DeleteEventAsync(removeId);
                return true;
            }

            case "duplicate_task":
            {
                if (!int.TryParse(item.TargetKey, out var removeId) || !int.TryParse(item.ProposedValue, out var keepId))
                {
                    return false;
                }

                var all = await tasks.GetAllAsync();
                if (!all.Any(t => t.Id == removeId) || !all.Any(t => t.Id == keepId))
                {
                    return false;
                }

                await tasks.DeleteAsync(removeId);
                return true;
            }

            default:
                return false;
        }
    }
}
