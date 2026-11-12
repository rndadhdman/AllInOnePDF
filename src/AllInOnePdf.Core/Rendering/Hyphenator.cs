using System.Text;

namespace AllInOnePdf.Core.Rendering;

/// <summary>
/// The layout engine has no dictionary hyphenation, so <see cref="TypographySettings.Hyphenate"/> inserts soft hyphens
/// (U+00AD) into long words using a language-agnostic rule: break between vowel–consonant–vowel (before the
/// consonant) and between two consonants that sit between vowels, never splitting common digraphs, keeping at least
/// three letters at each end and four between breaks. The line breaker only shows a hyphen where it actually breaks.
/// Limitations: it is a heuristic, so some breaks are unusual for a given language ("requi-res"); words with digits,
/// capitals-only words and words shorter than nine letters are left alone. The layout engine drops unused soft
/// hyphens before shaping, so they leave nothing in the PDF's text. Applied to body paragraphs only (never code, links, headings or tables).
/// </summary>
internal static class Hyphenator
{
    private const char SoftHyphen = '­';
    private const int MinWord = 9;
    private const int MinGap = 4;
    private const int MinEdge = 3;
    private static readonly string[] Digraphs = ["ch", "sh", "th", "ph", "ck", "ng", "qu", "gh", "wh", "gu", "sch", "tch"];

    private static bool IsVowel(char c) => "aeiouyàáâäãåæèéêëìíîïòóôöõøœùúûüýÿ".Contains(char.ToLowerInvariant(c), StringComparison.Ordinal);

    public static string Insert(string text)
    {
        if (text.Length < MinWord)
        {
            return text;
        }

        StringBuilder? sb = null;
        var i = 0;
        while (i < text.Length)
        {
            if (!char.IsLetter(text[i]))
            {
                sb?.Append(text[i]);
                i++;
                continue;
            }

            var start = i;
            while (i < text.Length && (char.IsLetter(text[i]) || text[i] == '\''))
            {
                i++;
            }

            // Skip tokens glued to digits, URLs or paths ("v2alpha", "example.com/abc").
            var glued = (start > 0 && (char.IsDigit(text[start - 1]) || text[start - 1] is '/' or '.' or '@' or '_'))
                        || (i < text.Length && (char.IsDigit(text[i]) || text[i] is '/' or '@' or '_' || (text[i] == '.' && i + 1 < text.Length && char.IsLetter(text[i + 1]))));
            var word = text.AsSpan(start, i - start);
            var breaks = glued ? null : BreakPoints(word);
            if (breaks is null)
            {
                sb?.Append(word);
                continue;
            }

            sb ??= new StringBuilder(text.Length + 16).Append(text, 0, start);
            var last = 0;
            foreach (var b in breaks)
            {
                sb.Append(word[last..b]).Append(SoftHyphen);
                last = b;
            }

            sb.Append(word[last..]);
        }

        return sb?.ToString() ?? text;
    }

    private static List<int>? BreakPoints(ReadOnlySpan<char> w)
    {
        if (w.Length < MinWord || w.Contains('\''))
        {
            return null;
        }

        var hasLower = false;
        foreach (var ch in w)
        {
            hasLower |= char.IsLower(ch);
        }

        if (!hasLower)
        {
            return null;
        }

        List<int>? result = null;
        var lastBreak = 0;
        for (var p = MinEdge; p <= w.Length - MinEdge; p++)
        {
            if (lastBreak > 0 && p - lastBreak < MinGap)
            {
                continue;
            }

            var prev = w[p - 1];
            var cur = w[p];
            var ok = false;
            if (IsVowel(prev) && !IsVowel(cur) && p + 1 < w.Length && IsVowel(w[p + 1]))
            {
                ok = true; // V-CV
            }
            else if (!IsVowel(prev) && !IsVowel(cur) && p >= 2 && IsVowel(w[p - 2]) && p + 1 < w.Length && IsVowel(w[p + 1]))
            {
                ok = !IsDigraph(w.Slice(p - 1, 2)); // VC-CV
            }

            if (ok)
            {
                (result ??= []).Add(p);
                lastBreak = p;
            }
        }

        return result;
    }

    private static bool IsDigraph(ReadOnlySpan<char> pair)
    {
        foreach (var d in Digraphs)
        {
            if (d.Length == 2 && pair.Equals(d, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
