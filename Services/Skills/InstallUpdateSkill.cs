using CLIo.Services.Update;

namespace CLIo.Services.Skills;

/// <summary>
/// install_update: installs the newest signed CLIo release. This is an "Acts" skill, so it never runs without
/// the user approving a preview that spells out every step. CLIo uses it only when the user asks to update.
/// </summary>
public sealed class InstallUpdateSkill : ISkill
{
    private readonly UpdateInstaller installer;

    public InstallUpdateSkill(UpdateInstaller installer) => this.installer = installer;

    public string Name => "install_update";

    public string Summary => "install the newest CLIo release (only when the user asks to update; they see exactly what will happen and must approve; CLIo restarts)";

    public string InputHint => "latest";

    public SkillPermission Permission => SkillPermission.Acts;

    public TimeSpan Timeout => TimeSpan.FromMinutes(20);

    public string DescribeRun(string input) => "Preparing the update...";

    public async Task<string> PreviewAsync(string input, CancellationToken cancellationToken)
    {
        var plan = await installer.PlanAsync(cancellationToken);
        if (!plan.Ok)
        {
            // Nothing to approve: tell the user why instead of asking.
            throw new SkillBlockedException(plan.Message);
        }

        return installer.Describe(plan);
    }

    public async Task<SkillResult> RunAsync(string input, CancellationToken cancellationToken)
    {
        // Plan again: the release could have changed between the preview and the approval.
        var plan = await installer.PlanAsync(cancellationToken);
        if (!plan.Ok)
        {
            return SkillResult.Fail(plan.Message);
        }

        var staging = await installer.StageAsync(plan, cancellationToken);
        installer.ApplyAndRestart(staging, plan.Manifest!);
        return new SkillResult(true, "The update has been downloaded and checked. CLIo is closing now and will restart on the new version by itself.");
    }
}
