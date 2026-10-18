using System.Collections.Concurrent;
using System.Diagnostics;
using CLIo.Models;

namespace CLIo.Services;

/// <summary>
/// The app's black-box recorder. Components call <see cref="Record"/> when something real happens; events are
/// batched into the encrypted database (last 3,000 kept, survives restarts) and read back with
/// <see cref="QueryAsync"/>. Recording never blocks the caller and never throws: a logging problem must not
/// break the thing being logged.
/// </summary>
public sealed class RuntimeLog
{
    public const int Keep = 3000;

    private readonly JournalDatabaseService database;
    private readonly ConcurrentQueue<RuntimeEvent> pending = new();
    private readonly SemaphoreSlim flushLock = new(1, 1);
    private readonly Timer timer;

    public RuntimeLog(JournalDatabaseService database)
    {
        this.database = database;
        timer = new Timer(_ => _ = FlushAsync(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    public void Record(
        string component,
        string type,
        string outcome,
        string detail,
        int? latencyMs = null,
        string? model = null,
        int? tokensIn = null,
        int? tokensOut = null)
    {
        try
        {
            pending.Enqueue(new RuntimeEvent(
                DateTimeOffset.UtcNow, component, type, outcome, Trim(detail, 600), latencyMs, model, tokensIn, tokensOut));
        }
        catch
        {
            // never let logging break the caller
        }
    }

    /// <summary>Starts a stopwatch; call <see cref="Timed.Done"/> to record the event with its duration.</summary>
    public Timed Start(string component, string type) => new(this, component, type);

    public async Task FlushAsync()
    {
        if (pending.IsEmpty)
        {
            return;
        }

        await flushLock.WaitAsync();
        try
        {
            var batch = new List<RuntimeEvent>();
            while (pending.TryDequeue(out var item))
            {
                batch.Add(item);
            }

            if (batch.Count > 0)
            {
                await database.AddRuntimeEventsAsync(batch, Keep);
            }
        }
        catch
        {
            // best effort
        }
        finally
        {
            flushLock.Release();
        }
    }

    public async Task<RuntimeQueryResult> QueryAsync(RuntimeQuery query)
    {
        await FlushAsync(); // include events still waiting in memory
        return await database.QueryRuntimeEventsAsync(query);
    }

    private static string Trim(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";

    public sealed class Timed
    {
        private readonly RuntimeLog log;
        private readonly string component;
        private readonly string type;
        private readonly long started = Stopwatch.GetTimestamp();

        internal Timed(RuntimeLog log, string component, string type)
        {
            this.log = log;
            this.component = component;
            this.type = type;
        }

        public int ElapsedMs => (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        public void Done(string outcome, string detail, string? model = null, int? tokensIn = null, int? tokensOut = null) =>
            log.Record(component, type, outcome, detail, ElapsedMs, model, tokensIn, tokensOut);
    }
}

/// <summary>
/// Says what an LLM call is for ("chat", "extract", "polish"...) without changing every call site's signature,
/// and carries the token counts DeepSeek reports back to the logger.
/// </summary>
public static class LlmCall
{
    private static readonly AsyncLocal<string?> CurrentPurpose = new();

    internal static readonly AsyncLocal<LlmCallStats?> Stats = new();

    public static string Purpose => CurrentPurpose.Value ?? "chat";

    public static IDisposable Use(string purpose)
    {
        var previous = CurrentPurpose.Value;
        CurrentPurpose.Value = purpose;
        return new Scope(() => CurrentPurpose.Value = previous);
    }

    private sealed class Scope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}

public sealed class LlmCallStats
{
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public int? CacheHitTokens { get; set; }
}
