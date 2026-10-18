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

        // Anything that kills the app or escapes a background task is written to the runtime log.
        var runtimeLog = Services.GetRequiredService<RuntimeLog>();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            runtimeLog.Record("app", "unhandled_exception", "error", e.ExceptionObject.ToString() ?? "unknown");
            runtimeLog.FlushAsync().Wait(2000);
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
            runtimeLog.Record("app", "unobserved_task_exception", "error", e.Exception.ToString());

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Keep running in the tray after the main window is closed; Quit calls Shutdown explicitly.
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
            desktop.Exit += (_, _) =>
            {
                runtimeLog.Record("app", "exit", "ok", "CLIo is closing");
                runtimeLog.FlushAsync().Wait(2000);
            };

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

    private void QuitMenuItem_OnClick(object? sender, EventArgs e) => Quit();

    /// <summary>Really exits the app (used by Quit in the tray menu and by the updater).</summary>
    public static void Quit()
    {
        IsQuitting = true;
        (Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
}