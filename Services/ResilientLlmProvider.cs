using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using CLIo.Models;

namespace CLIo.Services;

public sealed class ResilientLlmProvider : ILLMProvider
{
    private readonly DeepSeekProvider primary;
    private readonly FallbackLlmProvider fallback;
    private readonly ClioStateService clioState;
    private readonly DevLogService devLog;
    private readonly RuntimeLog? runtimeLog;

    public ResilientLlmProvider(
        DeepSeekProvider primary,
        FallbackLlmProvider fallback,
        ClioStateService clioState,
        DevLogService devLog,
        RuntimeLog? runtimeLog = null)
    {
        this.primary = primary;
        this.fallback = fallback;
        this.clioState = clioState;
        this.devLog = devLog;
        this.runtimeLog = runtimeLog;
    }

    public IAsyncEnumerable<string> ChatAsync(
        IEnumerable<LlmMessage> messages,
        CancellationToken cancellationToken = default)
    {
        var messageList = messages.ToList();
        var channel = Channel.CreateUnbounded<string>();

        // what this call is for and how it went, recorded once when the stream ends
        var purpose = LlmCall.Purpose;
        var stats = new LlmCallStats();
        LlmCall.Stats.Value = stats;
        var started = Stopwatch.GetTimestamp();
        var inputChars = messageList.Sum(m => m.Content.Length);
        var lastUser = messageList.LastOrDefault(m => m.Role == "user")?.Content ?? string.Empty;
        var model = Environment.GetEnvironmentVariable("DEEPSEEK_MODEL") ?? "deepseek-chat";
        var outputChars = 0;
        var preview = new StringBuilder();
        var engine = "deepseek";
        string? error = null;

        void Emit(string token)
        {
            outputChars += token.Length;
            if (preview.Length < 160)
            {
                preview.Append(token);
            }
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var token in primary.ChatAsync(messageList, cancellationToken))
                {
                    Emit(token);
                    await channel.Writer.WriteAsync(token, cancellationToken);
                }
            }
            catch (HttpRequestException ex)
            {
                devLog.Log($"LLM error (HttpRequest): {ex.Message}", "llm");
                _ = clioState.RecordLlmErrorAsync();
                engine = "offline-fallback";
                error = ex.Message;
                await foreach (var token in fallback.ChatAsync(messageList, cancellationToken))
                {
                    Emit(token);
                    await channel.Writer.WriteAsync(token, cancellationToken);
                }
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                devLog.Log("LLM error: request timed out, falling back to offline", "llm");
                _ = clioState.RecordLlmErrorAsync();
                engine = "offline-fallback";
                error = "timed out";
                await foreach (var token in fallback.ChatAsync(messageList, cancellationToken))
                {
                    Emit(token);
                    await channel.Writer.WriteAsync(token, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                devLog.Log($"LLM error: {ex.Message}", "llm");
                engine = "failed";
                error = ex.Message;
                if (outputChars > 0 && !cancellationToken.IsCancellationRequested)
                {
                    // the stream broke part-way; say so instead of leaving a silently cut-off reply
                    Emit("\n\n[The connection dropped before this reply finished.]");
                    await channel.Writer.WriteAsync("\n\n[The connection dropped before this reply finished.]");
                }
            }
            finally
            {
                channel.Writer.TryComplete();
                RecordCall();
            }
        }, cancellationToken);

        void RecordCall()
        {
            var estimated = stats.PromptTokens is null;
            var tokensIn = stats.PromptTokens ?? inputChars / 4;
            var tokensOut = stats.CompletionTokens ?? outputChars / 4;
            // DeepSeekProvider reports a rejected request as ordinary text rather than throwing
            var text = preview.ToString();
            if (engine == "deepseek"
                && (text.StartsWith("DeepSeek request failed", StringComparison.Ordinal)
                    || text.StartsWith("Add your DeepSeek API key", StringComparison.Ordinal)))
            {
                engine = "failed";
                error = text.Length > 80 ? text[..80] : text;
            }

            var outcome = engine == "deepseek" ? "ok" : engine == "offline-fallback" ? "fallback" : "error";
            var detail = new StringBuilder();
            detail.Append($"{purpose}; engine={engine}");
            if (estimated)
            {
                detail.Append("; tokens estimated from text length");
            }

            if (stats.CacheHitTokens is { } hit)
            {
                detail.Append($"; cache_hit={hit}");
            }

            if (error is not null)
            {
                detail.Append($"; problem: {error}");
            }

            detail.Append($"; asked: \"{Shorten(lastUser, 110)}\"; got: \"{Shorten(preview.ToString(), 110)}\"");
            runtimeLog?.Record(
                "llm", purpose, outcome, detail.ToString(),
                (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                engine == "offline-fallback" ? "offline-fallback" : model, tokensIn, tokensOut);
        }

        return channel.Reader.ReadAllAsync(cancellationToken);
    }

    private static string Shorten(string text, int max)
    {
        var flat = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return flat.Length <= max ? flat : flat[..(max - 1)] + "…";
    }
}
