namespace CLIo.Services;

public sealed class ClioStateService
{
    private readonly JournalDatabaseService database;

    public ClioStateService(JournalDatabaseService database)
    {
        this.database = database;
    }

    public Task<string> GetAsync(string key, string fallback = "") =>
        database.GetSettingAsync($"clio:{key}", fallback);

    public Task SetAsync(string key, string value) =>
        database.SetSettingAsync($"clio:{key}", value);

    public async Task<int> IncrementAsync(string key)
    {
        var current = int.TryParse(await GetAsync(key, "0"), out var n) ? n : 0;
        var next = current + 1;
        await SetAsync(key, next.ToString());
        return next;
    }

    public async Task RecordStartupAsync(string currentVersion)
    {
        var firstRun = await GetAsync("first_run");
        if (string.IsNullOrEmpty(firstRun))
            await SetAsync("first_run", DateTimeOffset.Now.ToString("O"));

        await SetAsync("version", currentVersion);
        await SetAsync("last_startup", DateTimeOffset.Now.ToString("O"));
        await IncrementAsync("session_count");
    }

    public async Task RecordMessageAsync()
    {
        await IncrementAsync("total_messages");
        await SetAsync("last_active", DateTimeOffset.Now.ToString("O"));
    }

    public async Task RecordLlmErrorAsync()
    {
        await IncrementAsync("llm_errors");
        await SetAsync("last_llm_error", DateTimeOffset.Now.ToString("O"));
    }

    public async Task<bool> ShouldAnnounceVersionAsync(string currentVersion)
    {
        var briefed = await GetAsync("last_version_briefed");
        return briefed != currentVersion;
    }

    public Task MarkVersionBriefedAsync(string currentVersion) =>
        SetAsync("last_version_briefed", currentVersion);

    public async Task<ClioSelfContext> BuildSelfContextAsync()
    {
        return new ClioSelfContext
        {
            Version = await GetAsync("version", "unknown"),
            FirstRun = await GetAsync("first_run"),
            LastActive = await GetAsync("last_active"),
            SessionCount = int.TryParse(await GetAsync("session_count", "0"), out var s) ? s : 0,
            TotalMessages = int.TryParse(await GetAsync("total_messages", "0"), out var m) ? m : 0,
            LlmErrors = int.TryParse(await GetAsync("llm_errors", "0"), out var e) ? e : 0,
            LastLlmError = await GetAsync("last_llm_error"),
        };
    }
}

public sealed class ClioSelfContext
{
    public string Version { get; init; } = "";
    public string FirstRun { get; init; } = "";
    public string LastActive { get; init; } = "";
    public int SessionCount { get; init; }
    public int TotalMessages { get; init; }
    public int LlmErrors { get; init; }
    public string LastLlmError { get; init; } = "";
}
