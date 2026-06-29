using BotSharp.Abstraction.MLTasks;
using BotSharp.Abstraction.Agents.Models;
using BotSharp.Abstraction.Conversations.Models;

namespace CLIo.Agents;

public sealed class DeepSeekChatCompletion : IChatCompletion
{
    private readonly Services.DeepSeekProvider provider;
    private string modelName = "deepseek-chat";

    public string Provider => "deepseek";
    public string Model => modelName;

    public DeepSeekChatCompletion(Services.DeepSeekProvider provider)
    {
        this.provider = provider;
    }

    public void SetModelName(string model) => modelName = model;

    public async Task<RoleDialogModel> GetChatCompletions(Agent agent, List<RoleDialogModel> conversations)
    {
        var llmMessages = conversations.Select(c =>
            new Models.LlmMessage(c.Role, c.Content, DateTimeOffset.Now)).ToList();

        var response = new System.Text.StringBuilder();
        await foreach (var token in provider.ChatAsync(llmMessages))
            response.Append(token);

        return new RoleDialogModel("assistant", response.ToString())
        {
            CurrentAgentId = agent.Id
        };
    }

    public async Task<bool> GetChatCompletionsAsync(
        Agent agent,
        List<RoleDialogModel> conversations,
        Func<RoleDialogModel, Task> onMessageReceived,
        Func<RoleDialogModel, Task> onFunctionExecuting)
    {
        var result = await GetChatCompletions(agent, conversations);
        await onMessageReceived(result);
        return true;
    }
}
