namespace CLIo.Agents;

public sealed class ClioRouter
{
    public string Route(string prefix, string body)
    {
        if (!string.IsNullOrEmpty(prefix))
        {
            return prefix switch
            {
                "++" => RouteAddCommand(body),
                "--" => RouteDeleteCommand(body),
                "==" => ClioAgentIds.Calendar,
                "__" => RouteDisplayCommand(body),
                _ => ClioAgentIds.GeneralChat
            };
        }

        return RouteByContent(body);
    }

    private static string RouteAddCommand(string body)
    {
        var lower = body.ToLowerInvariant();
        if (ContainsDateContext(lower))
            return ClioAgentIds.Calendar;
        return ClioAgentIds.Task;
    }

    private static string RouteDeleteCommand(string body)
    {
        var lower = body.ToLowerInvariant();
        if (lower.Contains("appointment") || lower.Contains("event") || lower.Contains("meeting")
            || lower.Contains("reminder") || lower.Contains("schedule"))
            return ClioAgentIds.Calendar;
        return ClioAgentIds.Task;
    }

    private static string RouteDisplayCommand(string body)
    {
        var lower = body.ToLowerInvariant().Trim();

        if (lower.StartsWith("forget") || lower.StartsWith("rename") || lower.StartsWith("merge")
            || lower.StartsWith("search") || lower is "compact"
            || lower is "memory" or "memories"
            || IsMemoryCategory(lower))
            return ClioAgentIds.Memory;

        if (lower is "tasks" or "task" or "todo" or "to-do" or "todos" or "to-dos")
            return ClioAgentIds.Task;

        return ClioAgentIds.Calendar;
    }

    private static string RouteByContent(string body)
    {
        var lower = body.ToLowerInvariant();

        if (lower.Contains("remember") || lower.Contains("my name is") || lower.Contains("i live in")
            || lower.Contains("save") || lower.Contains("forget"))
            return ClioAgentIds.Memory;

        if (ContainsDateContext(lower) && (lower.Contains("schedule") || lower.Contains("appointment")
            || lower.Contains("meeting") || lower.Contains("remind") || lower.Contains("event")))
            return ClioAgentIds.Calendar;

        if (lower.Contains("task") || lower.Contains("todo") || lower.Contains("to-do")
            || lower.Contains("need to") || lower.Contains("don't forget"))
            return ClioAgentIds.Task;

        return ClioAgentIds.GeneralChat;
    }

    private static bool ContainsDateContext(string lower)
    {
        return lower.Contains("monday") || lower.Contains("tuesday") || lower.Contains("wednesday")
            || lower.Contains("thursday") || lower.Contains("friday") || lower.Contains("saturday")
            || lower.Contains("sunday") || lower.Contains("tomorrow") || lower.Contains("next week")
            || lower.Contains("today") || lower.Contains("tonight") || lower.Contains("am")
            || lower.Contains("pm") || lower.Contains("at ") || lower.Contains("on ");
    }

    private static bool IsMemoryCategory(string lower)
    {
        return lower is "user" or "projects" or "people" or "systems" or "tools"
            or "goals" or "knowledge" or "context" or "meta" or "flex";
    }
}
