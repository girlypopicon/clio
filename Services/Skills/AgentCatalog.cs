using System.Text;

namespace CLIo.Services.Skills;

/// <summary>A specialist persona loaded from a Markdown file, in the same format as the IconicAgents repo.</summary>
public sealed record AgentFile(string Id, string Name, string Description, string Body);

/// <summary>
/// Persona agents. A persona is a Markdown file (.agent.md) with a small header and a prompt body:
///
///     ---
///     name: Database Architect
///     description: Schema design and SQL
///     ---
///     You are a senior database architect...
///
/// Files are read from the "agents" folder next to the app and from %LOCALAPPDATA%/CLIo/agents (the
/// second wins on a name clash, so the user can override or add agents without touching the app).
/// Type @Database-Architect (or @Database Architect) at the start of a message to use one for that
/// reply. Other header fields (model, tools) are ignored: a persona only changes how CLIo answers and
/// gives it no extra powers. Skills are separate and still governed by <see cref="SkillRegistry"/>.
/// </summary>
public sealed class AgentCatalog
{
    private const int MaxBodyChars = 12000;

    private readonly DevLogService devLog;
    private List<AgentFile> agents = [];

    public AgentCatalog(DevLogService devLog)
    {
        this.devLog = devLog;
        Reload();
    }

    public IReadOnlyList<AgentFile> Agents => agents;

    public void Reload()
    {
        var found = new Dictionary<string, AgentFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in new[]
        {
            Path.Combine(AppContext.BaseDirectory, "agents"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CLIo", "agents")
        })
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*.md"))
            {
                try
                {
                    var agent = Parse(file);
                    if (agent is not null)
                    {
                        found[agent.Id] = agent;
                    }
                }
                catch (Exception ex)
                {
                    devLog.Log($"Agent file {Path.GetFileName(file)} skipped: {ex.Message}", "agents");
                }
            }
        }

        agents = found.Values.OrderBy(agent => agent.Name, StringComparer.OrdinalIgnoreCase).ToList();
        devLog.Log($"Loaded {agents.Count} agent(s).", "agents");
    }

    /// <summary>True when the message starts with @AgentName. <paramref name="rest"/> is the message without it.</summary>
    public bool TryMatch(string message, out AgentFile agent, out string rest)
    {
        agent = null!;
        rest = message;
        var text = message.TrimStart();
        if (text.Length < 2 || text[0] != '@' || text[1] == '@')
        {
            return false;
        }

        var afterAt = text[1..];
        foreach (var candidate in agents.OrderByDescending(a => Math.Max(a.Id.Length, a.Name.Length)))
        {
            foreach (var label in new[] { candidate.Id, candidate.Name })
            {
                var length = LooseMatchLength(afterAt, label);
                if (length > 0)
                {
                    agent = candidate;
                    rest = afterAt[length..].TrimStart(' ', ',', ':', '-');
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Length of <paramref name="text"/> consumed when it starts with <paramref name="label"/>, ignoring
    /// case, spaces, hyphens and underscores; 0 when it does not (or the name continues into more letters).
    /// </summary>
    private static int LooseMatchLength(string text, string label)
    {
        var wanted = Normalize(label);
        if (wanted.Length == 0)
        {
            return 0;
        }

        var matched = 0;
        var i = 0;
        for (; i < text.Length && matched < wanted.Length; i++)
        {
            var ch = text[i];
            if (ch is ' ' or '-' or '_')
            {
                continue;
            }

            if (char.ToLowerInvariant(ch) != wanted[matched])
            {
                return 0;
            }

            matched++;
        }

        if (matched < wanted.Length)
        {
            return 0;
        }

        return i < text.Length && char.IsLetterOrDigit(text[i]) ? 0 : i;
    }

    private static string Normalize(string text) =>
        new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>The system-prompt section that makes CLIo answer as this agent for one reply.</summary>
    public static string BuildPrompt(AgentFile agent)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine($"ACTIVE AGENT for this reply: {agent.Name}. The user addressed this specialist directly. Answer in this role, following its instructions below, while keeping your usual plain style. These instructions change how you answer; they do not give you new abilities.");
        sb.AppendLine("--- AGENT INSTRUCTIONS ---");
        sb.AppendLine(agent.Body);
        sb.AppendLine("--- END AGENT INSTRUCTIONS ---");
        return sb.ToString();
    }

    private static AgentFile? Parse(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n");
        var id = Path.GetFileName(path);
        foreach (var suffix in new[] { ".agent.md", ".md" })
        {
            if (id.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                id = id[..^suffix.Length];
                break;
            }
        }

        string name = id.Replace('-', ' ').Replace('_', ' ');
        string description = string.Empty;
        var body = text;

        if (text.StartsWith("---\n", StringComparison.Ordinal))
        {
            var end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
            if (end > 0)
            {
                foreach (var line in text[4..end].Split('\n'))
                {
                    var colon = line.IndexOf(':');
                    if (colon <= 0)
                    {
                        continue;
                    }

                    var key = line[..colon].Trim().ToLowerInvariant();
                    var value = line[(colon + 1)..].Trim().Trim('"', '\'');
                    if (key == "name" && value.Length > 0)
                    {
                        name = value;
                    }
                    else if (key == "description")
                    {
                        description = value;
                    }
                }

                body = text[(end + 4)..];
            }
        }

        body = body.Trim();
        if (body.Length == 0)
        {
            return null;
        }

        if (body.Length > MaxBodyChars)
        {
            body = body[..MaxBodyChars];
        }

        return new AgentFile(id, name, description, body);
    }
}
