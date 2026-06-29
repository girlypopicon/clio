using System.Runtime.CompilerServices;
using CLIo.Models;

namespace CLIo.Services;

public sealed class FallbackLlmProvider
{
    private readonly OfflineNlpService nlp;
    private readonly DateTimeParsingService dateParsing;

    public FallbackLlmProvider(OfflineNlpService nlp, DateTimeParsingService dateParsing)
    {
        this.nlp = nlp;
        this.dateParsing = dateParsing;
    }

    public async IAsyncEnumerable<string> ChatAsync(
        IEnumerable<LlmMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var userMessage = messages.LastOrDefault(m => m.Role == "user")?.Content ?? "";

        var nlpResult = await nlp.AnalyzeAsync(userMessage);
        var dates = dateParsing.ParseDates(userMessage);

        var response = BuildOfflineResponse(userMessage, nlpResult, dates);

        yield return response;
    }

    private static string BuildOfflineResponse(
        string userMessage,
        NlpResult nlpResult,
        IReadOnlyList<DateTimeOffset> dates)
    {
        var parts = new List<string> { "I'm currently offline and can't reach the LLM." };

        if (nlpResult.Entities.Count > 0)
        {
            var entityList = string.Join(", ", nlpResult.Entities.Select(e => $"{e.Value} ({e.Type})"));
            parts.Add($"I detected: {entityList}.");
        }

        if (dates.Count > 0)
        {
            var dateList = string.Join(", ", dates.Select(d => d.ToString("f")));
            parts.Add($"Date references found: {dateList}.");
        }

        if (HasScheduleIntent(userMessage) && dates.Count > 0)
        {
            var title = ExtractTitleHint(userMessage);
            var when = dates[0];
            parts.Add($"\nSCHEDULE CANDIDATES\n{title} | {when:O} | (created offline)");
        }
        else if (HasTaskIntent(userMessage))
        {
            var title = ExtractTitleHint(userMessage);
            parts.Add($"\nTASK CANDIDATES\n{title} | (created offline)");
        }
        else
        {
            parts.Add("I'll process this fully when reconnected.");
        }

        return string.Join(" ", parts);
    }

    private static bool HasScheduleIntent(string text)
    {
        var lower = text.ToLowerInvariant();
        return lower.Contains("schedule") || lower.Contains("appointment") || lower.Contains("meeting")
            || lower.Contains("remind") || lower.Contains("event");
    }

    private static bool HasTaskIntent(string text)
    {
        var lower = text.ToLowerInvariant();
        return lower.Contains("todo") || lower.Contains("to-do") || lower.Contains("task")
            || lower.Contains("need to") || lower.Contains("don't forget");
    }

    private static string ExtractTitleHint(string text)
    {
        var cleaned = text.Trim();
        if (cleaned.Length > 80) cleaned = cleaned[..80];
        return cleaned;
    }
}
