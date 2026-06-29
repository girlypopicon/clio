using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CLIo.Models;

namespace CLIo.Services;

public sealed class ResilientLlmProvider : ILLMProvider
{
    private readonly DeepSeekProvider primary;
    private readonly FallbackLlmProvider fallback;
    private readonly ClioStateService clioState;
    private readonly DevLogService devLog;

    public ResilientLlmProvider(DeepSeekProvider primary, FallbackLlmProvider fallback, ClioStateService clioState, DevLogService devLog)
    {
        this.primary = primary;
        this.fallback = fallback;
        this.clioState = clioState;
        this.devLog = devLog;
    }

    public IAsyncEnumerable<string> ChatAsync(
        IEnumerable<LlmMessage> messages,
        CancellationToken cancellationToken = default)
    {
        var messageList = messages.ToList();
        var channel = Channel.CreateUnbounded<string>();

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var token in primary.ChatAsync(messageList, cancellationToken))
                    await channel.Writer.WriteAsync(token, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                devLog.Log($"LLM error (HttpRequest): {ex.Message}", "llm");
                _ = clioState.RecordLlmErrorAsync();
                await foreach (var token in fallback.ChatAsync(messageList, cancellationToken))
                    await channel.Writer.WriteAsync(token, cancellationToken);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                devLog.Log("LLM error: request timed out, falling back to offline", "llm");
                _ = clioState.RecordLlmErrorAsync();
                await foreach (var token in fallback.ChatAsync(messageList, cancellationToken))
                    await channel.Writer.WriteAsync(token, cancellationToken);
            }
            finally
            {
                channel.Writer.Complete();
            }
        }, cancellationToken);

        return channel.Reader.ReadAllAsync(cancellationToken);
    }
}
