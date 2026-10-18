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
        services.AddSingleton<RuntimeLog>();

        services.AddSingleton<WebSearchService>();

        // Skills (see Services/Skills/ISkill.cs) and persona agents
        services.AddSingleton<CLIo.Services.Skills.ISkill, CLIo.Services.Skills.WebSearchSkill>();
        services.AddSingleton<CLIo.Services.Skills.ISkill, CLIo.Services.Skills.BrowseSkill>();
        services.AddSingleton<CLIo.Services.Skills.ISkill, CLIo.Services.Skills.SelfDocsSkill>();
        services.AddSingleton<CLIo.Services.Skills.ISkill, CLIo.Services.Skills.QueryRuntimeLogSkill>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<CLIo.Services.Skills.ISkill, CLIo.Services.Skills.UpdateSkill>();
        services.AddSingleton<CLIo.Services.Update.UpdateInstaller>();
        services.AddSingleton<CLIo.Services.Skills.ISkill, CLIo.Services.Skills.InstallUpdateSkill>();
        services.AddSingleton<CLIo.Services.Skills.SkillRegistry>();
        services.AddSingleton<CLIo.Services.Skills.AgentCatalog>();

        // Domain services
        services.AddSingleton<IMemoryService, MemoryService>();
        services.AddSingleton<ICalendarService, CalendarService>();
        services.AddSingleton<ITaskService, TaskService>();
        services.AddSingleton<ReminderService>();

        // Voice: Whisper/Piper when models are in %LOCALAPPDATA%/CLIo/voice, else Windows engines
        services.AddSingleton<VoiceModelDownloader>();
        services.AddSingleton<SystemSpeechToTextService>();
        services.AddSingleton<ISpeechToTextService, WhisperSpeechToTextService>();
        services.AddSingleton<SystemTextToSpeechService>();
        services.AddSingleton<ITextToSpeechService, PiperTextToSpeechService>();

        // BotSharp agent routing
        services.AddSingleton<ClioRouter>();
        services.AddSingleton<DeepSeekChatCompletion>();

        // ViewModel
        services.AddTransient<MainWindowViewModel>();

        return services.BuildServiceProvider();
    }
}
