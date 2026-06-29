namespace CLIo.Models;

public sealed class MemoryTransaction
{
    public int Id { get; init; }
    public required string Timestamp { get; init; }
    public required string Action { get; init; }
    public required string Field { get; init; }
    public required string Value { get; init; }
    public required string Status { get; init; }
    public required string Source { get; init; }
    public required string Error { get; init; }
    public required string Type { get; init; }
}
