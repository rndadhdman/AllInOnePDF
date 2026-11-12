namespace AllInOnePdf.Core.Importers.Pdf;

/// <summary>Style flags derived from a letter's font (name hints plus the font descriptor).</summary>
[Flags]
internal enum FontFlags
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Mono = 4,

    /// <summary>Drawn with a line through the glyphs' middle (strikethrough).</summary>
    Strike = 8,

    /// <summary>Drawn over a filled rectangle the height of the text line (highlight).</summary>
    Highlight = 16,
}

/// <summary>Vertical position of a run relative to its line.</summary>
internal enum ScriptPosition { Normal, Super, Sub }

/// <summary>One glyph with the bits of PdfPig's <c>Letter</c> the layout analysis needs.</summary>
internal readonly record struct GlyphInfo(string Value, FontFlags Flags, double X, double Right, double Size);

/// <summary>A word (run of glyphs without a visible gap) in PDF user space (y grows upwards).</summary>
internal sealed class PdfWord
{
    public required string Text { get; init; }
    /// <summary>The glyphs; decoration flags (strike, highlight) are added once the page's drawings are known.</summary>
    public required IReadOnlyList<GlyphInfo> Glyphs { get; set; }
    public double Left { get; init; }
    public double Right { get; init; }
    public double Bottom { get; init; }
    public double Top { get; init; }
    public double Baseline { get; init; }

    /// <summary>Average point size of the glyphs.</summary>
    public double Size { get; init; }

    /// <summary>Majority style of the glyphs.</summary>
    public FontFlags Flags { get; init; }

    /// <summary>External link target when the word sits inside a URI link annotation.</summary>
    public string? LinkUrl { get; set; }

    /// <summary>Set by the line assembler for superscript / subscript words (footnote marks, H~2~O).</summary>
    public ScriptPosition Script { get; set; }

    public double CenterX => (Left + Right) / 2;

    public double CenterY => (Bottom + Top) / 2;

    public override string ToString() => Text;
}

/// <summary>A run of words of one line separated from its neighbours by a wide gap (table cells, TOC page numbers).</summary>
internal sealed class LineSegment
{
    public List<PdfWord> Words { get; } = [];
    public double Left => Words[0].Left;
    public double Right => Words[^1].Right;
    public double Center => (Left + Right) / 2;
}

/// <summary>A visual text line on one page.</summary>
internal sealed class PdfLine
{
    public List<PdfWord> Words { get; } = [];
    public List<LineSegment> Segments { get; } = [];
    public int Page { get; set; }
    public double Left { get; private set; }
    public double Right { get; private set; }
    public double Top { get; private set; }
    public double Bottom { get; private set; }
    public double Baseline { get; private set; }

    /// <summary>Dominant (letter-count weighted) point size.</summary>
    public double Size { get; private set; }

    public double BoldFraction { get; private set; }
    public double ItalicFraction { get; private set; }
    public double MonoFraction { get; private set; }
    public int LetterCount { get; private set; }
    public string Text { get; private set; } = string.Empty;

    /// <summary>Index of the smallest background box (code block, callout) fully containing the line.</summary>
    public int? BoxId { get; set; }

    /// <summary>Index of a vertical accent bar at the left of the line (quotes, callouts).</summary>
    public int? BarId { get; set; }

    public bool Removed { get; set; }

    public bool IsMono => MonoFraction >= 0.9;

    public bool IsBold => BoldFraction >= 0.9;

    /// <summary>Recomputes geometry, dominant size, style fractions and text after the words changed.</summary>
    public void Update(double segmentGapFactor = 1.5)
    {
        Words.Sort((a, b) => a.Left.CompareTo(b.Left));
        Left = double.MaxValue;
        Right = double.MinValue;
        Top = double.MinValue;
        Bottom = double.MaxValue;
        var sizeWeights = new Dictionary<double, int>();
        var baselineWeights = new Dictionary<double, int>();
        int bold = 0, italic = 0, mono = 0, total = 0;
        foreach (var w in Words)
        {
            Left = Math.Min(Left, w.Left);
            Right = Math.Max(Right, w.Right);
            Top = Math.Max(Top, w.Top);
            Bottom = Math.Min(Bottom, w.Bottom);
            var key = Math.Round(w.Size * 2) / 2;
            sizeWeights[key] = sizeWeights.GetValueOrDefault(key) + w.Glyphs.Count;
            var bkey = Math.Round(w.Baseline, 1);
            baselineWeights[bkey] = baselineWeights.GetValueOrDefault(bkey) + w.Glyphs.Count;
            foreach (var g in w.Glyphs)
            {
                total++;
                if ((g.Flags & FontFlags.Bold) != 0)
                {
                    bold++;
                }

                if ((g.Flags & FontFlags.Italic) != 0)
                {
                    italic++;
                }

                if ((g.Flags & FontFlags.Mono) != 0)
                {
                    mono++;
                }
            }
        }

        LetterCount = total;
        Size = sizeWeights.Count == 0 ? 0 : sizeWeights.MaxBy(kv => kv.Value).Key;
        Baseline = baselineWeights.Count == 0 ? Bottom : baselineWeights.MaxBy(kv => kv.Value).Key;
        BoldFraction = total == 0 ? 0 : (double)bold / total;
        ItalicFraction = total == 0 ? 0 : (double)italic / total;
        MonoFraction = total == 0 ? 0 : (double)mono / total;

        foreach (var w in Words)
        {
            w.Script = ScriptPosition.Normal;
            if (Words.Count > 1 && w.Size < Size * 0.85)
            {
                if (w.Baseline > Baseline + Size * 0.18)
                {
                    w.Script = ScriptPosition.Super;
                }
                else if (w.Baseline < Baseline - Size * 0.12)
                {
                    w.Script = ScriptPosition.Sub;
                }
            }
        }

        Segments.Clear();
        LineSegment? current = null;
        PdfWord? previous = null;
        foreach (var w in Words)
        {
            if (current is null || previous is null || w.Left - previous.Right > Math.Max(Size * segmentGapFactor, 4))
            {
                current = new LineSegment();
                Segments.Add(current);
            }

            current.Words.Add(w);
            previous = w;
        }

        Text = TextJoiner.Join(Words);
    }

    public override string ToString() => $"p{Page} y={Baseline:F1} x={Left:F1} s={Size:F1}: {Text}";
}

/// <summary>Joins words of one line: a space where there is a visible gap, nothing for touching runs.</summary>
internal static class TextJoiner
{
    public static bool NeedsSpace(PdfWord previous, PdfWord next)
    {
        var gap = next.Left - previous.Right;
        return gap > Math.Max(previous.Size, next.Size) * 0.12;
    }

    public static string Join(IReadOnlyList<PdfWord> words)
    {
        if (words.Count == 0)
        {
            return string.Empty;
        }

        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < words.Count; i++)
        {
            if (i > 0 && NeedsSpace(words[i - 1], words[i]))
            {
                sb.Append(' ');
            }

            sb.Append(words[i].Text);
        }

        return sb.ToString();
    }
}

/// <summary>An axis-aligned rectangle in PDF user space.</summary>
internal readonly record struct Box(double Left, double Bottom, double Right, double Top)
{
    public double Width => Right - Left;
    public double Height => Top - Bottom;
    public double Area => Math.Max(0, Width) * Math.Max(0, Height);

    public bool Contains(double x, double y, double tolerance = 0) =>
        x >= Left - tolerance && x <= Right + tolerance && y >= Bottom - tolerance && y <= Top + tolerance;

    public bool ContainsLine(PdfLine line, double tolerance = 1.5) =>
        line.Left >= Left - tolerance && line.Right <= Right + tolerance && line.Bottom >= Bottom - tolerance && line.Top <= Top + tolerance;
}

/// <summary>A horizontal rule (thin filled rectangle, stroked line or rectangle edge).</summary>
internal readonly record struct HorizontalRule(double Left, double Right, double Y, double Thickness);

/// <summary>A vertical rule.</summary>
internal readonly record struct VerticalRule(double X, double Bottom, double Top);

/// <summary>A small square drawn as a task checkbox; <see cref="Checked"/> when it is filled.</summary>
internal sealed class CheckboxMark
{
    public required Box Bounds { get; init; }
    public bool Checked { get; set; }
}

/// <summary>An image placed on a page, already extracted to an encoded format.</summary>
internal sealed class PageImage
{
    public required Box Bounds { get; init; }
    public required byte[] Data { get; init; }
    public required string MediaType { get; init; }
    public int PixelWidth { get; init; }
    public int PixelHeight { get; init; }
}

/// <summary>Everything the layout analysis knows about one page.</summary>
internal sealed class PageContent
{
    public required int Number { get; init; }
    public required double Width { get; init; }
    public required double Height { get; init; }
    public List<PdfLine> Lines { get; } = [];
    public List<PageImage> Images { get; } = [];
    public List<HorizontalRule> HorizontalRules { get; } = [];
    public List<VerticalRule> VerticalRules { get; } = [];
    public List<Box> Boxes { get; } = [];

    /// <summary>Filled (non-white) rectangles about one text line high: highlight candidates.</summary>
    public List<Box> Fills { get; } = [];
    public List<Box> Bars { get; } = [];
    public List<CheckboxMark> Checkboxes { get; } = [];

    /// <summary>Areas of internal (GoTo) link annotations; used to recognise printed tables of contents.</summary>
    public List<Box> InternalLinks { get; } = [];

    public bool HasLetters { get; set; }

    /// <summary>True when the page carries images (including ones skipped as too small or as backgrounds).</summary>
    public bool HasImages { get; set; }

    public IEnumerable<PdfLine> ActiveLines => Lines.Where(l => !l.Removed);
}
