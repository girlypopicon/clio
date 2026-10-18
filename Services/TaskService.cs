using CLIo.Models;

namespace CLIo.Services;

public sealed class TaskService : ITaskService
{
    private readonly JournalDatabaseService database;
    private readonly RuntimeLog? runtimeLog;

    public TaskService(JournalDatabaseService database, RuntimeLog? runtimeLog = null)
    {
        this.database = database;
        this.runtimeLog = runtimeLog;
    }

    public Task<IReadOnlyList<JournalTask>> GetAllAsync() => database.GetTasksAsync();

    public async Task AddAsync(string title, string source = "chat")
    {
        await database.AddTaskAsync(title, source);
        runtimeLog?.Record("tasks", "add", "ok", $"\"{title}\" [{source}]");
    }

    public async Task DeleteAsync(int id)
    {
        await database.DeleteTaskAsync(id);
        runtimeLog?.Record("tasks", "delete", "ok", $"task #{id}");
    }

    public async Task SetDoneAsync(int id, bool isDone)
    {
        await database.SetTaskDoneAsync(id, isDone);
        runtimeLog?.Record("tasks", isDone ? "done" : "reopen", "ok", $"task #{id}");
    }

    public async Task<bool> CompleteByTitleAsync(string title)
    {
        var completed = await database.CompleteTaskByTitleAsync(title);
        runtimeLog?.Record("tasks", "done", completed ? "ok" : "not_found", $"\"{title}\"");
        return completed;
    }

    public Task<JournalTask?> FindOpenTaskByText(string text, IReadOnlyList<JournalTask> tasks)
    {
        return Task.FromResult(
            tasks.FirstOrDefault(t => !t.IsDone && t.Title.Contains(text, StringComparison.OrdinalIgnoreCase))
            ?? tasks.FirstOrDefault(t => !t.IsDone && text.Contains(t.Title, StringComparison.OrdinalIgnoreCase)));
    }
}
