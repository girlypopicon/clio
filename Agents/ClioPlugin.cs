using BotSharp.Abstraction.MLTasks;
using BotSharp.Abstraction.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CLIo.Agents;

public sealed class ClioPlugin : IBotSharpPlugin
{
    public string Id => "clio-plugin";
    public string Name => "CLIo Plugin";
    public string Description => "CLIo desktop agent plugin with DeepSeek LLM and domain agents";
    public string[] AgentIds => [ClioAgentIds.Router, ClioAgentIds.GeneralChat, ClioAgentIds.Memory, ClioAgentIds.Calendar, ClioAgentIds.Task];

    public void RegisterDI(IServiceCollection services, IConfiguration config)
    {
        services.AddScoped<IChatCompletion, DeepSeekChatCompletion>();
    }
}
