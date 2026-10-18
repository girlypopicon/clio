namespace CLIo.Services.Skills;

/// <summary>
/// check_for_updates: when the user asks whether CLIo is up to date, or suggests she update, she checks
/// the published releases and reports. It can only look; it cannot download or install anything.
/// </summary>
public sealed class UpdateSkill : ISkill
{
    private readonly UpdateService updates;

    public UpdateSkill(UpdateService updates) => this.updates = updates;

    public string Name => "check_for_updates";

    public string Summary => "check whether a newer CLIo version is published (it can only look; it cannot install anything)";

    public string InputHint => "<anything, e.g. check>";

    public SkillPermission Permission => SkillPermission.ReadOnly;

    public TimeSpan Timeout => TimeSpan.FromSeconds(15);

    public string DescribeRun(string input) => "Checking for a newer version...";

    public async Task<SkillResult> RunAsync(string input, CancellationToken cancellationToken)
    {
        var info = await updates.CheckAsync(force: true, cancellationToken);
        return new SkillResult(info.Ok, UpdateService.Describe(info), info.Url.Length > 0 ? [info.Url] : null);
    }
}
