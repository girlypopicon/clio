using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CLIo.Models;

public partial class ChatMessage : ObservableObject
{
    public int Id { get; init; }
    public required string Role { get; init; }
    public required DateTimeOffset Timestamp { get; init; }

    public string DisplayRole => Role.Equals("assistant", StringComparison.OrdinalIgnoreCase)
        ? "CLIo"
        : Role.Equals("user", StringComparison.OrdinalIgnoreCase)
            ? "You"
            : Role;

    [ObservableProperty]
    private string content = string.Empty;

    [ObservableProperty]
    private string? calendarWidgetLabel;

    [ObservableProperty]
    private ObservableCollection<CalendarDayCell> calendarWidgetDays = [];

    [ObservableProperty]
    private int calendarWidgetColumns = 7;

    [ObservableProperty]
    private ObservableCollection<ActionBadge> actionBadges = [];

    public bool HasCalendarWidget => CalendarWidgetDays.Count > 0;

    public bool HasActionBadges => ActionBadges.Count > 0;

    public int CalendarWidgetRows => CalendarWidgetColumns == 0 ? 0 : CalendarWidgetDays.Count / CalendarWidgetColumns;

    partial void OnCalendarWidgetDaysChanged(ObservableCollection<CalendarDayCell> value)
    {
        OnPropertyChanged(nameof(HasCalendarWidget));
        OnPropertyChanged(nameof(CalendarWidgetRows));
    }

    partial void OnActionBadgesChanged(ObservableCollection<ActionBadge> value)
    {
        OnPropertyChanged(nameof(HasActionBadges));
    }
}
