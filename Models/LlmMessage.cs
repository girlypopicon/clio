namespace CLIo.Models;

public sealed record LlmMessage(string Role, string Content, DateTimeOffset Timestamp);
