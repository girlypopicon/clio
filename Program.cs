using Avalonia;
using System;

namespace CLIo;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        // "CLIo.exe --validate-services": checks that every service can be assembled (a wiring mistake would
        // otherwise crash the app at startup), creates nothing, touches no data, and reports through the exit
        // code (0 = fine) and the file named by CLIO_VALIDATE_OUT, because a windowed app has no console.
        if (args.Contains("--validate-services"))
        {
            try
            {
                Startup.ConfigureServices(validate: true);
                WriteValidation("OK");
                return 0;
            }
            catch (Exception ex)
            {
                WriteValidation("FAILED: " + ex.Message);
                return 1;
            }
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static void WriteValidation(string text)
    {
        var path = Environment.GetEnvironmentVariable("CLIO_VALIDATE_OUT");
        if (!string.IsNullOrEmpty(path))
        {
            System.IO.File.WriteAllText(path, text);
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
