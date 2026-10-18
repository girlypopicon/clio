using System.Text.RegularExpressions;

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

    // Whole-word matching: plain Contains("am") or Contains("on ") matched "name", "example", "money".
    private static readonly Regex DateContext = new(
        @"\b(monday|tuesday|wednesday|thursday|friday|saturday|sunday|tomorrow|tonight|today|next week|\d{1,2}(:\d{2})?\s?(am|pm)|at \d|on (mon|tue|wed|thu|fri|sat|sun)|on the \d|on \d)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool ContainsDateContext(string lower) => DateContext.IsMatch(lower);

    /// <summary>
    /// A short per-turn nudge for the routed domain, appended after the stable system prompt.
    /// General chat gets none. It narrows attention only; it grants no abilities.
    /// </summary>
    public static string? FocusFor(string agentId) => agentId switch
    {
        ClioAgentIds.Memory => "Turn focus: this message is about remembering or recalling information. Prefer the structured memory block over prose, and ask before treating a guess as a fact.",
        ClioAgentIds.Calendar => "Turn focus: this message is about time. Resolve relative dates against the current local time, give an ISO 8601 datetime with offset, and ask if the day or time is ambiguous.",
        ClioAgentIds.Task => "Turn focus: this message is about something to do. Keep the task title short and actionable; do not invent a due date.",
        _ => null
    };

    private static bool IsMemoryCategory(string lower)
    {
        return lower is "user" or "projects" or "people" or "systems" or "tools"
            or "goals" or "knowledge" or "context" or "meta" or "flex";
    }
}
