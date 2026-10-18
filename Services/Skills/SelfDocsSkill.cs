using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.DependencyInjection;

namespace CLIo.Services.Skills;

/// <summary>
/// self_docs: CLIo looks up how she works instead of guessing. Two sources:
///  - a short written reference (SelfDocs/architecture.md, embedded in the app): behavior and design,
///    no secrets and no source code;
///  - live facts generated from the running app, which cannot drift: skills, agents, database tables,
///    version, and a "code map" of class and method names (never method bodies).
/// </summary>
public sealed class SelfDocsSkill : ISkill
{
    private const string ResourceName = "CLIo.SelfDocs.architecture.bin";
    private const int MaxChars = 6500;

    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "are", "was", "you", "your", "how", "what", "why", "when", "where", "does", "did", "can",
        "could", "would", "should", "this", "that", "with", "from", "have", "has", "about", "into", "work", "works",
        "tell", "explain", "please", "clio", "she", "her", "its", "not", "but", "any", "all", "get", "use", "too"
    };

    private readonly IServiceProvider services;
    private readonly JournalDatabaseService database;
    private readonly Lazy<IReadOnlyList<(string Topic, string Body)>> sections;

    public SelfDocsSkill(IServiceProvider services, JournalDatabaseService database)
    {
        this.services = services;
        this.database = database;
        sections = new Lazy<IReadOnlyList<(string, string)>>(LoadSections);
    }

    public string Name => "self_docs";

    public string Summary => "look up how CLIo herself works: architecture, memory rules, commands, database tables, skills, agents, limits, class names";

    public string InputHint => "<topic | list | code map | code ClassName>";

    public SkillPermission Permission => SkillPermission.ReadOnly;

    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public string DescribeRun(string input) => "Checking my own documentation...";

    public async Task<SkillResult> RunAsync(string input, CancellationToken cancellationToken)
    {
        var query = input.Trim().ToLowerInvariant();
        string text;

        if (query is "" or "list" or "index" or "topics")
        {
            text = ListTopics();
        }
        else if (query is "skills")
        {
            text = DescribeSkills();
        }
        else if (query is "agents")
        {
            text = DescribeAgents();
        }
        else if (query is "schema" or "database" or "tables")
        {
            text = await database.GetSchemaSummaryAsync();
        }
        else if (query is "status" or "version")
        {
            text = DescribeStatus();
        }
        else if (query is "code" or "code map" or "codemap")
        {
            text = CodeMap();
        }
        else if (query.StartsWith("code ", StringComparison.Ordinal))
        {
            text = DescribeType(input.Trim()[5..].Trim());
        }
        else
        {
            text = FindSections(query);
        }

        if (text.Length > MaxChars)
        {
            text = text[..MaxChars] + "\n[truncated: ask for a narrower topic]";
        }

        return new SkillResult(true, text);
    }

    // ---- written reference -------------------------------------------------------------------

    private IReadOnlyList<(string Topic, string Body)> LoadSections()
    {
        var assembly = typeof(SelfDocsSkill).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            return [];
        }

        using var blob = new MemoryStream();
        stream.CopyTo(blob);
        var document = SelfDocsCrypto.TryDecrypt(blob.ToArray());
        if (document is null)
        {
            return [];
        }

        using var reader = new StringReader(document);
        var result = new List<(string, string)>();
        string? topic = null;
        var body = new StringBuilder();
        foreach (var line in reader.ReadToEnd().Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (topic is not null)
                {
                    result.Add((topic, body.ToString().Trim()));
                }

                topic = line[3..].Trim().ToLowerInvariant();
                body.Clear();
            }
            else if (topic is not null)
            {
                body.AppendLine(line);
            }
        }

        if (topic is not null)
        {
            result.Add((topic, body.ToString().Trim()));
        }

        return result;
    }

    private string ListTopics()
    {
        var sb = new StringBuilder("Topics in my documentation (ask self_docs for any of them):\n");
        foreach (var (topic, body) in sections.Value)
        {
            var firstSentence = body.Split('.', 2)[0].Replace("\n", " ").Trim();
            sb.AppendLine($"- {topic}: {(firstSentence.Length > 110 ? firstSentence[..107] + "..." : firstSentence)}");
        }

        sb.AppendLine("Generated live from the running app: skills, agents, schema, status, code map, code <ClassName>.");
        return sb.ToString().TrimEnd();
    }

    private string FindSections(string query)
    {
        var all = sections.Value;
        var exact = all.Where(s => s.Topic == query || s.Topic.Replace('-', ' ') == query).ToList();
        if (exact.Count == 0)
        {
            exact = all.Where(s => query.Contains(s.Topic.Replace('-', ' ')) || s.Topic.Contains(query)).ToList();
        }

        if (exact.Count == 0)
        {
            // Keyword search. Filler words are ignored and rare words count for more, so "conflict"
            // beats "chat" (which appears in nearly every section).
            var words = query
                .Split([' ', ',', '?', '.', '!', '"', '\'', ':', ';', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 2 && !Stopwords.Contains(w))
                .Distinct()
                .ToList();

            double Weight(string word)
            {
                var containing = all.Count(s => s.Body.Contains(word, StringComparison.OrdinalIgnoreCase) || s.Topic.Contains(word));
                return containing == 0 ? 0 : 1 + Math.Log((double)all.Count / containing);
            }

            var weights = words.ToDictionary(w => w, Weight);
            exact = all
                .Select(s => (Section: s, Score: words.Sum(w =>
                    (s.Topic.Contains(w) ? 3 * weights[w] : 0) +
                    (s.Body.Contains(w, StringComparison.OrdinalIgnoreCase) ? weights[w] : 0))))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .Take(2)
                .Select(x => x.Section)
                .ToList();
        }

        if (exact.Count == 0)
        {
            return $"Nothing in my documentation matches \"{query}\". I should say I don't know rather than guess.\n\n{ListTopics()}";
        }

        return string.Join("\n\n", exact.Select(s => $"## {s.Topic}\n{s.Body}"));
    }

    // ---- live facts --------------------------------------------------------------------------

    private string DescribeSkills()
    {
        var registry = services.GetRequiredService<SkillRegistry>();
        var sb = new StringBuilder("Skills I have right now:\n");
        foreach (var skill in registry.Skills)
        {
            sb.AppendLine($"- {skill.Name} | {skill.InputHint}: {skill.Summary} [{(skill.Permission == SkillPermission.ReadOnly ? "read-only, runs automatically" : "needs approval")}; timeout {skill.Timeout.TotalSeconds:0}s]");
        }

        return sb.ToString().TrimEnd();
    }

    private string DescribeAgents()
    {
        var catalog = services.GetRequiredService<AgentCatalog>();
        if (catalog.Agents.Count == 0)
        {
            return "No persona agents are installed.";
        }

        var sb = new StringBuilder("Persona agents (the user starts a message with @Name):\n");
        foreach (var agent in catalog.Agents)
        {
            sb.AppendLine($"- @{agent.Id}: {agent.Description}");
        }

        return sb.ToString().TrimEnd();
    }

    private string DescribeStatus()
    {
        var registry = services.GetRequiredService<SkillRegistry>();
        var catalog = services.GetRequiredService<AgentCatalog>();
        return $"CLIo version {SystemPromptBuilder.Version} (released {SystemPromptBuilder.ReleaseDate}). " +
               $"{registry.Skills.Count} skill(s) and {catalog.Agents.Count} persona agent(s) loaded. " +
               $"Runtime: .NET {Environment.Version}, assembly built {File.GetLastWriteTime(typeof(SelfDocsSkill).Assembly.Location):yyyy-MM-dd HH:mm}.";
    }

    // ---- code map (names only, never bodies) --------------------------------------------------

    private static IEnumerable<Type> AppTypes() =>
        typeof(SelfDocsSkill).Assembly.GetTypes()
            .Where(t => t.Namespace is not null
                && t.Namespace.StartsWith("CLIo", StringComparison.Ordinal)
                && !t.Name.Contains('<')
                && t.GetCustomAttribute<CompilerGeneratedAttribute>() is null)
            .OrderBy(t => t.Namespace).ThenBy(t => t.Name);

    private static string CodeMap()
    {
        var sb = new StringBuilder("Code map: namespaces and their classes. Ask \"code ClassName\" for a class's member names.\n");
        foreach (var group in AppTypes().GroupBy(t => t.Namespace))
        {
            sb.AppendLine($"{group.Key}: {string.Join(", ", group.Select(t => t.Name))}");
        }

        return sb.ToString().TrimEnd();
    }

    private static string DescribeType(string name)
    {
        var type = AppTypes().FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (type is null)
        {
            return $"No class named \"{name}\". Use \"code map\" to see the class names.";
        }

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var methods = type.GetMethods(flags)
            .Where(m => !m.IsSpecialName && m.GetCustomAttribute<CompilerGeneratedAttribute>() is null && !m.Name.Contains('<'))
            .Select(m => $"{(m.IsPublic ? "public" : "private")} {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))}) : {m.ReturnType.Name}");
        var properties = type.GetProperties(flags).Select(p => $"{p.Name} : {p.PropertyType.Name}");

        var sb = new StringBuilder($"{type.FullName}\n");
        sb.AppendLine("Properties: " + (properties.Any() ? string.Join(", ", properties) : "none"));
        sb.AppendLine("Members (names and signatures only):");
        foreach (var method in methods)
        {
            sb.AppendLine("  " + method);
        }

        return sb.ToString().TrimEnd();
    }
}
