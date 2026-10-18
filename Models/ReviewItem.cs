namespace CLIo.Models;

/// <summary>
/// A conflict the app will not resolve by itself: an automatic (chat-derived) value that disagrees
/// with something the user set by hand. The user accepts or dismisses it in the Review view.
/// </summary>
public sealed class ReviewItem
{
    public int Id { get; init; }

    /// <summary>"memory" or "event".</summary>
    public required string Kind { get; init; }

    /// <summary>Memory key, or the calendar event id for events.</summary>
    public required string TargetKey { get; init; }

    /// <summary>Human-readable name of the thing in conflict (memory key or event title).</summary>
    public string Label { get; init; } = string.Empty;

    public string CurrentValue { get; init; } = string.Empty;

    public string ProposedValue { get; init; } = string.Empty;

    /// <summary>Where the proposal came from (e.g. "auto_extract", "local_capture").</summary>
    public string Evidence { get; init; } = string.Empty;

    public string CreatedAt { get; init; } = string.Empty;

    public string Summary => Kind == "event"
        ? $"{Label}: you set {CurrentValue}; chat suggests {FriendlyProposed}"
        : $"{Label}: you set \"{CurrentValue}\"; chat suggests \"{ProposedValue}\"";

    private string FriendlyProposed =>
        DateTimeOffset.TryParse(ProposedValue, out var when) ? when.ToString("MMM d, h:mm tt") : ProposedValue;
}
