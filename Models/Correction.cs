namespace CLIo.Models;

/// <summary>
/// One time the user corrected, overrode or rejected something CLIo did or believed. Kept permanently (it is tiny)
/// as the raw material for any later fine-tuning or evaluation: what CLIo had, what the user wanted instead.
/// </summary>
public sealed record Correction(
    DateTimeOffset Time,
    string Kind,
    string Target,
    string OldValue,
    string NewValue,
    string Source,
    string Note);
