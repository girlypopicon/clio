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
    private List<CalendarEvent> FindMatchingEvents(string title, string? when)
    {
        DateTime? whenDate = null;
        TimeSpan? whenTime = null;
        if (DateTime.TryParse(when, out var parsedWhen))
        {
            whenDate = parsedWhen.Date;
            if (parsedWhen.TimeOfDay != TimeSpan.Zero)
            {
                whenTime = parsedWhen.TimeOfDay;
            }
        }

        var titleMatches = CalendarEvents
            .Where(e => e.Title.Contains(title, StringComparison.OrdinalIgnoreCase)
                || title.Contains(e.Title, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var matches = titleMatches
            .Where(e => whenDate is null || e.StartsAt.Date == whenDate.Value)
            .ToList();

        if (whenTime is not null && matches.Count > 1)
        {
            var exact = matches.Where(e => e.StartsAt.TimeOfDay == whenTime.Value).ToList();
            if (exact.Count > 0)
            {
                matches = exact;
            }
        }

        if (matches.Count == 0 && whenDate is not null && titleMatches.Count == 1)
        {
            // The given date didn't match, but there's only one event with this title overall -
            // treat it as the intended target rather than reporting "not found".
            matches = titleMatches;
        }

        return matches;
    }

    private static readonly Regex EventIdReferenceRegex = new(
        @"^(?:#|id[:=]?\s*)(\d+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Resolves the target event(s) for a delete/move/edit skill. If the match field is an
    // #id reference (which CLIo is told to prefer), resolve directly by primary key for an
    // exact, unambiguous hit; otherwise fall back to fuzzy title/date matching.
    private List<CalendarEvent> ResolveTargetEvents(string titleOrId, string? when)
    {
        var idMatch = EventIdReferenceRegex.Match(titleOrId.Trim());
        if (idMatch.Success && int.TryParse(idMatch.Groups[1].Value, out var id))
        {
            return CalendarEvents.Where(e => e.Id == id).ToList();
        }

        return FindMatchingEvents(titleOrId, when);
    }

    private static bool IsExplicitScheduleRequest(string prompt)
    {
        return ExplicitScheduleRequestRegex.IsMatch(prompt);
    }

    private static readonly Regex ExplicitDeleteRequestRegex = new(
        @"\b(delete|cancel|remove|get rid of)\b.{0,60}\b(calendar|appointment|event|reminder|meeting|item|items|thing|things|entry|entries)\b" +
        @"|\b(delete|cancel|remove|get rid of)\b.{0,40}\bfrom\b.{0,20}\b(calendar|schedule|week|month)\b" +
        @"|\b(delete|cancel|remove|get rid of)\b.{0,30}\b(on|off|of)\b.{0,20}\b(my|the)\b.{0,10}\b(calendar|schedule|week|month)\b" +
        @"|\b(delete|cancel|remove|get rid of)\b.{0,5}\b(my|the)\b.{1,40}\S",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex DeleteStopWordsRegex = new(
        @"\b(please|delete|cancel|remove|get|rid|of|my|the|an?|from|calendar|appointment|event|reminder|meeting|schedule|for)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool IsExplicitDeleteRequest(string prompt)
    {
        return ExplicitDeleteRequestRegex.IsMatch(prompt);
    }

    private static string ExtractDeleteTitleHint(string prompt)
    {
        var cleaned = DeleteStopWordsRegex.Replace(prompt, " ");
        return Regex.Replace(cleaned, @"\s+", " ").Trim();
    }

    // Move-intent verb set: reschedule, delay/postpone/defer, advance/bring-forward, numeric-shift
    private static readonly Regex ExplicitMoveRequestRegex = new(
        @"\b(?:move|reschedule|resked|push(?:\s+back)?|bump|shift|change"
        + @"|postpone|delay|defer|put\s+off"
        + @"|advance|bring\s+(?:forward|ahead)|pull\s+(?:forward|up|ahead)|move\s+(?:up|earlier|ahead))\b"
        + @".{0,60}\b(?:to|on|for|until|till|by|back|forward|ahead)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex MoveStopWordsRegex = new(
        @"\b(?:please|plz|pls|can|could|you|lets|let"
        + @"|move|reschedule|resked|push|bump|shift|change|postpone|delay|defer|put|off"
        + @"|advance|bring|pull|up|earlier"
        + @"|the|my|a|an|back|forward|ahead|to|on|for|until|till|over|by"
        + @"|this|that|next|coming|last|same|time"
        + @"|appointment|appt|event|meeting|reminder)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool IsExplicitMoveRequest(string prompt) => ExplicitMoveRequestRegex.IsMatch(prompt);

    private static string ExtractMoveTitleHint(string prompt)
    {
        var cleaned = MoveStopWordsRegex.Replace(prompt, " ");
        return Regex.Replace(cleaned, @"\s+", " ").Trim();
    }

    // ── Date-parsing vocabulary ──────────────────────────────────────────────

    private static readonly string[] MonthNames =
        { "january", "february", "march", "april", "may", "june",
          "july", "august", "september", "october", "november", "december" };

    // ISO: 2026-06-24
    private static readonly Regex IsoDateRegex = new(
        @"\b(\d{4})-(\d{1,2})-(\d{1,2})\b",
        RegexOptions.CultureInvariant);

    // Month-first: "June 24", "Jun 24th"
    private static readonly Regex MonthDayRegex = new(
        @"\b(jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?"
        + @"|aug(?:ust)?|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)\.?\s+(\d{1,2})(?:st|nd|rd|th)?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Day-first: "24 June", "24th June", "24th of June"
    private static readonly Regex DayMonthRegex = new(
        @"\b(\d{1,2})(?:st|nd|rd|th)?\s+(?:of\s+)?"
        + @"(jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?"
        + @"|aug(?:ust)?|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Word-ordinal of month: "first of June", "twenty-fourth of next month"
    private static readonly Regex WordOrdinalOfMonthRegex = new(
        @"\b(?:the\s+)?(first|second|third|fourth|fifth|sixth|seventh|eighth|ninth|tenth"
        + @"|eleventh|twelfth|thirteenth|fourteenth|fifteenth|sixteenth|seventeenth"
        + @"|eighteenth|nineteenth|twentieth|twenty[- ]first|twenty[- ]second|twenty[- ]third"
        + @"|twenty[- ]fourth|twenty[- ]fifth|twenty[- ]sixth|twenty[- ]seventh|twenty[- ]eighth"
        + @"|twenty[- ]ninth|thirtieth|thirty[- ]first)"
        + @"\s+of\s+"
        + @"(jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?"
        + @"|aug(?:ust)?|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?|next\s+month|this\s+month)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Numeric: "6/24", "06/24/26"
    private static readonly Regex NumericDateRegex = new(
        @"\b(\d{1,2})/(\d{1,2})(?:/(\d{2,4}))?\b",
        RegexOptions.CultureInvariant);

    // Bare weekday or today/tomorrow/yesterday (no modifier)
    private static readonly Regex DayOfWeekRegex = new(
        @"\b(today|tomorrow|yesterday|monday|tuesday|wednesday|thursday|friday|saturday|sunday)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Weekday with modifier: "next monday", "this friday", "coming wednesday", "last tuesday"
    private static readonly Regex WeekdayWithModifierRegex = new(
        @"\b(next|this|coming|last)\s+(monday|tuesday|wednesday|thursday|friday|saturday|sunday)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // "day after tomorrow" / "overmorrow"
    private static readonly Regex DayAfterTomorrowRegex = new(
        @"\b(?:day\s+after\s+tomorrow|overmorrow)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Bare ordinal: "the 24th", "5th" (checked last — lowest specificity)
    private static readonly Regex DayOfMonthRegex = new(
        @"\b(?:the\s+)?(\d{1,2})(?:st|nd|rd|th)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Last-resort: bare number 1–31 as a day, not followed by a time/duration unit
    private static readonly Regex BareDayNumberRegex = new(
        @"(?<!\d)\b([1-9]|[12]\d|3[01])\b(?!\s*(?:st|nd|rd|th|days?|weeks?|months?|years?|hrs?|hours?|mins?|minutes?|am|pm|:\d{2}))(?!\d)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Relative-from-now: "in 3 days", "a week from now", "3 days later"
    private static readonly Regex InXDaysRegex = new(
        @"\bin\s+(\d+|a|an|one|two|three|four|five|six|seven)\s+(days?|weeks?)\b"
        + @"|\b(\d+|a|an|one|two|three|four|five|six|seven)\s+(days?|weeks?)"
        + @"\s+(?:from\s+(?:now|today)|later|hence|out)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Relative delta for moves: "by 3 days", "back a week", "2 weeks forward", "3 days earlier"
    private static readonly Regex RelativeOffsetRegex = new(
        @"\b(?:by|back|forward|ahead)\s+(\d+|a|an|one|two|three|four|five|six|seven)\s+(days?|weeks?)\b"
        + @"|\b(\d+|a|an|one|two|three|four|five|six|seven)\s+(days?|weeks?)"
        + @"\s+(?:back|forward|ahead|earlier|later)\b"
        + @"|\bby\s+a\s+(day|week)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Period boundaries: "end of month", "start of next month", "beginning of the week"
    private static readonly Regex PeriodBoundaryRegex = new(
        @"\b(start|beginning|begin|end)\s+of\s+(?:the\s+)?(?:(next|this|last)\s+)?(month|week|year)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // ── ExtractFlexibleDate ──────────────────────────────────────────────────
    // Pulls a concrete target date from free text. Priority order:
    //   ISO → day-after-tomorrow → month+day → day+month → word-ordinal-of-month
    //   → numeric → relative-from-now → period-boundary → weekday+modifier
    //   → bare weekday/today/tomorrow → bare day ordinal
    private static DateTime? ExtractFlexibleDate(string text, out string textWithoutDate)
    {
        textWithoutDate = CleanRemainder(text);
        var today = DateTime.Today;

        // 1. ISO date: 2026-06-24
        var iso = IsoDateRegex.Match(text);
        if (iso.Success
            && int.TryParse(iso.Groups[1].Value, out var iy)
            && int.TryParse(iso.Groups[2].Value, out var im)
            && int.TryParse(iso.Groups[3].Value, out var idd))
        {
            var d = SafeDate(iy, im, idd);
            if (d is not null) { textWithoutDate = CleanRemainder(text.Remove(iso.Index, iso.Length)); return d; }
        }

        // 2. "day after tomorrow" / "overmorrow"
        var dat = DayAfterTomorrowRegex.Match(text);
        if (dat.Success)
        {
            textWithoutDate = CleanRemainder(text.Remove(dat.Index, dat.Length));
            return today.AddDays(2);
        }

        // 3. Month-first: "June 24", "Jun 24th"
        var mdf = MonthDayRegex.Match(text);
        if (mdf.Success && int.TryParse(mdf.Groups[2].Value, out var mdfDay))
        {
            var month = MonthFromToken(mdf.Groups[1].Value);
            var d = SafeDate(today.Year, month, mdfDay);
            if (d is not null)
            {
                if (d.Value < today.AddDays(-1)) d = SafeDate(today.Year + 1, month, mdfDay);
                textWithoutDate = CleanRemainder(text.Remove(mdf.Index, mdf.Length));
                return d;
            }
        }

        // 4. Day-first: "24 June", "24th of June"
        var dmf = DayMonthRegex.Match(text);
        if (dmf.Success && int.TryParse(dmf.Groups[1].Value, out var dmfDay))
        {
            var month = MonthFromToken(dmf.Groups[2].Value);
            var d = SafeDate(today.Year, month, dmfDay);
            if (d is not null)
            {
                if (d.Value < today.AddDays(-1)) d = SafeDate(today.Year + 1, month, dmfDay);
                textWithoutDate = CleanRemainder(text.Remove(dmf.Index, dmf.Length));
                return d;
            }
        }

        // 5. Word-ordinal of month: "first of June", "twenty-fourth of next month"
        var woom = WordOrdinalOfMonthRegex.Match(text);
        if (woom.Success)
        {
            var day = OrdinalWordToNumber(woom.Groups[1].Value);
            var monthToken = woom.Groups[2].Value.ToLowerInvariant();
            int month, year = today.Year;
            if (monthToken.Contains("next"))
            {
                var nm2 = today.AddMonths(1);
                month = nm2.Month; year = nm2.Year;
            }
            else if (monthToken.Contains("this"))
            {
                month = today.Month;
            }
            else
            {
                month = MonthFromToken(monthToken);
            }
            if (day > 0 && month > 0)
            {
                var d = SafeDate(year, month, day);
                if (d is not null)
                {
                    if (d.Value < today.AddDays(-1) && !monthToken.Contains("next") && !monthToken.Contains("this"))
                        d = SafeDate(today.Year + 1, month, day);
                    textWithoutDate = CleanRemainder(text.Remove(woom.Index, woom.Length));
                    return d;
                }
            }
        }

        // 6. Numeric date: "6/24", "06/24/26"
        var nd = NumericDateRegex.Match(text);
        if (nd.Success
            && int.TryParse(nd.Groups[1].Value, out var nm)
            && int.TryParse(nd.Groups[2].Value, out var ndd))
        {
            var year = today.Year;
            if (nd.Groups[3].Success && int.TryParse(nd.Groups[3].Value, out var ny))
                year = ny < 100 ? 2000 + ny : ny;
            var d = SafeDate(year, nm, ndd);
            if (d is not null)
            {
                if (!nd.Groups[3].Success && d.Value < today.AddDays(-1))
                    d = SafeDate(year + 1, nm, ndd);
                textWithoutDate = CleanRemainder(text.Remove(nd.Index, nd.Length));
                return d;
            }
        }

        // 7. Relative-from-now: "in 3 days", "a week from now", "3 days later"
        var inx = InXDaysRegex.Match(text);
        if (inx.Success)
        {
            var cStr = inx.Groups[1].Success && !string.IsNullOrEmpty(inx.Groups[1].Value)
                ? inx.Groups[1].Value : inx.Groups[3].Value;
            var uStr = inx.Groups[2].Success && !string.IsNullOrEmpty(inx.Groups[2].Value)
                ? inx.Groups[2].Value : inx.Groups[4].Value;
            var count = WordToNumber(cStr);
            if (count > 0)
            {
                var days = uStr.StartsWith("week", StringComparison.OrdinalIgnoreCase) ? count * 7 : count;
                textWithoutDate = CleanRemainder(text.Remove(inx.Index, inx.Length));
                return today.AddDays(days);
            }
        }

        // 8. Period boundaries: "end of month", "start of next month", "beginning of week"
        var pb = PeriodBoundaryRegex.Match(text);
        if (pb.Success)
        {
            var boundary = pb.Groups[1].Value.ToLowerInvariant();
            var modifier = pb.Groups[2].Success ? pb.Groups[2].Value.ToLowerInvariant() : "this";
            var period = pb.Groups[3].Value.ToLowerInvariant();
            var isEnd = boundary == "end";
            var isNext = modifier == "next";
            DateTime? d = period switch
            {
                "week" => isEnd ? EndOfWeek(today) : StartOfWeek(today),
                "month" when isEnd && isNext  => EndOfMonth(today.AddMonths(1)),
                "month" when isEnd            => EndOfMonth(today),
                "month" when isNext           => new DateTime(today.AddMonths(1).Year, today.AddMonths(1).Month, 1),
                "month"                       => new DateTime(today.Year, today.Month, 1),
                "year"  when isEnd && isNext  => new DateTime(today.Year + 1, 12, 31),
                "year"  when isEnd            => new DateTime(today.Year, 12, 31),
                "year"  when isNext           => new DateTime(today.Year + 1, 1, 1),
                "year"                        => new DateTime(today.Year, 1, 1),
                _ => null
            };
            if (d is not null) { textWithoutDate = CleanRemainder(text.Remove(pb.Index, pb.Length)); return d; }
        }

        // 9. Weekday with modifier: "next monday", "this friday", "last wednesday"
        var wm = WeekdayWithModifierRegex.Match(text);
        if (wm.Success)
        {
            var modifier = wm.Groups[1].Value.ToLowerInvariant();
            var dayName = wm.Groups[2].Value.ToLowerInvariant();
            var targetDow = (DayOfWeek)Array.IndexOf(
                new[] { "sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday" }, dayName);
            var diff = ((int)targetDow - (int)today.DayOfWeek + 7) % 7;
            var daysToNextMonday = (1 - (int)today.DayOfWeek + 7) % 7;
            if (daysToNextMonday == 0) daysToNextMonday = 7;
            var daysFromMondayToTarget = ((int)targetDow - 1 + 7) % 7;
            DateTime d = modifier switch
            {
                // "next [day]" = that weekday in next calendar week (Mon–Sun)
                "next" => today.AddDays(daysToNextMonday + daysFromMondayToTarget),
                // "last [day]" = most recent past occurrence
                "last" => today.AddDays(-((int)today.DayOfWeek - (int)targetDow + 7) % 7 is 0 ? 7 : (int)today.DayOfWeek - (int)targetDow),
                // "this" / "coming" = next upcoming occurrence
                _ => today.AddDays(diff == 0 ? 7 : diff)
            };
            textWithoutDate = CleanRemainder(text.Remove(wm.Index, wm.Length));
            return d;
        }

        // 10. Bare weekday / today / tomorrow / yesterday
        if (DayOfWeekRegex.IsMatch(text))
        {
            var word = DayOfWeekRegex.Match(text).Value.ToLowerInvariant();
            DateTime? d = word switch
            {
                "today"     => today,
                "tomorrow"  => today.AddDays(1),
                "yesterday" => today.AddDays(-1),
                _ => null
            };
            if (d is null)
            {
                var targetDow = (DayOfWeek)Array.IndexOf(
                    new[] { "sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday" }, word);
                var diff = ((int)targetDow - (int)today.DayOfWeek + 7) % 7;
                d = today.AddDays(diff == 0 ? 7 : diff);
            }
            var wo = DayOfWeekRegex.Match(text);
            textWithoutDate = CleanRemainder(text.Remove(wo.Index, wo.Length));
            return d;
        }

        // 11. Bare day ordinal: "the 24th", "5th"
        var dom = DayOfMonthRegex.Match(text);
        if (dom.Success && int.TryParse(dom.Groups[1].Value, out var domDay))
        {
            var d = SafeDate(today.Year, today.Month, domDay);
            if (d is null || d.Value < today)
            {
                var next = today.AddMonths(1);
                d = SafeDate(next.Year, next.Month, domDay);
            }
            if (d is not null) { textWithoutDate = CleanRemainder(text.Remove(dom.Index, dom.Length)); return d; }
        }

        // 12. Last resort: bare number 1–31 as day of month (e.g. "reschedule dentist for 24")
        var bdn = BareDayNumberRegex.Match(text);
        if (bdn.Success && int.TryParse(bdn.Groups[1].Value, out var bdDay))
        {
            var d = SafeDate(today.Year, today.Month, bdDay);
            if (d is null || d.Value < today)
            {
                var next = today.AddMonths(1);
                d = SafeDate(next.Year, next.Month, bdDay);
            }
            if (d is not null) { textWithoutDate = CleanRemainder(text.Remove(bdn.Index, bdn.Length)); return d; }
        }

        return null;
    }

    // Extracts a signed time delta for relative moves ("by a week", "back 3 days", "2 days earlier").
    // Returns false when no delta phrase is present.
    private static bool TryExtractRelativeOffset(string text, out string textWithoutOffset, out TimeSpan offset)
    {
        textWithoutOffset = CleanRemainder(text);
        offset = TimeSpan.Zero;
        var m = RelativeOffsetRegex.Match(text);
        if (!m.Success) return false;

        string countStr, unitStr;
        if (m.Groups[1].Success && !string.IsNullOrEmpty(m.Groups[1].Value))
        {
            countStr = m.Groups[1].Value; unitStr = m.Groups[2].Value;
        }
        else if (m.Groups[3].Success && !string.IsNullOrEmpty(m.Groups[3].Value))
        {
            countStr = m.Groups[3].Value; unitStr = m.Groups[4].Value;
        }
        else if (m.Groups[5].Success)
        {
            countStr = "1"; unitStr = m.Groups[5].Value;
        }
        else return false;

        var count = WordToNumber(countStr);
        if (count <= 0) return false;

        var days = unitStr.StartsWith("week", StringComparison.OrdinalIgnoreCase) ? count * 7 : count;
        var backward = Regex.IsMatch(m.Value, @"\b(?:back|earlier)\b", RegexOptions.IgnoreCase);
        offset = TimeSpan.FromDays(backward ? -days : days);
        textWithoutOffset = CleanRemainder(text.Remove(m.Index, m.Length));
        return true;
    }

    private static DateTime? SafeDate(int year, int month, int day)
    {
        if (year is < 1 or > 9999 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
            return null;
        return new DateTime(year, month, day);
    }

    private static int MonthFromToken(string token)
    {
        token = token.TrimEnd('.').ToLowerInvariant();
        for (var i = 0; i < MonthNames.Length; i++)
            if (MonthNames[i].StartsWith(token, StringComparison.Ordinal)) return i + 1;
        return 0;
    }

    private static int WordToNumber(string word) => word.ToLowerInvariant() switch
    {
        "a" or "an" or "one" => 1, "two" => 2, "three" => 3, "four" => 4,
        "five" => 5, "six" => 6, "seven" => 7,
        _ => int.TryParse(word, out var n) ? n : 0
    };

    private static int OrdinalWordToNumber(string word)
    {
        var w = word.ToLowerInvariant().Replace("-", " ");
        return w switch
        {
            "first" => 1, "second" => 2, "third" => 3, "fourth" => 4, "fifth" => 5,
            "sixth" => 6, "seventh" => 7, "eighth" => 8, "ninth" => 9, "tenth" => 10,
            "eleventh" => 11, "twelfth" => 12, "thirteenth" => 13, "fourteenth" => 14,
            "fifteenth" => 15, "sixteenth" => 16, "seventeenth" => 17, "eighteenth" => 18,
            "nineteenth" => 19, "twentieth" => 20,
            "twenty first" => 21, "twenty second" => 22, "twenty third" => 23,
            "twenty fourth" => 24, "twenty fifth" => 25, "twenty sixth" => 26,
            "twenty seventh" => 27, "twenty eighth" => 28, "twenty ninth" => 29,
            "thirtieth" => 30, "thirty first" => 31,
            _ => 0
        };
    }

    private static DateTime EndOfWeek(DateTime d)
    {
        // Sunday = end of week
        var diff = (7 - (int)d.DayOfWeek) % 7;
        return d.AddDays(diff == 0 ? 7 : diff);
    }

    private static DateTime StartOfWeek(DateTime d)
    {
        // Monday = start of week
        var diff = ((int)d.DayOfWeek - 1 + 7) % 7;
        return d.AddDays(-diff);
    }

    private static DateTime EndOfMonth(DateTime d) =>
        new(d.Year, d.Month, DateTime.DaysInMonth(d.Year, d.Month));

    private static string CleanRemainder(string value) => Regex.Replace(value, @"\s+", " ").Trim();

    // Legacy shim: resolves weekday names / today / tomorrow — called only from ExtractFlexibleDate step 10
    private static string? ExtractDateHint(string titleHint, out string titleWithoutDate)
    {
        titleWithoutDate = titleHint;
        return null; // superseded by ExtractFlexibleDate; kept to avoid orphan references
    }

    private static readonly Regex CalendarRequestRegex = new(
        @"\b(?:next|this|coming)?\s*(?:week|month|two\s*weeks?|fortnight)\b.{0,20}\b(?:look|looks|looking|going on|happening|schedule|calendar|on the calendar|view)" +
        @"|\b(?:show|see|view|tell)\b.{0,30}\b(?:calendar|schedule|week|month)" +
        @"|\b(?:week|month|two\s*weeks?|fortnight)\b.{0,10}\bview\b" +
        @"|\bschedule\b.{0,10}\b(?:look|looks)" +
        @"|\bcalendar\b.{0,10}\bfor\b" +
        @"|\b(?:on|in)\s+(?:my\s+)?calendar\b.{0,25}\b(?:week|month|today|tomorrow|two\s*weeks?|fortnight)?\b" +
        @"|\bcalendar\b.{0,25}\b(?:week|month|today|tomorrow|two\s*weeks?|fortnight)\b" +
        @"|\b(?:what'?s|whats|what\s+is|anything|what)\b.{0,30}\b(?:calendar|schedule)\b" +
        @"|\b(?:have|got|going on|happening)\b.{0,20}\b(?:this|next|coming)?\s*(?:week|month|two\s*weeks?|fortnight)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static CalendarDirective? InferCalendarDirectiveFromPrompt(string prompt)
    {
        // A real "show me my week" request is short. A long message that merely mentions a calendar
        // ("... what do I have next week ...") is a conversation, and must not be turned into a widget.
        if (prompt.Trim().Length > MaxCalendarRequestLength || !CalendarRequestRegex.IsMatch(prompt))
        {
            return null;
        }

        string mode;
        if (Regex.IsMatch(prompt, @"two\s*weeks?|fortnight|14\s*days?", RegexOptions.IgnoreCase))
        {
            mode = "TwoWeek";
        }
        else if (Regex.IsMatch(prompt, @"\bmonth\b", RegexOptions.IgnoreCase))
        {
            mode = "Month";
        }
        else
        {
            mode = "Week";
        }

        // "next two weeks" means starting now (this week + next week), not shifted 14 days out -
        // only "next week"/"next month" anchor the view forward by one period.
        DateTime? anchor = null;
        if (mode == "Month" && Regex.IsMatch(prompt, @"\bnext\s+month\b", RegexOptions.IgnoreCase))
        {
            anchor = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(1);
        }
        else if (mode == "Week" && Regex.IsMatch(prompt, @"\bnext\s+week\b", RegexOptions.IgnoreCase))
        {
            anchor = DateTime.Today.AddDays(7);
        }

        return new CalendarDirective(mode, anchor);
    }

    private const int MaxCalendarRequestLength = 120;

    private static readonly Regex AsciiCalendarBlockRegex = new(
        @"```[\s\S]*?```",
        RegexOptions.Compiled);

    private static readonly Regex AsciiCalendarLineRegex = new(
        @"\b(?:Sun|Mon|Tue|Wed|Thu|Fri|Sat)\s+\d{1,2}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex AsciiCalendarHeaderLineRegex = new(
        @"(?m)^\s*(?:Week|Month) of [A-Za-z]+ \d{1,2}.*$\n?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string StripAsciiCalendarBlocks(string content)
    {
        var result = AsciiCalendarBlockRegex.Replace(content, match => AsciiCalendarLineRegex.IsMatch(match.Value) ? string.Empty : match.Value);
        result = AsciiCalendarHeaderLineRegex.Replace(result, string.Empty);
        return Regex.Replace(result, @"\n{3,}", "\n\n").Trim();
    }

    private void ApplyCalendarDirective(ChatMessage assistant, CalendarDirective directive)
    {
        var anchor = directive.Anchor ?? DateTime.Today;
        var rows = directive.Mode switch
        {
            "Week" => 1,
            "TwoWeek" => 2,
            _ => 6
        };

        DateTime gridStart;
        if (directive.Mode == "Month")
        {
            var firstOfMonth = new DateTime(anchor.Year, anchor.Month, 1);
            gridStart = firstOfMonth.AddDays(-(int)firstOfMonth.DayOfWeek);
        }
        else
        {
            gridStart = anchor.AddDays(-(int)anchor.DayOfWeek);
        }

        var totalDays = rows * 7;
        var eventsByDate = CalendarEvents
            .GroupBy(item => item.StartsAt.Date)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<CalendarEvent>)group.OrderBy(item => item.StartsAt).ToList());

        var days = new List<CalendarDayCell>(totalDays);
        for (var i = 0; i < totalDays; i++)
        {
            var date = gridStart.AddDays(i);
            var dayEvents = eventsByDate.TryGetValue(date, out var events) ? events : [];
            days.Add(new CalendarDayCell
            {
                Date = date,
                IsCurrentMonth = directive.Mode != "Month" || date.Month == anchor.Month,
                IsToday = date == DateTime.Today,
                IsSelected = date == anchor,
                HasEvents = dayEvents.Count > 0,
                Events = dayEvents
            });
        }

        var end = gridStart.AddDays(totalDays - 1);
        assistant.CalendarWidgetLabel = directive.Mode switch
        {
            "Month" => anchor.ToString("MMMM yyyy"),
            _ => gridStart.Year == end.Year ? $"{gridStart:MMM d} – {end:MMM d, yyyy}" : $"{gridStart:MMM d, yyyy} – {end:MMM d, yyyy}"
        };
        assistant.CalendarWidgetDays = new ObservableCollection<CalendarDayCell>(days);

        var rangeLabel = directive.Mode switch
        {
            "Week" => "this week",
            "TwoWeek" => "the next two weeks",
            _ => "this month"
        };

        var eventCount = days.Sum(day => day.Events.Count);
        var summary = eventCount == 0
            ? $"Nothing on the calendar for {rangeLabel}."
            : $"Here's {rangeLabel} - {eventCount} item(s) on the calendar.";
        assistant.Content = ComposeCalendarReply(assistant.Content, summary);
    }

    /// <summary>
    /// The widget shows the calendar itself. The one-line summary stands in only when the model said almost
    /// nothing; a real answer is never replaced (that was a bug: the calendar swallowed the actual reply).
    /// </summary>
    private static string ComposeCalendarReply(string? modelText, string summary)
    {
        var text = modelText?.Trim() ?? string.Empty;
        return text.Length >= 60 ? text : summary;
    }
}
