using System.Text.RegularExpressions;

namespace CLIo.Services;

/// <summary>
/// The "homonym table": fixes words speech-to-text keeps mishearing.
/// 1) User rules ("heard => correct") from Settings, applied as whole-word, case-insensitive replacements.
/// 2) Automatic matching of names/terms from memory: a heard word (or 2-3 adjacent words) that sounds
///    like a known term (same first letter, same phonetic key, within a small edit distance) becomes that term.
/// </summary>
public static class VoiceCorrections
{
    private static readonly object Gate = new();
    private static List<(Regex Pattern, string Replacement)> userRules = new();
    private static List<string> vocabulary = new();

    /// <summary>Raised with "'heard' -> 'Correct'" whenever a correction changes the text (for the dev log).</summary>
    public static event Action<string>? Corrected;

    // Everyday words that must never be auto-"corrected" into a name (right/Wright, will/Will, mark/Mark...).
    private static readonly HashSet<string> CommonWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "about","above","after","again","against","all","also","always","another","answer","any","april","are","around","ask","away",
        "back","because","been","before","being","best","better","between","bill","both","bring","build","call","came","can","care",
        "change","check","come","could","dark","day","does","done","down","draft","drive","each","early","else","even","ever","every",
        "face","fall","feel","find","fine","first","five","for","form","found","four","free","from","game","gave","get","give","goes",
        "going","gone","good","grace","great","green","grow","half","hand","hard","have","head","hear","help","here","high","hold",
        "home","hope","hour","house","into","just","keep","kind","knew","know","last","late","lead","learn","left","less","let",
        "life","light","like","line","list","little","live","long","look","lose","lost","made","make","many","mark","matter","may",
        "mean","might","mind","miss","more","most","move","much","must","name","near","need","never","next","night","note","now",
        "number","often","once","only","open","order","other","over","page","part","past","pay","people","place","plan","play",
        "point","power","press","pretty","problem","put","quite","read","real","really","right","road","room","rose","run","said",
        "same","saw","say","school","see","seem","send","set","shall","should","show","side","since","small","some","soon","sound",
        "start","state","stay","still","stop","story","such","sure","take","talk","tell","than","that","their","them","then","there",
        "these","they","thing","think","this","those","though","three","through","time","today","together","told","too","took","try",
        "turn","under","until","upon","use","very","wait","walk","want","was","watch","water","way","week","well","went","were",
        "what","when","where","which","while","white","who","whole","why","will","with","without","word","work","world","would",
        "write","wrong","year","yes","yet","you","young","your"
    };

    public static void SetUserRules(string? text)
    {
        var rules = new List<(Regex, string)>();
        foreach (var raw in (text ?? string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var separator = line.Contains("=>") ? "=>" : line.Contains("->") ? "->" : null;
            if (separator is null) continue;
            var parts = line.Split(separator, 2);
            var heard = parts[0].Trim();
            var correct = parts[1].Trim();
            if (heard.Length == 0 || correct.Length == 0) continue;

            var escaped = string.Join(@"\s+", heard.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape));
            rules.Add((new Regex($@"(?<![\p{{L}}\p{{N}}]){escaped}(?![\p{{L}}\p{{N}}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), correct));
        }

        lock (Gate) userRules = rules;
    }

    public static void SetVocabulary(IEnumerable<string> terms)
    {
        var list = terms.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        lock (Gate) vocabulary = list;
    }

    public static string Apply(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        List<(Regex Pattern, string Replacement)> rules;
        List<string> terms;
        lock (Gate)
        {
            rules = userRules;
            terms = vocabulary;
        }

        foreach (var (pattern, replacement) in rules)
        {
            text = pattern.Replace(text, match =>
            {
                if (match.Value != replacement) Corrected?.Invoke($"'{match.Value}' -> '{replacement}'");
                return replacement;
            });
        }

        return terms.Count == 0 ? text : ApplyVocabulary(text, terms);
    }

    private static string ApplyVocabulary(string text, List<string> terms)
    {
        var words = Regex.Matches(text, @"[A-Za-z]+");
        if (words.Count == 0) return text;

        var replacements = new List<(int Start, int Length, string With)>();
        var i = 0;
        while (i < words.Count)
        {
            var matched = false;
            for (var width = Math.Min(3, words.Count - i); width >= 1 && !matched; width--)
            {
                if (!Contiguous(text, words, i, width)) continue;
                var start = words[i].Index;
                var end = words[i + width - 1].Index + words[i + width - 1].Length;
                var spoken = text[start..end];
                var candidate = Regex.Replace(spoken, "[^A-Za-z]", "");

                foreach (var term in terms)
                {
                    if (!Sounds(candidate, term, width)) continue;
                    if (spoken != term) replacements.Add((start, end - start, term));
                    i += width;
                    matched = true;
                    break;
                }
            }

            if (!matched) i++;
        }

        if (replacements.Count == 0) return text;
        var result = text;
        foreach (var (start, length, with) in replacements.OrderByDescending(r => r.Start))
        {
            Corrected?.Invoke($"'{result.Substring(start, length)}' -> '{with}'");
            result = result.Remove(start, length).Insert(start, with);
        }

        return result;
    }

    // Words in a window must be separated by exactly one space (so "dock lite" qualifies, "dock, lite" does not).
    private static bool Contiguous(string text, MatchCollection words, int first, int width)
    {
        for (var k = first; k < first + width - 1; k++)
        {
            var gapStart = words[k].Index + words[k].Length;
            if (words[k + 1].Index - gapStart != 1 || text[gapStart] != ' ') return false;
        }

        return true;
    }

    private static bool Sounds(string candidate, string term, int width)
    {
        var target = Regex.Replace(term, "[^A-Za-z]", "");
        if (target.Length < 5 || candidate.Length < 4) return false;

        if (candidate.Equals(target, StringComparison.OrdinalIgnoreCase)) return true; // casing / spacing fix only
        if (width == 1 && CommonWords.Contains(candidate)) return false;
        if (char.ToLowerInvariant(candidate[0]) != char.ToLowerInvariant(target[0])) return false;
        if (PhoneticKey(candidate) != PhoneticKey(target)) return false;
        return Distance(candidate.ToLowerInvariant(), target.ToLowerInvariant()) <= Math.Min(2, Math.Max(1, target.Length / 4));
    }

    /// <summary>A small Metaphone-style key: similar-sounding words share a key.</summary>
    public static string PhoneticKey(string word)
    {
        var w = Regex.Replace(word.ToLowerInvariant(), "[^a-z]", "");
        if (w.Length == 0) return string.Empty;

        foreach (var (from, to) in new[] { ("kn", "n"), ("gn", "n"), ("pn", "n"), ("wr", "r"), ("wh", "w"), ("ps", "s") })
        {
            if (w.StartsWith(from)) { w = to + w[from.Length..]; break; }
        }

        w = w.Replace("ph", "f").Replace("ck", "k").Replace("sch", "sk").Replace("tch", "ch").Replace("dg", "j")
             .Replace("gh", "").Replace("th", "t").Replace("sh", "x").Replace("ch", "x");

        var key = new System.Text.StringBuilder();
        for (var k = 0; k < w.Length; k++)
        {
            var c = w[k];
            var next = k + 1 < w.Length ? w[k + 1] : '\0';
            char mapped = c switch
            {
                'c' => next is 'e' or 'i' or 'y' ? 's' : 'k',
                'q' => 'k',
                'z' => 's',
                'v' => 'f',
                'a' or 'e' or 'i' or 'o' or 'u' => k == 0 ? c : '\0', // keep only a leading vowel
                'w' or 'y' or 'h' => k == 0 || "aeiou".Contains(next) ? c : '\0',
                _ => c
            };

            if (mapped != '\0' && (key.Length == 0 || key[^1] != mapped)) key.Append(mapped);
        }

        return key.ToString();
    }

    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        }

        return d[a.Length, b.Length];
    }
}
