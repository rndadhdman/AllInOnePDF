using System.Text;
using AllInOnePdf.Layout.Infrastructure;
using SkiaSharp;

namespace AllInOnePdf.Layout.Text;

/// <summary>
/// A paragraph: spans, page numbers and inline elements laid out in lines (see docs/LAYOUT-ENGINE.md section 5).
/// Shaping and line breaking are cached per available width and page-number values, so measuring repeatedly is
/// cheap and always gives the lines <see cref="Draw"/> paints. Paginates by whole lines.
/// </summary>
public sealed class TextBlock : Element
{
    /// <summary>
    /// The style a block inherits from its surroundings (page default, <c>DefaultTextStyle</c> containers), fully
    /// resolved. Called once per pass from <see cref="Initialize"/>. Until the core's <c>TextStyleScope</c> is
    /// connected here this answers <see cref="TextStyle.Default"/>.
    /// Merge wiring (one line, replace the initialiser): <c>= static e =&gt; TextStyleScope.GetInherited(e.PageContext);</c>
    /// It is called after <c>base.Initialize</c>, while the enclosing scopes are active.
    /// </summary>
    internal static Func<Element, TextStyle> EnclosingStyleResolver { get; set; } = static e => AllInOnePdf.Layout.Elements.TextStyleScope.GetInherited(e.PageContext);

    /// <summary>
    /// How a line box larger than the font's ascent + descent is filled. True (what QuestPDF's native text style
    /// sets: <c>setHalfLeading(true)</c> with a height override): the extra space is split evenly above and below.
    /// False: ascent and descent are scaled in proportion (Skia's paragraph default).
    /// </summary>
    internal static bool HalfLeading => true;

    /// <summary>The ellipsis <c>ClampLines</c> uses by default (U+2026).</summary>
    internal const string DefaultEllipsis = "\u2026";

    private const string NoBreakSpace = "\u00A0";
    private const string ZeroWidthSpace = "\u200B";
    private const char ObjectReplacement = '\uFFFC';
    private const char SoftHyphen = '\u00AD';

    private readonly List<TextBlockItem> _items = [];
    private ResolvedTextStyle[]? _styles;
    private ResolvedTextStyle? _blockStyle;
    private Content? _content;
    private string? _contentKey;
    private LayoutResult? _layout;
    private int _currentLine;
    private bool _rendered;

    internal IReadOnlyList<TextBlockItem> Items => _items;

    /// <summary>The block's own default style (relative; applied over the enclosing style).</summary>
    internal TextStyle DefaultStyle { get; set; } = TextStyle.Inherit;

    internal TextAlignment Alignment { get; set; } = TextAlignment.Left;

    internal float FirstLineIndent { get; set; }

    internal int? MaxLines { get; set; }

    internal string Ellipsis { get; set; } = DefaultEllipsis;

    internal void Add(TextBlockItem item)
    {
        _items.Add(item);
        _styles = null;
        _content = null;
        _layout = null;
    }

    /// <inheritdoc />
    public override IEnumerable<Element> GetChildren()
    {
        foreach (var item in _items)
        {
            if (item is InlineElementItem inline)
            {
                yield return inline.Host;
            }
        }
    }

    /// <inheritdoc />
    public override void Initialize(PageContext pageContext, IDrawingCanvas canvas)
    {
        base.Initialize(pageContext, canvas);
        ResolveStyles();
    }

    /// <inheritdoc />
    public override void Reset()
    {
        base.Reset();
        _currentLine = 0;
        _rendered = false;
        _content = null;
        _contentKey = null;
        _layout = null;
    }

    /// <inheritdoc />
    public override SpacePlan Measure(Size availableSpace)
    {
        if (_items.Count == 0 || _rendered)
        {
            return SpacePlan.Empty();
        }

        if (availableSpace.Width < 0 || availableSpace.Height < 0)
        {
            return SpacePlan.Wrap();
        }

        if (IsWhitespaceOnly())
        {
            var lineHeight = WhitespaceLineHeight();
            return lineHeight <= availableSpace.Height + LineBreaker.Epsilon ? SpacePlan.FullRender(0, lineHeight) : SpacePlan.Wrap();
        }

        if (availableSpace.Width < LineBreaker.Epsilon || availableSpace.Height < LineBreaker.Epsilon)
        {
            return SpacePlan.Wrap();
        }

        var layout = GetLayout(availableSpace.Width);
        if (layout.Lines.Count == 0 || _currentLine >= layout.Lines.Count)
        {
            return SpacePlan.FullRender(0, 0);
        }

        if (availableSpace.Width < layout.TotalWidth - LineBreaker.Epsilon)
        {
            return SpacePlan.Wrap();
        }

        var (count, taken) = LinesThatFit(layout, availableSpace.Height);
        if (count == 0)
        {
            return SpacePlan.Wrap();
        }

        var width = Math.Min(layout.TotalWidth, availableSpace.Width);
        var height = Math.Min(taken, availableSpace.Height);
        return _currentLine + count < layout.Lines.Count ? SpacePlan.PartialRender(width, height) : SpacePlan.FullRender(width, height);
    }

    /// <inheritdoc />
    public override void Draw(Size availableSpace)
    {
        if (_items.Count == 0 || _rendered || availableSpace.Width < 0 || availableSpace.Height < 0)
        {
            return;
        }

        if (IsWhitespaceOnly())
        {
            _rendered = true;
            return;
        }

        if (availableSpace.Width < LineBreaker.Epsilon || availableSpace.Height < LineBreaker.Epsilon)
        {
            return;
        }

        var layout = GetLayout(availableSpace.Width);
        if (_currentLine >= layout.Lines.Count)
        {
            _rendered = true;
            return;
        }

        var (count, taken) = LinesThatFit(layout, availableSpace.Height);
        if (count == 0)
        {
            return;
        }

        var partial = _currentLine + count < layout.Lines.Count;
        var canvas = Canvas ?? FreeCanvas.Instance;
        if (partial)
        {
            canvas.Save();
            canvas.ClipRectangle(Position.Zero, new Size(availableSpace.Width, taken));
        }

        var top = layout.Lines[_currentLine].Top;
        for (var i = _currentLine; i < _currentLine + count; i++)
        {
            DrawLine(canvas, layout, layout.Lines[i], layout.Lines[i].Top - top, availableSpace.Width);
        }

        if (partial)
        {
            canvas.Restore();
        }

        _currentLine += count;
        if (_currentLine >= layout.Lines.Count)
        {
            _rendered = true;
        }
    }

    /// <summary>The text of every line at <paramref name="width"/> (hyphens and ellipses shown, inline elements as U+FFFC, hard breaks as "\n"). For tests and diagnostics.</summary>
    internal IReadOnlyList<string> GetLineTexts(float width)
    {
        var result = new List<string>();
        if (_items.Count == 0 || IsWhitespaceOnly())
        {
            return result;
        }

        var layout = GetLayout(width);
        foreach (var line in layout.Lines)
        {
            var builder = new StringBuilder();
            for (var u = line.Start; u < line.End; u++)
            {
                var info = layout.Content.Infos[u];
                if (info.Text is not null)
                {
                    builder.Append(info.Text, info.CharStart, info.CharEnd - info.CharStart);
                }
                else
                {
                    builder.Append(info.First);
                }
            }

            if (line.Hyphenated)
            {
                builder.Append('-');
            }

            if (line.Ellipsis is not null)
            {
                builder.Append(Ellipsis);
            }

            result.Add(builder.ToString());
        }

        return result;
    }

    // ------------------------------------------------------------------ styles

    private void ResolveStyles()
    {
        var enclosing = EnclosingStyleResolver(this) ?? TextStyle.Default;
        var block = DefaultStyle.Apply(enclosing);
        _blockStyle = new ResolvedTextStyle(block);
        _styles = new ResolvedTextStyle[_items.Count];
        for (var i = 0; i < _items.Count; i++)
        {
            _styles[i] = new ResolvedTextStyle(_items[i].Style.Apply(block));
        }

        _content = null;
        _contentKey = null;
        _layout = null;
    }

    private ResolvedTextStyle StyleOf(int item)
    {
        if (_styles is null || _styles.Length != _items.Count)
        {
            ResolveStyles();
        }

        return _styles![item];
    }

    private ResolvedTextStyle BlockStyle
    {
        get
        {
            if (_blockStyle is null)
            {
                ResolveStyles();
            }

            return _blockStyle!;
        }
    }

    // ------------------------------------------------------------------ whitespace-only blocks

    /// <summary>QuestPDF: a block whose spans are all whitespace (no elements, no page numbers) is one empty line.</summary>
    private bool IsWhitespaceOnly()
    {
        foreach (var item in _items)
        {
            if (item is not TextSpanItem span || !string.IsNullOrWhiteSpace(span.Text))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Height of a line holding a no-break space in the first span's style.</summary>
    private float WhitespaceLineHeight()
    {
        var style = StyleOf(0);
        var pieces = ShapePieces(NoBreakSpace, style, 0);
        var above = 0f;
        var below = 0f;
        foreach (var piece in pieces)
        {
            above = Math.Max(above, piece.Above);
            below = Math.Max(below, piece.Below);
        }

        return above + below;
    }

    // ------------------------------------------------------------------ content (shaping)

    /// <summary>A run of glyphs in one face and style, or an inline element, or a hard break (metrics only).</summary>
    private sealed class Piece
    {
        public required int Item { get; init; }

        public required ResolvedTextStyle Style { get; init; }

        public ShapedRun? Run { get; init; }

        public InlineElementItem? Element { get; init; }

        public Size ElementSize { get; init; }

        /// <summary>Extent above the baseline, line height and baseline shift applied.</summary>
        public float Above { get; init; }

        /// <summary>Extent below the baseline, line height and baseline shift applied.</summary>
        public float Below { get; init; }

        /// <summary>Scaled ascent without the baseline shift (for background boxes and decorations).</summary>
        public float Ascent { get; init; }

        public float Descent { get; init; }

        public FaceMetrics Metrics { get; init; }
    }

    private struct UnitInfo
    {
        public int Piece;
        public int GlyphStart;
        public int GlyphEnd;
        public string? Text;
        public int CharStart;
        public int CharEnd;
        public char First;
        public char Last;
        public char BeforeLast;
    }

    private sealed class Content
    {
        public List<Piece> Pieces { get; } = [];

        public List<BreakUnit> Units { get; } = [];

        public List<UnitInfo> Infos { get; } = [];

        /// <summary>Metrics added to every line when the block has no text at all (QuestPDF appends a zero-width space).</summary>
        public Piece? Strut { get; set; }
    }

    private string ContentKey(float width)
    {
        var builder = new StringBuilder();
        var hasElements = false;
        foreach (var item in _items)
        {
            if (item is PageNumberItem number)
            {
                builder.Append(number.GetText(PageContext)).Append('\u001F');
            }
            else if (item is InlineElementItem)
            {
                hasElements = true;
            }
        }

        if (hasElements)
        {
            builder.Append(width.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private Content GetContent(float width)
    {
        var key = ContentKey(width);
        if (_content is not null && _contentKey == key)
        {
            return _content;
        }

        _content = BuildContent(width);
        _contentKey = key;
        _layout = null;
        return _content;
    }

    private Content BuildContent(float width)
    {
        var content = new Content();
        var hasText = false;
        for (var item = 0; item < _items.Count; item++)
        {
            var style = StyleOf(item);
            switch (_items[item])
            {
                case TextSpanItem span:
                    hasText = true;
                    AddText(content, span.Text, style, item);
                    break;
                case PageNumberItem number:
                    hasText = true;
                    AddText(content, number.GetText(PageContext), style, item);
                    break;
                case InlineElementItem inline:
                    AddElement(content, inline, style, item, width);
                    break;
            }
        }

        if (!hasText)
        {
            var strut = ShapePieces(ZeroWidthSpace, BlockStyle, -1);
            content.Strut = strut.Count > 0 ? strut[0] : null;
        }

        MarkBreakOpportunities(content);
        return content;
    }

    private void AddElement(Content content, InlineElementItem inline, ResolvedTextStyle style, int item, float width)
    {
        var plan = inline.Host.Measure(new Size(width, Size.Infinity));
        var size = plan.HasContent ? plan.Size : Size.Zero;
        var piece = new Piece { Item = item, Style = style, Element = inline, ElementSize = size, Above = size.Height, Below = 0, Ascent = size.Height };
        content.Pieces.Add(piece);
        content.Units.Add(new BreakUnit { Width = size.Width, Kind = BreakUnitKind.Content });
        content.Infos.Add(new UnitInfo { Piece = content.Pieces.Count - 1, First = ObjectReplacement, Last = ObjectReplacement });
    }

    private void AddText(Content content, string text, ResolvedTextStyle style, int item)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var segments = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var s = 0; s < segments.Length; s++)
        {
            if (s > 0)
            {
                // The hard break carries the span's metrics (an empty line still has the span's height).
                var metrics = ShapePieces(ZeroWidthSpace, style, item);
                var breakPiece = metrics.Count > 0 ? metrics[0] : null;
                if (breakPiece is not null)
                {
                    content.Pieces.Add(new Piece
                    {
                        Item = item,
                        Style = style,
                        Above = breakPiece.Above,
                        Below = breakPiece.Below,
                        Ascent = breakPiece.Ascent,
                        Descent = breakPiece.Descent,
                        Metrics = breakPiece.Metrics,
                    });
                }

                content.Units.Add(new BreakUnit { Kind = BreakUnitKind.HardBreak });
                content.Infos.Add(new UnitInfo { Piece = breakPiece is null ? -1 : content.Pieces.Count - 1, First = '\n', Last = '\n' });
            }

            AddSegment(content, segments[s], style, item);
        }
    }

    /// <summary>Shapes one line-break-free segment and appends its clusters as break units.</summary>
    private void AddSegment(Content content, string raw, ResolvedTextStyle style, int item)
    {
        if (raw.Length == 0)
        {
            return;
        }

        // Tabs are four spaces; soft hyphens are removed before shaping and remembered as break points.
        var builder = new StringBuilder(raw.Length);
        var softHyphens = new HashSet<int>();
        foreach (var c in raw)
        {
            switch (c)
            {
                case '\t':
                    builder.Append("    ");
                    break;
                case '\r':
                    break;
                case SoftHyphen:
                    softHyphens.Add(builder.Length);
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }

        var text = builder.ToString();
        if (text.Length == 0)
        {
            return;
        }

        foreach (var piece in ShapePieces(text, style, item))
        {
            content.Pieces.Add(piece);
            var pieceIndex = content.Pieces.Count - 1;
            var glyphs = piece.Run!.Glyphs;
            var g = 0;
            while (g < glyphs.Length)
            {
                var cluster = glyphs[g].Cluster;
                var end = g + 1;
                while (end < glyphs.Length && glyphs[end].Cluster == cluster)
                {
                    end++;
                }

                var charStart = Math.Clamp(cluster, piece.Run.Start, piece.Run.End);
                var charEnd = end < glyphs.Length ? Math.Clamp(glyphs[end].Cluster, charStart, piece.Run.End) : piece.Run.End;
                if (charEnd <= charStart)
                {
                    charEnd = Math.Min(piece.Run.End, charStart + 1);
                }

                var width = 0f;
                for (var k = g; k < end; k++)
                {
                    width += glyphs[k].Advance;
                }

                var isSpace = true;
                for (var k = charStart; k < charEnd; k++)
                {
                    if (!LineBreaker.IsBreakingSpace(text[k]))
                    {
                        isSpace = false;
                        break;
                    }
                }

                var soft = softHyphens.Contains(charEnd) && charEnd < text.Length;
                content.Units.Add(new BreakUnit
                {
                    Width = width,
                    Kind = isSpace ? BreakUnitKind.Space : BreakUnitKind.Content,
                    SoftHyphenAfter = soft && !isSpace,
                    HyphenWidth = soft ? style.HyphenWidth : 0,
                    TrailingSpacing = style.LetterSpacing,
                });
                content.Infos.Add(new UnitInfo
                {
                    Piece = pieceIndex,
                    GlyphStart = g,
                    GlyphEnd = end,
                    Text = text,
                    CharStart = charStart,
                    CharEnd = charEnd,
                    First = text[charStart],
                    Last = text[charEnd - 1],
                    BeforeLast = charEnd - 2 >= 0 ? text[charEnd - 2] : '\0',
                });
                g = end;
            }
        }
    }

    private static void MarkBreakOpportunities(Content content)
    {
        var units = content.Units;
        for (var i = 0; i < units.Count; i++)
        {
            var unit = units[i];
            if (unit.Kind == BreakUnitKind.Space)
            {
                // A space offers a break unless closing punctuation or a solidus follows (UAX #14 rule 13 holds
                // across spaces: "ct); // a" cannot break before "//").
                unit.CanBreakAfter = i + 1 >= units.Count || units[i + 1].Kind != BreakUnitKind.Content
                    || content.Pieces[content.Infos[i + 1].Piece].Element is not null
                    || !LineBreaker.IsNoBreakAfterSpace(content.Infos[i + 1].First);
            }
            else if (unit.Kind == BreakUnitKind.Content && i + 1 < units.Count && units[i + 1].Kind == BreakUnitKind.Content)
            {
                var a = content.Infos[i];
                var b = content.Infos[i + 1];
                var pa = content.Pieces[a.Piece];
                var pb = content.Pieces[b.Piece];
                if (pa.Element is not null || pb.Element is not null)
                {
                    unit.CanBreakAfter = true;
                }
                else
                {
                    // Break-anywhere breaks before every character inside one span; a span's first character keeps
                    // the ordinary rules (Skia's paragraph, which QuestPDF uses, does the same per style block).
                    var wrapAnywhere = pb.Style.WrapAnywhere && pa.Item == pb.Item;
                    unit.CanBreakAfter = unit.SoftHyphenAfter || LineBreaker.CanBreakBetween(a.BeforeLast, a.Last, b.First, wrapAnywhere);
                }
            }

            units[i] = unit;
        }
    }

    /// <summary>Shapes text in one style into pieces (one per face run) with their line metrics.</summary>
    private static List<Piece> ShapePieces(string text, ResolvedTextStyle style, int item)
    {
        var pieces = new List<Piece>();
        foreach (var run in TextShaper.Shape(text, style.Shaping))
        {
            var metrics = run.Font.Face.Metrics;
            var size = style.FontSize;
            var ascent = metrics.Ascent * size;
            var descent = metrics.Descent * size;
            var lineHeight = style.LineHeight * size;
            var intrinsic = ascent + descent;
            if (intrinsic > 0)
            {
                if (HalfLeading)
                {
                    var extra = (lineHeight - intrinsic) / 2;
                    ascent += extra;
                    descent += extra;
                }
                else
                {
                    ascent *= lineHeight / intrinsic;
                    descent *= lineHeight / intrinsic;
                }
            }

            // The glyphs move by the baseline shift once, but Skia's paragraph (and so QuestPDF) counts the shift twice
            // in the line metrics (once in the run's corrected ascent/descent, once more when the line adds the run):
            // a superscript line in the reference corpus is taller by exactly 2 × 0.35 × size minus the run's ascent.
            var shift = style.BaselineShift;
            pieces.Add(new Piece
            {
                Item = item,
                Style = style,
                Run = run,
                Ascent = ascent,
                Descent = descent,
                Above = ascent - (2 * shift),
                Below = descent + (2 * shift),
                Metrics = metrics,
            });
        }

        return pieces;
    }

    // ------------------------------------------------------------------ layout (lines)

    private sealed class LineBox
    {
        public required int Start { get; init; }

        /// <summary>One past the last unit drawn (shorter than the break when an ellipsis replaced the tail).</summary>
        public required int End { get; init; }

        public bool HardBreak { get; init; }

        public bool Hyphenated { get; init; }

        public float Indent { get; init; }

        /// <summary>Visible width, indent, hyphen and ellipsis included.</summary>
        public float Width { get; init; }

        /// <summary>The letter spacing after the last glyph, included in <see cref="Width"/> for alignment only.</summary>
        public float TrailingSpacing { get; init; }

        public bool Justified { get; init; }

        public List<Piece>? Ellipsis { get; init; }

        public float Top { get; set; }

        public float Above { get; set; }

        public float Below { get; set; }

        public float Height => Above + Below;

        public float Bottom => Top + Height;
    }

    private sealed class LayoutResult
    {
        public required float Width { get; init; }

        public required Content Content { get; init; }

        public required List<LineBox> Lines { get; init; }

        /// <summary>The widest line.</summary>
        public float TotalWidth { get; init; }
    }

    private LayoutResult GetLayout(float width)
    {
        var content = GetContent(width);
        if (_layout is not null && ReferenceEquals(_layout.Content, content) && Math.Abs(_layout.Width - width) < LineBreaker.Epsilon)
        {
            return _layout;
        }

        _layout = BuildLayout(content, width);
        return _layout;
    }

    private LayoutResult BuildLayout(Content content, float width)
    {
        var spans = LineBreaker.Break(content.Units, width, FirstLineIndent);
        var lines = new List<LineBox>(spans.Count);
        var limit = spans.Count;
        var cut = false;
        if (MaxLines is { } max && spans.Count > max)
        {
            limit = Math.Max(0, max);
            cut = true;
        }

        for (var i = 0; i < limit; i++)
        {
            var span = spans[i];
            var indent = span.StartsParagraph ? FirstLineIndent : 0;
            if (cut && i == limit - 1 && !span.EndsWithHardBreak && !string.IsNullOrEmpty(Ellipsis))
            {
                lines.Add(EllipsisLine(content, span, indent, width));
                continue;
            }

            var isLast = i == spans.Count - 1;

            // Line breaking ignores the letter spacing after a line's last glyph, but Skia's paragraph keeps it in
            // the line's width: right-aligned and centred lines in the reference sit where it is counted.
            var trailing = 0f;
            for (var u = span.End - 1; u >= span.Start; u--)
            {
                if (content.Units[u].Kind == BreakUnitKind.Content)
                {
                    trailing = content.Units[u].TrailingSpacing;
                    break;
                }
            }

            lines.Add(new LineBox
            {
                Start = span.Start,
                End = span.End,
                HardBreak = span.EndsWithHardBreak,
                Hyphenated = span.Hyphenated,
                Indent = indent,
                Width = indent + span.Width + trailing,
                TrailingSpacing = trailing,
                Justified = Alignment == TextAlignment.Justify && !isLast && !span.EndsWithHardBreak && !(cut && i == limit - 1),
            });
        }

        // Vertical metrics: the tallest extents above and below the baseline among the line's pieces.
        var top = 0f;
        var totalWidth = 0f;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            float above = 0, below = 0;
            var any = false;
            void Include(Piece? piece)
            {
                if (piece is null)
                {
                    return;
                }

                above = Math.Max(above, piece.Above);
                below = Math.Max(below, piece.Below);
                any = true;
            }

            var last = -1;
            for (var u = line.Start; u < line.End; u++)
            {
                var p = content.Infos[u].Piece;
                if (p >= 0 && p != last)
                {
                    Include(content.Pieces[p]);
                    last = p;
                }
            }

            line.Ellipsis?.ForEach(Include);
            Include(content.Strut);
            if (!any)
            {
                // An empty line (after a trailing "\n", or between two): the metrics of the break before it.
                var previous = line.Start - 1;
                if (previous >= 0 && content.Infos[previous].Piece >= 0)
                {
                    Include(content.Pieces[content.Infos[previous].Piece]);
                }
                else if (content.Pieces.Count > 0)
                {
                    Include(content.Pieces[0]);
                }
            }

            line.Above = above;
            line.Below = below;
            line.Top = top;
            top += line.Height;
            totalWidth = Math.Max(totalWidth, line.Width - line.TrailingSpacing);
        }

        return new LayoutResult { Width = width, Content = content, Lines = lines, TotalWidth = totalWidth };
    }

    /// <summary>
    /// The last line of a clamped block: as many units from the line start as fit together with the ellipsis
    /// (cut mid-word, as Skia's paragraph does), followed by the ellipsis in the style of the last unit kept.
    /// </summary>
    private LineBox EllipsisLine(Content content, LineSpan span, float indent, float width)
    {
        var units = content.Units;
        var stop = span.Start;
        while (stop < units.Count && units[stop].Kind != BreakUnitKind.HardBreak)
        {
            stop++;
        }

        var styleUnit = Math.Max(span.Start, span.End - 1);
        while (styleUnit > span.Start && (content.Infos[styleUnit].Piece < 0 || content.Pieces[content.Infos[styleUnit].Piece].Element is not null))
        {
            styleUnit--;
        }

        var style = styleUnit < units.Count && content.Infos[styleUnit].Piece >= 0
            ? content.Pieces[content.Infos[styleUnit].Piece].Style
            : BlockStyle;
        var ellipsis = ShapePieces(Ellipsis, style, -1);
        var ellipsisWidth = ellipsis.Sum(p => p.Run!.Width) - style.LetterSpacing;

        var end = span.Start;
        var used = 0f;
        while (end < stop && indent + used + units[end].Width + ellipsisWidth <= width + LineBreaker.Epsilon)
        {
            used += units[end].Width;
            end++;
        }

        return new LineBox
        {
            Start = span.Start,
            End = end,
            Indent = indent,
            Width = indent + used + ellipsisWidth,
            Ellipsis = ellipsis,
        };
    }

    private (int Count, float Taken) LinesThatFit(LayoutResult layout, float availableHeight)
    {
        var lines = layout.Lines;
        if (_currentLine >= lines.Count)
        {
            return (0, 0);
        }

        var top = lines[_currentLine].Top;
        var count = 0;
        var taken = 0f;
        for (var i = _currentLine; i < lines.Count; i++)
        {
            var height = lines[i].Bottom - top;
            if (height >= availableHeight + LineBreaker.Epsilon)
            {
                break;
            }

            count++;
            taken = height;
        }

        return (count, taken);
    }

    // ------------------------------------------------------------------ drawing

    private void DrawLine(IDrawingCanvas canvas, LayoutResult layout, LineBox line, float top, float width)
    {
        var content = layout.Content;
        var units = content.Units;
        var baseline = top + line.Above;

        var offset = Alignment switch
        {
            TextAlignment.Center => (width - line.Width) / 2,
            TextAlignment.Right => width - line.Width,
            _ => 0,
        };

        // Last unit that is not a hanging space or the hard break.
        var lastContent = line.End - 1;
        while (lastContent >= line.Start && units[lastContent].Kind != BreakUnitKind.Content)
        {
            lastContent--;
        }

        // Justification, as Skia's paragraph (QuestPDF) does it: every whitespace patch between words (a run of
        // spaces) becomes the same width, whatever the font of its spaces, so the free width plus the spaces' own
        // width is shared equally (a monospaced space in inline code is no wider than a text space).
        var firstContent = line.Start;
        while (firstContent <= lastContent && units[firstContent].Kind != BreakUnitKind.Content)
        {
            firstContent++;
        }

        var patchWidth = -1f;
        if (line.Justified && lastContent > firstContent)
        {
            var patches = 0;
            var patchSpaces = 0f;
            for (var u = firstContent + 1; u < lastContent; u++)
            {
                if (units[u].Kind == BreakUnitKind.Space)
                {
                    patchSpaces += units[u].Width;
                    if (units[u - 1].Kind == BreakUnitKind.Content)
                    {
                        patches++;
                    }
                }
            }

            if (patches > 0)
            {
                // The hyphen of a soft-hyphen break hangs past the right edge: Skia's paragraph justifies the line
                // without it.
                var justifiedWidth = line.Hyphenated ? line.Width - units[lastContent].HyphenWidth : line.Width;
                if (width > justifiedWidth)
                {
                    patchWidth = (width - justifiedWidth + patchSpaces) / patches;
                }
            }
        }

        // Unit x positions (start) and drawn widths.
        var count = line.End - line.Start;
        var xs = new float[count + 1];
        var x = offset + line.Indent;
        for (var u = line.Start; u < line.End; u++)
        {
            var w = units[u].Width;
            if (patchWidth >= 0 && units[u].Kind == BreakUnitKind.Space && u > firstContent && u < lastContent)
            {
                // The patch's first space takes the whole patch width, the others none.
                w = units[u - 1].Kind == BreakUnitKind.Content ? patchWidth : 0;
            }

            xs[u - line.Start] = x;
            x += w;
        }

        xs[count] = x;
        var contentEnd = lastContent >= line.Start ? xs[lastContent - line.Start + 1] - units[lastContent].TrailingSpacing : offset + line.Indent;

        // Group consecutive units of one piece (only up to the last content unit for boxes and decorations).
        var groups = new List<(int Piece, int From, int To)>();
        for (var u = line.Start; u < line.End; u++)
        {
            var p = content.Infos[u].Piece;
            if (units[u].Kind == BreakUnitKind.HardBreak || p < 0)
            {
                continue;
            }

            if (groups.Count > 0 && groups[^1].Piece == p && groups[^1].To == u)
            {
                groups[^1] = (p, groups[^1].From, u + 1);
            }
            else
            {
                groups.Add((p, u, u + 1));
            }
        }

        float Left(int unit) => xs[unit - line.Start];

        float Right(int unit) => unit - 1 == lastContent ? contentEnd : xs[unit - line.Start];

        float VisibleRight(int to) => to - 1 >= lastContent ? contentEnd : Right(to);

        // 1. Backgrounds.
        foreach (var (p, from, to) in groups)
        {
            var piece = content.Pieces[p];
            if (piece.Style.Background is not { } bg || piece.Element is not null || from > lastContent)
            {
                continue;
            }

            var x0 = Left(from);
            var x1 = VisibleRight(to);
            var boxTop = baseline + piece.Style.BaselineShift - piece.Ascent;
            if (x1 > x0)
            {
                canvas.DrawRectangle(new Position(x0, boxTop), new Size(x1 - x0, piece.Ascent + piece.Descent), bg);
            }
        }

        // 2. Glyphs and inline elements.
        foreach (var (p, from, to) in groups)
        {
            var piece = content.Pieces[p];
            if (piece.Element is not null)
            {
                var size = piece.ElementSize;
                if (size.Width > 0 || size.Height > 0)
                {
                    var position = new Position(Left(from), baseline - size.Height);
                    canvas.Translate(position);
                    piece.Element.Host.Draw(size);
                    canvas.Translate(position.Reverse());
                }

                continue;
            }

            if (piece.Run is null)
            {
                continue;
            }

            var origin = Left(from);
            var glyphIds = new List<ushort>();
            var positions = new List<SKPoint>();
            var clusters = new List<int>();
            var source = new StringBuilder();
            // The spaces hanging after the last word are not written (Skia's paragraph leaves them out too).
            for (var u = from; u < Math.Min(to, lastContent + 1); u++)
            {
                var info = content.Infos[u];
                var gx = Left(u) - origin;

                // Spaces are drawn as glyphs too (justification only moves the next word): text extraction finds
                // word boundaries from them, as in the PDFs Skia's paragraph writes.
                var clusterStart = source.Length;
                if (info.Text is not null && info.CharEnd > info.CharStart)
                {
                    source.Append(info.Text, info.CharStart, info.CharEnd - info.CharStart);
                }

                for (var g = info.GlyphStart; g < info.GlyphEnd; g++)
                {
                    var glyph = piece.Run.Glyphs[g];
                    glyphIds.Add(glyph.GlyphId);
                    positions.Add(new SKPoint(gx + glyph.OffsetX, glyph.OffsetY));
                    clusters.Add(clusterStart + Math.Max(0, glyph.Cluster - info.CharStart));
                    gx += glyph.Advance;
                }
            }

            if (glyphIds.Count > 0)
            {
                var runWidth = Math.Max(0, VisibleRight(to) - origin);
                var run = new GlyphRun(piece.Run.Font.GetFont(piece.Run.FontSize), [.. glyphIds], [.. positions], runWidth, piece.Style.Color);
                // Skia's paragraph centres letter spacing on the glyph: half of it before, half after.
                canvas.DrawGlyphRun(
                    GlyphRunText.Attach(run, new GlyphRunText(source.ToString(), [.. clusters])),
                    new Position(origin + (piece.Style.LetterSpacing / 2), baseline + piece.Style.BaselineShift));
            }
        }

        // The hyphen at a soft-hyphen break, and the ellipsis of a clamped block.
        var tail = new List<Piece>();
        var tailX = contentEnd;
        if (line.Hyphenated && lastContent >= line.Start && content.Infos[lastContent].Piece >= 0)
        {
            var style = content.Pieces[content.Infos[lastContent].Piece].Style;
            tail.AddRange(ShapePieces("-", style, content.Pieces[content.Infos[lastContent].Piece].Item));
        }

        if (line.Ellipsis is not null)
        {
            tailX = line.End > line.Start ? xs[count] : offset + line.Indent;
            tail.AddRange(line.Ellipsis);
        }

        foreach (var piece in tail)
        {
            var run = piece.Run!;
            var gx = 0f;
            var ids = new ushort[run.Glyphs.Length];
            var pos = new SKPoint[run.Glyphs.Length];
            for (var g = 0; g < run.Glyphs.Length; g++)
            {
                ids[g] = run.Glyphs[g].GlyphId;
                pos[g] = new SKPoint(gx + run.Glyphs[g].OffsetX, run.Glyphs[g].OffsetY);
                gx += run.Glyphs[g].Advance;
            }

            var tailRun = new GlyphRun(run.Font.GetFont(run.FontSize), ids, pos, gx, piece.Style.Color);
            var whole = line.Ellipsis is not null && line.Ellipsis.Contains(piece) ? Ellipsis : "-";
            var tailText = whole.Substring(Math.Min(run.Start, whole.Length), Math.Max(0, Math.Min(run.End, whole.Length) - run.Start));
            var tailClusters = run.Glyphs.Select(g => Math.Clamp(g.Cluster - run.Start, 0, tailText.Length - 1)).ToArray();
            canvas.DrawGlyphRun(GlyphRunText.Attach(tailRun, new GlyphRunText(tailText, tailClusters)), new Position(tailX, baseline + piece.Style.BaselineShift));
            DrawDecorations(canvas, piece, tailX, tailX + gx - piece.Style.LetterSpacing, baseline);
            tailX += gx;
        }

        // 3. Underline and strikethrough.
        foreach (var (p, from, to) in groups)
        {
            var piece = content.Pieces[p];
            if (piece.Element is not null || piece.Run is null || from > lastContent)
            {
                continue;
            }

            DrawDecorations(canvas, piece, Left(from), VisibleRight(to), baseline);
        }

        // 4. Links, one rectangle per item fragment on the line.
        var linkStart = -1;
        for (var gi = 0; gi <= groups.Count; gi++)
        {
            var item = gi < groups.Count ? content.Pieces[groups[gi].Piece].Item : -2;
            var startItem = linkStart >= 0 ? content.Pieces[groups[linkStart].Piece].Item : -3;
            if (linkStart >= 0 && item == startItem)
            {
                continue;
            }

            if (linkStart >= 0 && startItem >= 0 && _items[startItem] is TextSpanItem { } link && (link.Url is not null || link.SectionName is not null))
            {
                var x0 = Left(groups[linkStart].From);
                var x1 = VisibleRight(groups[gi - 1].To);
                if (x1 > x0)
                {
                    var position = new Position(x0, top);
                    var size = new Size(x1 - x0, line.Height);
                    if (link.Url is not null)
                    {
                        canvas.DrawHyperlink(position, size, link.Url);
                    }
                    else
                    {
                        canvas.DrawSectionLink(position, size, link.SectionName!);
                    }
                }
            }

            linkStart = gi;
        }
    }

    /// <summary>
    /// Underline and strikethrough as Skia's paragraph draws them: thickness from the font (post/OS2 tables) or
    /// size / 14; underline at the font's underline position below the baseline (or one thickness when the font
    /// has none); strikethrough at the font's strikeout position (or half the x-height above the baseline).
    /// </summary>
    private static void DrawDecorations(IDrawingCanvas canvas, Piece piece, float x0, float x1, float baseline)
    {
        var style = piece.Style;
        if ((!style.Underline && !style.Strikethrough) || x1 <= x0)
        {
            return;
        }

        var size = style.FontSize;
        var m = piece.Metrics;
        var y = baseline + style.BaselineShift;
        var baseThickness = m.UnderlineThickness is > 0 ? m.UnderlineThickness.Value * size : size / 14f;
        if (style.Underline)
        {
            var position = m.UnderlinePosition is > 0 ? m.UnderlinePosition.Value * size : baseThickness;
            canvas.DrawLine(new Position(x0, y + position), new Position(x1, y + position), baseThickness, style.Color);
        }

        if (style.Strikethrough)
        {
            var thickness = m.StrikeoutThickness is > 0 ? m.StrikeoutThickness.Value * size : baseThickness;
            var position = m.StrikeoutPosition is { } s ? s * size : -m.XHeight * size / 2;
            canvas.DrawLine(new Position(x0, y + position), new Position(x1, y + position), thickness, style.Color);
        }
    }
}
