namespace CLIo.Models;

/// <summary>
/// Something the app will not decide by itself, shown in the Review view. Two families:
///  - conflicts ("memory", "event"): an automatic value disagrees with something the user set by hand;
///  - tidy proposals ("merge_memory", "rename_memory", "duplicate_event", "duplicate_task"): the nightly scan
///    thinks something is stored twice or is badly shaped.
/// </summary>
public sealed class ReviewItem
{
    public int Id { get; init; }

    public required string Kind { get; init; }

    /// <summary>What the item is about; see <see cref="Kind"/> and TidyProposal for the meaning per kind.</summary>
    public required string TargetKey { get; init; }

    /// <summary>Human-readable name of the thing in conflict, or the full sentence for a tidy proposal.</summary>
    public string Label { get; init; } = string.Empty;

    public string CurrentValue { get; init; } = string.Empty;

    public string ProposedValue { get; init; } = string.Empty;

    /// <summary>Where the proposal came from (e.g. "auto_extract", "tidy").</summary>
    public string Evidence { get; init; } = string.Empty;

    public string CreatedAt { get; init; } = string.Empty;

    public bool IsTidy => Kind is "merge_memory" or "rename_memory" or "duplicate_event" or "duplicate_task";

    public string Summary => Kind switch
    {
        "event" => $"{Label}: you set {CurrentValue}; chat suggests {FriendlyProposed}",
        "memory" => $"{Label}: you set \"{CurrentValue}\"; chat suggests \"{ProposedValue}\"",
        _ => Label
    };

    /// <summary>Button text for the "yes, do it" choice.</summary>
    public string AcceptLabel => Kind switch
    {
        "merge_memory" => "MERGE",
        "rename_memory" => "RENAME",
        "duplicate_event" or "duplicate_task" => "REMOVE DUPLICATE",
        _ => "USE CHAT'S"
    };

    /// <summary>Button text for the "no, leave it" choice.</summary>
    public string DismissLabel => IsTidy ? "LEAVE IT" : "KEEP MINE";

    private string FriendlyProposed =>
        DateTimeOffset.TryParse(ProposedValue, out var when) ? when.ToString("MMM d, h:mm tt") : ProposedValue;
}
