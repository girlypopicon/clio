namespace CLIo.Services.Skills;

/// <summary>
/// The skill contract. A skill is a capability CLIo can use on the user's behalf (search the web,
/// read a page, later: read mail, look at an image). Every skill:
///
///  - has a stable <see cref="Name"/> and a one-line <see cref="Summary"/> that is shown to the model;
///  - declares a <see cref="Permission"/>. <see cref="SkillPermission.ReadOnly"/> skills only fetch
///    information and run automatically. <see cref="SkillPermission.Acts"/> skills change something
///    in the world (send, delete, click) and never run without the user's approval;
///  - takes ONE text input and returns text. Inputs are written by the model, so a skill must treat
///    them as untrusted and validate them itself;
///  - finishes within <see cref="Timeout"/> and returns a bounded amount of text;
///  - never writes to memory, calendar or tasks itself. Whatever it fetched is untrusted data: it
///    reaches the model only inside a data fence, and only the user's own messages are ever filed.
///
/// The model calls a skill by replying with a block (and nothing else):
///     SKILL CALL
///     web_search | latest avalonia release
/// The app runs it and gives the model the result to write its answer from.
/// </summary>
public interface ISkill
{
    string Name { get; }

    /// <summary>One line telling the model what the skill is for.</summary>
    string Summary { get; }

    /// <summary>How to write the input, e.g. "&lt;search query&gt;".</summary>
    string InputHint { get; }

    SkillPermission Permission { get; }

    TimeSpan Timeout { get; }

    /// <summary>Short status shown in the chat while the skill runs ("Searching the web for ...").</summary>
    string DescribeRun(string input);

    Task<SkillResult> RunAsync(string input, CancellationToken cancellationToken);

    /// <summary>
    /// Dry run, required in practice for <see cref="SkillPermission.Acts"/> skills: describe exactly what
    /// <see cref="RunAsync"/> would do, without doing any of it. The text is shown to the user, who must
    /// approve before RunAsync is called. Throw <see cref="SkillBlockedException"/> when there is nothing
    /// sensible to approve (the message is passed on instead).
    /// </summary>
    Task<string> PreviewAsync(string input, CancellationToken cancellationToken) =>
        Task.FromResult($"Run {Name} with: {input}");
}

/// <summary>Thrown by PreviewAsync when the action cannot go ahead; the message explains why.</summary>
public sealed class SkillBlockedException(string message) : Exception(message);

/// <summary>Asks the user to approve an action. Implemented by the UI.</summary>
public interface IApprovalProvider
{
    /// <returns>True only if the user explicitly approved.</returns>
    Task<bool> RequestAsync(string title, string preview, CancellationToken cancellationToken);
}

public enum SkillPermission
{
    /// <summary>Only reads. Runs automatically.</summary>
    ReadOnly,

    /// <summary>Changes something outside CLIo. Needs the user's approval every time.</summary>
    Acts
}

public sealed record SkillCall(string Name, string Input);

/// <param name="Ok">False when the skill could not do its job (the text then explains why).</param>
/// <param name="Text">What the model gets to read. Already size-limited by the skill.</param>
/// <param name="Sources">URLs or ids the answer can cite.</param>
public sealed record SkillResult(bool Ok, string Text, IReadOnlyList<string>? Sources = null)
{
    public static SkillResult Fail(string reason) => new(false, reason);
}
