using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CLIo.Models;

namespace CLIo.Services.Skills;

/// <summary>
/// query_runtime_log: how CLIo finds out what the app really did. She asks instead of guessing about
/// background work ("did a model call go out?", "did the filing pass find anything?", "what failed today?").
/// Input is free-form key=value filters, for example:
///     since=1h component=llm
///     since=today outcome=error
///     since=30m contains=dentist
/// </summary>
public sealed class QueryRuntimeLogSkill : ISkill
{
    private readonly RuntimeLog log;

    public QueryRuntimeLogSkill(RuntimeLog log) => this.log = log;

    public string Name => "query_runtime_log";

    public string Summary => "look up what the app ACTUALLY did recently (LLM calls with tokens and cost, filing passes, skill runs, calendar/task/memory changes, errors); use it instead of guessing about background activity";

    public string InputHint => "since=1h component=llm outcome=error contains=text";

    public SkillPermission Permission => SkillPermission.ReadOnly;

    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public string DescribeRun(string input) => "Checking the runtime log...";

    public async Task<SkillResult> RunAsync(string input, CancellationToken cancellationToken) =>
        new(true, await BuildReportAsync(log, input));

    // ---- shared with the __events command ----------------------------------------------------

    public static async Task<string> BuildReportAsync(RuntimeLog log, string input)
    {
        var query = Parse(input, DateTimeOffset.Now, out var description);
        var result = await log.QueryAsync(query);
        var coverage = await log.QueryAsync(new RuntimeQuery(Limit: 1));
        return Format(result, query, description, coverage.Earliest, coverage.Latest);
    }

    private static readonly Regex Pair = new(@"(?<k>[a-zA-Z_]+)\s*=\s*(?:""(?<v>[^""]*)""|(?<v>\S+))", RegexOptions.Compiled);
    private static readonly Regex Duration = new(@"^(?<n>\d+)\s*(?<u>m|min|mins|minutes?|h|hr|hrs|hours?|d|days?)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    internal static RuntimeQuery Parse(string input, DateTimeOffset now, out string description)
    {
        DateTimeOffset? since = now.AddHours(-24);
        string? component = null, type = null, outcome = null, contains = null;
        var limit = 25;
        var parts = new List<string> { "last 24 hours" };

        var remainder = Pair.Replace(input ?? string.Empty, match =>
        {
            var key = match.Groups["k"].Value.ToLowerInvariant();
            var value = match.Groups["v"].Value.Trim();
            switch (key)
            {
                case "since" or "last" or "window" or "within":
                    since = value.Equals("all", StringComparison.OrdinalIgnoreCase) ? null : ParseSince(value, now) ?? since;
                    parts[0] = value.Equals("all", StringComparison.OrdinalIgnoreCase) ? "all recorded time" : "since " + value;
                    break;
                case "component" or "comp" or "area":
                    component = value;
                    parts.Add("component=" + value);
                    break;
                case "type" or "kind" or "event":
                    type = value;
                    parts.Add("type=" + value);
                    break;
                case "outcome" or "result" or "status":
                    outcome = value;
                    parts.Add("outcome=" + value);
                    break;
                case "contains" or "text" or "q" or "about":
                    contains = value;
                    parts.Add($"text~\"{value}\"");
                    break;
                case "limit" or "n" or "max" or "count":
                    if (int.TryParse(value, out var n))
                    {
                        limit = Math.Clamp(n, 1, 100);
                    }

                    break;
            }

            return string.Empty;
        });

        if (value_is_all(remainder, ref since))
        {
            parts[0] = "all recorded time";
        }

        // anything left over that is not a filter is treated as text to look for
        var leftover = Regex.Replace(remainder, @"\b(all|today)\b", " ", RegexOptions.IgnoreCase).Trim();
        if (leftover.Length > 0 && contains is null)
        {
            contains = leftover;
            parts.Add($"text~\"{leftover}\"");
        }

        description = string.Join(", ", parts);
        return new RuntimeQuery(since, component, type, outcome, contains, limit);

        static bool value_is_all(string text, ref DateTimeOffset? sinceValue)
        {
            if (Regex.IsMatch(text, @"\ball\b", RegexOptions.IgnoreCase))
            {
                sinceValue = null;
                return true;
            }

            return false;
        }
    }

    private static DateTimeOffset? ParseSince(string value, DateTimeOffset now)
    {
        if (value.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (value.Equals("today", StringComparison.OrdinalIgnoreCase))
        {
            return new DateTimeOffset(now.Date, now.Offset);
        }

        var m = Duration.Match(value);
        if (!m.Success)
        {
            return null;
        }

        var n = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
        return char.ToLowerInvariant(m.Groups["u"].Value[0]) switch
        {
            'm' => now.AddMinutes(-n),
            'h' => now.AddHours(-n),
            _ => now.AddDays(-n)
        };
    }

    internal static string Format(RuntimeQueryResult r, RuntimeQuery query, string description, DateTimeOffset? logStart, DateTimeOffset? logEnd)
    {
        var sb = new StringBuilder();
        var coverage = logStart is null
            ? "The log is empty (nothing has been recorded yet)."
            : $"The log covers {logStart.Value.ToLocalTime():yyyy-MM-dd HH:mm} to {logEnd!.Value.ToLocalTime():yyyy-MM-dd HH:mm} and keeps only the last {RuntimeLog.Keep:N0} events.";

        sb.AppendLine($"Runtime log ({description}). {coverage}");
        if (r.Total == 0)
        {
            sb.AppendLine("No matching events. That means nothing matching was recorded in this window, as far as the log goes; events from before the log started, or from components that do not record here, cannot be seen.");
            return sb.ToString().TrimEnd();
        }

        sb.AppendLine($"{r.Total} matching event(s), {r.Errors} with outcome=error.");
        if (r.LlmCalls > 0)
        {
            sb.AppendLine($"LLM calls: {r.LlmCalls}; tokens in {r.TokensIn:N0} / out {r.TokensOut:N0}; average {r.AverageLlmMs / 1000:0.0}s; models: {(r.Models.Count == 0 ? "unknown" : string.Join(", ", r.Models))}. (Token counts are DeepSeek's own when reported, otherwise estimated from text length, which the event says.)");
        }

        sb.AppendLine("By component: " + string.Join(", ", r.ByComponent.OrderByDescending(x => x.Value).Select(x => $"{x.Key} {x.Value}")));
        sb.AppendLine($"Newest first{(r.Total > r.Events.Count ? $" (showing {r.Events.Count} of {r.Total}; narrow the filters or raise limit)" : string.Empty)}:");

        var today = DateTimeOffset.Now.Date;
        foreach (var e in r.Events)
        {
            var local = e.Time.ToLocalTime();
            var stamp = local.Date == today ? local.ToString("HH:mm:ss") : local.ToString("MM-dd HH:mm:ss");
            var extras = new List<string>();
            if (e.LatencyMs is { } ms)
            {
                extras.Add(ms >= 1000 ? $"{ms / 1000.0:0.0}s" : $"{ms}ms");
            }

            if (e.Model is not null)
            {
                extras.Add(e.Model);
            }

            if (e.TokensIn is not null || e.TokensOut is not null)
            {
                extras.Add($"{e.TokensIn}→{e.TokensOut} tok");
            }

            sb.AppendLine($"  {stamp} {e.Component}/{e.Type} {e.Outcome}{(extras.Count > 0 ? " [" + string.Join(", ", extras) + "]" : string.Empty)} - {e.Detail}");
        }

        return sb.ToString().TrimEnd();
    }
}
