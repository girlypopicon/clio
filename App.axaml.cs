using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using CLIo.Models;
using CLIo.Services;
using CLIo.ViewModels;
using CLIo.Views;
using Microsoft.Extensions.DependencyInjection;

namespace CLIo;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>True once the user chose Quit; until then closing the main window only hides it to the tray.</summary>
    public static bool IsQuitting { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Services = Startup.ConfigureServices();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Keep running in the tray after the main window is closed; Quit calls Shutdown explicitly.
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;

            var splash = new SplashWindow();
            desktop.MainWindow = splash;
            splash.Show();

            var viewModel = Services.GetRequiredService<MainWindowViewModel>();
            _ = ShowMainWindowAsync(desktop, splash, viewModel);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async System.Threading.Tasks.Task ShowMainWindowAsync(
        IClassicDesktopStyleApplicationLifetime desktop, SplashWindow splash, MainWindowViewModel viewModel)
    {
        await System.Threading.Tasks.Task.WhenAll(viewModel.Initialization, System.Threading.Tasks.Task.Delay(1200));

        var main = new MainWindow
        {
            DataContext = viewModel,
        };
        desktop.MainWindow = main;
        main.Show();
        splash.Close();

        var reminders = Services.GetRequiredService<ReminderService>();
        reminders.ReminderDue += reminder => ShowReminderPopup(reminders, reminder);
        reminders.Start();
    }

    private static void ShowReminderPopup(ReminderService reminders, Reminder reminder)
    {
        var popup = new ReminderPopupWindow(
            reminder,
            onDismiss: () => reminders.DismissAsync(reminder.Id),
            onSnooze: delay => reminders.SnoozeAsync(reminder.Id, delay),
            onOpen: ShowMainWindow);
        popup.Show();
    }

    public static void ShowMainWindow()
    {
        if (Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
        {
            return;
        }

        window.Show();
        if (window.WindowState == Avalonia.Controls.WindowState.Minimized)
        {
            window.WindowState = Avalonia.Controls.WindowState.Maximized;
        }

        window.Activate();
    }

    private void TrayIcon_OnClicked(object? sender, EventArgs e) => ShowMainWindow();

    private void OpenMenuItem_OnClick(object? sender, EventArgs e) => ShowMainWindow();

    private void QuitMenuItem_OnClick(object? sender, EventArgs e)
    {
        IsQuitting = true;
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
}