namespace CLIo.Models;

public sealed class DevNote
{
    public int Id { get; init; }
    public required string Note { get; init; }
    public required string Source { get; init; }
    public required string Status { get; init; }
    public required string CreatedAt { get; init; }
    public required string CompletedAt { get; init; }
}
