using System.Text;
using CLIo.Services.Update;

namespace CLIo.Services.Skills;

/// <summary>
/// read_source: CLIo reads one of her own source files, so she can point at the real code path when something
/// behaves unexpectedly instead of guessing. Read-only, and only on a copy that runs from a source checkout (an
/// installed release has no source). Confined to the project folder: it cannot leave it, follow a link out of it,
/// or read build output, git internals or anything that could hold a secret.
/// </summary>
public sealed class ReadSourceSkill : ISkill
{
    private const long MaxFileBytes = 500 * 1024;
    private const int MaxChars = 6000;

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".axaml", ".xaml", ".csproj", ".props", ".md", ".json", ".toml", ".js", ".mjs", ".ps1", ".txt", ".wxs", ".wixproj", ".sln"
    };

    private static readonly HashSet<string> BlockedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", "bin", "obj", "node_modules", "artifacts", ".wrangler", "backups", "voice", "publish"
    };

    private static readonly string[] BlockedNameParts = ["secret", "password", "credential", ".env", ".key", ".dpapi", ".pem", ".pfx", ".db", "master"];

    private readonly SourceUpdater source;

    public ReadSourceSkill(SourceUpdater source) => this.source = source;

    public string Name => "read_source";

    public string Summary => "read one of CLIo's own source files (only on a copy run from source), e.g. to see the real code behind a behavior; use self_docs' code map first to find the file";

    public string InputHint => "<relative path> [first-last lines]   or   list <folder>";

    public SkillPermission Permission => SkillPermission.ReadOnly;

    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public string DescribeRun(string input) => "Reading my own code...";

    public Task<SkillResult> RunAsync(string input, CancellationToken cancellationToken)
    {
        var root = source.FindRoot();
        if (root is null)
        {
            return Task.FromResult(SkillResult.Fail("This copy of CLIo doesn't carry its source (it's an installed release). self_docs has a written reference and a code map of class and method names."));
        }

        var text = (input ?? string.Empty).Trim();
        if (text.StartsWith("list", StringComparison.OrdinalIgnoreCase) && (text.Length == 4 || char.IsWhiteSpace(text[4])))
        {
            return Task.FromResult(List(root, text[4..].Trim()));
        }

        // optional trailing line range: "Services/Foo.cs 120-180"
        int? first = null, last = null;
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            var range = parts[^1].Split('-', 2);
            if (int.TryParse(range[0], out var a) && (range.Length == 1 || int.TryParse(range[1], out _)))
            {
                first = a;
                last = range.Length == 2 && int.TryParse(range[1], out var b) ? b : a + 79;
                text = string.Join(' ', parts[..^1]);
            }
        }

        return Task.FromResult(Read(root, text.Trim('"', '\''), first, last));
    }

    private static SkillResult Read(string root, string relative, int? first, int? last)
    {
        if (!TryResolve(root, relative, out var full, out var problem))
        {
            return SkillResult.Fail(problem);
        }

        if (!File.Exists(full))
        {
            return SkillResult.Fail($"No file at \"{relative}\". Use \"list <folder>\" to look around, or the code map in self_docs.");
        }

        if (!AllowedExtensions.Contains(Path.GetExtension(full)))
        {
            return SkillResult.Fail("That type of file isn't something I read (source, project, docs and config text only).");
        }

        var info = new FileInfo(full);
        if (info.Length > MaxFileBytes)
        {
            return SkillResult.Fail("That file is too large to read here.");
        }

        var lines = File.ReadAllLines(full);
        var from = Math.Clamp(first ?? 1, 1, Math.Max(1, lines.Length));
        var to = Math.Clamp(last ?? lines.Length, from, lines.Length);

        var sb = new StringBuilder();
        sb.AppendLine($"{relative.Replace('\\', '/')} ({lines.Length} lines)");
        var shown = from;
        for (var i = from; i <= to; i++)
        {
            var row = $"{i,5}  {lines[i - 1]}";
            if (sb.Length + row.Length + 1 > MaxChars)
            {
                break;
            }

            sb.AppendLine(row);
            shown = i;
        }

        if (shown < to || to < lines.Length || from > 1)
        {
            sb.AppendLine($"[showing lines {from}-{shown} of {lines.Length}; ask for another range, e.g. \"{relative} {shown + 1}-{Math.Min(lines.Length, shown + 120)}\"]");
        }

        return new SkillResult(true, sb.ToString().TrimEnd());
    }

    private static SkillResult List(string root, string relative)
    {
        if (!TryResolve(root, relative.Length == 0 ? "." : relative, out var full, out var problem))
        {
            return SkillResult.Fail(problem);
        }

        if (!Directory.Exists(full))
        {
            return SkillResult.Fail($"No folder at \"{relative}\".");
        }

        var sb = new StringBuilder($"{(relative.Length == 0 ? "." : relative.Replace('\\', '/'))}:\n");
        foreach (var dir in Directory.GetDirectories(full).Select(Path.GetFileName).Where(n => n is not null && !BlockedFolders.Contains(n)).OrderBy(n => n))
        {
            sb.AppendLine($"  {dir}/");
        }

        foreach (var file in Directory.GetFiles(full).Where(f => AllowedExtensions.Contains(Path.GetExtension(f)) && !IsBlockedName(Path.GetFileName(f))).OrderBy(f => f))
        {
            sb.AppendLine($"  {Path.GetFileName(file)} ({new FileInfo(file).Length / 1024.0:0.#} KB)");
        }

        return new SkillResult(true, sb.ToString().TrimEnd());
    }

    /// <summary>Turns a requested path into a real one, or says why not. Everything must stay inside <paramref name="root"/>.</summary>
    internal static bool TryResolve(string root, string relative, out string full, out string problem)
    {
        full = string.Empty;
        problem = string.Empty;

        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.StartsWith('/') || relative.StartsWith('\\') || relative.Contains(':'))
        {
            problem = "Give a path relative to the project folder (for example Services/MemoryService.cs).";
            return false;
        }

        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(rootFull, relative));
        if (!candidate.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) && candidate + Path.DirectorySeparatorChar != rootFull)
        {
            problem = "That path is outside the project folder, so I won't read it.";
            return false;
        }

        // every folder and the file itself must be allowed, and none may be a link that leads elsewhere
        var current = rootFull.TrimEnd(Path.DirectorySeparatorChar);
        var rest = candidate.Length >= rootFull.Length ? candidate[rootFull.Length..] : string.Empty; // empty when it is the root itself
        foreach (var segment in rest.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (BlockedFolders.Contains(segment) || (IsBlockedName(segment) && Path.HasExtension(segment)))
            {
                problem = "That location isn't something I read (build output, git internals, or anything that could hold a secret).";
                return false;
            }

            current = Path.Combine(current, segment);
            try
            {
                if ((File.Exists(current) || Directory.Exists(current))
                    && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                {
                    problem = "That path goes through a link, so I won't follow it.";
                    return false;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problem = "I can't open that path.";
                return false;
            }
        }

        full = candidate;
        return true;
    }

    private static bool IsBlockedName(string fileName) =>
        BlockedNameParts.Any(part => fileName.Contains(part, StringComparison.OrdinalIgnoreCase));
}
