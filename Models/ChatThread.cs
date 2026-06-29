namespace CLIo.Models;

public sealed class ChatThread
{
    public int Id { get; init; }
    public required string Title { get; init; }
    public required string Summary { get; init; }
    public bool IsMain { get; init; }
    public required string UpdatedAt { get; init; }
}
