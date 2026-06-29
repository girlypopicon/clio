using CommunityToolkit.Mvvm.ComponentModel;

namespace CLIo.Models;

public partial class JournalEntry : ObservableObject
{
    public int Id { get; init; }
    public required string EntryDate { get; init; }
    public required string Title { get; init; }
    public required string Mood { get; init; }
    public required string Body { get; init; }
    public required string UpdatedAt { get; init; }
}
