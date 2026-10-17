using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CLIo.Models;

namespace CLIo.Views;

/// <summary>Small always-on-top toast shown bottom-right when a reminder fires. Built in code to keep it self-contained.</summary>
public sealed class ReminderPopupWindow : Window
{
    private const int PopupWidth = 340;
    private const int PopupHeight = 150;
    private static int openCount;

    public ReminderPopupWindow(Reminder reminder, Func<Task> onDismiss, Func<TimeSpan, Task> onSnooze, Action onOpen)
    {
        Width = PopupWidth;
        Height = PopupHeight;
        CanResize = false;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        SystemDecorations = SystemDecorations.None;
        Background = Brushes.Transparent;
        Title = "CLIo reminder";

        var accent = new SolidColorBrush(Color.Parse("#C9CED6"));
        var muted = new SolidColorBrush(Color.Parse("#7B818C"));

        Button MakeButton(string text, bool primary)
        {
            return new Button
            {
                Content = text,
                Padding = new Thickness(12, 5),
                Background = primary ? accent : new SolidColorBrush(Color.Parse("#17191D")),
                Foreground = primary ? new SolidColorBrush(Color.Parse("#0B0C0E")) : Brushes.White,
                BorderBrush = new SolidColorBrush(Color.Parse("#2A2D33"))
            };
        }

        var done = MakeButton("Done", true);
        var snooze = MakeButton("Snooze 10m", false);
        var open = MakeButton("Open", false);
        done.Click += async (_, _) => { await onDismiss(); Close(); };
        snooze.Click += async (_, _) => { await onSnooze(TimeSpan.FromMinutes(10)); Close(); };
        open.Click += (_, _) => { onOpen(); };

        Content = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#0B0C0E")),
            BorderBrush = accent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "REMINDER", FontSize = 11, Foreground = muted, LetterSpacing = 2 },
                    new TextBlock
                    {
                        Text = reminder.Title,
                        FontSize = 15,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = Brushes.White,
                        TextWrapping = TextWrapping.Wrap,
                        MaxLines = 3
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Margin = new Thickness(0, 4, 0, 0),
                        Children = { done, snooze, open }
                    }
                }
            }
        };

        Opened += (_, _) => Place();
        Closed += (_, _) => openCount = Math.Max(0, openCount - 1);
        openCount++;
    }

    private void Place()
    {
        var screen = Screens.Primary;
        if (screen is null)
        {
            return;
        }

        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        var slot = Math.Max(0, openCount - 1);
        var x = area.Right - (int)((PopupWidth + 16) * scale);
        var y = area.Bottom - (int)((PopupHeight + 12) * scale * (slot + 1));
        Position = new PixelPoint(x, Math.Max(area.Y, y));
    }
}
