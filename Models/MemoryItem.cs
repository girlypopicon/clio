namespace CLIo.Models;

public sealed class MemoryItem
{
    public required string Key { get; init; }
    public required string Value { get; init; }
    public required string UpdatedAt { get; init; }
    public required string Type { get; init; }
}
