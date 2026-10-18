using System.Globalization;
using System.Text.RegularExpressions;

namespace CLIo.Services;

public enum RecurrenceFrequency { Daily, Weekly, Monthly, Yearly }

/// <summary>
/// A recurrence rule: the RFC 5545 RRULE subset CLIo supports (FREQ, INTERVAL, BYDAY for weekly,
/// UNTIL, COUNT). Stored as the RRULE string, e.g. "FREQ=WEEKLY;INTERVAL=1;BYDAY=TU,TH;UNTIL=20261231".
/// Occurrences are computed on demand from the event's first start; nothing is materialized.
/// </summary>
public sealed record RecurrenceRule(
    RecurrenceFrequency Frequency,
    int Interval = 1,
    IReadOnlyList<DayOfWeek>? Days = null,
    DateOnly? Until = null,
    int? Count = null)
{
    private const int MaxIterations = 5000;

    private static readonly string[] DayCodes = ["SU", "MO", "TU", "WE", "TH", "FR", "SA"];

    public override string ToString()
    {
        var parts = new List<string>
        {
            "FREQ=" + Frequency.ToString().ToUpperInvariant(),
            "INTERVAL=" + Interval
        };
        if (Days is { Count: > 0 })
        {
            parts.Add("BYDAY=" + string.Join(",", Days.Select(d => DayCodes[(int)d])));
        }

        if (Until is { } until)
        {
            parts.Add("UNTIL=" + until.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        }

        if (Count is { } count)
        {
            parts.Add("COUNT=" + count);
        }

        return string.Join(";", parts);
    }

    /// <summary>Human wording for badges and replies, e.g. "every 2 weeks on Tue, Thu".</summary>
    public string Describe()
    {
        var unit = Frequency switch
        {
            RecurrenceFrequency.Daily => "day",
            RecurrenceFrequency.Weekly => "week",
            RecurrenceFrequency.Monthly => "month",
            _ => "year"
        };
        var text = Interval == 1 ? $"every {unit}" : $"every {Interval} {unit}s";
        if (Days is { Count: > 0 })
        {
            text += " on " + string.Join(", ", Days.Select(d => CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedDayNames[(int)d]));
        }

        if (Until is { } until)
        {
            text += $" until {until:yyyy-MM-dd}";
        }

        if (Count is { } count)
        {
            text += $" ({count} times)";
        }

        return text;
    }

    public static bool TryParse(string? rrule, out RecurrenceRule rule)
    {
        rule = null!;
        if (string.IsNullOrWhiteSpace(rrule))
        {
            return false;
        }

        RecurrenceFrequency? freq = null;
        var interval = 1;
        List<DayOfWeek>? days = null;
        DateOnly? until = null;
        int? count = null;

        foreach (var part in rrule.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2)
            {
                return false;
            }

            var value = kv[1];
            switch (kv[0].ToUpperInvariant())
            {
                case "FREQ":
                    if (!Enum.TryParse<RecurrenceFrequency>(value, ignoreCase: true, out var f)) return false;
                    freq = f;
                    break;
                case "INTERVAL":
                    if (!int.TryParse(value, out interval) || interval < 1 || interval > 366) return false;
                    break;
                case "BYDAY":
                    days = [];
                    foreach (var code in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var index = Array.IndexOf(DayCodes, code.ToUpperInvariant());
                        if (index < 0) return false;
                        days.Add((DayOfWeek)index);
                    }

                    break;
                case "UNTIL":
                    if (!DateOnly.TryParseExact(value.Length >= 8 ? value[..8] : value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var u)) return false;
                    until = u;
                    break;
                case "COUNT":
                    if (!int.TryParse(value, out var c) || c < 1) return false;
                    count = c;
                    break;
                default:
                    return false;
            }
        }

        if (freq is null)
        {
            return false;
        }

        rule = new RecurrenceRule(freq.Value, interval, days is { Count: > 0 } ? days.Distinct().OrderBy(d => ((int)d + 6) % 7).ToList() : null, until, count);
        return true;
    }

    /// <summary>
    /// Plain-English phrases: "every day", "weekly", "every 2 weeks", "every tuesday and thursday",
    /// "weekdays", "every month", "yearly", each optionally followed by "until 2026-12-31" or "for 10 times".
    /// </summary>
    public static bool TryParseNatural(string? text, out RecurrenceRule rule)
    {
        rule = null!;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var lower = text.ToLowerInvariant().Trim();

        DateOnly? until = null;
        var untilMatch = Regex.Match(lower, @"\buntil\s+(\d{4}-\d{2}-\d{2})\b");
        if (untilMatch.Success)
        {
            if (!DateOnly.TryParseExact(untilMatch.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var u)) return false;
            until = u;
            lower = lower.Remove(untilMatch.Index, untilMatch.Length).Trim();
        }

        int? count = null;
        var countMatch = Regex.Match(lower, @"\bfor\s+(\d{1,3})\s+(?:times|occurrences|weeks|days)\b");
        if (countMatch.Success)
        {
            count = int.Parse(countMatch.Groups[1].Value);
            lower = lower.Remove(countMatch.Index, countMatch.Length).Trim();
        }

        if (Regex.IsMatch(lower, @"\bweekdays?\b"))
        {
            rule = new RecurrenceRule(RecurrenceFrequency.Weekly, 1,
                [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday], until, count);
            return true;
        }

        var intervalMatch = Regex.Match(lower, @"\bevery\s+(\d{1,3})\s+(day|week|month|year)s?\b");
        if (intervalMatch.Success)
        {
            var n = int.Parse(intervalMatch.Groups[1].Value);
            if (n < 1) return false;
            rule = new RecurrenceRule(FromUnit(intervalMatch.Groups[2].Value), n, null, until, count);
            return true;
        }

        var dayMatches = Regex.Matches(lower, @"\b(sun|mon|tue|wed|thu|fri|sat)(?:day|s|sday|nesday|r|rs|rsday|urday)?\b");
        if (dayMatches.Count > 0 && Regex.IsMatch(lower, @"\bevery\b|\bweekly\b|\beach\b"))
        {
            var days = dayMatches
                .Select(m => (DayOfWeek)Array.IndexOf(new[] { "sun", "mon", "tue", "wed", "thu", "fri", "sat" }, m.Groups[1].Value))
                .Distinct().OrderBy(d => ((int)d + 6) % 7).ToList();
            rule = new RecurrenceRule(RecurrenceFrequency.Weekly, 1, days, until, count);
            return true;
        }

        var simple = Regex.Match(lower, @"\b(?:every|each)\s+(day|week|month|year)\b|\b(daily|weekly|monthly|yearly|annually)\b");
        if (simple.Success)
        {
            var word = simple.Groups[1].Success ? simple.Groups[1].Value : simple.Groups[2].Value;
            var frequency = word switch
            {
                "day" or "daily" => RecurrenceFrequency.Daily,
                "week" or "weekly" => RecurrenceFrequency.Weekly,
                "month" or "monthly" => RecurrenceFrequency.Monthly,
                _ => RecurrenceFrequency.Yearly
            };
            rule = new RecurrenceRule(frequency, 1, null, until, count);
            return true;
        }

        return false;
    }

    private static RecurrenceFrequency FromUnit(string unit) => unit switch
    {
        "day" => RecurrenceFrequency.Daily,
        "week" => RecurrenceFrequency.Weekly,
        "month" => RecurrenceFrequency.Monthly,
        _ => RecurrenceFrequency.Yearly
    };

    /// <summary>
    /// Occurrence starts (including the first one) that fall within [from, to], in order. Computed from the
    /// wall-clock time of <paramref name="first"/> so a 10:00 event stays at 10:00 across daylight-saving changes.
    /// </summary>
    public IReadOnlyList<DateTimeOffset> Expand(DateTimeOffset first, DateTimeOffset from, DateTimeOffset to)
    {
        var result = new List<DateTimeOffset>();
        var wall = first.DateTime;
        var emitted = 0;

        foreach (var candidate in Candidates(wall))
        {
            if (Until is { } until && DateOnly.FromDateTime(candidate) > until)
            {
                break;
            }

            emitted++;
            if (Count is { } count && emitted > count)
            {
                break;
            }

            var at = new DateTimeOffset(candidate, TimeZoneInfo.Local.GetUtcOffset(candidate));
            if (at > to)
            {
                break;
            }

            if (at >= from)
            {
                result.Add(at);
            }
        }

        return result;
    }

    private IEnumerable<DateTime> Candidates(DateTime start)
    {
        switch (Frequency)
        {
            case RecurrenceFrequency.Daily:
                for (var k = 0; k < MaxIterations; k++)
                    yield return start.AddDays((long)k * Interval);
                break;

            case RecurrenceFrequency.Weekly:
                var days = Days is { Count: > 0 } ? Days : [start.DayOfWeek];
                var weekStart = start.Date.AddDays(-(((int)start.DayOfWeek + 6) % 7)); // Monday
                for (var w = 0; w < MaxIterations; w++)
                {
                    foreach (var day in days.OrderBy(d => ((int)d + 6) % 7))
                    {
                        var date = weekStart.AddDays((long)w * Interval * 7 + ((int)day + 6) % 7);
                        var at = date + start.TimeOfDay;
                        if (at >= start)
                            yield return at;
                    }
                }

                break;

            case RecurrenceFrequency.Monthly:
                for (var k = 0; k < MaxIterations; k++)
                {
                    var months = (long)k * Interval;
                    if (months > 12000) yield break;
                    yield return start.AddMonths((int)months);
                }

                break;

            default:
                for (var k = 0; k < 500; k++)
                {
                    var years = (long)k * Interval;
                    if (years > 9000) yield break;
                    yield return start.AddYears((int)years);
                }

                break;
        }
    }
}
