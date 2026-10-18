using CLIo.Models;

namespace CLIo.Services;

public sealed class TaskService : ITaskService
{
    private readonly JournalDatabaseService database;

    public TaskService(JournalDatabaseService database)
    {
        this.database = database;
    }

    public Task<IReadOnlyList<JournalTask>> GetAllAsync() => database.GetTasksAsync();

    public Task AddAsync(string title, string source = "chat") => database.AddTaskAsync(title, source);

    public Task DeleteAsync(int id) => database.DeleteTaskAsync(id);

    public Task SetDoneAsync(int id, bool isDone) => database.SetTaskDoneAsync(id, isDone);

    public Task<bool> CompleteByTitleAsync(string title) => database.CompleteTaskByTitleAsync(title);

    public Task<JournalTask?> FindOpenTaskByText(string text, IReadOnlyList<JournalTask> tasks)
    {
        return Task.FromResult(
            tasks.FirstOrDefault(t => !t.IsDone && t.Title.Contains(text, StringComparison.OrdinalIgnoreCase))
            ?? tasks.FirstOrDefault(t => !t.IsDone && text.Contains(t.Title, StringComparison.OrdinalIgnoreCase)));
    }
}
