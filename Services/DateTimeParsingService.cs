using Microsoft.Recognizers.Text;
using Microsoft.Recognizers.Text.DateTime;

namespace CLIo.Services;

public sealed class DateTimeParsingService
{
    public IReadOnlyList<DateTimeOffset> ParseDates(string text, DateTimeOffset? reference = null)
    {
        var refTime = (reference ?? DateTimeOffset.Now).DateTime;
        var results = DateTimeRecognizer.RecognizeDateTime(text, Culture.English, refTime: refTime);
        var dates = new List<DateTimeOffset>();

        foreach (var result in results)
        {
            if (result.Resolution?.TryGetValue("values", out var valuesObj) != true
                || valuesObj is not IList<Dictionary<string, string>> values)
                continue;

            foreach (var entry in values)
            {
                if (entry.TryGetValue("value", out var dateStr) && DateTimeOffset.TryParse(dateStr, out var parsed))
                    dates.Add(parsed);
            }
        }

        return dates;
    }

    public DateTimeOffset? ParseSingleDate(string text, DateTimeOffset? reference = null)
    {
        var dates = ParseDates(text, reference);
        return dates.Count > 0 ? dates[0] : null;
    }
}
