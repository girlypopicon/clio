using CLIo.Models;

namespace CLIo.Services;

/// <summary>One thing the scan thinks is a duplicate or a structure problem. It only ever PROPOSES: the user decides in Review.</summary>
public sealed record TidyProposal(
    /// <summary>merge_memory, rename_memory, duplicate_event or duplicate_task.</summary>
    string Kind,
    /// <summary>merge_memory: the key to keep. rename_memory: the key to rename. duplicate_*: the id to remove.</summary>
    string Target,
    /// <summary>merge_memory: the key to remove. rename_memory: the new key. duplicate_*: the id that is kept.</summary>
    string Proposed,
    /// <summary>A plain-language description for the Review list.</summary>
    string Label,
    /// <summary>The value being kept (shown next to the label).</summary>
    string Current);

/// <summary>
/// The deterministic part of the nightly tidy: finds memory keys that look like the same fact stored twice,
/// keys that are both a value and a parent, and duplicate events and tasks. No model call, no writes. It errs on
/// the side of saying nothing, because a missed duplicate costs nothing and a wrong merge loses data; and it
/// never decides on its own.
/// </summary>
public static class TidyScanner
{
    public static IReadOnlyList<TidyProposal> Scan(
        IReadOnlyList<MemoryItem> memory,
        IReadOnlyList<CalendarEvent> events,
        IReadOnlyList<JournalTask> tasks)
    {
        var proposals = new List<TidyProposal>();
        ScanMemory(memory.Where(m => !string.IsNullOrWhiteSpace(m.Value)).ToList(), proposals);
        ScanEvents(events, proposals);
        ScanTasks(tasks, proposals);
        return proposals;
    }

    // ---- memory --------------------------------------------------------------------------------

    private static void ScanMemory(List<MemoryItem> items, List<TidyProposal> proposals)
    {
        var byKey = items.ToDictionary(i => i.Key, StringComparer.OrdinalIgnoreCase);
        var handledPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // A key that is a value AND a parent: the value should live at key:summary.
        foreach (var item in items)
        {
            // a malformed twin ("movies:") is not a sub-entry of "movies"; it gets its own merge proposal below
            var hasChildren = items.Any(o => o.Key.Trim(':') != item.Key
                && o.Key.StartsWith(item.Key + ":", StringComparison.OrdinalIgnoreCase));
            if (hasChildren && !byKey.ContainsKey(item.Key + ":summary"))
            {
                proposals.Add(new TidyProposal(
                    "rename_memory", item.Key, item.Key + ":summary",
                    $"\"{item.Key}\" holds a value and also has sub-entries under it. Move its value to \"{item.Key}:summary\"?",
                    item.Value));
            }
        }

        // Malformed twin: "user:likes:movies:" next to "user:likes:movies".
        foreach (var item in items)
        {
            var clean = item.Key.Trim(':');
            if (clean != item.Key && byKey.TryGetValue(clean, out var good))
            {
                AddMerge(proposals, handledPairs, good, item, "this key has a stray colon and a clean twin");
            }
        }

        // Same parent, related key names, compatible values.
        foreach (var group in items.GroupBy(i => Parent(i.Key), StringComparer.OrdinalIgnoreCase))
        {
            var siblings = group.ToList();
            for (var i = 0; i < siblings.Count; i++)
            {
                for (var j = i + 1; j < siblings.Count; j++)
                {
                    var a = siblings[i];
                    var b = siblings[j];
                    if (a.Key.Trim(':') == b.Key.Trim(':'))
                    {
                        continue; // handled as a malformed twin above
                    }

                    if (RelatedNames(Tail(a.Key), Tail(b.Key)) && CompatibleValues(a.Value, b.Value))
                    {
                        AddMerge(proposals, handledPairs, a, b, "their names are related and their values agree");
                    }
                }
            }
        }
    }

    private static void AddMerge(List<TidyProposal> proposals, HashSet<string> handled, MemoryItem a, MemoryItem b, string why)
    {
        var pair = string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase) < 0 ? a.Key + "|" + b.Key : b.Key + "|" + a.Key;
        if (!handled.Add(pair))
        {
            return;
        }

        var (keep, remove) = Choose(a, b);
        proposals.Add(new TidyProposal(
            "merge_memory", keep.Key, remove.Key,
            $"These look like the same fact stored twice ({why}): keep \"{keep.Key}\" = \"{Short(keep.Value)}\" and remove \"{remove.Key}\" = \"{Short(remove.Value)}\"?",
            keep.Value));
    }

    /// <summary>Keep a hand-entered one over an automatic one, then the longer (more informative) value, then the cleaner key.</summary>
    private static (MemoryItem Keep, MemoryItem Remove) Choose(MemoryItem a, MemoryItem b)
    {
        if (a.Source == "manual" && b.Source != "manual") return (a, b);
        if (b.Source == "manual" && a.Source != "manual") return (b, a);
        if (a.Key != a.Key.Trim(':')) return (b, a);
        if (b.Key != b.Key.Trim(':')) return (a, b);
        if (a.Value.Length != b.Value.Length) return a.Value.Length > b.Value.Length ? (a, b) : (b, a);
        return a.Key.Length <= b.Key.Length ? (a, b) : (b, a);
    }

    private static string Parent(string key)
    {
        var trimmed = key.Trim(':');
        var cut = trimmed.LastIndexOf(':');
        return cut < 0 ? string.Empty : trimmed[..cut];
    }

    private static string Tail(string key)
    {
        var trimmed = key.Trim(':');
        return trimmed[(trimmed.LastIndexOf(':') + 1)..];
    }

    private static readonly HashSet<string> GenericWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "status", "name", "idea", "type", "goal", "use", "summary", "description", "value", "info", "notes", "data"
    };

    private static readonly string[][] Synonyms =
    [
        ["length", "window", "size", "limit"],
        ["birthday", "birthdate", "dob"],
        ["phone", "telephone", "mobile", "cell"],
        ["email", "mail"],
    ];

    /// <summary>
    /// Are two key names (the last segment of each) plausibly the same attribute? Yes when they use the same words,
    /// when one is a clear refinement of the other ("price" / "price_flash"), or when they differ only by synonyms
    /// ("context_length" / "context_window"). Merely sharing a word is not enough: zodiac_moon and zodiac_sun are
    /// different facts.
    /// </summary>
    private static bool RelatedNames(string a, string b)
    {
        var wordsA = Words(a);
        var wordsB = Words(b);
        if (wordsA.Count == 0 || wordsB.Count == 0)
        {
            return false;
        }

        if (wordsA.SetEquals(wordsB))
        {
            return true;
        }

        var smaller = wordsA.Count <= wordsB.Count ? wordsA : wordsB;
        var larger = ReferenceEquals(smaller, wordsA) ? wordsB : wordsA;
        if (smaller.IsSubsetOf(larger)
            && (smaller.Count >= 2 || smaller.Any(w => w.Length >= 4 && !GenericWords.Contains(w))))
        {
            return true;
        }

        if (wordsA.Select(Canonical).ToHashSet().SetEquals(wordsB.Select(Canonical)))
        {
            return true;
        }

        return TextMatch.TitlesSimilar(string.Join(' ', wordsA.OrderBy(w => w)), string.Join(' ', wordsB.OrderBy(w => w)));
    }

    private static string Canonical(string word) =>
        Synonyms.FirstOrDefault(set => set.Contains(word))?[0] ?? word;

    private static HashSet<string> Words(string tail) =>
        tail.Split(['_', '-', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.ToLowerInvariant())
            .ToHashSet();

    /// <summary>Equal after ignoring punctuation and case, or one is the start of the other ("1M" / "1M (milli)").</summary>
    private static bool CompatibleValues(string a, string b)
    {
        var x = TextMatch.Normalize(a);
        var y = TextMatch.Normalize(b);
        if (x.Length < 2 || y.Length < 2)
        {
            return false;
        }

        return x == y || (Math.Min(x.Length, y.Length) >= 2 && (x.StartsWith(y, StringComparison.Ordinal) || y.StartsWith(x, StringComparison.Ordinal)));
    }

    private static string Short(string text) => text.Length <= 50 ? text : text[..49] + "…";

    // ---- events and tasks ----------------------------------------------------------------------

    private static void ScanEvents(IReadOnlyList<CalendarEvent> events, List<TidyProposal> proposals)
    {
        var ordered = events.OrderBy(e => e.Id).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            for (var j = i + 1; j < ordered.Count; j++)
            {
                var a = ordered[i];
                var b = ordered[j];
                if (a.StartsAt.Date != b.StartsAt.Date || !TextMatch.TitlesSimilar(a.Title, b.Title))
                {
                    continue;
                }

                // same day and nearly the same title; different times of day are probably two real events
                var sameMoment = Math.Abs((a.StartsAt - b.StartsAt).TotalMinutes) < 1
                    || a.StartsAt.TimeOfDay == TimeSpan.Zero || b.StartsAt.TimeOfDay == TimeSpan.Zero;
                if (!sameMoment)
                {
                    continue;
                }

                // keep a hand-entered one; otherwise the older entry
                var (keep, remove) = b.Source == "manual" && a.Source != "manual" ? (b, a) : (a, b);
                proposals.Add(new TidyProposal(
                    "duplicate_event", remove.Id.ToString(), keep.Id.ToString(),
                    $"Two calendar entries look like the same event on {keep.StartsAt:MMM d}: keep \"{keep.Title}\" and remove \"{remove.Title}\"?",
                    keep.Title));
            }
        }
    }

    private static void ScanTasks(IReadOnlyList<JournalTask> tasks, List<TidyProposal> proposals)
    {
        var open = tasks.Where(t => !t.IsDone).OrderBy(t => t.Id).ToList();
        for (var i = 0; i < open.Count; i++)
        {
            for (var j = i + 1; j < open.Count; j++)
            {
                if (TextMatch.TitlesSimilar(open[i].Title, open[j].Title))
                {
                    proposals.Add(new TidyProposal(
                        "duplicate_task", open[j].Id.ToString(), open[i].Id.ToString(),
                        $"Two open tasks look the same: keep \"{open[i].Title}\" and remove \"{open[j].Title}\"?",
                        open[i].Title));
                }
            }
        }
    }
}
