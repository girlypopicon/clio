using CommunityToolkit.Mvvm.ComponentModel;

namespace CLIo.Models;

public partial class JournalTask : ObservableObject
{
    public int Id { get; init; }
    public required string Title { get; init; }

    [ObservableProperty]
    private bool isDone;
}
