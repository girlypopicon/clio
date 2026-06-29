using CLIo.Models;

namespace CLIo.Services;

public interface ILLMProvider
{
    IAsyncEnumerable<string> ChatAsync(IEnumerable<LlmMessage> messages, CancellationToken cancellationToken = default);
}
