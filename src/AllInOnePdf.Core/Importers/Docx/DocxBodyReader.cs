using System.Globalization;
using System.Text;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Exporters.Docx;
using AllInOnePdf.Core.Rendering;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Document = AllInOnePdf.Core.Documents.Document;
using Footnote = DocumentFormat.OpenXml.Wordprocessing.Footnote;
using ListItem = AllInOnePdf.Core.Documents.ListItem;
using ModelCell = AllInOnePdf.Core.Documents.TableCell;
using ModelFootnote = AllInOnePdf.Core.Documents.Footnote;
using ModelRow = AllInOnePdf.Core.Documents.TableRow;
using Table = DocumentFormat.OpenXml.Wordprocessing.Table;
using TableCell = DocumentFormat.OpenXml.Wordprocessing.TableCell;
using TableRow = DocumentFormat.OpenXml.Wordprocessing.TableRow;
using TextAlignment = AllInOnePdf.Core.Documents.TextAlignment;

namespace AllInOnePdf.Core.Importers.Docx;

/// <summary>
/// Walks the body (and footnotes, headers, footers) block by block: paragraph styles become headings, quotes,
/// code, callouts, captions and list items; tables keep spans and header rows; content controls written by the Word
/// exporter restore exact quote/callout/code grouping; images become image blocks or inline images by size.
/// </summary>
internal sealed class DocxBodyReader
{
    private static readonly char[] BulletGlyphs = ['•', '◦', '▪', '●', '○', '■', '□', '–', '·', '‣', '⁃', '➢', '►', '✓', '-', '*'];

    private readonly MainDocumentPart _main;
    private readonly Document _doc;
    private readonly DocxImportSettings _settings;
    private readonly ImportContext _context;
    private readonly CancellationToken _ct;
    private readonly DocxStyleResolver _styles;
    private readonly DocxNumberingResolver _numbering;
    private readonly DocxInlineReader _inlines;
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HeadingBlock> _bookmarkHeadings = new(StringComparer.Ordinal);
    private readonly HashSet<HeadingBlock> _titleHeadings = [];
    private readonly List<(bool Endnote, int Id, string Label)> _notes = [];
    private readonly double _smallImagePt;
    private int _paragraphs;
    private int _totalParagraphs;

    public DocxBodyReader(WordprocessingDocument package, Document doc, DocxImportSettings settings, ImportContext context, CancellationToken ct)
    {
        _main =package.MainDocumentPart ?? throw new InvalidDataException("The Word document has no main document part; it may be damaged.");
        _doc = doc;
        _settings = settings;
        _context = context;
        _ct = ct;
        _styles = new DocxStyleResolver(_main);
        _numbering = new DocxNumberingResolver(_main);
        var images = new DocxImageStore(doc, settings, Warn);
        _inlines = new DocxInlineReader(_styles, settings, images, RegisterNote);
        _smallImagePt = 1.5 * _styles.DefaultFontSizePt * 1.2;
    }

    public void Read()
    {
        var body = _main.Document?.Body ?? throw new InvalidDataException("The Word document has no body; it may be damaged.");
        _totalParagraphs = Math.Max(1, body.Descendants<Paragraph>().Count());

        var builder = new Builder(Mode.Normal);
        ReadContainer(body.ChildElements, builder, _main);
        Finish(builder);
        _doc.Blocks.AddRange(builder.Blocks);

        if (_settings.IncludeHeadersFooters)
        {
            ReadHeadersFooters(body);
        }

        ReadNotes();
        RemoveDuplicateTitleHeadings();
        AssignHeadingIds();
        ResolveInternalLinks();
    }

    // ----------------------------------------------------------------- builder state

    private enum Mode { Normal, Quote, Callout, Cell }

    private sealed class ListFrame
    {
        public required ListBlock List { get; init; }
        public ListItem? Item { get; set; }
        public int NumId { get; init; }
        public int IndentTwips { get; init; }
    }

    private sealed class Builder(Mode mode)
    {
        public Mode Mode { get; } = mode;
        public List<Block> Blocks { get; } = [];
        public List<ListFrame> Lists { get; } = [];
        public CodeBlock? OpenCode { get; set; }
        public QuoteBlock? OpenQuote { get; set; }
        public CalloutBlock? OpenCallout { get; set; }
        public ParagraphBlock? PendingCaption { get; set; }
        public CalloutBlock? Callout { get; init; }
        public string? Attribution { get; set; }
    }

    private void Add(Builder b, Block block)
    {
        b.OpenCode = null;
        b.OpenQuote = null;
        b.OpenCallout = null;
        b.Lists.Clear();

        if (b.PendingCaption is { } caption)
        {
            b.PendingCaption = null;
            var text = caption.ToPlainText().Trim();
            if (block is TableBlock { Caption: null } next)
            {
                next.Caption = text;
            }
            else if (b.Blocks.Count > 0 && b.Blocks[^1] is TableBlock { Caption: null } previous)
            {
                previous.Caption = text;
            }
            else
            {
                b.Blocks.Add(caption);
            }
        }

        b.Blocks.Add(block);
    }

    private void Finish(Builder b)
    {
        if (b.PendingCaption is { } caption)
        {
            b.PendingCaption = null;
            if (b.Blocks.Count > 0 && b.Blocks[^1] is TableBlock { Caption: null } previous)
            {
                previous.Caption = caption.ToPlainText().Trim();
            }
            else
            {
                b.Blocks.Add(caption);
            }
        }

        b.Lists.Clear();
        b.OpenCode = null;
    }

    // ----------------------------------------------------------------- containers

    private void ReadContainer(IEnumerable<OpenXmlElement> elements, Builder b, OpenXmlPart owner)
    {
        foreach (var element in elements)
        {
            _ct.ThrowIfCancellationRequested();
            switch (element)
            {
                case Paragraph p:
                    ReadParagraph(p, b, owner);
                    break;
                case Table t:
                    ReadTable(t, b, owner);
                    break;
                case SdtBlock sdt:
                    ReadSdt(sdt, b, owner);
                    break;
                case CustomXmlBlock or SdtContentBlock:
                    ReadContainer(element.ChildElements, b, owner);
                    break;
                case AltChunk:
                    Warn("Embedded documents (altChunk) are not imported.");
                    break;
            }
        }
    }

    private void ReadSdt(SdtBlock sdt, Builder b, OpenXmlPart owner)
    {
        var props = sdt.SdtProperties;
        var tag = props?.GetFirstChild<Tag>()?.Val?.Value ?? string.Empty;
        var gallery = props?.Descendants<DocPartGallery>().FirstOrDefault()?.Val?.Value ?? string.Empty;
        var content = sdt.SdtContentBlock;
        if (content is null)
        {
            return;
        }

        if (gallery.Contains("Table of Contents", StringComparison.OrdinalIgnoreCase) && _settings.SkipToc)
        {
            return;
        }

        if (tag == DocxStyleIds.CoverTag)
        {
            foreach (var p in content.Descendants<Paragraph>())
            {
                var role = _styles.Role(p.ParagraphProperties?.ParagraphStyleId?.Val?.Value).Role;
                var text = p.InnerText.Trim();
                if (text.Length == 0)
                {
                    continue;
                }

                if (role == ParaRole.Title)
                {
                    _doc.Metadata.Title = text;
                }
                else if (role == ParaRole.Subtitle)
                {
                    _doc.Metadata.Subtitle = text;
                }
            }

            return;
        }

        if (tag == DocxStyleIds.QuoteTag)
        {
            var child = new Builder(Mode.Quote);
            ReadContainer(content.ChildElements, child, owner);
            Finish(child);
            var quote = new QuoteBlock { Attribution = child.Attribution };
            quote.Blocks.AddRange(child.Blocks);
            Add(b, quote);
            return;
        }

        if (tag.StartsWith(DocxStyleIds.CalloutTagPrefix, StringComparison.Ordinal))
        {
            var kind = Enum.TryParse<CalloutKind>(tag[DocxStyleIds.CalloutTagPrefix.Length..], ignoreCase: true, out var k) ? k : CalloutKind.Note;
            var callout = new CalloutBlock { Kind = kind };
            var child = new Builder(Mode.Callout) { Callout = callout };
            ReadContainer(content.ChildElements, child, owner);
            Finish(child);
            callout.Blocks.AddRange(child.Blocks);
            Add(b, callout);
            return;
        }

        if (tag.StartsWith(DocxStyleIds.CodeTagPrefix, StringComparison.Ordinal))
        {
            var language = tag[DocxStyleIds.CodeTagPrefix.Length..];
            string? label = null;
            var lines = new List<string>();
            int? indent = null;
            foreach (var p in content.Descendants<Paragraph>())
            {
                var styleId = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
                var result = _inlines.Read(p, owner, styleId);
                CountParagraph();
                if (_styles.Role(styleId).Role == ParaRole.CodeLabel)
                {
                    label = Inline.ToPlainText(result.Inlines).Trim();
                    continue;
                }

                indent ??= DirectIndent(p);
                lines.Add(CodeText(result.Inlines));
            }

            var code = new CodeBlock
            {
                Code = string.Join("\n", lines),
                Language = language.Length == 0 ? null : language,
                Caption = string.IsNullOrWhiteSpace(label) || string.Equals(label, language, StringComparison.OrdinalIgnoreCase) ? null : label,
            };
            AddOrAttach(b, code, indent ?? 0);
            return;
        }

        ReadContainer(content.ChildElements, b, owner);
    }

    // ----------------------------------------------------------------- paragraphs

    private void ReadParagraph(Paragraph p, Builder b, OpenXmlPart owner)
    {
        CountParagraph();
        var pPr = p.ParagraphProperties;
        var styleId = pPr?.ParagraphStyleId?.Val?.Value;
        var role = _styles.Role(styleId);
        if (role.Role == ParaRole.Normal && pPr?.OutlineLevel?.Val?.Value is { } outline && outline is >= 0 and <= 5)
        {
            role = new StyleRole(ParaRole.Heading, outline + 1);
        }

        var result = _inlines.Read(p, owner, styleId);
        if (role.Role is ParaRole.TocEntry or ParaRole.TocHeading && _settings.SkipToc)
        {
            return;
        }

        if (pPr?.PageBreakBefore is { } pbb && (pbb.Val is null || pbb.Val.Value) && _settings.PageBreakMarkers)
        {
            AddPageBreak(b);
        }

        var segments = new List<List<Inline>> { new() };
        foreach (var inline in result.Inlines)
        {
            if (inline is PageBreakMarker)
            {
                segments.Add([]);
            }
            else
            {
                segments[^1].Add(inline);
            }
        }

        for (var i = 0; i < segments.Count; i++)
        {
            if (i > 0 && _settings.PageBreakMarkers)
            {
                AddPageBreak(b);
            }

            ReadSegment(p, segments[i], role, result, b, owner);
        }

        var sect = pPr?.SectionProperties;
        if (sect is not null && _settings.PageBreakMarkers && sect.GetFirstChild<SectionType>()?.Val?.Value != SectionMarkValues.Continuous)
        {
            AddPageBreak(b);
        }
    }

    private void AddPageBreak(Builder b)
    {
        if (b.Mode == Mode.Normal && (b.Blocks.Count > 0 || b.PendingCaption is not null) && b.Blocks.LastOrDefault() is not PageBreakBlock)
        {
            Add(b, new PageBreakBlock());
        }
    }

    private void ReadSegment(Paragraph p, List<Inline> inlines, StyleRole role, InlineResult result, Builder b, OpenXmlPart owner)
    {
        // Large pictures become image blocks; text around them stays in paragraphs of the same role.
        if (inlines.Any(i => i is DrawingInline d && !IsSmall(d)))
        {
            var part = new List<Inline>();
            foreach (var inline in inlines)
            {
                if (inline is DrawingInline d && !IsSmall(d))
                {
                    if (part.Count > 0)
                    {
                        ReadText(p, part, role, result, b);
                        part = [];
                    }

                    AddOrAttach(b, new ImageBlock
                    {
                        Source = d.Source,
                        AltText = d.AltText,
                        Title = d.Title,
                        PixelWidth = d.WidthPt > 0 ? (int)Math.Round(d.WidthPt / 0.75) : null,
                        PixelHeight = d.HeightPt > 0 ? (int)Math.Round(d.HeightPt / 0.75) : null,
                    }, DirectIndent(p));
                }
                else
                {
                    part.Add(inline);
                }
            }

            if (part.Count > 0)
            {
                ReadText(p, part, role, result, b);
            }

            return;
        }

        ReadText(p, inlines, role, result, b);
    }

    private bool IsSmall(DrawingInline d) => d.HeightPt > 0 && d.HeightPt < _smallImagePt;

    private void ReadText(Paragraph p, List<Inline> raw, StyleRole role, InlineResult result, Builder b)
    {
        var pPr = p.ParagraphProperties;
        var styleId = pPr?.ParagraphStyleId?.Val?.Value;

        // Code paragraphs keep tabs and leading spaces. A paragraph set entirely in a monospace font counts as code only
        // when that sets it apart from the body: in a document whose own paragraph style is monospace (a typewriter-style
        // theme, a plain-text export), every paragraph would otherwise come back as code.
        var bodyIsMono = DocxStyleResolver.IsMonospace(_styles.ParagraphFont(styleId));
        var isCode = (role.Role == ParaRole.Code && _settings.DetectCode)
            || (_settings.DetectCode && !bodyIsMono && role.Role is ParaRole.Normal or ParaRole.ListContinue && result.AllMonospace && !raw.Any(i => i is DrawingInline) && NumberingOf(pPr, styleId, role) is null);
        if (isCode)
        {
            var text = CodeText(raw);
            if (b.OpenCode is { } open)
            {
                open.Code += "\n" + text;
            }
            else if (text.Trim().Length > 0)
            {
                var code = new CodeBlock { Code = text };
                AddOrAttach(b, code, DirectIndent(p));
                b.OpenCode = code;
            }

            return;
        }

        var inlines = Clean(raw);
        var plain = Inline.ToPlainText(inlines).Trim();
        var empty = inlines.Count == 0 || (plain.Length == 0 && !Has<ImageInline>(inlines) && !Has<FootnoteReferenceInline>(inlines));

        if (result.CalloutLabel is { } label && plain.Length == 0)
        {
            var target = b.Mode == Mode.Callout ? b.Callout : role.Role == ParaRole.Callout ? OpenCallout(b, role.Kind) : null;
            if (target is not null)
            {
                label = label.Trim();
                target.Title = string.Equals(label, target.Kind.ToString(), StringComparison.OrdinalIgnoreCase) ? null : label;
                return;
            }

            inlines = [new TextInline(label)];
            plain = label;
            empty = false;
        }

        if (role.Role == ParaRole.HorizontalRule || (empty && pPr?.ParagraphBorders?.BottomBorder is { } bottom && bottom.Val?.Value != BorderValues.None && b.Mode != Mode.Cell))
        {
            if (empty)
            {
                Add(b, new ThematicBreakBlock());
                return;
            }
        }

        // Lists: numbering on the paragraph or its style.
        var numbering = NumberingOf(pPr, styleId, role);
        if (numbering is { } num && _numbering.Get(num.NumId, num.Level) is { } info)
        {
            var item = AddListItem(b, num.Level, num.NumId, info.Ordered, info.Start, info.IndentTwips);
            if (!empty)
            {
                AddItemParagraph(item, inlines);
            }

            return;
        }

        if (empty)
        {
            return;
        }

        if (role.Role is ParaRole.ListParagraph or ParaRole.Normal && StripBullet(inlines) is { } bulleted)
        {
            var indent = DirectIndent(p);
            var level = 0;
            if (b.Lists.Count > 0 && indent > b.Lists[^1].IndentTwips + 100)
            {
                level = b.Lists.Count;
            }
            else if (MatchLevel(b, indent) is { } matched)
            {
                level = matched;
            }

            var item = AddListItem(b, level, -1, false, 1, indent);
            AddItemParagraph(item, bulleted);
            return;
        }

        switch (role.Role)
        {
            case ParaRole.Heading:
                var heading = new HeadingBlock { Level = role.Level };
                heading.Inlines.AddRange(inlines);
                foreach (var name in result.Bookmarks)
                {
                    _bookmarkHeadings.TryAdd(name, heading);
                }

                Add(b, heading);
                return;
            case ParaRole.Title:
                _doc.Metadata.Title ??= plain;
                var titleHeading = new HeadingBlock { Level = 1 };
                titleHeading.Inlines.AddRange(inlines);
                foreach (var name in result.Bookmarks)
                {
                    _bookmarkHeadings.TryAdd(name, titleHeading);
                }

                _titleHeadings.Add(titleHeading);
                Add(b, titleHeading);
                return;
            case ParaRole.Subtitle:
                _doc.Metadata.Subtitle ??= plain;
                return;
            case ParaRole.CoverMeta:
                return;
            case ParaRole.Quote when b.Mode != Mode.Quote:
                var quote = b.OpenQuote;
                if (quote is null)
                {
                    quote = new QuoteBlock();
                    Add(b, quote);
                    b.OpenQuote = quote;
                }

                quote.Blocks.Add(Para(inlines, null));
                return;
            case ParaRole.QuoteAttribution:
                var attribution = plain.TrimStart('—', '–', '-', ' ');
                if (b.Mode == Mode.Quote)
                {
                    b.Attribution = attribution;
                    return;
                }

                if (b.Blocks.LastOrDefault() is QuoteBlock lastQuote && lastQuote.Attribution is null)
                {
                    lastQuote.Attribution = attribution;
                    return;
                }

                break;
            case ParaRole.Callout when b.Mode != Mode.Callout:
                OpenCallout(b, role.Kind).Blocks.Add(Para(inlines, null));
                return;
            case ParaRole.Caption:
                if (b.Blocks.LastOrDefault() is ImageBlock image && b.PendingCaption is null && b.Lists.Count == 0)
                {
                    image.Title = plain;
                    return;
                }

                if (b.PendingCaption is null)
                {
                    b.OpenCode = null;
                    b.OpenQuote = null;
                    b.OpenCallout = null;
                    b.Lists.Clear();
                    b.PendingCaption = Para(inlines, null);
                    return;
                }

                break;
            case ParaRole.MathBlock:
                Add(b, new MathBlock { Latex = CodeText(raw).Trim('\n') });
                return;
            case ParaRole.HtmlSource:
                Add(b, new HtmlBlock { Html = CodeText(raw).Trim('\n') });
                return;
        }

        if (result.HasOnlyMath && b.Mode != Mode.Cell)
        {
            Add(b, new MathBlock { Latex = string.Concat(inlines.OfType<MathInline>().Select(m => m.Latex)) });
            return;
        }

        // A paragraph indented like an open list item continues that item.
        if (b.Lists.Count > 0 && (role.Role == ParaRole.ListContinue || DirectIndent(p) > 0) && MatchLevel(b, DirectIndent(p)) is { } lvl)
        {
            while (b.Lists.Count > lvl + 1)
            {
                b.Lists.RemoveAt(b.Lists.Count - 1);
            }

            var frame = b.Lists[lvl];
            frame.Item ??= NewItem(frame.List);
            frame.Item.Blocks.Add(Para(inlines, Alignment(pPr, b.Mode)));
            b.OpenCode = null;
            return;
        }

        Add(b, Para(inlines, Alignment(pPr, b.Mode)));
    }

    private CalloutBlock OpenCallout(Builder b, CalloutKind kind)
    {
        if (b.OpenCallout is { } open && open.Kind == kind)
        {
            return open;
        }

        var callout = new CalloutBlock { Kind = kind };
        Add(b, callout);
        b.OpenCallout = callout;
        return callout;
    }

    private (int NumId, int Level)? NumberingOf(ParagraphProperties? pPr, string? styleId, StyleRole role)
    {
        if (role.Role is ParaRole.Heading or ParaRole.Title or ParaRole.TocEntry or ParaRole.Caption)
        {
            return null;
        }

        var direct = pPr?.NumberingProperties;
        var numId = direct?.NumberingId?.Val?.Value;
        var level = direct?.NumberingLevelReference?.Val?.Value;
        if (numId is null || level is null)
        {
            var inherited = _styles.StyleNumbering(styleId);
            numId ??= inherited?.NumId;
            level ??= inherited?.Level;
        }

        return numId is { } id && id != 0 ? (id, Math.Clamp(level ?? 0, 0, 8)) : null;
    }

    private static ParagraphBlock Para(List<Inline> inlines, TextAlignment? alignment)
    {
        var p = new ParagraphBlock { Alignment = alignment };
        p.Inlines.AddRange(inlines);
        return p;
    }

    private static TextAlignment? Alignment(ParagraphProperties? pPr, Mode mode)
    {
        var jc = pPr?.Justification?.Val?.Value;
        if (jc is null)
        {
            return null;
        }

        if (jc == JustificationValues.Center)
        {
            return TextAlignment.Center;
        }

        if (jc == JustificationValues.Right || jc == JustificationValues.End)
        {
            return TextAlignment.Right;
        }

        if (jc == JustificationValues.Both || jc == JustificationValues.Distribute)
        {
            return TextAlignment.Justify;
        }

        return mode == Mode.Cell && (jc == JustificationValues.Left || jc == JustificationValues.Start) ? TextAlignment.Left : null;
    }

    private static int DirectIndent(Paragraph p)
    {
        var ind = p.ParagraphProperties?.Indentation;
        return ind is null ? 0 : DocxNumberingResolver.ParseTwips(ind.Left?.Value ?? ind.Start?.Value);
    }

    // ----------------------------------------------------------------- lists

    private ListItem AddListItem(Builder b, int level, int numId, bool ordered, int start, int indentTwips)
    {
        b.OpenCode = null;
        b.OpenQuote = null;
        b.OpenCallout = null;
        if (b.PendingCaption is { } caption)
        {
            b.PendingCaption = null;
            b.Blocks.Add(caption);
        }

        level = Math.Min(level, b.Lists.Count);
        while (b.Lists.Count > level + 1)
        {
            b.Lists.RemoveAt(b.Lists.Count - 1);
        }

        if (b.Lists.Count == level + 1 && (b.Lists[level].NumId != numId || b.Lists[level].List.Ordered != ordered))
        {
            b.Lists.RemoveAt(level);
        }

        if (b.Lists.Count == level)
        {
            var list = new ListBlock { Ordered = ordered, Start = ordered ? start : 1 };
            if (level == 0)
            {
                b.Blocks.Add(list);
            }
            else
            {
                var parent = b.Lists[level - 1];
                parent.Item ??= NewItem(parent.List);
                parent.Item.Blocks.Add(list);
            }

            b.Lists.Add(new ListFrame { List = list, NumId = numId, IndentTwips = indentTwips });
        }

        var frame = b.Lists[level];
        var item = NewItem(frame.List);
        frame.Item = item;
        return item;
    }

    private static ListItem NewItem(ListBlock list)
    {
        var item = new ListItem();
        list.Items.Add(item);
        return item;
    }

    private static void AddItemParagraph(ListItem item, List<Inline> inlines)
    {
        if (inlines.FirstOrDefault() is TextInline first)
        {
            var t = first.Text;
            var mark = t.Length > 0 ? t[0] : '\0';
            if (mark is '☐' or '☑' or '☒' or '✓' or '✔' or '❑')
            {
                item.Checked = mark != '☐' && mark != '❑';
                first.Text = t[1..].TrimStart();
            }
            else if (t.StartsWith("[ ]", StringComparison.Ordinal) || t.StartsWith("[x]", StringComparison.OrdinalIgnoreCase))
            {
                item.Checked = t[1] != ' ';
                first.Text = t[3..].TrimStart();
            }

            if (first.Text.Length == 0)
            {
                inlines.RemoveAt(0);
            }
        }

        if (inlines.Count > 0)
        {
            item.Blocks.Add(Para(inlines, null));
        }
    }

    /// <summary>The deepest open list level whose numbering indent matches a paragraph indent.</summary>
    private static int? MatchLevel(Builder b, int indentTwips)
    {
        if (indentTwips <= 0)
        {
            return null;
        }

        for (var i = b.Lists.Count - 1; i >= 0; i--)
        {
            if (Math.Abs(b.Lists[i].IndentTwips - indentTwips) <= 40)
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>Adds a block, or attaches it to the open list item its indentation belongs to.</summary>
    private void AddOrAttach(Builder b, Block block, int indentTwips)
    {
        if (b.Lists.Count > 0 && MatchLevel(b, indentTwips) is { } level)
        {
            while (b.Lists.Count > level + 1)
            {
                b.Lists.RemoveAt(b.Lists.Count - 1);
            }

            var frame = b.Lists[level];
            frame.Item ??= NewItem(frame.List);
            frame.Item.Blocks.Add(block);
            b.OpenCode = null;
            return;
        }

        Add(b, block);
    }

    private static List<Inline>? StripBullet(List<Inline> inlines)
    {
        if (inlines.FirstOrDefault() is not TextInline first || first.Text.Length < 2)
        {
            return null;
        }

        var t = first.Text;
        if (t[0] is '☐' or '☑' or '☒' && char.IsWhiteSpace(t[1]))
        {
            // Checkbox characters typed as text: a task item (the mark is read by AddItemParagraph).
            return inlines.ToList();
        }

        if (Array.IndexOf(BulletGlyphs, t[0]) < 0 || !char.IsWhiteSpace(t[1]))
        {
            return null;
        }

        if (t[0] is '-' or '*' && t.Length > 2 && char.IsWhiteSpace(t[2]))
        {
            return null;
        }

        var copy = inlines.ToList();
        var rest = t[2..].TrimStart();
        if (rest.Length == 0)
        {
            copy.RemoveAt(0);
        }
        else
        {
            copy[0] = new TextInline(rest);
        }

        return copy.Count == 0 ? null : copy;
    }

    // ----------------------------------------------------------------- tables

    private void ReadTable(Table t, Builder b, OpenXmlPart owner)
    {
        var tblPr = t.GetFirstChild<TableProperties>();
        var styleId = tblPr?.TableStyle?.Val?.Value;
        var look = tblPr?.TableLook;
        var firstRowLook = look?.FirstRow?.Value
            ?? (int.TryParse(look?.Val?.Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var bits) ? (bits & 0x20) != 0 : true);
        var styleHeader = _styles.TableStyleHasHeaderRow(styleId);

        var table = new TableBlock { Caption = string.IsNullOrWhiteSpace(tblPr?.TableCaption?.Val?.Value) ? null : tblPr!.TableCaption!.Val!.Value };
        var origins = new Dictionary<int, ModelCell>();
        var placed = new List<(ModelCell Cell, int Column, int Span)>();
        var rowIndex = 0;
        foreach (var tr in Rows(t))
        {
            var trPr = tr.TableRowProperties;
            var header = trPr?.GetFirstChild<TableHeader>() is { } th && (th.Val is null || th.Val.Value == OnOffOnlyValues.On);
            var row = new ModelRow { IsHeader = header || (rowIndex == 0 && firstRowLook && styleHeader) };
            var col = trPr?.GetFirstChild<GridBefore>()?.Val?.Value ?? 0;
            foreach (var tc in Cells(tr))
            {
                var tcPr = tc.TableCellProperties;
                var span = Math.Max(1, tcPr?.GridSpan?.Val?.Value ?? 1);
                var vm = tcPr?.VerticalMerge;
                var isContinue = vm is not null && (vm.Val is null || vm.Val.Value == MergedCellValues.Continue);
                if (isContinue && origins.TryGetValue(col, out var origin))
                {
                    origin.RowSpan++;
                    col += span;
                    continue;
                }

                var cell = new ModelCell { ColumnSpan = span };
                var child = new Builder(Mode.Cell);
                ReadContainer(tc.ChildElements.Where(e => e is not TableCellProperties), child, owner);
                Finish(child);
                cell.Blocks.AddRange(child.Blocks);
                if (vm is not null && vm.Val?.Value == MergedCellValues.Restart)
                {
                    origins[col] = cell;
                }
                else
                {
                    origins.Remove(col);
                }

                placed.Add((cell, col, span));
                row.Cells.Add(cell);
                col += span;
            }

            if (row.Cells.Count > 0)
            {
                table.Rows.Add(row);
            }

            rowIndex++;
        }

        if (table.Rows.Count == 0)
        {
            return;
        }

        // Column alignment from cell paragraph alignment when every non-empty single-column cell agrees.
        var columns = table.ColumnCount;
        var aligns = new List<ColumnAlignment>();
        for (var c = 0; c < columns; c++)
        {
            var values = placed
                .Where(x => x.Column == c && x.Span == 1 && x.Cell.ToPlainText().Trim().Length > 0)
                .Select(x => x.Cell.Blocks.OfType<ParagraphBlock>().FirstOrDefault()?.Alignment)
                .ToList();
            var first = values.FirstOrDefault();
            var alignment = values.Count > 0 && first is not null && values.All(v => v == first)
                ? first switch
                {
                    TextAlignment.Left => ColumnAlignment.Left,
                    TextAlignment.Center => ColumnAlignment.Center,
                    TextAlignment.Right => ColumnAlignment.Right,
                    _ => ColumnAlignment.Default,
                }
                : ColumnAlignment.Default;
            aligns.Add(alignment);
        }

        if (aligns.Any(a => a != ColumnAlignment.Default))
        {
            table.Alignments.AddRange(aligns);
        }

        foreach (var (cell, column, _) in placed)
        {
            var expected = table.GetAlignment(column) switch
            {
                ColumnAlignment.Left => TextAlignment.Left,
                ColumnAlignment.Center => TextAlignment.Center,
                ColumnAlignment.Right => TextAlignment.Right,
                _ => (TextAlignment?)TextAlignment.Left,
            };
            foreach (var p in cell.Blocks.OfType<ParagraphBlock>())
            {
                if (p.Alignment == expected)
                {
                    p.Alignment = null;
                }
            }
        }

        var indent = tblPr?.TableIndentation?.Width?.Value ?? 0;
        AddOrAttach(b, table, indent);
    }

    private static IEnumerable<TableRow> Rows(OpenXmlElement table)
    {
        foreach (var child in table.ChildElements)
        {
            if (child is TableRow tr)
            {
                yield return tr;
            }
            else if (child is SdtRow or CustomXmlRow)
            {
                foreach (var nested in child.Descendants<TableRow>())
                {
                    yield return nested;
                }
            }
        }
    }

    private static IEnumerable<TableCell> Cells(TableRow row)
    {
        foreach (var child in row.ChildElements)
        {
            if (child is TableCell tc)
            {
                yield return tc;
            }
            else if (child is SdtCell or CustomXmlCell)
            {
                foreach (var nested in child.Descendants<TableCell>())
                {
                    yield return nested;
                }
            }
        }
    }

    // ----------------------------------------------------------------- notes, headers, footers

    private string? RegisterNote(bool endnote, int id)
    {
        if (id <= 0 && !endnote)
        {
            return null;
        }

        var existing = _notes.FindIndex(n => n.Endnote == endnote && n.Id == id);
        if (existing >= 0)
        {
            return _notes[existing].Label;
        }

        var label = (_notes.Count + 1).ToString(CultureInfo.InvariantCulture);
        _notes.Add((endnote, id, label));
        return label;
    }

    private void ReadNotes()
    {
        for (var i = 0; i < _notes.Count; i++)
        {
            var (endnote, id, label) = _notes[i];
            OpenXmlPart? part = endnote ? _main.EndnotesPart : _main.FootnotesPart;
            OpenXmlElement? note = endnote
                ? _main.EndnotesPart?.Endnotes?.Elements<Endnote>().FirstOrDefault(n => n.Id?.Value == id)
                : _main.FootnotesPart?.Footnotes?.Elements<Footnote>().FirstOrDefault(n => n.Id?.Value == id);
            if (part is null || note is null)
            {
                continue;
            }

            var builder = new Builder(Mode.Normal);
            ReadContainer(note.ChildElements, builder, part);
            Finish(builder);
            var footnote = new ModelFootnote { Label = label };
            footnote.Blocks.AddRange(builder.Blocks.Where(x => x is not PageBreakBlock));
            _doc.Footnotes.Add(footnote);
        }
    }

    private void ReadHeadersFooters(Body body)
    {
        var sect = body.Elements<SectionProperties>().LastOrDefault() ?? body.Descendants<SectionProperties>().LastOrDefault();
        if (sect is null)
        {
            return;
        }

        string? Pick<T>()
            where T : HeaderFooterReferenceType =>
            sect.Elements<T>().FirstOrDefault(r => r.Type is null || r.Type.Value == HeaderFooterValues.Default)?.Id?.Value
            ?? sect.Elements<T>().FirstOrDefault()?.Id?.Value;

        if (Pick<HeaderReference>() is { } headerId && _main.GetPartById(headerId) is HeaderPart header && header.Header is { } h)
        {
            var builder = new Builder(Mode.Normal);
            ReadContainer(h.ChildElements, builder, header);
            Finish(builder);
            _doc.Blocks.InsertRange(0, builder.Blocks);
        }

        if (Pick<FooterReference>() is { } footerId && _main.GetPartById(footerId) is FooterPart footer && footer.Footer is { } f)
        {
            var builder = new Builder(Mode.Normal);
            ReadContainer(f.ChildElements, builder, footer);
            Finish(builder);
            _doc.Blocks.AddRange(builder.Blocks);
        }
    }

    // ----------------------------------------------------------------- post-processing

    private void RemoveDuplicateTitleHeadings()
    {
        foreach (var title in _titleHeadings)
        {
            var index = _doc.Blocks.IndexOf(title);
            if (index < 0)
            {
                continue;
            }

            var next = _doc.Blocks.Skip(index + 1).OfType<HeadingBlock>().FirstOrDefault();
            if (next is { Level: 1 } && string.Equals(next.ToPlainText().Trim(), title.ToPlainText().Trim(), StringComparison.Ordinal))
            {
                _doc.Blocks.RemoveAt(index);
                foreach (var key in _bookmarkHeadings.Where(kv => kv.Value == title).Select(kv => kv.Key).ToList())
                {
                    _bookmarkHeadings[key] = next;
                }
            }
        }
    }

    private void AssignHeadingIds()
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var heading in _doc.Headings)
        {
            var slug = DocumentHtmlRenderer.Slugify(heading.ToPlainText());
            var unique = slug;
            var n = 2;
            while (!used.Add(unique))
            {
                unique = $"{slug}-{n++}";
            }

            heading.Id = unique;
        }
    }

    private void ResolveInternalLinks()
    {
        IEnumerable<Block> all = _doc.EnumerateAllBlocks().Concat(_doc.Footnotes.SelectMany(f => Block.EnumerateRecursive(f.Blocks)));
        foreach (var block in all.OfType<InlineContainerBlock>())
        {
            foreach (var link in Links(block.Inlines))
            {
                if (link.IsInternal && _bookmarkHeadings.TryGetValue(link.Url[1..], out var heading) && heading.Id is not null)
                {
                    link.Url = "#" + heading.Id;
                }
            }
        }
    }

    private static IEnumerable<LinkInline> Links(IEnumerable<Inline> inlines)
    {
        foreach (var inline in inlines)
        {
            if (inline is LinkInline link)
            {
                yield return link;
            }

            foreach (var nested in Links(inline.Children))
            {
                yield return nested;
            }
        }
    }

    // ----------------------------------------------------------------- inline helpers

    /// <summary>Text of a code paragraph: line breaks become newlines, tabs stay tabs.</summary>
    private static string CodeText(IEnumerable<Inline> inlines)
    {
        var sb = new StringBuilder();
        void Walk(IEnumerable<Inline> items)
        {
            foreach (var inline in items)
            {
                switch (inline)
                {
                    case TextInline t:
                        sb.Append(t.Text);
                        break;
                    case CodeInline c:
                        sb.Append(c.Code);
                        break;
                    case MathInline m:
                        sb.Append(m.Latex);
                        break;
                    case LineBreakInline:
                        sb.Append('\n');
                        break;
                    case FootnoteReferenceInline f:
                        sb.Append('[').Append(f.Label).Append(']');
                        break;
                    case DrawingInline d:
                        sb.Append(d.AltText);
                        break;
                    default:
                        Walk(inline.Children);
                        break;
                }
            }
        }

        Walk(inlines);
        return sb.ToString();
    }

    /// <summary>Tabs → spaces, small drawings → inline images, outer whitespace trimmed, empty runs dropped.</summary>
    private List<Inline> Clean(List<Inline> raw)
    {
        var list = new List<Inline>();
        foreach (var inline in raw)
        {
            var cleaned = CleanInline(inline);
            if (cleaned is not null)
            {
                list.Add(cleaned);
            }
        }

        TrimEdge(list, start: true);
        TrimEdge(list, start: false);
        return list;
    }

    private Inline? CleanInline(Inline inline)
    {
        switch (inline)
        {
            case TextInline t:
                t.Text = t.Text.Replace('\t', ' ');
                return t.Text.Length == 0 ? null : t;
            case DrawingInline d:
                return new ImageInline { Source = d.Source, AltText = d.AltText, Title = d.Title };
            case StyledInline s:
                var styled = CleanInner(s.Inlines);
                s.Inlines.Clear();
                s.Inlines.AddRange(styled);
                return s.Inlines.Count == 0 ? null : s;
            case LinkInline l:
                var inner = CleanInner(l.Inlines);
                l.Inlines.Clear();
                l.Inlines.AddRange(inner);
                return l.Inlines.Count == 0 ? null : l;
            default:
                return inline;
        }
    }

    /// <summary>Cleans nested inlines without trimming (only the paragraph edges are trimmed).</summary>
    private List<Inline> CleanInner(List<Inline> raw)
    {
        var list = new List<Inline>();
        foreach (var inline in raw.ToList())
        {
            if (CleanInline(inline) is { } c)
            {
                list.Add(c);
            }
        }

        return list;
    }

    private static void TrimEdge(List<Inline> list, bool start)
    {
        while (list.Count > 0)
        {
            var index = start ? 0 : list.Count - 1;
            var item = list[index];
            switch (item)
            {
                case TextInline t:
                    t.Text = start ? t.Text.TrimStart() : t.Text.TrimEnd();
                    if (t.Text.Length == 0)
                    {
                        list.RemoveAt(index);
                        continue;
                    }

                    return;
                case LineBreakInline:
                    list.RemoveAt(index);
                    continue;
                case StyledInline s:
                    TrimEdge(s.Inlines, start);
                    if (s.Inlines.Count == 0)
                    {
                        list.RemoveAt(index);
                        continue;
                    }

                    return;
                case LinkInline l:
                    TrimEdge(l.Inlines, start);
                    if (l.Inlines.Count == 0)
                    {
                        list.RemoveAt(index);
                        continue;
                    }

                    return;
                default:
                    return;
            }
        }
    }

    private static bool Has<T>(IEnumerable<Inline> inlines)
        where T : Inline => inlines.Any(i => i is T || Has<T>(i.Children));

    private void CountParagraph()
    {
        _paragraphs++;
        if (_paragraphs % 200 == 0)
        {
            _context.Report(0.1 + 0.85 * Math.Min(1, (double)_paragraphs / _totalParagraphs), $"Paragraph {_paragraphs} of {_totalParagraphs}");
        }
    }

    private void Warn(string message)
    {
        // The conversion service copies context warnings into Document.ImportWarnings.
        if (_warned.Add(message))
        {
            _context.Warnings.Add(message);
        }
    }
}
