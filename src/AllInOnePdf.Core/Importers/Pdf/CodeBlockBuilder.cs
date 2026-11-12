using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AllInOnePdf.Core.Documents;

namespace AllInOnePdf.Core.Importers.Pdf;

/// <summary>
/// Rebuilds a code block from monospace lines: indentation and inner spacing from x positions divided by the
/// character advance, blank lines from baseline gaps, a printed line-number gutter stripped, and a small label
/// line above the block ("css", "python") taken as the language.
/// </summary>
internal static partial class CodeBlockBuilder
{
    public static bool IsCodeLine(PdfLine line) => line.IsMono && line.LetterCount > 0;

    public static CodeBlock Build(IReadOnlyList<PdfLine> source, PageContent? page = null)
    {
        var lines = source.ToList();
        string? language = null;

        // Language label: a single short word above the block, smaller or outside the block's background box.
        if (lines.Count >= 2 && lines[0].Words.Count == 1 && LabelWord().IsMatch(lines[0].Text))
        {
            var rest = lines.Skip(1).ToList();
            var restSize = rest.Select(l => l.Size).Order().ElementAt(rest.Count / 2);
            var outsideBox = lines[0].BoxId is null && rest.All(l => l.BoxId is not null);
            if (lines[0].Size < restSize - 0.4 || outsideBox)
            {
                language = lines[0].Text.Trim().ToLowerInvariant();
                lines.RemoveAt(0);
            }
        }

        var words = lines.Select(l => l.Words.ToList()).ToList();
        var numbered = StripLineNumbers(words, out var numberless);
        var charWidth = CharWidth(lines);

        var minLeft = words.Where(w => w.Count > 0).Select(w => w[0].Left).DefaultIfEmpty(0).Min();
        var pitch = Pitch(lines);
        var maxColumns = MaxColumns(lines, words, page, minLeft, charWidth);
        if (numberless.Count > 0)
        {
            // The gutter hides the padding; the widest line followed by a wrapped continuation is the line width.
            maxColumns = numberless.Where(i => words[i - 1].Count > 0)
                .Select(i => (int)Math.Round((words[i - 1][^1].Right - minLeft) / charWidth)).DefaultIfEmpty(0).Max();
        }
        var sb = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0 && WrapJoin(words, i, numberless, maxColumns, minLeft, charWidth, lines, pitch) is { } joiner)
            {
                // A long line the renderer wrapped (break-anywhere, no indent on the continuation): rejoin it.
                sb.Append(joiner);
                AppendWords(sb, words[i], charWidth);
                continue;
            }

            if (i > 0)
            {
                sb.Append('\n');
                if (!numbered && pitch > 0)
                {
                    var delta = lines[i - 1].Baseline - lines[i].Baseline;
                    var blanks = (int)Math.Round(delta / pitch) - 1;
                    for (var b = 0; b < Math.Clamp(blanks, 0, 3); b++)
                    {
                        sb.Append('\n');
                    }
                }
            }

            var lineWords = words[i];
            if (lineWords.Count == 0)
            {
                continue;
            }

            var indent = (int)Math.Round((lineWords[0].Left - minLeft) / charWidth);
            sb.Append(' ', Math.Clamp(indent, 0, 200));
            AppendWords(sb, lineWords, charWidth);
        }

        return new CodeBlock { Code = sb.ToString().TrimEnd(), Language = language };
    }

    /// <summary>The words of one line with their inner spacing reconstructed from the gaps.</summary>
    private static void AppendWords(StringBuilder sb, List<PdfWord> lineWords, double charWidth)
    {
        for (var w = 0; w < lineWords.Count; w++)
        {
            if (w > 0)
            {
                var gap = (int)Math.Round((lineWords[w].Left - lineWords[w - 1].Right) / charWidth);
                sb.Append(' ', Math.Clamp(gap, lineWords[w].Left - lineWords[w - 1].Right > charWidth * 0.3 ? 1 : 0, 200));
            }

            sb.Append(lineWords[w].Text);
        }
    }

    /// <summary>
    /// How many characters fit on a line of the block: from its background box (text area = box minus the padding
    /// seen on the left), or null when the block has no box (then wrapped lines cannot be told from long ones).
    /// </summary>
    private static int? MaxColumns(List<PdfLine> lines, List<List<PdfWord>> words, PageContent? page, double minLeft, double charWidth)
    {
        if (page is null || lines.Count < 2 || lines.Select(l => l.BoxId).Distinct().Count() != 1 || lines[^1].BoxId is not { } boxId || boxId >= page.Boxes.Count)
        {
            return null;
        }

        var box = page.Boxes[boxId];
        var padding = lines.Min(l => l.Left) - box.Left;
        if (padding < 0 || padding > box.Width / 3)
        {
            return null;
        }

        var limit = box.Right - padding;
        var columns = (int)Math.Floor((limit - minLeft) / charWidth + 0.05);
        return columns >= 8 ? columns : null;
    }

    /// <summary>
    /// When line <paramref name="i"/> continues the line above it (the renderer wraps long lines anywhere and starts
    /// the continuation at the left edge), the text to put between them: nothing for a break inside a word, a space
    /// when the line above stops one column short (the break was at a space). Null for a new source line.
    /// </summary>
    private static string? WrapJoin(List<List<PdfWord>> words, int i, HashSet<int> numberless, int? maxColumns, double minLeft, double charWidth, List<PdfLine> lines, double pitch)
    {
        var previous = words[i - 1];
        var current = words[i];
        if (previous.Count == 0 || current.Count == 0)
        {
            return null;
        }

        var delta = lines[i - 1].Baseline - lines[i].Baseline;
        if (pitch > 0 && delta > pitch * 1.3)
        {
            return null;
        }

        var startsAtEdge = Math.Abs(current[0].Left - minLeft) <= charWidth * 0.4;
        var previousColumns = (int)Math.Round((previous[^1].Right - minLeft) / charWidth);
        if (numberless.Contains(i))
        {
            // A numbered block: a line without its own number is always a continuation.
            return maxColumns is { } m && previousColumns == m - 1 ? " " : string.Empty;
        }

        if (maxColumns is not { } max || !startsAtEdge)
        {
            return null;
        }

        if (previousColumns >= max)
        {
            return string.Empty;
        }

        return previousColumns == max - 1 && !char.IsWhiteSpace(current[0].Text[0]) ? " " : null;
    }

    /// <summary>Median glyph advance of monospace glyphs; falls back to 0.6 em.</summary>
    internal static double CharWidth(IEnumerable<PdfLine> lines)
    {
        var advances = new List<double>();
        double size = 0;
        foreach (var line in lines)
        {
            size = Math.Max(size, line.Size);
            foreach (var word in line.Words)
            {
                foreach (var g in word.Glyphs)
                {
                    var adv = g.Right - g.X;
                    if (adv > 0.5 && g.Value.Length == 1)
                    {
                        advances.Add(adv);
                    }
                }
            }
        }

        if (advances.Count == 0)
        {
            return Math.Max(1, size * 0.6);
        }

        advances.Sort();
        return Math.Max(0.5, advances[advances.Count / 2]);
    }

    private static double Pitch(List<PdfLine> lines)
    {
        var deltas = new List<double>();
        for (var i = 1; i < lines.Count; i++)
        {
            var d = lines[i - 1].Baseline - lines[i].Baseline;
            if (d > 0.5)
            {
                deltas.Add(d);
            }
        }

        if (deltas.Count == 0)
        {
            return 0;
        }

        // The smallest frequent distance is one line; larger ones contain blank lines.
        var min = deltas.Min();
        var near = deltas.Where(d => d <= min * 1.25).ToList();
        return near.Average();
    }

    /// <summary>
    /// Removes a printed line-number gutter (1, 2, 3 ... in the first word of every line, right-aligned). Lines of a
    /// wrapped source line carry no number; they start in the text column and are returned in
    /// <paramref name="numberless"/>.
    /// </summary>
    private static bool StripLineNumbers(List<List<PdfWord>> words, out HashSet<int> numberless)
    {
        numberless = [];
        if (words.Count < 2)
        {
            return false;
        }

        int? expected = null;
        var rights = new List<double>();
        var candidates = new List<int>();
        for (var i = 0; i < words.Count; i++)
        {
            var line = words[i];
            if (line.Count == 0)
            {
                return false;
            }

            if (!int.TryParse(line[0].Text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || (expected is not null && n != expected))
            {
                if (i == 0)
                {
                    return false;
                }

                candidates.Add(i);
                continue;
            }

            expected = n + 1;
            rights.Add(line[0].Right);
        }

        if (rights.Count < 2 || rights.Max() - rights.Min() > 2)
        {
            return false;
        }

        var gutterRight = rights.Max();
        var textLefts = words.Where((l, i) => l.Count > 1 && !candidates.Contains(i)).Select(l => l[1].Left).ToList();
        if (textLefts.Count == 0 || textLefts.Min() - gutterRight < 3)
        {
            return false;
        }

        // A continuation starts in the text column, right of the gutter.
        var textLeft = textLefts.Min();
        if (candidates.Any(i => Math.Abs(words[i][0].Left - textLeft) > 1.5))
        {
            return false;
        }

        for (var i = 0; i < words.Count; i++)
        {
            if (!candidates.Contains(i))
            {
                words[i].RemoveAt(0);
            }
        }

        numberless = [.. candidates];
        return true;
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+#._-]{0,23}$")]
    private static partial Regex LabelWord();
}
