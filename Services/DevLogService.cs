namespace CLIo.Services;

public sealed class DevLogService
{
    private readonly List<DevLogEntry> entries = [];
    private const int MaxEntries = 100;

    public void Log(string message, string category = "system")
    {
        entries.Add(new DevLogEntry(DateTimeOffset.Now, category, message));
        if (entries.Count > MaxEntries)
            entries.RemoveAt(0);
    }

    public IReadOnlyList<DevLogEntry> GetEntries() => entries.AsReadOnly();

    public string Format(int count = 50)
    {
        var recent = entries.TakeLast(count).Reverse().ToList();
        if (recent.Count == 0)
            return "No dev log entries this session.";

        return $"Dev log ({recent.Count} entries this session):\n" +
            string.Join("\n", recent.Select(e =>
                $"  [{e.Timestamp:HH:mm:ss}] [{e.Category}] {e.Message}"));
    }
}

public sealed record DevLogEntry(DateTimeOffset Timestamp, string Category, string Message);
