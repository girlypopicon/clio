using CLIo.Models;

namespace CLIo.Services;

public interface IMemoryService
{
    Task<IReadOnlyList<MemoryItem>> GetAllMemoryAsync();
    Task<IReadOnlyList<MemoryTransaction>> GetTransactionsAsync(int limit = 80);
    Task UpsertAsync(string key, string value, string type);
    Task DeleteAsync(string key);
    Task<IReadOnlyList<MemoryItem>> SearchAsync(string term);
    Task RenameKeyAsync(string oldKey, string newKey);
    Task MergeKeysAsync(string targetKey, string sourceKey);
    Task<int> GetNonEmptyCountAsync();

    MemoryValidationResult ValidateCandidate(MemoryCandidate candidate, string? existingValue, bool isMainChat, bool isExplicitWrite);
    string? MapMemoryField(string normalized);
    bool IsValidMemoryKeyName(string key);
    string MemoryTypeForKey(string key);
    string BuildPreferenceKey(string value);
    string BuildPatternKey(string value);
    IReadOnlyList<MemoryCandidate> ExtractLocalMemoryCandidates(string text);
}

public sealed record MemoryCandidate(string Key, string Value, bool IsExplicitWrite, string Type);
public sealed record MemoryValidationResult(string Status, string Error, bool ShouldWrite);
