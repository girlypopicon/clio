using CommunityToolkit.Mvvm.ComponentModel;

namespace CLIo.Models;

/// <summary>Editable copy of a calendar event shown in the day modal.</summary>
public partial class CalendarEventEditor : ObservableObject
{
    public required int Id { get; init; }

    [ObservableProperty]
    private string title = string.Empty;

    [ObservableProperty]
    private string details = string.Empty;

    [ObservableProperty]
    private TimeSpan? time;
}
