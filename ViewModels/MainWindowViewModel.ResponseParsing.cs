using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;
using CLIo.Models;
using CLIo.Services;
using CLIo.Services.Skills;
using CLIo.Services.Update;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CLIo.ViewModels;

public partial class MainWindowViewModel
{
    private static ParsedAssistantResponse ParseAssistantResponse(string content)
    {
        var visibleLines = new List<string>();
        var candidates = new List<MemoryCandidate>();
        var scheduleCandidates = new List<ScheduleCandidate>();
        var taskCandidates = new List<TaskCandidate>();
        var taskCompleteCandidates = new List<TaskCompleteCandidate>();
        var deleteCandidates = new List<DeleteCandidate>();
        var moveCandidates = new List<MoveCandidate>();
        var editCandidates = new List<EditCandidate>();
        var devNoteCandidates = new List<string>();
        var structuredBlock = string.Empty;
        CalendarDirective? calendarDirective = null;
        var insideCodeFence = false;

        foreach (var line in content.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (IsLeakedTimestampLine(trimmed))
            {
                continue;
            }

            // Strip code fences — unwrap content, skip the fence lines themselves.
            if (string.IsNullOrEmpty(structuredBlock) && CodeFenceOpenRegex.IsMatch(trimmed))
            {
                insideCodeFence = !insideCodeFence;
                continue;
            }

            if (string.IsNullOrEmpty(structuredBlock) && TryParseCalendarDirective(trimmed, out var directive))
            {
                calendarDirective = directive;
                continue;
            }

            if (IsStructuredBlockHeader(trimmed))
            {
                structuredBlock = NormalizeStructuredBlockHeader(trimmed);
                continue;
            }

            if (!string.IsNullOrEmpty(structuredBlock))
            {
                if (string.Equals(structuredBlock, "MEMORY CANDIDATES", StringComparison.OrdinalIgnoreCase))
                {
                    var separatorIndex = trimmed.IndexOf("->", StringComparison.Ordinal);
                    if (separatorIndex > 0)
                    {
                        var key = CleanStructuredMemoryKey(trimmed[..separatorIndex]);
                        var value = CleanStructuredMemoryValue(trimmed[(separatorIndex + 2)..]);
                        if (IsValidMemoryCandidate(key, value))
                        {
                            candidates.Add(new MemoryCandidate(key, value, IsExplicitWrite: false, MemoryTypeForKey(key)));
                        }
                    }
                }
                else if (string.Equals(structuredBlock, "SCHEDULE CANDIDATES", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = trimmed.TrimStart('-', '*', ' ').Split('|');
                    if (parts.Length >= 2)
                    {
                        var title = parts[0].Trim();
                        var when = parts[1].Trim();
                        var details = parts.Length >= 3 ? parts[2].Trim() : string.Empty;
                        // A timestamp in details means a second date leaked in (move artifact) - drop it.
                        if (IsoTimestampRegex.IsMatch(details))
                        {
                            details = string.Empty;
                        }

                        if (!string.IsNullOrWhiteSpace(title)
                            && !string.IsNullOrWhiteSpace(when)
                            && !string.Equals(title, "title", StringComparison.OrdinalIgnoreCase)
                            && !IsoTimestampRegex.IsMatch(title))
                        {
                            scheduleCandidates.Add(new ScheduleCandidate(title, when, details));
                        }
                    }
                }
                else if (string.Equals(structuredBlock, "TASK CANDIDATES", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = trimmed.TrimStart('-', '*', ' ').Split('|');
                    var title = parts[0].Trim();
                    if (!string.IsNullOrWhiteSpace(title) && !string.Equals(title, "title", StringComparison.OrdinalIgnoreCase))
                    {
                        taskCandidates.Add(new TaskCandidate(title));
                    }
                }
                else if (string.Equals(structuredBlock, "TASK COMPLETE", StringComparison.OrdinalIgnoreCase))
                {
                    var title = trimmed.TrimStart('-', '*', ' ').Split('|')[0].Trim();
                    if (!string.IsNullOrWhiteSpace(title) && !string.Equals(title, "title", StringComparison.OrdinalIgnoreCase))
                    {
                        taskCompleteCandidates.Add(new TaskCompleteCandidate(title));
                    }
                }
                else if (string.Equals(structuredBlock, "CALENDAR DELETE", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = trimmed.TrimStart('-', '*', ' ').Split('|');
                    var title = parts[0].Trim();
                    var when = parts.Length >= 2 ? parts[1].Trim() : null;
                    if (!string.IsNullOrWhiteSpace(title) && !string.Equals(title, "title", StringComparison.OrdinalIgnoreCase))
                    {
                        deleteCandidates.Add(new DeleteCandidate(title, string.IsNullOrWhiteSpace(when) ? null : when));
                    }
                }
                else if (string.Equals(structuredBlock, "CALENDAR MOVE", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = trimmed.TrimStart('-', '*', ' ').Split('|');
                    var title = parts[0].Trim();
                    if (!string.IsNullOrWhiteSpace(title) && !string.Equals(title, "title", StringComparison.OrdinalIgnoreCase) && parts.Length >= 2)
                    {
                        var newWhen = parts[^1].Trim();
                        var when = parts.Length >= 3 ? parts[1].Trim() : null;
                        if (!string.IsNullOrWhiteSpace(newWhen))
                        {
                            moveCandidates.Add(new MoveCandidate(title, string.IsNullOrWhiteSpace(when) ? null : when, newWhen));
                        }
                    }
                }
                else if (string.Equals(structuredBlock, "CALENDAR EDIT", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = trimmed.TrimStart('-', '*', ' ').Split('|');
                    var matchTitle = parts[0].Trim();
                    if (!string.IsNullOrWhiteSpace(matchTitle) && !string.Equals(matchTitle, "title", StringComparison.OrdinalIgnoreCase) && parts.Length >= 3)
                    {
                        static string? Field(string[] p, int i) =>
                            p.Length > i && !string.IsNullOrWhiteSpace(p[i]) ? p[i].Trim() : null;

                        var matchWhen = Field(parts, 1);
                        var newTitle = Field(parts, 2);
                        var newWhen = Field(parts, 3);
                        var newDetails = Field(parts, 4);
                        if (newTitle is not null || newWhen is not null || newDetails is not null)
                        {
                            editCandidates.Add(new EditCandidate(matchTitle, matchWhen, newTitle, newWhen, newDetails));
                        }
                    }
                }
                else if (string.Equals(structuredBlock, "DEV NOTE", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(structuredBlock, "DEV NOTES", StringComparison.OrdinalIgnoreCase))
                {
                    // Collected as task candidates with a special prefix; processed in ApplyStructuredMemoryAsync
                    var noteText = trimmed.TrimStart('-', '*', ' ');
                    if (!string.IsNullOrWhiteSpace(noteText))
                        devNoteCandidates.Add(noteText);
                }

                continue;
            }

            // Fallback: catch headerless pipe-separated schedule lines the LLM wrote as plain text.
            var fallbackTarget = trimmed.TrimStart('-', '*', ' ');
            var fallbackMatch = ScheduleLineFallbackRegex.Match(fallbackTarget);
            // Exactly one timestamp is a real add; two or more means a date leaked into the
            // title/details (a botched move) - ignore it rather than create a corrupt row.
            if (fallbackMatch.Success && string.IsNullOrEmpty(structuredBlock)
                && IsoTimestampRegex.Matches(fallbackTarget).Count == 1)
            {
                var title = fallbackMatch.Groups["title"].Value.Trim();
                var when = fallbackMatch.Groups["when"].Value.Trim();
                var details = fallbackMatch.Groups["details"].Value.Trim();
                if (!string.Equals(title, "title", StringComparison.OrdinalIgnoreCase)
                    && !IsoTimestampRegex.IsMatch(title))
                {
                    scheduleCandidates.Add(new ScheduleCandidate(title, when, details));
                    continue;
                }
            }

            visibleLines.Add(line);
        }

        return new ParsedAssistantResponse(string.Join(Environment.NewLine, visibleLines).Trim(), candidates, scheduleCandidates, taskCandidates, taskCompleteCandidates, deleteCandidates, moveCandidates, editCandidates, calendarDirective, devNoteCandidates);
    }

    private static bool TryParseCalendarDirective(string line, out CalendarDirective? directive)
    {
        directive = null;

        var candidate = line.Trim().Trim('`', '*', '_').Trim();

        var match = Regex.Match(
            candidate,
            @"^CALENDAR VIEW\s*:\s*(week|two[\s-]?week|fortnight|month)(?:\s*(?:starting|from|beginning)?\s*(\d{4}-\d{2}-\d{2}))?\.?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        if (!match.Success)
        {
            return false;
        }

        var modeText = match.Groups[1].Value.ToLowerInvariant().Replace(" ", string.Empty).Replace("-", string.Empty);
        var mode = modeText switch
        {
            "week" => "Week",
            "twoweek" or "fortnight" => "TwoWeek",
            _ => "Month"
        };

        DateTime? anchor = match.Groups[2].Success && DateTime.TryParse(match.Groups[2].Value, out var parsedDate)
            ? parsedDate.Date
            : null;

        directive = new CalendarDirective(mode, anchor);
        return true;
    }

    private static string RemoveLeakedTimestampLines(string content)
    {
        var lines = content.Replace("\r\n", "\n")
            .Split('\n')
            .Where(line => !IsLeakedTimestampLine(line.Trim()));

        return string.Join(Environment.NewLine, lines).Trim();
    }

    private static bool IsLeakedTimestampLine(string line)
    {
        return line.StartsWith("[timestamp:", StringComparison.OrdinalIgnoreCase)
            && line.EndsWith(']');
    }

    private static bool IsStructuredBlockHeader(string line)
    {
        var normalized = NormalizeStructuredBlockHeader(line);
        return string.Equals(normalized, "MEMORY CANDIDATES", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "SCHEDULE CANDIDATES", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "TASK CANDIDATES", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "TASK COMPLETE", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "CALENDAR DELETE", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "CALENDAR MOVE", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "CALENDAR EDIT", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "TEMPORAL PATTERNS", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "QUESTIONS", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "DEV NOTE", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "DEV NOTES", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeStructuredBlockHeader(string line)
    {
        return line.Trim().TrimEnd(':').ToUpperInvariant();
    }

    private static string CleanStructuredMemoryKey(string key)
    {
        return Regex.Replace(key.Trim(), @"^[-*]\s*", string.Empty, RegexOptions.CultureInvariant).Trim();
    }

    private static string CleanStructuredMemoryValue(string value)
    {
        var cleaned = value.Trim();
        var metadataIndex = cleaned.IndexOf(" | ", StringComparison.Ordinal);
        if (metadataIndex >= 0)
        {
            cleaned = cleaned[..metadataIndex];
        }

        return CleanMemoryValue(cleaned);
    }

    private static bool IsValidMemoryCandidate(string key, string value)
    {
        return !string.IsNullOrWhiteSpace(key)
            && !string.IsNullOrWhiteSpace(value)
            && key.Length <= 100
            && !string.Equals(key, "key", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(value, "value", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record MemoryCandidate(string Key, string Value, bool IsExplicitWrite, string Type);

    private sealed record MemoryValidationResult(string Status, string Error, bool ShouldWrite);

    private sealed record ScheduleCandidate(string Title, string When, string Details);

    private sealed record TaskCandidate(string Title);

    private sealed record TaskCompleteCandidate(string Title);

    private sealed record DeleteCandidate(string Title, string? When);

    private sealed record MoveCandidate(string Title, string? When, string NewWhen);

    private sealed record EditCandidate(string Title, string? When, string? NewTitle, string? NewWhen, string? NewDetails);

    private sealed record CalendarDirective(string Mode, DateTime? Anchor);

    private sealed record ParsedAssistantResponse(string VisibleContent, IReadOnlyList<MemoryCandidate> MemoryCandidates, IReadOnlyList<ScheduleCandidate> ScheduleCandidates, IReadOnlyList<TaskCandidate> TaskCandidates, IReadOnlyList<TaskCompleteCandidate> TaskCompleteCandidates, IReadOnlyList<DeleteCandidate> DeleteCandidates, IReadOnlyList<MoveCandidate> MoveCandidates, IReadOnlyList<EditCandidate> EditCandidates, CalendarDirective? CalendarDirective, IReadOnlyList<string> DevNoteCandidates);

}
