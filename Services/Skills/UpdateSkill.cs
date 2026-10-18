using System.Text;
using CLIo.Services.Update;

namespace CLIo.Services.Skills;

/// <summary>
/// check_for_updates: when the user asks whether CLIo is up to date, or suggests she update, she checks and
/// reports. It can only look. For an installed release it looks at published GitHub releases; for a copy run
/// from a source checkout (the usual case while the project is in development) it looks at what is new on the
/// repo's main branch. It cannot download or install anything: install_update and update_from_source do that,
/// after the user approves.
/// </summary>
public sealed class UpdateSkill : ISkill
{
    private readonly UpdateService updates;
    private readonly SourceUpdater? source;

    public UpdateSkill(UpdateService updates, SourceUpdater? source = null)
    {
        this.updates = updates;
        this.source = source;
    }

    public string Name => "check_for_updates";

    public string Summary => "check whether a newer CLIo is available (a published release, or new changes on GitHub if she runs from source); it only looks, then offer the matching update skill";

    public string InputHint => "<anything, e.g. check>";

    public SkillPermission Permission => SkillPermission.ReadOnly;

    public TimeSpan Timeout => TimeSpan.FromSeconds(60);

    public string DescribeRun(string input) => "Checking for a newer version...";

    public async Task<SkillResult> RunAsync(string input, CancellationToken cancellationToken)
    {
        var info = await updates.CheckAsync(force: true, cancellationToken);
        var status = source is null ? null : await source.CheckAsync(cancellationToken);

        if (status is not { IsSourceCopy: true })
        {
            return new SkillResult(info.Ok, UpdateService.Describe(info), info.Url.Length > 0 ? [info.Url] : null);
        }

        var sb = new StringBuilder();
        sb.AppendLine($"This copy of CLIo (version {AppVersion.Text}, commit {(status.Head.Length > 0 ? status.Head : "unknown")}) runs from a source checkout, so what matters is GitHub's main branch.");
        sb.AppendLine(status.Message);
        if (status.Behind > 0)
        {
            foreach (var commit in status.Commits)
            {
                sb.AppendLine("  " + commit);
            }

            if (status.DiffSummary.Length > 0)
            {
                sb.AppendLine("  (" + status.DiffSummary + ")");
            }

            var blocker = SourceUpdater.Blocker(status);
            sb.AppendLine(blocker is null
                ? "I can update from source: I would pull this, check that it builds, then ask you to restart me. Offer that; it needs the user's approval."
                : "I can't update from source right now: " + blocker);
        }

        if (info.Ok && info.IsNewer)
        {
            sb.AppendLine($"A published release {info.Tag} also exists, but releases are for installed copies.");
        }

        return new SkillResult(true, sb.ToString().TrimEnd());
    }
}

/// <summary>
/// update_from_source: pulls the new code for a source-checkout copy and proves it builds. An "Acts" skill: the
/// user is shown exactly which commits would arrive and must approve. CLIo does not restart herself; she asks.
/// </summary>
public sealed class UpdateFromSourceSkill : ISkill
{
    private readonly SourceUpdater source;

    public UpdateFromSourceSkill(SourceUpdater source) => this.source = source;

    public string Name => "update_from_source";

    public string Summary => "update a copy that runs from source: pull the new code from GitHub, check that it builds, then ask the user to restart (only when the user asks to update; they approve first)";

    public string InputHint => "latest";

    public SkillPermission Permission => SkillPermission.Acts;

    public TimeSpan Timeout => TimeSpan.FromMinutes(10);

    public string DescribeRun(string input) => "Updating from GitHub...";

    public async Task<string> PreviewAsync(string input, CancellationToken cancellationToken)
    {
        var status = await source.CheckAsync(cancellationToken);
        var blocker = SourceUpdater.Blocker(status);
        if (blocker is not null)
        {
            throw new SkillBlockedException(blocker);
        }

        return SourceUpdater.Describe(status);
    }

    public async Task<SkillResult> RunAsync(string input, CancellationToken cancellationToken)
    {
        var (ok, message) = await source.ApplyAsync(cancellationToken);
        return new SkillResult(ok, message);
    }
}
