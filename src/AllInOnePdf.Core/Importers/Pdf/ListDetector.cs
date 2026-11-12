using System.Globalization;
using System.Text.RegularExpressions;

namespace AllInOnePdf.Core.Importers.Pdf;

/// <summary>A recognised list marker at the start of a line.</summary>
/// <param name="Ordered">Numbered / lettered item.</param>
/// <param name="Number">Item number for ordered markers (1 for "a", 4 for "iv").</param>
/// <param name="Checked">Task state for checkbox items, null otherwise.</param>
/// <param name="MarkerLeft">x of the marker; nesting is derived from it.</param>
/// <param name="TextLeft">x where the item text starts (continuation lines align here).</param>
/// <param name="SkipWords">Number of leading words that form the marker.</param>
internal sealed record ListMarker(bool Ordered, int Number, bool? Checked, double MarkerLeft, double TextLeft, int SkipWords);

/// <summary>
/// Recognises list items: bullet glyphs (• ◦ ▪ – — - * › …), ordered markers (1. 1) a) iv. (3)), checkbox glyphs
/// (☐ ☑ ✓) and vector-drawn checkboxes left of the line. Ambiguous markers (dashes, asterisks, numbers) need a
/// hanging indent: a gap wider than a space after the marker, or a following line aligned with the item text.
/// </summary>
internal static partial class ListDetector
{
    private static readonly HashSet<string> Bullets = new(StringComparer.Ordinal)
    {
        "•", "◦", "▪", "▫", "‣", "●", "○", "■", "◆", "◇", "♦", "►", "▸", "▹", "➢", "➤", "⁃", "∙", "·", "⦁", "⚫",
    };

    private static readonly HashSet<string> AmbiguousBullets = new(StringComparer.Ordinal) { "-", "–", "—", "*", "›", "»", "+", "~" };

    private static readonly HashSet<string> Unchecked = new(StringComparer.Ordinal) { "☐", "❑", "❒", "□", "[ ]", "[]" };

    private static readonly HashSet<string> CheckedGlyphs = new(StringComparer.Ordinal) { "☑", "☒", "✓", "✔", "✗", "✘", "[x]", "[X]" };

    public static ListMarker? TryParse(PdfLine line, PdfLine? next, PageContent? page)
    {
        if (line.Words.Count == 0)
        {
            return null;
        }

        // Vector checkbox drawn just left of the text.
        if (page is not null)
        {
            foreach (var box in page.Checkboxes)
            {
                var b = box.Bounds;
                var overlap = Math.Min(b.Top, line.Top) - Math.Max(b.Bottom, line.Bottom);
                if (b.Right <= line.Left + 1 && b.Right >= line.Left - line.Size * 3 && overlap > b.Height * 0.4)
                {
                    return new ListMarker(false, 1, box.Checked, b.Left, line.Left, 0);
                }
            }
        }

        if (line.Words.Count < 2)
        {
            return null;
        }

        var first = line.Words[0];
        var second = line.Words[1];
        var gap = second.Left - first.Right;
        var hanging = gap >= line.Size * 0.5
            || (next is not null && Math.Abs(next.Left - second.Left) <= 2 && line.Baseline - next.Baseline < line.Size * 2.5);
        var token = first.Text;

        if (Unchecked.Contains(token))
        {
            return new ListMarker(false, 1, false, first.Left, second.Left, 1);
        }

        if (CheckedGlyphs.Contains(token))
        {
            return new ListMarker(false, 1, true, first.Left, second.Left, 1);
        }

        // "[ ]" and "[x]" may arrive as two words.
        if (token == "[" && line.Words.Count >= 3 && line.Words[1].Text == "]")
        {
            return new ListMarker(false, 1, false, first.Left, line.Words[2].Left, 2);
        }

        if (Bullets.Contains(token))
        {
            // A following checkbox glyph turns a bullet item into a task item.
            if (line.Words.Count >= 3 && (Unchecked.Contains(second.Text) || CheckedGlyphs.Contains(second.Text)))
            {
                return new ListMarker(false, 1, CheckedGlyphs.Contains(second.Text), first.Left, line.Words[2].Left, 2);
            }

            return new ListMarker(false, 1, null, first.Left, second.Left, 1);
        }

        if (AmbiguousBullets.Contains(token) && hanging)
        {
            return new ListMarker(false, 1, null, first.Left, second.Left, 1);
        }

        var m = OrderedMarker().Match(token);
        if (m.Success && hanging)
        {
            var value = m.Groups["v"].Value;
            return new ListMarker(true, ParseOrdinal(value), null, first.Left, second.Left, 1);
        }

        return null;
    }

    internal static int ParseOrdinal(string value)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
        {
            return n;
        }

        if (value.Length == 1 && char.IsLetter(value[0]) && !"ivxIVX".Contains(value[0], StringComparison.Ordinal))
        {
            return char.ToLowerInvariant(value[0]) - 'a' + 1;
        }

        var roman = RomanToInt(value.ToLowerInvariant());
        if (roman > 0)
        {
            return roman;
        }

        return value.Length == 1 && char.IsLetter(value[0]) ? char.ToLowerInvariant(value[0]) - 'a' + 1 : 1;
    }

    private static int RomanToInt(string s)
    {
        var total = 0;
        var prev = 0;
        for (var i = s.Length - 1; i >= 0; i--)
        {
            var v = s[i] switch
            {
                'i' => 1,
                'v' => 5,
                'x' => 10,
                'l' => 50,
                'c' => 100,
                'd' => 500,
                'm' => 1000,
                _ => 0,
            };
            if (v == 0)
            {
                return 0;
            }

            total += v < prev ? -v : v;
            prev = Math.Max(prev, v);
        }

        return total;
    }

    [GeneratedRegex(@"^\(?(?<v>\d{1,3}|[a-zA-Z]|[ivxlcdm]{1,6}|[IVXLCDM]{1,6})[.)]$", RegexOptions.CultureInvariant)]
    private static partial Regex OrderedMarker();
}
