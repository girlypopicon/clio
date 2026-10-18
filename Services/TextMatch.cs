using System.Text;

namespace CLIo.Services;

/// <summary>Loose title comparison so "Kesha's birthday" and "Ke$ha's birthday" count as the same thing.</summary>
public static class TextMatch
{
    /// <summary>Lowercase letters and digits only: punctuation, symbols and spacing are ignored.</summary>
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }

        return sb.ToString();
    }

    public static bool TitlesSimilar(string a, string b)
    {
        var x = Normalize(a);
        var y = Normalize(b);
        if (x.Length == 0 || y.Length == 0)
        {
            return false;
        }

        if (x == y)
        {
            return true;
        }

        // One title is the other plus a little extra ("Dentist" / "Dentist appointment").
        if (Math.Min(x.Length, y.Length) >= 5 && (x.Contains(y) || y.Contains(x)))
        {
            return true;
        }

        // A character or two off ("Ke$ha" vs "Keesha") on longer titles.
        return Math.Min(x.Length, y.Length) >= 8 && Distance(x, y) <= 2;
    }

    private static int Distance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
