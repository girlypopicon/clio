using CLIo.Models;

namespace CLIo.Services;

public interface ITaskService
{
    Task<IReadOnlyList<JournalTask>> GetAllAsync();
    Task AddAsync(string title);
    Task DeleteAsync(int id);
    Task SetDoneAsync(int id, bool isDone);
    Task<bool> CompleteByTitleAsync(string title);
    Task<JournalTask?> FindOpenTaskByText(string text, IReadOnlyList<JournalTask> tasks);
}
