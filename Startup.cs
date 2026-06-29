using CLIo.Agents;
using CLIo.Services;
using CLIo.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace CLIo;

public static class Startup
{
    public static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // Core infrastructure
        services.AddSingleton<DataProtectionService>();
        services.AddSingleton<JournalDatabaseService>();

        // LLM providers
        services.AddSingleton<DeepSeekProvider>();
        services.AddSingleton<OfflineNlpService>();
        services.AddSingleton<FallbackLlmProvider>();
        services.AddSingleton<ILLMProvider, ResilientLlmProvider>();

        // Utilities
        services.AddSingleton<DateTimeParsingService>();
        services.AddSingleton<SystemPromptBuilder>();

        // CLIo self-state
        services.AddSingleton<ClioStateService>();
        services.AddSingleton<DevLogService>();

        services.AddSingleton<WebSearchService>();

        // Domain services
        services.AddSingleton<IMemoryService, MemoryService>();
        services.AddSingleton<ICalendarService, CalendarService>();
        services.AddSingleton<ITaskService, TaskService>();

        // BotSharp agent routing
        services.AddSingleton<ClioRouter>();
        services.AddSingleton<DeepSeekChatCompletion>();

        // ViewModel
        services.AddTransient<MainWindowViewModel>();

        return services.BuildServiceProvider();
    }
}
