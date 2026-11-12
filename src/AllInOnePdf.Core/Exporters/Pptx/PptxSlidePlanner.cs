using System.Text.RegularExpressions;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Themes;

namespace AllInOnePdf.Core.Exporters.Pptx;

internal enum PptxParaKind { Plain, Bullet, Numbered, Subheading, Continuation }

/// <summary>One text paragraph to write on a slide.</summary>
internal sealed record PptxPara(
    IReadOnlyList<Inline> Inlines,
    PptxParaKind Kind,
    int Level = 0,
    int Number = 1,
    TextAlignment? Alignment = null,
    InlineStyle Extra = InlineStyle.None,
    string? Prefix = null,
    bool Muted = false,
    bool Monospace = false,
    int HeadingLevel = 2);

internal abstract class PptxSlideItem;

internal sealed class PptxTextItem : PptxSlideItem
{
    public List<PptxPara> Paragraphs { get; } = [];

    public double Lines { get; set; }
}

internal sealed class PptxCodeItem(string code, double lines) : PptxSlideItem
{
    public string Code { get; } = code;

    public double Lines { get; } = lines;
}

internal sealed class PptxTableItem(TableBlock table, double lines) : PptxSlideItem
{
    public TableBlock Table { get; } = table;

    public double Lines { get; } = lines;
}

internal sealed class PptxImageItem(ImageBlock image) : PptxSlideItem
{
    public ImageBlock Image { get; } = image;
}

internal sealed class PptxSlidePlan
{
    public required string Title { get; init; }

    public bool IsTitleSlide { get; init; }

    public List<string> SubtitleLines { get; } = [];

    public List<PptxSlideItem> Items { get; } = [];
}

/// <summary>
/// Splits the document into slides: sections at the split headings (or page breaks), then continuation slides
/// whenever the estimated body lines exceed the limit, a table is too long, or a picture needs its own slide.
/// </summary>
internal sealed partial class PptxSlidePlanner
{
    private const int MaxTableRows = 15;
    private const string Continued = " (cont.)";

    private readonly PptxExportSettings _settings;
    private readonly PptxGeometry _geometry;
    private readonly PdfTheme _theme;
    private readonly Func<ImageBlock, bool> _canPlace;

    public PptxSlidePlanner(PptxExportSettings settings, PptxGeometry geometry, PdfTheme theme, Func<ImageBlock, bool> canPlace)
    {
        _settings = settings;
        _geometry = geometry;
        _theme = theme;
        _canPlace = canPlace;
    }

    private sealed class Section(string title, bool fromHeading)
    {
        public string Title { get; } = title;

        public bool FromHeading { get; } = fromHeading;

        public List<Block> Blocks { get; } = [];
    }

    private abstract record Unit;

    private sealed record TextUnit(PptxPara Para, double Lines) : Unit;

    private sealed record CodeUnit(string Code) : Unit;

    private sealed record TableUnit(TableBlock Table) : Unit;

    private sealed record ImageUnit(ImageBlock Image) : Unit;

    public List<PptxSlidePlan> Plan(Document document, string overviewTitle)
    {
        var sections = new List<Section>();
        Section? current = null;
        foreach (var block in document.Blocks)
        {
            if (block is HeadingBlock h && IsSplit(h))
            {
                var text = Collapse(h.ToPlainText());
                current = new Section(text.Length > 0 ? text : "Untitled", true);
                sections.Add(current);
                continue;
            }

            if (block is PageBreakBlock)
            {
                if (_settings.Split == PptxSplitMode.PageBreak && current is not null && current.Blocks.Count > 0)
                {
                    current = new Section(BaseTitle(current.Title) + Continued, false);
                    sections.Add(current);
                }

                continue;
            }

            if (current is null)
            {
                current = new Section(overviewTitle, false);
                sections.Add(current);
            }

            current.Blocks.Add(block);
        }

        var slides = new List<PptxSlidePlan>();
        foreach (var section in sections)
        {
            var units = new List<Unit>();
            foreach (var block in section.Blocks)
            {
                Flatten(block, 0, units);
            }

            if (units.Count == 0 && !section.FromHeading)
            {
                continue;
            }

            Paginate(section.Title, units, slides);
        }

        return slides;
    }

    private bool IsSplit(HeadingBlock h) => _settings.Split switch
    {
        PptxSplitMode.H2 => h.Level <= 2,
        _ => h.Level == 1,
    };

    private static string BaseTitle(string title) =>
        title.EndsWith(Continued, StringComparison.Ordinal) ? title[..^Continued.Length] : title;

    private void Paginate(string title, List<Unit> units, List<PptxSlidePlan> slides)
    {
        var max = _settings.MaxLines;
        var slide = new PptxSlidePlan { Title = title };
        double used = 0;
        var full = false;

        void NewSlide()
        {
            slides.Add(slide);
            slide = new PptxSlidePlan { Title = BaseTitle(title) + Continued };
            used = 0;
            full = false;
        }

        bool NeedsNew(double lines) => full || (slide.Items.Count > 0 && used + lines > max + 0.01);

        foreach (var unit in units)
        {
            switch (unit)
            {
                case TextUnit t:
                    if (NeedsNew(t.Lines))
                    {
                        NewSlide();
                    }

                    if (slide.Items.Count == 0 || slide.Items[^1] is not PptxTextItem)
                    {
                        slide.Items.Add(new PptxTextItem());
                    }

                    var textItem = (PptxTextItem)slide.Items[^1];
                    textItem.Paragraphs.Add(t.Para);
                    textItem.Lines += t.Lines;
                    used += t.Lines;
                    break;

                case CodeUnit c:
                    foreach (var chunk in CodeChunks(c.Code, Math.Max(3, (int)((max - 0.6) / 0.8))))
                    {
                        var lines = CodeLines(chunk);
                        if (NeedsNew(lines))
                        {
                            NewSlide();
                        }

                        slide.Items.Add(new PptxCodeItem(chunk, lines));
                        used += lines;
                    }

                    break;

                case TableUnit tu:
                    foreach (var (chunk, lines) in TableChunks(tu.Table))
                    {
                        if (NeedsNew(lines))
                        {
                            NewSlide();
                        }

                        slide.Items.Add(new PptxTableItem(chunk, lines));
                        used += lines;
                    }

                    break;

                case ImageUnit iu:
                    if (slide.Items.Count > 0)
                    {
                        NewSlide();
                    }

                    slide.Items.Add(new PptxImageItem(iu.Image));
                    full = true;
                    break;
            }
        }

        slides.Add(slide);
    }

    private static double CodeLines(string code) => code.Split('\n').Length * 0.8 + 0.6;

    private static IEnumerable<string> CodeChunks(string code, int linesPerChunk)
    {
        var lines = code.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd('\n').Split('\n');
        for (var i = 0; i < lines.Length; i += linesPerChunk)
        {
            yield return string.Join('\n', lines.Skip(i).Take(linesPerChunk));
        }
    }

    /// <summary>Splits a long table into chunks of at most 15 rows (header rows repeated), never inside a row span when avoidable.</summary>
    private IEnumerable<(TableBlock Table, double Lines)> TableChunks(TableBlock table)
    {
        var columns = Math.Max(1, PptxTableGrid.Layout(table).Columns);
        var headerCount = 0;
        while (headerCount < table.Rows.Count && table.Rows[headerCount].IsHeader)
        {
            headerCount++;
        }

        var header = table.Rows.Take(headerCount).ToList();
        var body = table.Rows.Skip(headerCount).ToList();
        var rowLines = table.Rows.Select(r => RowLines(r, columns)).ToList();
        var headerLines = rowLines.Take(headerCount).Sum();
        var budget = Math.Max(2, _settings.MaxLines);
        var maxBody = Math.Max(1, MaxTableRows - headerCount);

        // Where may a chunk end? After row i when no row span from rows <= i reaches past it.
        var reach = new int[body.Count];
        var furthest = -1;
        for (var i = 0; i < body.Count; i++)
        {
            var spanEnd = body[i].Cells.Count == 0 ? i : i + body[i].Cells.Max(c => Math.Max(1, c.RowSpan)) - 1;
            furthest = Math.Max(furthest, spanEnd);
            reach[i] = furthest;
        }

        if (body.Count == 0)
        {
            yield return (table, Math.Max(1, headerLines));
            yield break;
        }

        var start = 0;
        while (start < body.Count)
        {
            var end = start;
            var lines = headerLines;
            var lastSafe = -1;
            while (end < body.Count && end - start < maxBody && (end == start || lines + rowLines[headerCount + end] <= budget))
            {
                lines += rowLines[headerCount + end];
                if (reach[end] <= end)
                {
                    lastSafe = end;
                }

                end++;
            }

            if (end < body.Count && lastSafe >= start)
            {
                end = lastSafe + 1;
            }

            var chunk = new TableBlock { Caption = table.Caption };
            chunk.Alignments.AddRange(table.Alignments);
            chunk.Rows.AddRange(header);
            var count = end - start;
            for (var r = start; r < end; r++)
            {
                var source = body[r];
                var row = new TableRow { IsHeader = false };
                foreach (var cell in source.Cells)
                {
                    var copy = new TableCell { ColumnSpan = cell.ColumnSpan, RowSpan = Math.Min(Math.Max(1, cell.RowSpan), end - r) };
                    copy.Blocks.AddRange(cell.Blocks);
                    row.Cells.Add(copy);
                }

                chunk.Rows.Add(row);
            }

            var chunkLines = headerLines + rowLines.Skip(headerCount + start).Take(count).Sum();
            yield return (chunk, chunkLines);
            start = end;
        }
    }

    private double RowLines(TableRow row, int columns)
    {
        var perColumn = Math.Max(8, _geometry.CharsPerLine / 0.8 / columns);
        var lines = 1;
        foreach (var cell in row.Cells)
        {
            var chars = cell.ToPlainText().Length;
            var width = perColumn * Math.Max(1, cell.ColumnSpan);
            lines = Math.Max(lines, (int)Math.Ceiling(chars / width));
        }

        return 0.35 + lines * 0.85;
    }

    private void Flatten(Block block, int level, List<Unit> units, InlineStyle extra = InlineStyle.None)
    {
        switch (block)
        {
            case HeadingBlock h:
                AddText(units, new PptxPara(h.Inlines, PptxParaKind.Subheading, Extra: extra, HeadingLevel: h.Level));
                break;
            case ParagraphBlock p:
                AddText(units, new PptxPara(p.Inlines, level > 0 ? PptxParaKind.Continuation : PptxParaKind.Plain, Math.Max(0, level - 1), Alignment: p.Alignment, Extra: extra));
                break;
            case ListBlock list:
                FlattenList(list, level, units, extra);
                break;
            case QuoteBlock q:
                foreach (var child in q.Blocks)
                {
                    Flatten(child, level, units, extra | InlineStyle.Italic);
                }

                if (!string.IsNullOrWhiteSpace(q.Attribution))
                {
                    AddText(units, new PptxPara([new TextInline("— " + q.Attribution.Trim())], PptxParaKind.Plain, Muted: true));
                }

                break;
            case CalloutBlock c:
                var label = (c.Title ?? _theme.Callouts.Kind(c.Kind).Label ?? c.Kind.ToString()).Trim().TrimEnd(':');
                var prefix = label + ": ";
                var first = true;
                foreach (var child in c.Blocks)
                {
                    if (first && child is ParagraphBlock cp)
                    {
                        AddText(units, new PptxPara(cp.Inlines, level > 0 ? PptxParaKind.Continuation : PptxParaKind.Plain, Math.Max(0, level - 1), Extra: extra, Prefix: prefix));
                    }
                    else
                    {
                        if (first)
                        {
                            AddText(units, new PptxPara([], PptxParaKind.Plain, Extra: extra, Prefix: prefix));
                        }

                        Flatten(child, level, units, extra);
                    }

                    first = false;
                }

                if (first)
                {
                    AddText(units, new PptxPara([], PptxParaKind.Plain, Prefix: label));
                }

                break;
            case CodeBlock code:
                if (level > 0)
                {
                    foreach (var line in code.Code.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n'))
                    {
                        AddText(units, new PptxPara([new TextInline(line)], PptxParaKind.Continuation, level - 1, Monospace: true));
                    }
                }
                else
                {
                    units.Add(new CodeUnit(code.Code));
                }

                break;
            case TableBlock table when table.Rows.Count > 0:
                if (level > 0)
                {
                    foreach (var row in table.Rows)
                    {
                        AddText(units, new PptxPara([new TextInline(string.Join(" | ", row.Cells.Select(c => c.ToPlainText())))], PptxParaKind.Continuation, level - 1));
                    }
                }
                else
                {
                    units.Add(new TableUnit(table));
                }

                break;
            case ImageBlock image:
                if (_canPlace(image))
                {
                    units.Add(new ImageUnit(image));
                }
                else if (!string.IsNullOrWhiteSpace(image.AltText))
                {
                    AddText(units, new PptxPara([new TextInline(image.AltText.Trim())], PptxParaKind.Plain, Extra: InlineStyle.Italic, Muted: true));
                }

                break;
            case HtmlBlock html:
                var text = Collapse(HtmlTag().Replace(html.Html, " "));
                if (text.Length > 0)
                {
                    AddText(units, new PptxPara([new TextInline(System.Net.WebUtility.HtmlDecode(text))], PptxParaKind.Plain));
                }

                break;
            case MathBlock math:
                AddText(units, new PptxPara([new TextInline(math.Latex.Trim())], PptxParaKind.Plain, Monospace: true));
                break;
        }
    }

    private void FlattenList(ListBlock list, int level, List<Unit> units, InlineStyle extra)
    {
        var kind = list.Ordered ? PptxParaKind.Numbered : PptxParaKind.Bullet;
        var listLevel = Math.Min(level, 8);
        for (var i = 0; i < list.Items.Count; i++)
        {
            var item = list.Items[i];
            var number = list.Start + i;
            var blocks = item.Blocks;
            var startIndex = 0;
            var check = item.Checked switch
            {
                true => "☑ ",
                false => "☐ ",
                _ => null,
            };

            if (blocks.Count > 0 && blocks[0] is InlineContainerBlock first)
            {
                AddText(units, new PptxPara(first.Inlines, kind, listLevel, number, Extra: extra, Prefix: check));
                startIndex = 1;
            }
            else
            {
                AddText(units, new PptxPara([], kind, listLevel, number, Extra: extra, Prefix: check));
            }

            for (var b = startIndex; b < blocks.Count; b++)
            {
                if (blocks[b] is ListBlock nested)
                {
                    FlattenList(nested, level + 1, units, extra);
                }
                else
                {
                    Flatten(blocks[b], listLevel + 1, units, extra);
                }
            }
        }
    }

    private void AddText(List<Unit> units, PptxPara para)
    {
        var text = (para.Prefix ?? string.Empty) + Inline.ToPlainText(para.Inlines);
        var breaks = CountHardBreaks(para.Inlines);
        var width = Math.Max(20, _geometry.CharsPerLine - 4 * (para.Level + (para.Kind is PptxParaKind.Plain or PptxParaKind.Subheading ? 0 : 1)));
        double lines = Math.Max(1, (int)Math.Ceiling(text.Trim().Length / width)) + breaks;
        if (para.Kind == PptxParaKind.Subheading)
        {
            lines *= 1.2;
        }

        units.Add(new TextUnit(para, lines));
    }

    private static int CountHardBreaks(IEnumerable<Inline> inlines)
    {
        var count = 0;
        foreach (var inline in inlines)
        {
            count += inline switch
            {
                LineBreakInline { IsHard: true } => 1,
                _ => CountHardBreaks(inline.Children),
            };
        }

        return count;
    }

    private static string Collapse(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex HtmlTag();
}
