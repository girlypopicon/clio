using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CLIo.Models;
using CLIo.ViewModels;

namespace CLIo.Views;

public partial class MainWindow : Window
{
    private const double BottomThreshold = 48;

    private MainWindowViewModel? viewModel;
    private INotifyCollectionChanged? observedMessages;
    private ScrollViewer? chatScrollViewer;
    private bool isAtBottom = true;
    private bool isSelecting;
    private DateTime lastScrollTime;

    public MainWindow()
    {
        InitializeComponent();
        ChatInputBox.AddHandler(InputElement.KeyDownEvent, ChatInputBox_OnKeyDown, RoutingStrategies.Tunnel);
        KeyDown += MainWindow_OnKeyDown;
        DataContextChanged += OnDataContextChanged;
        Closing += MainWindow_OnClosing;
        Opened += (_, _) => ScrollChatToBottom(force: true);
        Opened += (_, _) => ApplyThemedTitleBar();
        ChatMessagesList.AddHandler(InputElement.PointerPressedEvent, ChatMessagesList_OnPointerPressed, RoutingStrategies.Tunnel);
        ChatMessagesList.AddHandler(InputElement.PointerReleasedEvent, ChatMessagesList_OnPointerReleased, RoutingStrategies.Tunnel);
    }

    private void MainWindow_OnClosing(object? sender, WindowClosingEventArgs e)
    {
        // Close button minimizes to the system tray so reminders keep firing; Quit lives in the tray menu.
        if (!App.IsQuitting && e.CloseReason == WindowCloseReason.WindowClosing)
        {
            e.Cancel = true;
            Hide();
        }
    }

    private void ApplyThemedTitleBar()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var handle = TryGetGetPlatformHandle();
        if (handle == IntPtr.Zero)
        {
            return;
        }

        // DWM caption/text/border colors are 0x00BBGGRR.
        var captionColor = 0x00221D1Bu; // #1B1D22 in BGR
        var textColor = 0x00D6CEC9u;    // #C9CED6 in BGR
        var borderColor = 0x00332D2Au;  // #2A2D33 in BGR

        DwmSetWindowAttribute(handle, DwmwaCaptionColor, ref captionColor, sizeof(uint));
        DwmSetWindowAttribute(handle, DwmwaTextColor, ref textColor, sizeof(uint));
        DwmSetWindowAttribute(handle, DwmwaBorderColor, ref borderColor, sizeof(uint));
    }

    private IntPtr TryGetGetPlatformHandle()
    {
        return TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
    }

    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref uint value, int size);

    private void ChatMessagesList_OnPointerPressed(object? sender, PointerEventArgs e)
    {
        if (e.Source is TextBox)
        {
            isSelecting = true;
        }
    }

    private void ChatMessagesList_OnPointerReleased(object? sender, PointerEventArgs e)
    {
        if (!isSelecting)
        {
            return;
        }

        isSelecting = false;
        if (isAtBottom)
        {
            ScrollChatToBottom();
        }
    }

    private ScrollViewer? GetChatScrollViewer()
    {
        return chatScrollViewer ??= ChatMessagesList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
    }

    private void AttachScrollViewer()
    {
        var scrollViewer = GetChatScrollViewer();
        if (scrollViewer is null)
        {
            return;
        }

        scrollViewer.ScrollChanged -= OnChatScrollChanged;
        scrollViewer.ScrollChanged += OnChatScrollChanged;
    }

    private void OnChatScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        var distanceFromBottom = scrollViewer.Extent.Height - (scrollViewer.Offset.Y + scrollViewer.Viewport.Height);
        isAtBottom = distanceFromBottom <= BottomThreshold;
    }

    private async void MainWindow_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.C || !e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            return;
        }

        if (e.Source is TextBox)
        {
            return;
        }

        if (ChatMessagesList.SelectedItem is ChatMessage message)
        {
            await CopyTextAsync(message.Content);
            e.Handled = true;
        }
    }

    private async void CopyMessageButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Control { Tag: string content })
        {
            await CopyTextAsync(content);
            e.Handled = true;
        }
    }

    private void RenameChatThreadMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: ChatThread thread } && DataContext is MainWindowViewModel vm)
        {
            vm.BeginRenameChatThread(thread);
            e.Handled = true;
        }
    }

    private async void SummarizeChatThreadMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: ChatThread thread } && DataContext is MainWindowViewModel vm)
        {
            await vm.SummarizeChatThreadAsync(thread);
            e.Handled = true;
        }
    }

    private async void ClearChatThreadMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: ChatThread thread } && DataContext is MainWindowViewModel vm)
        {
            await vm.ClearChatThreadAsync(thread);
            e.Handled = true;
        }
    }

    private void CalendarDayButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: CalendarDayCell day } && DataContext is MainWindowViewModel vm)
        {
            vm.SelectCalendarDayCommand.Execute(day);
            e.Handled = true;
        }
    }

    private async void DeleteChatThreadMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: ChatThread thread } && DataContext is MainWindowViewModel vm)
        {
            await vm.DeleteChatThreadAsync(thread);
            e.Handled = true;
        }
    }

    private async Task CopyTextAsync(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (OperatingSystem.IsWindows() && TrySetWindowsClipboardText(text))
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    private static bool TrySetWindowsClipboardText(string text)
    {
        const uint globalMoveable = 0x0002;
        const uint clipboardUnicodeText = 13;

        if (!OpenClipboard(IntPtr.Zero))
        {
            return false;
        }

        var handle = IntPtr.Zero;
        try
        {
            EmptyClipboard();

            var bytes = Encoding.Unicode.GetBytes(text + "\0");
            handle = GlobalAlloc(globalMoveable, (UIntPtr)bytes.Length);
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                Marshal.Copy(bytes, 0, pointer, bytes.Length);
            }
            finally
            {
                GlobalUnlock(handle);
            }

            if (SetClipboardData(clipboardUnicodeText, handle) == IntPtr.Zero)
            {
                return false;
            }

            handle = IntPtr.Zero;
            return true;
        }
        finally
        {
            CloseClipboard();
            if (handle != IntPtr.Zero)
            {
                GlobalFree(handle);
            }
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (viewModel is not null)
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        UnobserveMessages();
        viewModel = DataContext as MainWindowViewModel;
        if (viewModel is null)
        {
            return;
        }

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        ObserveMessages();
        ScrollChatToBottom(force: true);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsChatRenameOpen) && viewModel?.IsChatRenameOpen == true)
        {
            FocusRenameTitleBox();
            return;
        }

        if (e.PropertyName != nameof(MainWindowViewModel.ChatMessages))
        {
            return;
        }

        UnobserveMessages();
        ObserveMessages();
        ScrollChatToBottom(force: true);
    }

    private void FocusRenameTitleBox()
    {
        Dispatcher.UIThread.Post(() =>
        {
            RenameChatTitleBox.Focus();
            RenameChatTitleBox.SelectAll();
        }, DispatcherPriority.Background);
    }

    private void ObserveMessages()
    {
        observedMessages = viewModel?.ChatMessages;
        if (observedMessages is not null)
        {
            observedMessages.CollectionChanged += OnMessagesChanged;
        }

        if (viewModel is null)
        {
            return;
        }

        foreach (var message in viewModel.ChatMessages)
        {
            message.PropertyChanged += OnMessagePropertyChanged;
        }
    }

    private void UnobserveMessages()
    {
        if (observedMessages is not null)
        {
            observedMessages.CollectionChanged -= OnMessagesChanged;
        }

        if (viewModel is not null)
        {
            foreach (var message in viewModel.ChatMessages)
            {
                message.PropertyChanged -= OnMessagePropertyChanged;
            }
        }

        observedMessages = null;
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (ChatMessage message in e.OldItems)
            {
                message.PropertyChanged -= OnMessagePropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (ChatMessage message in e.NewItems)
            {
                message.PropertyChanged += OnMessagePropertyChanged;
            }
        }

        // A brand-new message always jumps to the bottom, wherever the user had scrolled to.
        // (Streaming updates to an existing message only follow if the user is already at the bottom.)
        ScrollChatToBottom(force: e.Action == NotifyCollectionChangedAction.Add);
    }

    private void OnMessagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatMessage.Content))
        {
            ScrollChatToBottom();
        }
    }

    private void ScrollChatToBottom(bool force = false)
    {
        if (!force && (isSelecting || !isAtBottom))
        {
            return;
        }

        // Debounce: skip if we scrolled less than 50ms ago (prevents flicker during streaming)
        var now = DateTime.UtcNow;
        if (!force && (now - lastScrollTime).TotalMilliseconds < 50)
        {
            return;
        }
        lastScrollTime = now;

        Dispatcher.UIThread.Post(() =>
        {
            AttachScrollViewer();

            var messages = ChatMessagesList.ItemsSource?.Cast<object>().ToList();
            if (messages is { Count: > 0 })
            {
                ChatMessagesList.ScrollIntoView(messages[^1]);
            }

            isAtBottom = true;
        }, DispatcherPriority.Background);
    }

    private void TitleBar_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Custom title bar (NoChrome): start a window drag, or toggle maximize on double-click.
        // Ignore presses that land on a button so the menu and window controls keep working.
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
        {
            return;
        }

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        BeginMoveDrag(e);
    }

    private void MinimizeButton_OnClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_OnClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void CloseButton_OnClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ChatInputBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && sender is TextBox textBox)
        {
            var text = textBox.Text ?? string.Empty;
            var caretIndex = Math.Clamp(textBox.CaretIndex, 0, text.Length);
            textBox.Text = text.Insert(caretIndex, Environment.NewLine);
            textBox.CaretIndex = caretIndex + Environment.NewLine.Length;
            e.Handled = true;
            return;
        }

        if (DataContext is MainWindowViewModel viewModel && viewModel.SendChatCommand.CanExecute(null))
        {
            viewModel.SendChatCommand.Execute(null);
            e.Handled = true;
        }
    }
}
