using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using CLIo.ViewModels;
using CLIo.Views;
using Microsoft.Extensions.DependencyInjection;

namespace CLIo;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Services = Startup.ConfigureServices();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
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
    }
}