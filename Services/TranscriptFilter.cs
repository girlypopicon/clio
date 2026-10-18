using System.Text;
using System.Text.RegularExpressions;

namespace CLIo.Services;

/// <summary>
/// Deterministic clean-up for speech-to-text output: removes Whisper's well-known hallucinations
/// (repetition loops, digit runs, "Thank you." from silence), fixes the CLIo brand name, and
/// joins utterances without leaving their trailing "..." / dashes in the middle of a sentence.
/// </summary>
public static class TranscriptFilter
{
    private static readonly HashSet<string> SilenceHallucinations = new(StringComparer.OrdinalIgnoreCase)
    {
        "thank you", "thanks", "thank you very much", "thanks for watching", "thank you for watching",
        "you", "bye", "bye bye", "goodbye", "the end", "please subscribe", "subscribe", "so", "oh", "hmm", "uh", "um"
    };

    // Subtitle/credit boilerplate Whisper invents from noise.
    private static readonly Regex Credits = new(
        @"amara\.org|subtitles?\s+(by|from)|captions?\s+(by|from)|transcribed\s+by|transcription\s+by|www\.\S+|♪|\bsubscribe to\b|like and subscribe",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 8+ single digits separated by spaces/hyphens/commas: "6-4-6-4-8-1-2-1-2..." (not phone numbers, which have longer groups).
    private static readonly Regex DigitRun = new(@"\b(?:\d[\s\-,.]+){7,}\d\b", RegexOptions.Compiled);

    private static readonly Regex BrandName = new(@"\b(?:cleo|clio|clyo|kleo|cleio)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NonWord = new(@"[^\p{L}\p{N}]+", RegexOptions.Compiled);

    /// <summary>Cleans one transcribed utterance. Returns "" when nothing real was said.</summary>
    public static string Apply(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var t = Regex.Replace(text, @"\s+", " ").Trim();
        t = DigitRun.Replace(t, " ");
        t = CollapseRepeats(t);
        t = Regex.Replace(t, @"\s+", " ").Trim();

        if (t.Length == 0) return string.Empty;
        if (t.Length < 120 && Credits.IsMatch(t)) return string.Empty;
        if (SilenceHallucinations.Contains(NonWord.Replace(t, " ").Trim())) return string.Empty;
        if (NonWord.Replace(t, "").Length == 0) return string.Empty; // only punctuation/symbols

        return VoiceCorrections.Apply(FixBrandName(t));
    }

    /// <summary>Whisper hears "CLIo" as Cleo/Clio; the name is always CLIo in this app.</summary>
    public static string FixBrandName(string text) => BrandName.Replace(text, "CLIo");

    /// <summary>
    /// Collapses a phrase of 1-8 words repeated back to back into one copy. Single words must repeat
    /// 4+ times and longer phrases 3+ times, so real emphasis ("no, no, no") is left alone.
    /// </summary>
    public static string CollapseRepeats(string text)
    {
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var keys = tokens.Select(token => NonWord.Replace(token, "").ToLowerInvariant()).ToArray();
        var output = new List<string>(tokens.Length);

        var i = 0;
        while (i < tokens.Length)
        {
            var collapsed = false;
            for (var n = 8; n >= 1; n--)
            {
                if (i + n > tokens.Length) continue;
                var repeats = 1;
                while (i + (repeats + 1) * n <= tokens.Length && SameRun(keys, i, i + repeats * n, n))
                {
                    repeats++;
                }

                var threshold = n == 1 ? 4 : 3;
                if (repeats >= threshold)
                {
                    output.AddRange(tokens.Skip(i).Take(n));
                    i += n * repeats;
                    collapsed = true;
                    break;
                }
            }

            if (!collapsed)
            {
                output.Add(tokens[i]);
                i++;
            }
        }

        return string.Join(' ', output);
    }

    private static bool SameRun(string[] keys, int a, int b, int n)
    {
        for (var k = 0; k < n; k++)
        {
            if (keys[a + k].Length == 0 || keys[a + k] != keys[b + k]) return false;
        }

        return true;
    }

    /// <summary>
    /// Joins utterances in order. An utterance cut at a thinking pause often ends in "..." or a dash,
    /// which would look wrong in the middle of the sentence, so those are stripped except at the end.
    /// </summary>
    public static string JoinUtterances(IReadOnlyList<string> utterances)
    {
        var parts = utterances.Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u.Trim()).ToList();
        var sb = new StringBuilder();
        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            if (i < parts.Count - 1)
            {
                part = Regex.Replace(part, @"\s*(\.{2,}|…|—|–|-)\s*$", "");
            }

            if (sb.Length > 0) sb.Append(' ');
            sb.Append(part);
        }

        return sb.ToString().Trim();
    }
}
