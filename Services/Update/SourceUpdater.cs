using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace CLIo.Services.Update;

public sealed record SourceStatus(
    bool IsSourceCopy,
    string Root,
    string Message,
    int Behind,
    int Ahead,
    bool Dirty,
    string Branch,
    string Head,
    string Remote,
    IReadOnlyList<string> Commits,
    string DiffSummary);

/// <summary>
/// Keeps a copy that is run from source (dotnet run from the repo folder) up to date: looks at what is new on
/// the repo's main branch, and on approval pulls it, proves it builds, and asks the user to restart.
///
/// Guard rails: only for a real git checkout of THIS project's repo; never over uncommitted changes; only a
/// fast-forward of main (never a merge or force); the exact git commands are fixed (nothing the model writes is
/// ever run); the new code must build before it is accepted, otherwise the pull is undone; CLIo never restarts
/// herself, she asks the user to.
/// </summary>
public sealed class SourceUpdater
{
    private static readonly Regex ExpectedRemote =
        new(@"^(https://github\.com/|git@github\.com:)girlypopicon/clio(\.git)?/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly DevLogService devLog;
    private readonly RuntimeLog? runtimeLog;
    private readonly string? rootOverride;

    private readonly Regex remotePattern;

    /// <param name="rootOverride">Tests only: use this folder instead of searching from the app's location.</param>
    /// <param name="remotePattern">Tests only: the repo address that is accepted. Production accepts only this project's GitHub repo.</param>
    public SourceUpdater(DevLogService devLog, RuntimeLog? runtimeLog = null, string? rootOverride = null, Regex? remotePattern = null)
    {
        this.remotePattern = remotePattern ?? ExpectedRemote;
        this.devLog = devLog;
        this.runtimeLog = runtimeLog;
        this.rootOverride = rootOverride;
    }

    /// <summary>The repo folder when this copy runs from a source checkout; null for an installed release.</summary>
    public string? FindRoot()
    {
        if (rootOverride is not null)
        {
            return Directory.Exists(Path.Combine(rootOverride, ".git")) ? rootOverride : null;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CLIo.csproj")) && Directory.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return dir.FullName;
            }
        }

        return null;
    }

    // ---- status --------------------------------------------------------------------------------

    public async Task<SourceStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        var timer = runtimeLog?.Start("source_update", "check");
        var root = FindRoot();
        if (root is null)
        {
            return Empty(false, string.Empty, "This copy of CLIo is not running from a source checkout.");
        }

        try
        {
            var remote = (await Git(root, cancellationToken, "remote", "get-url", "origin")).Out.Trim();
            if (!remotePattern.IsMatch(remote))
            {
                timer?.Done("blocked", $"unexpected origin {remote}");
                return Empty(true, root, $"The git remote 'origin' is \"{remote}\", not this project's GitHub repo, so I won't pull from it.");
            }

            var fetch = await Git(root, cancellationToken, "fetch", "--quiet", "origin", "main");
            if (fetch.Code != 0)
            {
                timer?.Done("error", "git fetch failed: " + Shorten(fetch.Err, 120));
                return Empty(true, root, "Couldn't reach GitHub to look for new changes.") with { Remote = remote };
            }

            var branch = (await Git(root, cancellationToken, "branch", "--show-current")).Out.Trim();
            var head = (await Git(root, cancellationToken, "rev-parse", "--short", "HEAD")).Out.Trim();
            // Only edits to files git already tracks count. Untracked files (build output, notes) cannot be
            // overwritten by a fast-forward; git itself refuses the pull if one would be.
            var dirty = (await Git(root, cancellationToken, "status", "--porcelain", "--untracked-files=no")).Out.Trim().Length > 0;
            _ = int.TryParse((await Git(root, cancellationToken, "rev-list", "--count", "HEAD..origin/main")).Out.Trim(), out var behind);
            _ = int.TryParse((await Git(root, cancellationToken, "rev-list", "--count", "origin/main..HEAD")).Out.Trim(), out var ahead);

            var commits = new List<string>();
            var diffSummary = string.Empty;
            if (behind > 0)
            {
                commits = (await Git(root, cancellationToken, "log", "--oneline", "-n", "12", "HEAD..origin/main"))
                    .Out.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                diffSummary = (await Git(root, cancellationToken, "diff", "--shortstat", "HEAD", "origin/main")).Out.Trim();
            }

            var message = behind == 0
                ? (ahead > 0 ? $"This copy is up to date with GitHub (and {ahead} commit(s) ahead of it)." : "This copy is up to date with GitHub.")
                : $"GitHub's main branch has {behind} new commit(s) this copy doesn't have.";
            timer?.Done("ok", $"{branch} @ {head}: {behind} behind, {ahead} ahead{(dirty ? ", uncommitted changes" : string.Empty)}");
            return new SourceStatus(true, root, message, behind, ahead, dirty, branch, head, remote, commits, diffSummary);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            timer?.Done("error", ex.Message);
            return Empty(true, root, $"Couldn't run git: {ex.Message}");
        }
    }

    private static SourceStatus Empty(bool isSource, string root, string message) =>
        new(isSource, root, message, 0, 0, false, string.Empty, string.Empty, string.Empty, [], string.Empty);

    /// <summary>Why an update cannot go ahead right now, or null if it can.</summary>
    public static string? Blocker(SourceStatus s)
    {
        if (!s.IsSourceCopy) return "This copy isn't running from a source checkout, so there's nothing to pull. (An installed release updates through install_update.)";
        if (s.Remote.Length == 0 || s.Head.Length == 0) return s.Message;
        if (s.Branch != "main") return $"This copy is on the branch \"{s.Branch}\", not main, so I won't pull into it.";
        if (s.Dirty) return "There are uncommitted changes in the code folder. I won't pull over them: commit or stash them first.";
        if (s.Behind > 0 && s.Ahead > 0) return $"This copy and GitHub have both moved ({s.Ahead} local commit(s) not on GitHub, {s.Behind} new on GitHub). That needs a merge by hand, not an automatic pull.";
        if (s.Behind == 0) return s.Message;
        return null;
    }

    public static string Describe(SourceStatus s)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Update this copy of CLIo from GitHub ({s.Behind} new commit(s), you are on {s.Head}).");
        sb.AppendLine();
        sb.AppendLine("New on GitHub:");
        foreach (var commit in s.Commits)
        {
            sb.AppendLine("  " + commit);
        }

        if (s.Behind > s.Commits.Count)
        {
            sb.AppendLine($"  ...and {s.Behind - s.Commits.Count} more");
        }

        if (s.DiffSummary.Length > 0)
        {
            sb.AppendLine("  (" + s.DiffSummary + ")");
        }

        sb.AppendLine();
        sb.AppendLine($"1. git pull --ff-only from {s.Remote} (main only; a plain fast-forward, never a merge).");
        sb.AppendLine("2. Build the new code into a temporary folder to prove it compiles. If it doesn't, the pull is undone and nothing changes.");
        sb.AppendLine("3. Tell you to restart me, since I keep running the old code until you do.");
        sb.AppendLine();
        sb.AppendLine("Your data is not touched. Note that building runs the project's build steps, so only approve this for code you trust.");
        return sb.ToString().TrimEnd();
    }

    // ---- apply ---------------------------------------------------------------------------------

    public async Task<(bool Ok, string Message)> ApplyAsync(CancellationToken cancellationToken = default)
    {
        var timer = runtimeLog?.Start("source_update", "apply");
        var status = await CheckAsync(cancellationToken);
        var blocker = Blocker(status);
        if (blocker is not null)
        {
            timer?.Done("blocked", blocker);
            return (false, blocker);
        }

        var root = status.Root;
        var oldHead = (await Git(root, cancellationToken, "rev-parse", "HEAD")).Out.Trim();

        var pull = await Git(root, cancellationToken, "pull", "--ff-only", "origin", "main");
        if (pull.Code != 0)
        {
            timer?.Done("error", "git pull failed: " + Shorten(pull.Err, 160));
            return (false, "The pull failed, so nothing changed: " + Shorten(pull.Err.Length > 0 ? pull.Err : pull.Out, 300));
        }

        var newHead = (await Git(root, cancellationToken, "rev-parse", "--short", "HEAD")).Out.Trim();

        // Prove the new code builds before it is accepted. Output goes to a temp folder so the running copy is untouched.
        var buildDir = Path.Combine(Path.GetTempPath(), "clio-update-check-" + Guid.NewGuid().ToString("N")[..8]);
        var build = await Run(root, TimeSpan.FromMinutes(6), cancellationToken, "dotnet", "build", "CLIo.csproj", "-o", buildDir, "--nologo", "-v", "q");
        try { Directory.Delete(buildDir, recursive: true); } catch { /* temp folder, best effort */ }

        if (build.Code != 0)
        {
            // The tree was clean and we only fast-forwarded, so going back to the old commit loses nothing.
            await Git(root, cancellationToken, "reset", "--hard", oldHead);
            var problem = Shorten(string.Join(" ", build.Out.Split('\n').Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)).Take(2)), 300);
            devLog.Log($"Source update undone: the new code did not build. {problem}", "update");
            timer?.Done("error", "new code did not build; pull undone. " + problem);
            return (false, "The new code didn't build, so I undid the pull and nothing changed. " + problem);
        }

        devLog.Log($"Source update applied: {status.Head} -> {newHead}", "update");
        timer?.Done("ok", $"{status.Head} -> {newHead} ({status.Behind} commit(s)); builds");
        return (true, $"Pulled {status.Behind} new commit(s) ({status.Head} → {newHead}) and checked that the code builds. I'm still running the old version: please restart me to run the new one.");
    }

    // ---- git / process helpers -----------------------------------------------------------------

    private static Task<(int Code, string Out, string Err)> Git(string root, CancellationToken ct, params string[] args) =>
        Run(root, TimeSpan.FromSeconds(60), ct, "git", args);

    private static async Task<(int Code, string Out, string Err)> Run(string root, TimeSpan timeout, CancellationToken cancellationToken, string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file)
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        psi.Environment["GIT_TERMINAL_PROMPT"] = "0"; // never wait for a password prompt that nobody can see

        using var process = Process.Start(psi) ?? throw new IOException($"Could not start {file}.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        var output = process.StandardOutput.ReadToEndAsync(cts.Token);
        var error = process.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await process.WaitForExitAsync(cts.Token);
            return (process.ExitCode, await output, await error);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }
    }

    private static string Shorten(string text, int max)
    {
        var flat = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return flat.Length <= max ? flat : flat[..(max - 1)] + "…";
    }
}
