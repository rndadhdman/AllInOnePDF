using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Fonts;
using AllInOnePdf.Core.Themes;
using AllInOnePdf.Layout.Fluent;
using AllInOnePdf.Layout.Infrastructure;
using Document = AllInOnePdf.Core.Documents.Document;
using QColor = AllInOnePdf.Layout.Infrastructure.Color;
using QFontWeight = AllInOnePdf.Layout.Infrastructure.FontWeight;
using ThemeFontWeight = AllInOnePdf.Core.Themes.FontWeight;

namespace AllInOnePdf.Core.Rendering;

/// <summary>Options for a <see cref="ThemedComposer"/>.</summary>
public sealed class ThemedComposerOptions
{
    /// <summary>
    /// Lower quality, faster output for live previews. The composer draws the same layout either way;
    /// <see cref="ThemedComposer.CreateDocumentSettings"/> lowers the image settings when it is set.
    /// </summary>
    public bool Draft { get; init; }

    /// <summary>Checked between top-level blocks while composing; a cancelled token stops layout with an exception.</summary>
    public CancellationToken CancellationToken { get; init; }

    /// <summary>Title used for <c>{title}</c> and the cover when the context document has none (default "Document").</summary>
    public string? FallbackTitle { get; init; }
}

/// <summary>
/// Text overrides for one <see cref="ThemedComposer.ComposeBlocks(IContainer, IReadOnlyList{Block}, double?, BlockTextStyle?)"/>
/// call: every themed text size is multiplied by <paramref name="FontScale"/> and body, heading and quote text takes
/// <paramref name="TextColor"/> (links, code, markers and accents keep their own colours).
/// </summary>
/// <param name="FontScale">1 = the theme's sizes.</param>
/// <param name="TextColor">Hex colour, or null to keep the theme's text colours.</param>
public sealed record BlockTextStyle(double FontScale = 1, string? TextColor = null)
{
    /// <summary>True when the style changes nothing.</summary>
    public bool IsDefault => Math.Abs(FontScale - 1) < 0.0005 && string.IsNullOrWhiteSpace(TextColor);
}

/// <summary>
/// Draws the document model on the layout engine's containers exactly the way a <see cref="PdfTheme"/> says: page setup, cover,
/// running header and footer, contents list, every block type, inline formatting and footnotes. This is the single
/// themed drawing implementation; <see cref="PdfRenderer"/> is built on it and other renderers (publications)
/// compose its pieces into their own layouts.
/// </summary>
/// <remarks>
/// The composer keeps per-document state (section ids, heading numbers, the <c>{section}</c> captures and paragraphs
/// that follow headings), so use one instance per generated PDF. Its methods must be called while the layout engine composes the
/// document (inside <c>Document.Create</c>); they are not thread-safe.
/// </remarks>
public sealed partial class ThemedComposer
{
    /// <summary>Named destination on the cover page (read back by the PDF importer's <c>PdfMarkers</c>).</summary>
    internal const string CoverMarker = "aio-cover";

    /// <summary>Prefix of the named destination at a code block with a language: <c>aio-code-&lt;n&gt;-&lt;language&gt;</c>.</summary>
    internal const string CodeMarkerPrefix = "aio-code-";

    private int _codeMarkers;
    private readonly Document _doc;
    private readonly PdfTheme _t;
    private readonly ThemedComposerOptions _opt;
    private readonly ISyntaxHighlighter? _highlighter;
    private readonly CancellationToken _ct;
    private readonly double _contentWidthPt;
    private readonly double _contentHeightPt;
    private readonly string _title;
    private readonly Dictionary<HeadingBlock, string> _sectionIds = [];
    private readonly Dictionary<HeadingBlock, string> _numbers = [];
    private readonly bool _darkPage;
    private readonly QColor _pageBg;
    /// <summary>Content height minus the header and footer bands.</summary>
    private readonly double _bodyHeightPt;

    /// <summary>Top-level section headings in document order, with the capture ids used to resolve {section}.</summary>
    private readonly List<(string CaptureId, string Text)> _sections = [];
    private readonly Dictionary<HeadingBlock, string> _sectionCaptureIds = [];
    private readonly HashSet<ParagraphBlock> _noIndent = [];
    private readonly HashSet<string> _usedIds = new(StringComparer.Ordinal);
    private readonly int[] _counters = new int[7];

    /// <summary>Width used by the estimates while a <see cref="ComposeBlocks"/> call with an explicit width runs.</summary>
    private double? _widthOverridePt;

    /// <summary>Multiplies every themed text size while a <see cref="ComposeBlocks"/> call with a <see cref="BlockTextStyle"/> runs.</summary>
    private double _fontScale = 1;

    /// <summary>Replaces the body, heading and quote text colour while a <see cref="ComposeBlocks"/> call with a <see cref="BlockTextStyle"/> runs.</summary>
    private string? _textColor;

    /// <summary>First block of the running <see cref="ComposeBlocks"/> call: a level-1 heading there never breaks the page.</summary>
    private Block? _flowFirst;

    private static readonly float[] LeaderDash = [0.9f, 2.7f];

    /// <summary>Creates a composer for one PDF.</summary>
    /// <param name="theme">The look. Read, never modified.</param>
    /// <param name="context">
    /// Supplies metadata (title for <c>{title}</c>, cover and PDF info), <see cref="Document.Resources"/> for
    /// <c>res:</c> images, <see cref="Document.Footnotes"/> and the source path for relative images. Its blocks may be
    /// empty. Headings are not registered automatically: call <see cref="RegisterHeadings"/>.
    /// </param>
    /// <param name="highlighter">Syntax highlighter for code blocks; null draws code in one colour.</param>
    /// <param name="options">Draft mode, cancellation and the fallback title.</param>
    public ThemedComposer(PdfTheme theme, Document context, ISyntaxHighlighter? highlighter = null, ThemedComposerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(context);
        BundledFonts.EnsureRegistered();
        _doc = context;
        _t = theme;
        _opt = options ?? new ThemedComposerOptions();
        _highlighter = highlighter;
        _ct = _opt.CancellationToken;
        var (w, h) = theme.Page.GetSizeMm();
        _contentWidthPt = MmToPt(w - theme.Page.MarginLeftMm - theme.Page.MarginRightMm);
        _contentHeightPt = MmToPt(h - theme.Page.MarginTopMm - theme.Page.MarginBottomMm);
        _title = context.ResolveTitle() ?? _opt.FallbackTitle ?? "Document";
        _darkPage = ThemeDrawing.IsDark(theme.Page.Background);
        _pageBg = Hex(theme.Page.Background, "#FFFFFF");
        static double Band(HeaderFooterSettings hf) => hf.HasContent ? (hf.SizePt * 1.25) + hf.SpacingPt + (hf.Rule ? 6 : 0) : 0;
        _bodyHeightPt = Math.Max(72, _contentHeightPt - Band(theme.Header) - Band(theme.Footer));
    }

    /// <summary>The theme this composer draws with.</summary>
    public PdfTheme Theme => _t;

    /// <summary>Page width minus the left and right margins, in points.</summary>
    public double ContentWidthPt => _contentWidthPt;

    /// <summary>Page content height minus the header and footer bands, in points.</summary>
    public double BodyHeightPt => _bodyHeightPt;

    /// <summary>Resolved document title: the context's title, else <see cref="ThemedComposerOptions.FallbackTitle"/>, else "Document".</summary>
    public string Title => _title;

    /// <summary>
    /// Layout engine document settings for this composer: image quality from <paramref name="imageQuality"/> (1-100), or
    /// medium quality at 96 dpi when <see cref="ThemedComposerOptions.Draft"/> is set.
    /// </summary>
    public DocumentSettings CreateDocumentSettings(int imageQuality = 85) => new()
    {
        ImageCompressionQuality = _opt.Draft ? ImageCompressionQuality.Medium : QualityFrom(imageQuality),
        ImageRasterDpi = _opt.Draft ? 96 : 220,
        ContentDirection = ContentDirection.LeftToRight,
    };

    // ------------------------------------------------------------ public api

    /// <summary>Theme.Header strip (template cells, rule), with <c>{page}</c>, <c>{section}</c> and friends resolved per page.</summary>
    public void ComposeHeader(IContainer container) => ComposeHeaderFooter(container, _t.Header, isHeader: true);

    /// <summary>Theme.Footer strip (template cells, rule), with <c>{page}</c>, <c>{section}</c> and friends resolved per page.</summary>
    public void ComposeFooter(IContainer container) => ComposeHeaderFooter(container, _t.Footer, isHeader: false);

    /// <summary>
    /// Composes a flow of blocks into <paramref name="container"/> as one column: headings keep with the next block,
    /// paragraphs get orphan control, level-1 headings start a new page when the theme asks (never the first block of
    /// the flow or of the context document).
    /// </summary>
    /// <param name="container">Target container.</param>
    /// <param name="blocks">Blocks in reading order. Headings should be registered first so they get section ids and numbers.</param>
    /// <param name="availableWidthPt">
    /// Width of the target in points, used instead of <see cref="ContentWidthPt"/> for every estimate and size (code
    /// shrink-to-fit, keep-with-next heights, image widths) while this call runs. Null keeps the width of an enclosing
    /// call, which outside any call is the page content width.
    /// </param>
    /// <param name="style">Font scale and text colour overrides for this flow; null keeps the theme's.</param>
    /// <param name="pageKey">
    /// Gives a block the key its items are page-tracked under (<c>TrackPages</c>; a page builder maps blocks back to
    /// its modules), or null for no tracking. Null keeps the key function of an enclosing call.
    /// </param>
    public void ComposeBlocks(IContainer container, IReadOnlyList<Block> blocks, double? availableWidthPt = null, BlockTextStyle? style = null, Func<Block, string?>? pageKey = null)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(blocks);
        container.Column(col => ComposeBlocks(col, blocks, availableWidthPt, style, pageKey));
    }

    /// <summary>The page-tracking key function of the running <see cref="ComposeBlocks(ColumnDescriptor, IReadOnlyList{Block}, double?, BlockTextStyle?, Func{Block, string?}?)"/> call.</summary>
    private Func<Block, string?>? _pageKey;

    /// <summary>The item decorator that tracks <paramref name="block"/>'s pages, or null when nothing tracks it.</summary>
    private Func<IContainer, IContainer>? PageDecorator(Block block) =>
        _pageKey?.Invoke(block) is { } key ? c => c.TrackPages(key) : null;

    /// <summary><paramref name="container"/> wrapped to track <paramref name="block"/>'s pages, or itself when nothing tracks it.</summary>
    private IContainer Tracked(IContainer container, Block block) =>
        _pageKey?.Invoke(block) is { } key ? container.TrackPages(key) : container;

    /// <summary>
    /// Same as <see cref="ComposeBlocks(IContainer, IReadOnlyList{Block}, double?, BlockTextStyle?)"/>, but adds the
    /// blocks as items of an existing column instead of a nested one. <see cref="PdfRenderer"/> uses this form:
    /// the layout engine (like QuestPDF before it) paginates a flow nested in a column item slightly differently from
    /// the same items added directly to the page column. With a non-default <paramref name="style"/> the flow is nested anyway, so the style can wrap it.
    /// </summary>
    public void ComposeBlocks(ColumnDescriptor col, IReadOnlyList<Block> blocks, double? availableWidthPt = null, BlockTextStyle? style = null, Func<Block, string?>? pageKey = null)
    {
        ArgumentNullException.ThrowIfNull(col);
        ArgumentNullException.ThrowIfNull(blocks);
        var previousWidth = _widthOverridePt;
        var previousFirst = _flowFirst;
        var previousScale = _fontScale;
        var previousColor = _textColor;
        var previousKey = _pageKey;
        _widthOverridePt = availableWidthPt is > 0 ? availableWidthPt : previousWidth;
        _flowFirst = blocks.Count > 0 ? blocks[0] : null;
        _pageKey = pageKey ?? previousKey;
        try
        {
            if (style is { IsDefault: false })
            {
                _fontScale = Math.Clamp(double.IsFinite(style.FontScale) && style.FontScale > 0 ? style.FontScale : 1, 0.25, 4);
                _textColor = NormalizeHex(style.TextColor);
                var scale = _fontScale;
                var color = _textColor;
                col.Item()
                    .DefaultTextStyle(ts =>
                    {
                        var s = ts.FontSize((float)(_t.Typography.BodySizePt * scale));
                        return color is null ? s : s.FontColor(Hex(color, "#1F2937"));
                    })
                    .Column(inner => ComposeTopLevel(inner, blocks));
            }
            else
            {
                ComposeTopLevel(col, blocks);
            }
        }
        finally
        {
            _widthOverridePt = previousWidth;
            _flowFirst = previousFirst;
            _fontScale = previousScale;
            _textColor = previousColor;
            _pageKey = previousKey;
        }
    }

    /// <summary>A themed size in points scaled by the running <see cref="BlockTextStyle"/>.</summary>
    private float Scaled(double pt) => (float)(pt * _fontScale);

    /// <summary>
    /// Composes inline content into a text block with the theme's inline styles (bold, code, links, footnote marks…).
    /// Internal links resolve to registered headings.
    /// </summary>
    /// <param name="text">Target text descriptor; its default style is the caller's.</param>
    /// <param name="inlines">Inline runs.</param>
    /// <param name="uppercase">Upper-case every text run (small caps style headings).</param>
    public void ComposeInlines(TextDescriptor text, IEnumerable<Inline> inlines, bool uppercase = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(inlines);
        ComposeInlines(text, inlines, new InlineState { Uppercase = uppercase });
    }

    /// <summary>
    /// The contents list: a title in the heading font, then one entry per heading with a link to it, indentation per
    /// level and (when the theme shows them) the page number with dot leaders. Entries that were never registered with
    /// <see cref="RegisterHeadings"/> are listed without link or page number.
    /// </summary>
    /// <param name="container">Target container.</param>
    /// <param name="entries">Headings to list, in order (filter by depth before calling).</param>
    /// <param name="title">Title above the list; null uses Theme.Toc.Title.</param>
    /// <param name="showPageNumbers">Page numbers with dot leaders; null uses Theme.Toc.ShowPageNumbers.</param>
    public void ComposeToc(IContainer container, IReadOnlyList<HeadingBlock> entries, string? title = null, bool? showPageNumbers = null)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(entries);
        container.Column(col => ComposeTocItems(col, entries, title ?? _t.Toc.Title, showPageNumbers ?? _t.Toc.ShowPageNumbers));
    }

    /// <summary>The context document's footnotes under a short rule, in the theme's footnote style.</summary>
    public void ComposeFootnotes(IContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);
        container.Column(ComposeFootnoteItems);
    }

    // ---------------------------------------------------------------- page

    /// <summary>
    /// Page size, margins, page colour (the cover background when <paramref name="isCover"/> and the theme sets one)
    /// and the body text style from the theme.
    /// </summary>
    public void ConfigurePage(PageDescriptor page, bool isCover = false)
    {
        ArgumentNullException.ThrowIfNull(page);
        var (w, h) = _t.Page.GetSizeMm();
        page.Size((float)w, (float)h, Unit.Millimetre);
        page.MarginTop((float)_t.Page.MarginTopMm, Unit.Millimetre);
        page.MarginBottom((float)_t.Page.MarginBottomMm, Unit.Millimetre);
        page.MarginLeft((float)_t.Page.MarginLeftMm, Unit.Millimetre);
        page.MarginRight((float)_t.Page.MarginRightMm, Unit.Millimetre);
        var bg = isCover && !string.IsNullOrWhiteSpace(_t.Cover.Background) ? _t.Cover.Background : _t.Page.Background;
        page.PageColor(Hex(bg, "#FFFFFF"));
        page.DefaultTextStyle(ts => ts
            .FontFamily(FontChain(_t.Typography.BodyFont))
            .FontSize((float)_t.Typography.BodySizePt)
            .FontColor(Hex(_t.Colors.Text, "#1F2937"))
            .LineHeight((float)_t.Typography.LineHeight)
            .LetterSpacing((float)_t.Typography.LetterSpacingEm)
            // Inter's contextual alternates swap ':' '-' between capitals/digits ("10:30", "S:A") for
            // private-use glyphs that extract as garbage; keep the text layer faithful.
            .DisableFontFeature("calt"));
    }

    // --------------------------------------------------------------- cover

    /// <summary>The cover page content in the theme's cover style: title, subtitle, author, date and note from the context metadata.</summary>
    public void ComposeCover(IContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);

        // Invisible marker (a named destination that draws nothing): the PDF importer recognises the page as the
        // cover and folds it into the metadata instead of importing it as content.
        container = container.Section(CoverMarker);
        var cover = _t.Cover;
        var title = cover.TitleOverride ?? _title;
        var titleColor = Hex(cover.TitleColor ?? _t.Headings.Color, "#111827");
        var accent = Hex(_t.Colors.Accent, "#0F6CBD");
        var muted = Hex(_t.Colors.Muted, "#6B7280");
        var headingFont = FontChain(_t.Headings.Font ?? _t.Typography.BodyFont);

        void Line(ColumnDescriptor col, bool alignCenter, float paddingTop, string content, Action<TextSpanDescriptor> style)
        {
            col.Item().PaddingTop(paddingTop).Text(text =>
            {
                if (alignCenter)
                {
                    text.AlignCenter();
                }
                else
                {
                    text.AlignLeft();
                }

                style(text.Span(content));
            });
        }

        List<string> MetaLine()
        {
            var meta = new List<string>();
            if (cover.ShowAuthor && _doc.Metadata.Authors.Count > 0)
            {
                meta.Add(string.Join(", ", _doc.Metadata.Authors));
            }

            if (cover.ShowDate)
            {
                meta.Add(DocumentDates.Long(_doc.Metadata.Date ?? DateTimeOffset.Now, _doc.Metadata.Language));
            }

            return meta;
        }

        void TitleBlock(ColumnDescriptor col, bool alignCenter)
        {
            if (cover.AccentBar && cover.Style != CoverStyle.Banner)
            {
                var bar = col.Item().PaddingBottom(14);
                bar = alignCenter ? bar.AlignCenter() : bar.AlignLeft();
                bar.Width(64).Height(5).Background(accent);
            }

            if (cover.ShowTitle)
            {
                Line(col, alignCenter, 0, title, s => s.FontFamily(headingFont).FontSize((float)cover.TitleSizePt).Bold().FontColor(titleColor).LineHeight(1.1f));
            }

            if (cover.ShowSubtitle && !string.IsNullOrWhiteSpace(_doc.Metadata.Subtitle))
            {
                Line(col, alignCenter, 10, _doc.Metadata.Subtitle, s => s.FontFamily(headingFont).FontSize((float)(cover.TitleSizePt * 0.45)).FontColor(muted).LineHeight(1.25f));
            }

            var meta = MetaLine();
            if (meta.Count > 0)
            {
                Line(col, alignCenter, 28, string.Join("  ·  ", meta), s => s.FontSize((float)(_t.Typography.BodySizePt * 1.05)).FontColor(muted));
            }

            if (!string.IsNullOrWhiteSpace(cover.Note))
            {
                Line(col, alignCenter, 8, cover.Note, s => s.FontSize((float)(_t.Typography.BodySizePt * 0.9)).Italic().FontColor(muted));
            }
        }

        switch (cover.Style)
        {
            case CoverStyle.Centered:
                container.AlignMiddle().Column(col => TitleBlock(col, alignCenter: true));
                break;

            case CoverStyle.Minimal:
                container.AlignTop().PaddingTop(40).Column(col => TitleBlock(col, alignCenter: false));
                break;

            case CoverStyle.Bold:
                container.AlignBottom().PaddingBottom(30).Column(col => TitleBlock(col, alignCenter: false));
                break;

            case CoverStyle.Split:
                container.Row(row =>
                {
                    row.ConstantItem(18).Background(accent);
                    row.RelativeItem().PaddingLeft(28).AlignMiddle().Column(col => TitleBlock(col, alignCenter: false));
                });
                break;

            case CoverStyle.Banner:
            default:
                container.Column(col =>
                {
                    col.Item().Background(accent).Padding(28).PaddingVertical(40).Column(inner =>
                    {
                        if (cover.ShowTitle)
                        {
                            inner.Item().Text(title).FontFamily(headingFont).FontSize((float)cover.TitleSizePt).Bold().FontColor(QColor.FromHex("#FFFFFF")).LineHeight(1.1f);
                        }

                        if (cover.ShowSubtitle && !string.IsNullOrWhiteSpace(_doc.Metadata.Subtitle))
                        {
                            inner.Item().PaddingTop(10).Text(_doc.Metadata.Subtitle).FontFamily(headingFont).FontSize((float)(cover.TitleSizePt * 0.45)).FontColor(QColor.FromHex("#E6F0FA"));
                        }
                    });

                    col.Item().PaddingTop(30).Column(inner =>
                    {
                        var meta = MetaLine();
                        if (meta.Count > 0)
                        {
                            inner.Item().Text(string.Join("  ·  ", meta)).FontColor(muted);
                        }

                        if (!string.IsNullOrWhiteSpace(cover.Note))
                        {
                            inner.Item().PaddingTop(6).Text(cover.Note).Italic().FontColor(muted);
                        }
                    });
                });
                break;
        }
    }

    // ------------------------------------------------------- header/footer

    private void ComposeHeaderFooter(IContainer container, HeaderFooterSettings hf, bool isHeader)
    {
        var color = Hex(hf.Color ?? _t.Colors.Muted, "#6B7280");
        var border = Hex(_t.Colors.Border, "#D1D5DB");
        var c = isHeader ? container.PaddingBottom((float)hf.SpacingPt) : container.PaddingTop((float)hf.SpacingPt);
        c.DefaultTextStyle(ts => ts.FontSize((float)hf.SizePt).FontColor(color).LineHeight(1.2f)).Element(inner =>
        {
            if (hf.Rule)
            {
                inner = isHeader ? inner.BorderBottom(0.5f).BorderColor(border).PaddingBottom(5) : inner.BorderTop(0.5f).BorderColor(border).PaddingTop(5);
            }

            inner.Row(row =>
            {
                TemplateCell(row.RelativeItem(), hf.Left, HorizontalAlign.Left);
                TemplateCell(row.RelativeItem(), hf.Center, HorizontalAlign.Center);
                TemplateCell(row.RelativeItem(), hf.Right, HorizontalAlign.Right);
            });
        });
    }

    private enum HorizontalAlign { Left, Center, Right }

    private static IContainer AlignX(IContainer c, HorizontalAlign align) => align switch
    {
        HorizontalAlign.Center => c.AlignCenter(),
        HorizontalAlign.Right => c.AlignRight(),
        _ => c.AlignLeft(),
    };

    private void TemplateCell(IContainer cell, string? template, HorizontalAlign align)
    {
        if (template is not null && template.Contains("{section}", StringComparison.OrdinalIgnoreCase) && _sections.Count > 0)
        {
            // {section} needs to know which heading is on the page being drawn: a dynamic component asks the layout engine
            // where each section heading was captured and picks the right one per page.
            cell.Dynamic(new SectionTemplateComponent(this, template, align));
            return;
        }

        AlignX(cell, align).Text(t => ComposeTemplate(t, template, section: null));
    }

    /// <summary>
    /// Running-header text for a page: the first top-level heading that starts on this page, otherwise the last one
    /// that started on an earlier page. Before the first heading (cover, TOC) it is the document title.
    /// </summary>
    private string SectionForPage(DynamicContext context)
    {
        string? current = null;
        foreach (var (captureId, text) in _sections)
        {
            var first = int.MaxValue;
            foreach (var location in context.GetContentCapturedPositions(captureId))
            {
                first = Math.Min(first, location.PageNumber);
            }

            if (first == int.MaxValue || first > context.PageNumber)
            {
                // Not laid out yet (first pass) or on a later page: headings are in document order, so stop.
                break;
            }

            current = text;
            if (first == context.PageNumber)
            {
                break;
            }
        }

        return current ?? _title;
    }

    private sealed class SectionTemplateComponent(ThemedComposer owner, string template, HorizontalAlign align) : IDynamicComponent
    {
        public DynamicComponentComposeResult Compose(DynamicContext context)
        {
            var section = owner.SectionForPage(context);
            var content = context.CreateElement(e =>
                AlignX(e.Width(context.AvailableSize.Width), align).Text(t => owner.ComposeTemplate(t, template, section)));
            return new DynamicComponentComposeResult { Content = content, HasMoreContent = false };
        }
    }

    private void ComposeTemplate(TextDescriptor text, string? template, string? section)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            text.Span(string.Empty);
            return;
        }

        foreach (Match m in TemplateToken().Matches(template))
        {
            if (m.Groups["lit"].Success)
            {
                text.Span(m.Groups["lit"].Value);
                continue;
            }

            switch (m.Groups["tok"].Value.ToLowerInvariant())
            {
                case "page":
                    text.CurrentPageNumber();
                    break;
                case "pages":
                    text.TotalPages();
                    break;
                case "title":
                    text.Span(_title);
                    break;
                case "section":
                    text.Span(section ?? _title);
                    break;
                case "subtitle":
                    text.Span(_doc.Metadata.Subtitle ?? string.Empty);
                    break;
                case "author":
                    text.Span(string.Join(", ", _doc.Metadata.Authors));
                    break;
                case "date":
                    text.Span(DocumentDates.Short(_doc.Metadata.Date ?? DateTimeOffset.Now, _doc.Metadata.Language));
                    break;
                case "filename":
                    text.Span(Path.GetFileName(_doc.Metadata.SourcePath ?? string.Empty));
                    break;
                default:
                    text.Span(m.Value);
                    break;
            }
        }
    }

    // ------------------------------------------------------------------ toc

    private void ComposeTocItems(ColumnDescriptor col, IReadOnlyList<HeadingBlock> entries, string title, bool pageNumbers)
    {
        var toc = _t.Toc;
        var headingFont = FontChain(_t.Headings.Font ?? _t.Typography.BodyFont);
        var h1 = _t.Headings.H1;
        var tocTitle = col.Item().PaddingBottom((float)h1.SpaceAfterPt)
            .Text(title).FontFamily(headingFont).FontSize(Scaled(h1.SizePt * 0.8)).FontColor(Hex(_textColor ?? _t.Headings.Color, "#111827"));
        SpanWeight(tocTitle, h1.Weight);

        var muted = Hex(_t.Colors.Muted, "#6B7280");
        var minLevel = entries.Count == 0 ? 1 : entries.Min(h => h.Level);
        foreach (var h in entries)
        {
            var id = _sectionIds.GetValueOrDefault(h);
            var label = (_numbers.TryGetValue(h, out var num) ? num + "  " : string.Empty) + h.ToPlainText();
            var indent = toc.IndentLevels ? (h.Level - minLevel) * 14f : 0f;
            var size = Scaled(_t.Typography.BodySizePt * (h.Level == minLevel ? 1.0 : 0.94));
            var top = h.Level == minLevel && entries.Count > 1 ? 5f : 2.5f;
            var showNumbers = pageNumbers && id is not null;
            var leaders = toc.DotLeaders && showNumbers;
            col.Item().PaddingLeft(indent).PaddingTop(top).PaddingBottom(2.5f).Layers(layers =>
            {
                if (leaders)
                {
                    // Dotted leader under the whole entry, on the last line's baseline. The label and the page
                    // number carry the page colour as text background, so the dots show only between them.
                    layers.Layer().AlignBottom().PaddingBottom(size * 0.32f).LineHorizontal(0.9f).LineColor(muted).LineDashPattern(LeaderDash);
                }

                layers.PrimaryLayer().Row(row =>
                {
                    row.RelativeItem().Text(t =>
                    {
                        var span = (id is null ? t.Span(label) : t.SectionLink(label, id)).FontSize(size).FontColor(Hex(_t.Colors.Text, "#1F2937"));
                        if (h.Level == minLevel)
                        {
                            span.SemiBold();
                        }

                        if (leaders)
                        {
                            span.BackgroundColor(_pageBg);
                            t.Span("  ").FontSize(size).BackgroundColor(_pageBg);
                        }
                    });

                    if (showNumbers)
                    {
                        row.ConstantItem(32).AlignRight().AlignBottom().Text(t =>
                        {
                            if (leaders)
                            {
                                t.Span("  ").FontSize(size).BackgroundColor(_pageBg);
                            }

                            var num = t.BeginPageNumberOfSection(id!).FontSize(size).FontColor(muted);
                            if (leaders)
                            {
                                num.BackgroundColor(_pageBg);
                            }

                            if (h.Level == minLevel)
                            {
                                num.SemiBold();
                            }
                        });
                    }
                });
            });
        }
    }

    // --------------------------------------------------------------- blocks

    private void ComposeBlock(ColumnDescriptor col, Block block, bool nested)
    {
        switch (block)
        {
            case HeadingBlock h:
                ComposeHeading(col, h);
                break;
            case ParagraphBlock p:
                {
                    var item = col.Item();
                    if (!nested && _t.Typography.OrphanControl)
                    {
                        // Orphan control: never leave just one line of a paragraph at the bottom of a page.
                        item = item.EnsureSpace((float)((2 * BodyLineHeight) + 1));
                    }

                    ComposeParagraph(item, p, nested);
                    break;
                }

            case ListBlock list:
                ComposeList(col.Item(), list, nested);
                break;
            case CodeBlock code:
                ComposeCode(nested ? col.Item() : col.Item().EnsureSpace((float)EstimateLeadHeight(code)), code);
                break;
            case QuoteBlock quote:
                ComposeQuote(nested ? col.Item() : KeepShortBoxTogether(col.Item(), quote, quote.Blocks), quote);
                break;
            case CalloutBlock callout:
                ComposeCallout(nested ? col.Item() : KeepShortBoxTogether(col.Item(), callout, callout.Blocks), callout);
                break;
            case TableBlock table:
                ComposeTable(nested ? col.Item() : col.Item().EnsureSpace((float)EstimateLeadHeight(table)), table);
                break;
            case ImageBlock image:
                ComposeImage(col.Item(), image);
                break;
            case ThematicBreakBlock:
                col.Item().PaddingVertical((float)Math.Max(6, _t.Typography.ParagraphSpacingPt)).LineHorizontal(0.75f).LineColor(Hex(_t.Colors.Border, "#D1D5DB"));
                break;
            case PageBreakBlock:
                col.Item().PageBreak();
                break;
            case HtmlBlock html:
                {
                    var text = StripTags().Replace(html.Html, string.Empty).Trim();
                    if (text.Length > 0)
                    {
                        col.Item().PaddingBottom((float)_t.Typography.ParagraphSpacingPt).Text(text).Italic().FontColor(Hex(_t.Colors.Muted, "#6B7280"));
                    }

                    break;
                }

            case MathBlock math:
                col.Item().PaddingBottom((float)_t.Typography.ParagraphSpacingPt).AlignCenter().Text(math.Latex).FontFamily(FontChain(_t.Code.Font)).FontSize((float)_t.Code.SizePt);
                break;
        }
    }

    /// <summary>
    /// Top-level flow with keep-with-next: a run of headings is grouped with the start of the following block and
    /// the group asks for enough room (headings + about three lines) before it starts, so a heading never ends a page.
    /// Paragraphs get orphan control (at least two lines at the bottom of a page).
    /// </summary>
    private void ComposeTopLevel(ColumnDescriptor col, IReadOnlyList<Block> blocks)
    {
        for (var i = 0; i < blocks.Count; i++)
        {
            _ct.ThrowIfCancellationRequested();
            if (blocks[i] is not HeadingBlock first || !_t.Headings.KeepWithNext)
            {
                col.ItemDecorator = PageDecorator(blocks[i]);
                try
                {
                    ComposeBlock(col, blocks[i], nested: false);
                }
                finally
                {
                    col.ItemDecorator = null;
                }

                continue;
            }

            var group = new List<HeadingBlock> { first };
            var j = i + 1;
            while (j < blocks.Count && blocks[j] is HeadingBlock more && !BreaksBefore(more))
            {
                group.Add(more);
                j++;
            }

            var next = j < blocks.Count && blocks[j] is not (HeadingBlock or PageBreakBlock or ThematicBreakBlock) ? blocks[j] : null;
            if (BreaksBefore(first))
            {
                col.Item().PageBreak();
            }

            if (next is ParagraphBlock lead)
            {
                _noIndent.Add(lead); // book typography: no first-line indent right after a heading
            }

            var headingsHeight = group.Sum(EstimateHeadingHeight);
            var item = Tracked(col.Item(), first);
            if (next is ImageBlock image)
            {
                // Images cannot split: keep heading + image together when they fit on one page at all,
                // otherwise let them flow (PreventPageBreak would leave an empty page).
                if (headingsHeight + EstimateImageHeight(image) < _bodyHeightPt * 0.95)
                {
                    item = item.PreventPageBreak();
                }
            }
            else
            {
                var minHeight = headingsHeight + EstimateLeadHeight(next);
                item = item.EnsureSpace((float)Math.Min(minHeight, _bodyHeightPt * 0.6));
            }
            item.Column(g =>
            {
                foreach (var h in group)
                {
                    ComposeHeadingItem(Tracked(g.Item(), h), h);
                }

                if (next is not null)
                {
                    g.ItemDecorator = PageDecorator(next);
                    try
                    {
                        ComposeBlock(g, next, nested: false);
                    }
                    finally
                    {
                        g.ItemDecorator = null;
                    }
                }
            });

            i = next is null ? j - 1 : j;
        }
    }

    /// <summary>A level-1 heading starts a new page when the theme asks, except at the start of the flow or of the context document.</summary>
    private bool BreaksBefore(HeadingBlock h) =>
        h.Level == 1 && _t.Headings.PageBreakBeforeH1 && !ReferenceEquals(h, _flowFirst) && !ReferenceEquals(h, _doc.Blocks.FirstOrDefault());

    /// <summary>Width the estimates use: the running <see cref="ComposeBlocks"/> width, else the page content width.</summary>
    private double WidthPt => _widthOverridePt ?? _contentWidthPt;

    private double BodyLineHeight => _t.Typography.BodySizePt * _t.Typography.LineHeight;

    private double EstimateLines(string text, double sizePt, double widthPt) =>
        Math.Max(1, Math.Ceiling(text.Length * sizePt * 0.52 / Math.Max(40, widthPt)));

    private double EstimateHeadingHeight(HeadingBlock h)
    {
        var style = _t.Headings.Level(h.Level);
        var lines = EstimateLines(h.ToPlainText(), style.SizePt, WidthPt);
        return style.SpaceBeforePt + style.SpaceAfterPt + (lines * style.SizePt * _t.Headings.LineHeight) + (style.Underline ? 3 + style.RuleWidthPt : 0);
    }

    /// <summary>Height of the first few lines of a block, used for keep-with-next.</summary>
    private double EstimateLeadHeight(Block? next)
    {
        var body = BodyLineHeight;
        switch (next)
        {
            case null:
                return 0;
            case ParagraphBlock p:
                return (Math.Min(2, EstimateLines(p.ToPlainText(), _t.Typography.BodySizePt, WidthPt)) * body) + 1;
            case ListBlock:
                return body + 1;
            case CodeBlock code:
                var codeLines = Math.Min(3, code.Code.Count(ch => ch == '\n') + 1);
                return (codeLines * _t.Code.SizePt * _t.Code.LineHeight) + (2 * _t.Code.PaddingPt);
            case TableBlock:
                var row = (_t.Typography.BodySizePt * _t.Tables.FontScale * 1.3) + (2 * _t.Tables.CellPaddingPt);
                return 2 * row;
            case QuoteBlock or CalloutBlock:
                return (2 * body) + 20;
            default:
                return 2 * body;
        }
    }

    /// <summary>Short boxed blocks (quotes, callouts) move to the next page whole instead of splitting; long ones may split.</summary>
    private IContainer KeepShortBoxTogether(IContainer item, Block box, List<Block> children)
    {
        var estimate = 30 + children.Sum(b => b is InlineContainerBlock icb
            ? EstimateLines(icb.ToPlainText(), _t.Typography.BodySizePt, WidthPt - 40) * BodyLineHeight + _t.Typography.ParagraphSpacingPt
            : 3 * BodyLineHeight);
        return estimate < _contentHeightPt * 0.3 ? item.PreventPageBreak() : item.EnsureSpace((float)EstimateLeadHeight(box));
    }

    private void ComposeHeading(ColumnDescriptor col, HeadingBlock h)
    {
        if (BreaksBefore(h))
        {
            col.Item().PageBreak();
        }

        ComposeHeadingItem(col.Item(), h);
    }

    private void ComposeHeadingItem(IContainer item, HeadingBlock h)
    {
        var style = _t.Headings.Level(h.Level);
        var color = Hex(_textColor ?? style.Color ?? _t.Headings.Color, "#111827");
        var font = FontChain(_t.Headings.Font ?? _t.Typography.BodyFont);
        var align = h.Alignment switch
        {
            Documents.TextAlignment.Center => TextAlign.Center,
            Documents.TextAlignment.Right => TextAlign.Right,
            Documents.TextAlignment.Left or Documents.TextAlignment.Justify => TextAlign.Left,
            _ => style.Alignment,
        };

        // Unregistered headings draw the same but are no link or page-number target.
        var container = _sectionIds.TryGetValue(h, out var sectionId) ? item.Section(sectionId) : item;
        if (_sectionCaptureIds.TryGetValue(h, out var captureId))
        {
            container = container.CaptureContentPosition(captureId);
        }

        container = container
            .PaddingTop((float)style.SpaceBeforePt)
            .PaddingBottom((float)style.SpaceAfterPt);

        if (style.Underline)
        {
            container = container.BorderBottom((float)Math.Max(0.25, style.RuleWidthPt)).BorderColor(Hex(style.RuleColor ?? _t.Colors.Accent, "#0F6CBD")).PaddingBottom(3);
        }

        container.Text(text =>
        {
            text.DefaultTextStyle(ts => ts
                .FontFamily(font)
                .FontSize(Scaled(style.SizePt))
                .Weight(Weight(style.Weight))
                .Italic(style.Italic)
                .FontColor(color)
                .LineHeight((float)_t.Headings.LineHeight)
                .LetterSpacing((float)style.LetterSpacingEm));

            switch (align)
            {
                case TextAlign.Center:
                    text.AlignCenter();
                    break;
                case TextAlign.Right:
                    text.AlignRight();
                    break;
                default:
                    text.AlignLeft();
                    break;
            }

            if (_numbers.TryGetValue(h, out var num))
            {
                text.Span(num + "  ").FontColor(Hex(_t.Colors.Accent, "#0F6CBD"));
            }

            ComposeInlines(text, h.Inlines, new InlineState { Uppercase = style.Uppercase });
        });
    }

    private void ComposeParagraph(IContainer item, ParagraphBlock p, bool nested, double? spacingOverride = null)
    {
        var spacing = spacingOverride ?? (nested ? Math.Min(_t.Typography.ParagraphSpacingPt, 4) : _t.Typography.ParagraphSpacingPt);
        item.PaddingBottom((float)spacing).Text(text =>
        {
            var align = p.Alignment switch
            {
                Documents.TextAlignment.Center => TextAlign.Center,
                Documents.TextAlignment.Justify => TextAlign.Justify,
                Documents.TextAlignment.Right => TextAlign.Right,
                Documents.TextAlignment.Left => TextAlign.Left,
                _ => _t.Typography.Alignment,
            };
            switch (align)
            {
                case TextAlign.Justify:
                    text.Justify();
                    break;
                case TextAlign.Center:
                    text.AlignCenter();
                    break;
                case TextAlign.Right:
                    text.AlignRight();
                    break;
                default:
                    text.AlignLeft();
                    break;
            }

            // Book-style indents belong to left-aligned and justified text; a centred or right-aligned paragraph
            // (a title block, a signature line) would just look shifted.
            if (_t.Typography.FirstLineIndentPt > 0 && !nested && !_noIndent.Contains(p) && align is TextAlign.Left or TextAlign.Justify)
            {
                text.ParagraphFirstLineIndentation((float)_t.Typography.FirstLineIndentPt);
            }

            ComposeInlines(text, p.Inlines, new InlineState { Hyphenate = _t.Typography.Hyphenate });
        });
    }

    private void ComposeList(IContainer item, ListBlock list, bool nested)
    {
        var markerColor = Hex(_t.Lists.MarkerColor ?? _t.Colors.Accent, "#0F6CBD");
        var indent = (float)_t.Lists.IndentPt;
        var bottom = nested ? 2f : (float)_t.Typography.ParagraphSpacingPt;

        item.PaddingBottom(bottom).Column(col =>
        {
            col.Spacing((float)_t.Lists.ItemSpacingPt);
            var number = list.Start;
            foreach (var li in list.Items)
            {
                var marker = list.Ordered ? FormatNumber(number++, _t.Lists.Numbering) : Bullet(_t.Lists.Bullet);
                col.Item().Row(row =>
                {
                    if (li.Checked is { } isChecked && _t.Lists.TaskCheckboxes)
                    {
                        row.ConstantItem(indent).AlignLeft().PaddingTop((float)(_t.Typography.BodySizePt * 0.22)).Element(box =>
                        {
                            var size = (float)(_t.Typography.BodySizePt * 0.85);
                            var b = box.Width(size).Height(size).Border(0.9f).BorderColor(markerColor).CornerRadius(size * 0.18f);
                            if (isChecked)
                            {
                                var mark = NormalizeHex(_t.Lists.MarkerColor ?? _t.Colors.Accent) ?? "#0F6CBD";
                                var tick = IsDark(mark) ? "#FFFFFF" : "#111111";
                                b.Background(markerColor).Svg($"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 16 16' width='16' height='16'><path d='M3.6 8.4 L6.6 11.4 L12.4 4.8' stroke='{tick}' stroke-width='2.2' fill='none' stroke-linecap='round' stroke-linejoin='round'/></svg>").FitArea();
                            }
                        });
                    }
                    else
                    {
                        row.ConstantItem(indent).AlignLeft().Text(marker).FontColor(markerColor);
                    }

                    row.RelativeItem().Column(inner =>
                    {
                        for (var k = 0; k < li.Blocks.Count; k++)
                        {
                            // Tight lists: the item's last paragraph adds no space; Lists.ItemSpacingPt spaces items.
                            if (list.Tight && k == li.Blocks.Count - 1 && li.Blocks[k] is ParagraphBlock lastPara)
                            {
                                ComposeParagraph(inner.Item(), lastPara, nested: true, spacingOverride: 0);
                            }
                            else
                            {
                                ComposeBlock(inner, li.Blocks[k], nested: true);
                            }
                        }
                    });
                });
            }
        });
    }

    private void ComposeCode(IContainer item, CodeBlock code)
    {
        var c = _t.Code;
        var bg = Hex(c.Background ?? _t.Colors.Surface, "#F3F4F6");
        var fg = Hex(c.TextColor ?? _t.Colors.Text, "#1F2937");
        var border = Hex(c.BorderColor ?? _t.Colors.Border, "#D1D5DB");
        var mono = FontChain(c.Font);
        var dark = IsDark(c.Background ?? _t.Colors.Surface);

        // Invisible marker carrying the language (a named destination at the block's top-left that draws nothing),
        // so the PDF importer can restore it when the theme prints no language label.
        if (MarkerLanguage(code.Language) is { } language)
        {
            item = item.Section(string.Create(CultureInfo.InvariantCulture, $"{CodeMarkerPrefix}{++_codeMarkers}-{language}"));
        }

        var outer = item.PaddingBottom((float)_t.Typography.ParagraphSpacingPt);
        outer.Column(col =>
        {
            if (c.ShowLanguageLabel && (!string.IsNullOrWhiteSpace(code.Caption) || !string.IsNullOrWhiteSpace(code.Language)))
            {
                col.Item().PaddingBottom(2).Text(code.Caption ?? code.Language!).FontSize(Scaled(c.SizePt * 0.9)).FontColor(Hex(_t.Colors.Muted, "#6B7280")).FontFamily(mono);
            }

            var box = col.Item().Background(bg);
            if (c.BorderWidthPt > 0)
            {
                box = box.Border((float)c.BorderWidthPt).BorderColor(border);
            }

            if (c.CornerRadiusPt > 0)
            {
                box = box.CornerRadius((float)c.CornerRadiusPt);
            }

            var padding = (float)c.PaddingPt;
            box = box.Padding(padding);

            var source = code.Code.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd('\n').Replace("\t", "    ", StringComparison.Ordinal);
            IReadOnlyList<HighlightedRun>? runs = null;
            if (c.SyntaxHighlighting && _highlighter is not null)
            {
                try
                {
                    runs = _highlighter.Highlight(source, code.Language, c.Palette, dark);
                }
                catch (Exception)
                {
                    runs = null; // a highlighter must never break the document
                }
            }

            var lines = SplitLines(runs ?? [new HighlightedRun(source, null)]);
            var digits = Math.Max(2, lines.Count.ToString(CultureInfo.InvariantCulture).Length);
            var size = Scaled(c.SizePt);
            var gutter = c.ShowLineNumbers ? (float)((c.SizePt * MonoAdvance * digits) + 10) : 0f;

            if (!c.WrapLongLines)
            {
                // No wrapping: shrink the block's font (down to 75 %) so the longest line fits; any line still too
                // long is clipped with an ellipsis instead of wrapping. Nested blocks (lists, quotes) are narrower
                // than estimated, so the clip is the backstop.
                var available = WidthPt - (2 * padding) - (2 * c.BorderWidthPt) - gutter;
                var longest = lines.Count == 0 ? 0 : lines.Max(l => l.Sum(r => r.Text.Length));
                var needed = longest * MonoAdvance * c.SizePt;
                if (needed > available && longest > 0)
                {
                    size = (float)Math.Max(c.SizePt * 0.75, available / (longest * MonoAdvance));
                }
            }

            var lineHeight = (float)c.LineHeight;
            var muted = Hex(_t.Colors.Muted, "#6B7280");

            void LineText(IContainer target, List<HighlightedRun> line)
            {
                target.Text(text =>
                {
                    text.DefaultTextStyle(ts =>
                    {
                        // Programming ligatures (-> != =>) are turned off so code reads exactly as typed.
                        var s = ts.FontFamily(mono).FontSize(size).FontColor(fg).LineHeight(lineHeight)
                            .DisableFontFeature("calt").DisableFontFeature("liga");
                        return c.WrapLongLines ? s.BreakAnywhere() : s;
                    });

                    if (!c.WrapLongLines)
                    {
                        text.ClampLines(1, "…");
                    }

                    if (line.Count == 0)
                    {
                        text.Span(" ");
                        return;
                    }

                    foreach (var run in line)
                    {
                        var span = text.Span(run.Text);
                        if (run.ColorHex is not null)
                        {
                            span.FontColor(Hex(run.ColorHex, c.TextColor ?? _t.Colors.Text));
                        }

                        if (run.Bold)
                        {
                            span.Bold();
                        }

                        if (run.Italic)
                        {
                            span.Italic();
                        }
                    }
                });
            }

            // One text element per source line: line numbers stay aligned with wrapped lines and long blocks page
            // cleanly between lines.
            box.Column(lc =>
            {
                for (var n = 0; n < lines.Count; n++)
                {
                    var line = lines[n];
                    if (c.ShowLineNumbers)
                    {
                        var number = (n + 1).ToString(CultureInfo.InvariantCulture);
                        lc.Item().Row(row =>
                        {
                            row.ConstantItem(gutter).PaddingRight(8).AlignRight().Text(number)
                                .FontFamily(mono).FontSize(size).FontColor(muted).LineHeight(lineHeight);
                            row.RelativeItem().Element(e => LineText(e, line));
                        });
                    }
                    else
                    {
                        LineText(lc.Item(), line);
                    }
                }
            });
        });
    }

    /// <summary>The language as a marker name part (lower case letters, digits and <c>+#._-</c>), or null.</summary>
    private static string? MarkerLanguage(string? language)
    {
        var value = language?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(value) || value.Length > 32 || !value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '+' or '#' or '.' or '_' or '-')
            ? null
            : value;
    }

    /// <summary>Approximate advance width of a monospace glyph in em (JetBrains Mono: 600/1000).</summary>
    private const double MonoAdvance = 0.6;

    private static List<List<HighlightedRun>> SplitLines(IReadOnlyList<HighlightedRun> runs)
    {
        var lines = new List<List<HighlightedRun>> { new() };
        foreach (var run in runs)
        {
            var text = run.Text;
            var start = 0;
            while (true)
            {
                var nl = text.IndexOf('\n', start);
                var piece = nl < 0 ? text[start..] : text[start..nl];
                if (piece.Length > 0)
                {
                    lines[^1].Add(run with { Text = piece });
                }

                if (nl < 0)
                {
                    break;
                }

                lines.Add([]);
                start = nl + 1;
            }
        }

        return lines;
    }

    private void ComposeQuote(IContainer item, QuoteBlock quote)
    {
        var q = _t.Quotes;
        var border = Hex(q.BorderColor ?? _t.Colors.Accent, "#0F6CBD");
        var c = item.PaddingBottom((float)_t.Typography.ParagraphSpacingPt);
        if (q.BorderWidthPt > 0)
        {
            c = c.BorderLeft((float)q.BorderWidthPt).BorderColor(border);
        }

        if (!string.IsNullOrWhiteSpace(q.Background))
        {
            c = c.Background(Hex(q.Background, "#F3F4F6"));
        }

        c.PaddingLeft((float)q.PaddingPt).PaddingVertical((float)(q.PaddingPt * 0.5)).PaddingRight((float)(q.PaddingPt * 0.5))
            .DefaultTextStyle(ts => ts.Italic(q.Italic).FontSize(Scaled(_t.Typography.BodySizePt * q.FontScale)).FontColor(Hex(_textColor ?? q.TextColor ?? _t.Colors.Text, "#1F2937")))
            .Column(col =>
            {
                var (blocks, attribution) = SplitAttribution(quote);
                foreach (var b in blocks)
                {
                    ComposeBlock(col, b, nested: true);
                }

                var mutedColor = Hex(_t.Colors.Muted, "#6B7280");
                var attrSize = Scaled(_t.Typography.BodySizePt * 0.9);
                if (attribution is not null)
                {
                    col.Item().PaddingTop(2).Text(text =>
                    {
                        text.AlignRight();
                        text.DefaultTextStyle(ts => ts.Italic(false).FontSize(attrSize).FontColor(mutedColor));
                        text.Span("— ");
                        ComposeInlines(text, attribution, new InlineState());
                    });
                }
                else if (!string.IsNullOrWhiteSpace(quote.Attribution))
                {
                    col.Item().PaddingTop(2).AlignRight().Text("— " + quote.Attribution).Italic(false).FontSize(attrSize).FontColor(mutedColor);
                }
            });
    }

    /// <summary>
    /// A quote whose last paragraph (or last line of its last paragraph) starts with an em dash or "--" is treated as
    /// "quote — attribution". Returns the blocks to render and the attribution inlines (without the dash), never
    /// mutating the model.
    /// </summary>
    private static (IReadOnlyList<Block> Blocks, List<Inline>? Attribution) SplitAttribution(QuoteBlock quote)
    {
        if (!string.IsNullOrWhiteSpace(quote.Attribution) || quote.Blocks.Count == 0 || quote.Blocks[^1] is not ParagraphBlock last)
        {
            return (quote.Blocks, null);
        }

        var blocks = quote.Blocks.Take(quote.Blocks.Count - 1).ToList();
        if (StripDash(last.Inlines) is { } whole)
        {
            return blocks.Count == 0 ? (quote.Blocks, null) : (blocks, whole);
        }

        var br = last.Inlines.FindLastIndex(i => i is LineBreakInline);
        if (br <= 0 || StripDash(last.Inlines.Skip(br + 1).ToList()) is not { } tail)
        {
            return (quote.Blocks, null);
        }

        var head = new ParagraphBlock { Alignment = last.Alignment };
        head.Inlines.AddRange(last.Inlines.Take(br));
        blocks.Add(head);
        return (blocks, tail);
    }

    private static List<Inline>? StripDash(List<Inline> inlines)
    {
        if (inlines.Count == 0 || inlines[0] is not TextInline first)
        {
            return null;
        }

        var text = first.Text.TrimStart();
        string rest;
        if (text.StartsWith('—') || text.StartsWith('―') || text.StartsWith('–'))
        {
            rest = text[1..];
        }
        else if (text.StartsWith("--", StringComparison.Ordinal))
        {
            rest = text[2..];
        }
        else
        {
            return null;
        }

        rest = rest.TrimStart();
        var result = new List<Inline>(inlines.Count);
        if (rest.Length > 0)
        {
            result.Add(new TextInline(rest));
        }

        result.AddRange(inlines.Skip(1));
        return Inline.ToPlainText(result).Trim().Length == 0 ? null : result;
    }

    private void ComposeCallout(IContainer item, CalloutBlock callout)
    {
        var cs = _t.Callouts;
        var kind = cs.Kind(callout.Kind);
        var accentHex = kind.Accent ?? _t.Colors.Accent;
        var color = Hex(accentHex, "#0F6CBD");
        var label = !string.IsNullOrWhiteSpace(callout.Title) ? callout.Title : kind.Label ?? callout.Kind.ToString();
        var labelSize = Scaled(_t.Typography.BodySizePt * 0.95);

        var box = item.PaddingBottom((float)_t.Typography.ParagraphSpacingPt)
            .Background(Hex(kind.Background ?? cs.Background ?? _t.Colors.Surface, "#F3F4F6"));
        if (cs.BorderWidthPt > 0)
        {
            box = box.BorderLeft((float)cs.BorderWidthPt).BorderColor(color);
        }

        if (cs.CornerRadiusPt > 0)
        {
            box = box.CornerRadius((float)cs.CornerRadiusPt);
        }

        box.Padding((float)cs.PaddingPt).Column(col =>
        {
            if (cs.ShowIcon || cs.ShowLabel)
            {
                col.Item().PaddingBottom(4).Row(row =>
                {
                    if (cs.ShowIcon)
                    {
                        var iconSize = labelSize * 1.15f;
                        row.ConstantItem(iconSize).AlignMiddle().Height(iconSize).Svg(CalloutIcon(callout.Kind, accentHex)).FitArea();
                        row.ConstantItem(6);
                    }

                    if (cs.ShowLabel)
                    {
                        row.RelativeItem().AlignMiddle().Text(label).SemiBold().FontColor(color).FontSize(labelSize).LineHeight(1.2f);
                    }
                    else
                    {
                        row.RelativeItem();
                    }
                });
            }

            foreach (var b in callout.Blocks)
            {
                ComposeBlock(col, b, nested: true);
            }
        });
    }

    /// <summary>Tiny vector icons (no font glyph dependencies): info, check, exclamation, warning triangle, stop sign.</summary>
    private static string CalloutIcon(CalloutKind kind, string accentHex)
    {
        var a = NormalizeHex(accentHex) ?? "#0F6CBD";
        var f = IsDark(a) ? "#FFFFFF" : "#111111";
        var body = kind switch
        {
            CalloutKind.Tip => $"<circle cx='8' cy='8' r='8' fill='{a}'/><path d='M4.4 8.4 L6.9 10.9 L11.6 5.5' stroke='{f}' stroke-width='1.8' fill='none' stroke-linecap='round' stroke-linejoin='round'/>",
            CalloutKind.Important => $"<circle cx='8' cy='8' r='8' fill='{a}'/><rect x='7' y='3.4' width='2' height='6.2' rx='1' fill='{f}'/><circle cx='8' cy='12.2' r='1.2' fill='{f}'/>",
            CalloutKind.Warning => $"<path d='M8 0.8 L15.6 14.8 L0.4 14.8 Z' fill='{a}'/><rect x='7.1' y='5.4' width='1.8' height='5' rx='0.9' fill='{f}'/><circle cx='8' cy='12.6' r='1.05' fill='{f}'/>",
            CalloutKind.Caution => $"<polygon points='4.7,0.3 11.3,0.3 15.7,4.7 15.7,11.3 11.3,15.7 4.7,15.7 0.3,11.3 0.3,4.7' fill='{a}'/><path d='M5.5 5.5 L10.5 10.5 M10.5 5.5 L5.5 10.5' stroke='{f}' stroke-width='1.8' stroke-linecap='round'/>",
            _ => $"<circle cx='8' cy='8' r='8' fill='{a}'/><rect x='7' y='6.8' width='2' height='5.8' rx='1' fill='{f}'/><circle cx='8' cy='4.4' r='1.2' fill='{f}'/>",
        };
        return $"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 16 16' width='16' height='16'>{body}</svg>";
    }

    private void ComposeTable(IContainer item, TableBlock table)
    {
        var tb = _t.Tables;
        var columns = Math.Max(1, table.ColumnCount);
        var border = Hex(tb.BorderColor ?? _t.Colors.Border, "#D1D5DB");
        var headerBg = tb.HeaderBackground is null ? (QColor?)null : Hex(tb.HeaderBackground, "#F3F4F6");
        if (headerBg is null && tb.Style is TableStyle.Grid or TableStyle.Striped)
        {
            headerBg = Hex(_t.Colors.Surface, "#F3F4F6");
        }

        var headerFg = Hex(tb.HeaderTextColor ?? _t.Colors.Text, "#1F2937");
        var stripe = Hex(tb.StripeColor ?? _t.Colors.Surface, "#F3F4F6");
        var fontSize = Scaled(_t.Typography.BodySizePt * tb.FontScale);
        var pad = (float)tb.CellPaddingPt;
        var bw = (float)tb.BorderWidthPt;

        item.PaddingBottom((float)_t.Typography.ParagraphSpacingPt).Column(col =>
        {
            if (tb.ShowCaptions && !string.IsNullOrWhiteSpace(table.Caption))
            {
                col.Item().PaddingBottom(4).Text(table.Caption).FontSize(Scaled(_t.Typography.BodySizePt * 0.9)).Italic().FontColor(Hex(_t.Colors.Muted, "#6B7280"));
            }

            col.Item().Table(t =>
            {
                t.ColumnsDefinition(cd =>
                {
                    for (var i = 0; i < columns; i++)
                    {
                        cd.RelativeColumn();
                    }
                });

                var headerRows = table.Rows.Where(r => r.IsHeader).ToList();
                var bodyRows = table.Rows.Where(r => !r.IsHeader).ToList();

                IContainer CellStyle(IContainer cell, bool isHeader, int rowIndex, bool lastRow)
                {
                    var c = cell;
                    switch (tb.Style)
                    {
                        case TableStyle.Grid:
                            c = c.Border(bw).BorderColor(border);
                            break;
                        case TableStyle.Lines:
                            c = c.BorderBottom(isHeader ? bw * 1.6f : bw).BorderColor(border);
                            break;
                        case TableStyle.Minimal:
                            c = isHeader ? c.BorderBottom(bw * 1.6f).BorderColor(border) : c;
                            break;
                        case TableStyle.Striped:
                            c = c.BorderBottom(isHeader ? bw * 1.6f : 0).BorderColor(border);
                            if (!isHeader && rowIndex % 2 == 1)
                            {
                                c = c.Background(stripe);
                            }

                            break;
                    }

                    if (isHeader && headerBg is { } hb)
                    {
                        c = c.Background(hb);
                    }

                    return c.Padding(pad);
                }

                void FillCell(IContainer container, TableCell cell, int column, bool isHeader)
                {
                    container.DefaultTextStyle(ts =>
                    {
                        var s = ts.FontSize(fontSize).LineHeight(1.3f);
                        return isHeader ? s.Weight(Weight(tb.HeaderWeight)).FontColor(headerFg) : s;
                    }).Column(cc =>
                    {
                        foreach (var b in cell.Blocks)
                        {
                            if (b is ParagraphBlock p)
                            {
                                cc.Item().Text(text =>
                                {
                                    switch (table.GetAlignment(column))
                                    {
                                        case ColumnAlignment.Center:
                                            text.AlignCenter();
                                            break;
                                        case ColumnAlignment.Right:
                                            text.AlignRight();
                                            break;
                                        default:
                                            text.AlignLeft();
                                            break;
                                    }

                                    ComposeInlines(text, p.Inlines, new InlineState());
                                });
                            }
                            else
                            {
                                ComposeBlock(cc, b, nested: true);
                            }
                        }
                    });
                }

                void HeaderCells(Func<ITableCellContainer> newCell)
                {
                    foreach (var row in headerRows)
                    {
                        var column = 0;
                        foreach (var cell in row.Cells)
                        {
                            var c = newCell();
                            if (cell.ColumnSpan > 1)
                            {
                                c = c.ColumnSpan((uint)cell.ColumnSpan);
                            }

                            var col0 = column;
                            c.Element(e => CellStyle(e, true, 0, false)).Element(e => FillCell(e, cell, col0, true));
                            column += cell.ColumnSpan;
                        }
                    }
                }

                if (headerRows.Count > 0)
                {
                    if (tb.RepeatHeaderOnNewPage)
                    {
                        // The layout engine repeats table.Header on every page the table spans.
                        t.Header(h => HeaderCells(h.Cell));
                    }
                    else
                    {
                        HeaderCells(t.Cell);
                    }
                }

                for (var r = 0; r < bodyRows.Count; r++)
                {
                    var row = bodyRows[r];
                    var column = 0;
                    foreach (var cell in row.Cells)
                    {
                        var c = t.Cell();
                        if (cell.ColumnSpan > 1)
                        {
                            c = c.ColumnSpan((uint)cell.ColumnSpan);
                        }

                        if (cell.RowSpan > 1)
                        {
                            c = c.RowSpan((uint)cell.RowSpan);
                        }

                        var rowIndex = r;
                        var col0 = column;
                        var last = r == bodyRows.Count - 1;
                        c.Element(e => CellStyle(e, false, rowIndex, last)).Element(e => FillCell(e, cell, col0, false));
                        column += cell.ColumnSpan;
                    }
                }
            });
        });
    }

    /// <summary>Images leave room for a heading and a caption on the same page.</summary>
    private float MaxImageHeight => (float)(_bodyHeightPt * 0.72);

    /// <summary>
    /// Width and (when the pixel size is known) exact height of an image box, so Border and CornerRadius hug the
    /// picture. Small images are not blown up past their natural size (96 dpi) unless the source asked for a width.
    /// </summary>
    private (float Width, float? Height) ImageBoxSize(ImageBlock image, byte[]? data, bool isSvg)
    {
        var pct = Math.Clamp(image.WidthPercent ?? _t.Images.MaxWidthPercent, 5, 100) / 100.0;
        var width = (float)(WidthPt * pct);
        var (pw, ph) = image.PixelWidth is > 0 && image.PixelHeight is > 0
            ? (image.PixelWidth.Value, image.PixelHeight.Value)
            : data is not null && !isSvg ? ImageHeader.ReadPixelSize(data) : (0, 0);
        if (pw <= 0 || ph <= 0)
        {
            return (width, null);
        }

        if (image.WidthPercent is null)
        {
            width = Math.Min(width, pw * 0.75f);
        }

        var height = width * ph / pw;
        if (height > MaxImageHeight)
        {
            height = MaxImageHeight;
            width = height * pw / ph;
        }

        return (width, height);
    }

    private double EstimateImageHeight(ImageBlock image)
    {
        var data = ResolveImageBytes(image.Source, out var isSvg);
        var (_, height) = ImageBoxSize(image, data, isSvg);
        var caption = _t.Images.ShowCaptions && image.ResolveCaption() is not null ? _t.Images.CaptionSizePt * 1.6 + 4 : 0;
        return (height ?? MaxImageHeight) + caption + _t.Images.SpacingPt;
    }

    private void ComposeImage(IContainer item, ImageBlock image)
    {
        var im = _t.Images;
        var data = ResolveImageBytes(image.Source, out var isSvg);
        var outer = item.PaddingBottom((float)im.SpacingPt);

        outer.Column(col =>
        {
            var holder = col.Item();
            holder = im.Align switch
            {
                ImageAlign.Left => holder.AlignLeft(),
                ImageAlign.Right => holder.AlignRight(),
                _ => holder.AlignCenter(),
            };

            var (width, height) = ImageBoxSize(image, data, isSvg);
            var box = height is { } h ? holder.Width(width).Height(h) : holder.Width(width).MaxHeight(MaxImageHeight);

            if (im.CornerRadiusPt > 0)
            {
                box = box.CornerRadius((float)im.CornerRadiusPt);
            }

            if (im.Border)
            {
                box = box.Border(0.75f).BorderColor(Hex(_t.Colors.Border, "#D1D5DB"));
            }

            if (data is null)
            {
                box.Background(Hex(_t.Colors.Surface, "#F3F4F6")).Padding(12).AlignCenter().Text(image.AltText ?? image.Source).Italic().FontColor(Hex(_t.Colors.Muted, "#6B7280"));
            }
            else if (isSvg)
            {
                box.Svg(Encoding.UTF8.GetString(data)).FitArea();
            }
            else
            {
                try
                {
                    box.Image(data).FitArea();
                }
                catch (Exception)
                {
                    box.Background(Hex(_t.Colors.Surface, "#F3F4F6")).Padding(12).AlignCenter().Text(image.AltText ?? "Unsupported image").Italic().FontColor(Hex(_t.Colors.Muted, "#6B7280"));
                }
            }

            var caption = image.ResolveCaption();
            if (im.ShowCaptions && !string.IsNullOrWhiteSpace(caption))
            {
                var cap = col.Item().PaddingTop(4);
                cap = im.Align switch
                {
                    ImageAlign.Left => cap.AlignLeft(),
                    ImageAlign.Right => cap.AlignRight(),
                    _ => cap.AlignCenter(),
                };
                cap.Text(caption).FontSize((float)im.CaptionSizePt).Italic().FontColor(Hex(_t.Colors.Muted, "#6B7280"));
            }
        });
    }

    private void ComposeFootnoteItems(ColumnDescriptor col)
    {
        col.Item().PaddingTop(18).PaddingBottom(6).LineHorizontal(0.5f).LineColor(Hex(_t.Colors.Border, "#D1D5DB"));
        foreach (var fn in _doc.Footnotes)
        {
            col.Item().Row(row =>
            {
                row.ConstantItem(18).Text(fn.Label).FontSize((float)(_t.Typography.BodySizePt * 0.8)).FontColor(Hex(_t.Colors.Muted, "#6B7280"));
                row.RelativeItem().DefaultTextStyle(ts => ts.FontSize((float)(_t.Typography.BodySizePt * 0.85))).Column(inner =>
                {
                    foreach (var b in fn.Blocks)
                    {
                        ComposeBlock(inner, b, nested: true);
                    }
                });
            });
        }
    }

    // -------------------------------------------------------------- inlines

    private sealed class InlineState
    {
        public bool Bold;
        public bool Italic;
        public bool Strike;
        public bool Underline;
        public bool Highlight;
        public bool Super;
        public bool Sub;
        public bool Uppercase;
        public bool Hyphenate;

        public InlineState Clone() => (InlineState)MemberwiseClone();
    }

    private void ComposeInlines(TextDescriptor text, IEnumerable<Inline> inlines, InlineState state)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TextInline t:
                    {
                        var value = state.Uppercase ? t.Text.ToUpperInvariant() : t.Text;
                        if (state.Hyphenate)
                        {
                            value = Hyphenator.Insert(value);
                        }

                        Apply(text.Span(value), state);
                        break;
                    }

                case StyledInline s:
                    {
                        var next = state.Clone();
                        if (s.Style.HasFlag(InlineStyle.Bold)) next.Bold = true;
                        if (s.Style.HasFlag(InlineStyle.Italic)) next.Italic = true;
                        if (s.Style.HasFlag(InlineStyle.Strikethrough)) next.Strike = true;
                        if (s.Style.HasFlag(InlineStyle.Underline)) next.Underline = true;
                        if (s.Style.HasFlag(InlineStyle.Highlight)) next.Highlight = true;
                        if (s.Style.HasFlag(InlineStyle.Superscript)) next.Super = true;
                        if (s.Style.HasFlag(InlineStyle.Subscript)) next.Sub = true;
                        if (s.Style.HasFlag(InlineStyle.SmallCaps)) next.Uppercase = true;
                        ComposeInlines(text, s.Inlines, next);
                        break;
                    }

                case CodeInline c:
                    {
                        var span = text.Span(c.Code)
                            .FontFamily(FontChain(_t.Code.Font))
                            .DisableFontFeature("calt").DisableFontFeature("liga")
                            .FontSize(Scaled(_t.Typography.BodySizePt * _t.Code.InlineScale))
                            .BackgroundColor(Hex(_t.Code.InlineBackground ?? _t.Code.Background ?? _t.Colors.Surface, "#F3F4F6"));
                        if (_t.Code.InlineTextColor is not null || _t.Code.TextColor is not null)
                        {
                            span.FontColor(Hex(_t.Code.InlineTextColor ?? _t.Code.TextColor!, "#1F2937"));
                        }

                        break;
                    }

                case LinkInline link:
                    {
                        var label = Inline.ToPlainText(link.Inlines);
                        if (string.IsNullOrEmpty(label))
                        {
                            label = link.Url;
                        }

                        var linkColor = Hex(_t.Links.Color ?? _t.Colors.Accent, "#0F6CBD");
                        TextSpanDescriptor span;
                        if (link.IsInternal)
                        {
                            var target = link.Url.TrimStart('#');
                            var heading = _sectionIds.FirstOrDefault(kv => string.Equals(kv.Value, target, StringComparison.OrdinalIgnoreCase) || string.Equals(kv.Key.Id, target, StringComparison.OrdinalIgnoreCase));
                            span = heading.Key is null ? text.Span(label) : text.SectionLink(label, heading.Value);
                        }
                        else if (Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https" || uri.Scheme == "mailto"))
                        {
                            span = text.Hyperlink(label, link.Url);
                        }
                        else
                        {
                            span = text.Span(label);
                        }

                        Apply(span, state);
                        span.FontColor(linkColor).Underline(_t.Links.Underline);
                        SpanWeight(span, _t.Links.Weight);
                        if (_t.Links.ShowUrl && !link.IsInternal && !string.Equals(label, link.Url, StringComparison.Ordinal))
                        {
                            text.Span(" (" + link.Url + ")").FontSize(Scaled(_t.Typography.BodySizePt * 0.85)).FontColor(Hex(_t.Colors.Muted, "#6B7280"));
                        }

                        break;
                    }

                case ImageInline img:
                    {
                        var data = ResolveImageBytes(img.Source, out var isSvg);
                        if (data is not null && !isSvg)
                        {
                            var h = (float)(_t.Typography.BodySizePt * 1.2);
                            text.Element().Height(h).Image(data).FitHeight();
                        }
                        else if (!string.IsNullOrEmpty(img.AltText))
                        {
                            Apply(text.Span(img.AltText), state);
                        }

                        break;
                    }

                case LineBreakInline br:
                    text.Span(br.IsHard ? "\n" : " ");
                    break;

                case FootnoteReferenceInline fr:
                    text.Span(fr.Label).Superscript().FontColor(Hex(_t.Colors.Accent, "#0F6CBD"));
                    break;

                case MathInline math:
                    text.Span(math.Latex).FontFamily(FontChain(_t.Code.Font)).FontSize(Scaled(_t.Typography.BodySizePt * 0.95));
                    break;

                case HtmlInline:
                    break;
            }
        }
    }

    private void Apply(TextSpanDescriptor span, InlineState s)
    {
        if (s.Bold) span.Bold();
        if (s.Italic) span.Italic();
        if (s.Strike) span.Strikethrough();
        if (s.Underline) span.Underline();
        if (s.Highlight) span.BackgroundColor(QColor.FromHex(_darkPage ? "#5C4B00" : "#FFF3A0"));
        if (s.Super) span.Superscript();
        if (s.Sub) span.Subscript();
    }

    // -------------------------------------------------------------- helpers

    private byte[]? ResolveImageBytes(string source, out bool isSvg)
    {
        isSvg = false;
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        var res = _doc.FindResource(source);
        if (res is not null)
        {
            isSvg = res.MediaType.Contains("svg", StringComparison.OrdinalIgnoreCase);
            return res.Data;
        }

        if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = source.IndexOf(',', StringComparison.Ordinal);
            if (comma > 0 && source[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    isSvg = source[..comma].Contains("svg", StringComparison.OrdinalIgnoreCase);
                    return Convert.FromBase64String(source[(comma + 1)..]);
                }
                catch (FormatException)
                {
                    return null;
                }
            }

            return null;
        }

        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && !uri.IsFile)
        {
            // Remote images are never fetched: the app is offline by design.
            return null;
        }

        var path = uri is { IsFile: true } ? uri.LocalPath : source;
        if (!Path.IsPathRooted(path) && _doc.Metadata.SourcePath is { } sp)
        {
            path = Path.Combine(Path.GetDirectoryName(sp) ?? string.Empty, path);
        }

        try
        {
            if (File.Exists(path) && new FileInfo(path).Length < 50 * 1024 * 1024)
            {
                isSvg = path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);
                return File.ReadAllBytes(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }

    /// <summary>
    /// Registers headings that will be composed later: gives each a unique section id (link and page-number target;
    /// a heading without <see cref="HeadingBlock.Id"/> gets a slug of its text, written back to the block), a number
    /// when the theme numbers headings, and a <c>{section}</c> capture for running headers. <c>{section}</c> follows
    /// the shallowest level that occurs more than once among <paramref name="headings"/> (H1 normally; H2 when there
    /// is a single title-like H1). Call it once with every heading in reading order before composing; later calls
    /// continue the ids and numbering. Headings already registered are skipped.
    /// </summary>
    public void RegisterHeadings(IEnumerable<HeadingBlock> headings)
    {
        ArgumentNullException.ThrowIfNull(headings);
        var list = headings.Where(h => h is not null && !_sectionIds.ContainsKey(h)).Distinct().ToList();
        foreach (var h in list)
        {
            var id = string.IsNullOrWhiteSpace(h.Id) ? DocumentHtmlRenderer.Slugify(h.ToPlainText()) : h.Id!;
            var unique = id;
            var n = 2;
            while (!_usedIds.Add(unique))
            {
                unique = $"{id}-{n++}";
            }

            h.Id ??= unique;
            _sectionIds[h] = "sec-" + unique;

            if (_t.Headings.NumberHeadings)
            {
                var level = Math.Clamp(h.Level, 1, _counters.Length - 1);
                _counters[level]++;
                for (var l = level + 1; l < _counters.Length; l++)
                {
                    _counters[l] = 0;
                }

                _numbers[h] = string.Join('.', _counters.Skip(1).Take(level).Select(c => c.ToString(CultureInfo.InvariantCulture)));
            }
        }

        // {section} follows the shallowest heading level that occurs more than once (H1 normally; H2 when the
        // document has a single title-like H1 or no H1 at all).
        if (list.Count > 0)
        {
            var level = list.GroupBy(h => h.Level).OrderBy(g => g.Key).FirstOrDefault(g => g.Count() > 1)?.Key ?? list.Min(h => h.Level);
            foreach (var h in list.Where(h => h.Level == level))
            {
                var captureId = "aio-section-" + _sections.Count.ToString(CultureInfo.InvariantCulture);
                var text = (_numbers.TryGetValue(h, out var num) ? num + "  " : string.Empty) + h.ToPlainText();
                _sections.Add((captureId, text));
                _sectionCaptureIds[h] = captureId;
            }
        }
    }

    private static void SpanWeight(TextSpanDescriptor span, ThemeFontWeight w)
    {
        switch (w)
        {
            case ThemeFontWeight.Medium:
                span.Medium();
                break;
            case ThemeFontWeight.SemiBold:
                span.SemiBold();
                break;
            case ThemeFontWeight.Bold:
                span.Bold();
                break;
            default:
                span.NormalWeight();
                break;
        }
    }

    private static QFontWeight Weight(ThemeFontWeight w) => w switch
    {
        ThemeFontWeight.Medium => QFontWeight.Medium,
        ThemeFontWeight.SemiBold => QFontWeight.SemiBold,
        ThemeFontWeight.Bold => QFontWeight.Bold,
        _ => QFontWeight.Normal,
    };

    private static string Bullet(BulletStyle style) => style switch
    {
        BulletStyle.Circle => "◦",
        BulletStyle.Square => "▪",
        BulletStyle.Dash => "–",
        BulletStyle.Arrow => "›",
        _ => "•",
    };

    private static string FormatNumber(int n, NumberStyle style) => style switch
    {
        NumberStyle.DecimalParen => n.ToString(CultureInfo.InvariantCulture) + ")",
        NumberStyle.LowerAlpha => Alpha(n).ToLowerInvariant() + ".",
        NumberStyle.UpperAlpha => Alpha(n) + ".",
        NumberStyle.LowerRoman => Roman(n).ToLowerInvariant() + ".",
        NumberStyle.UpperRoman => Roman(n) + ".",
        _ => n.ToString(CultureInfo.InvariantCulture) + ".",
    };

    private static string Alpha(int n)
    {
        var sb = new StringBuilder();
        n = Math.Max(1, n);
        while (n > 0)
        {
            n--;
            sb.Insert(0, (char)('A' + n % 26));
            n /= 26;
        }

        return sb.ToString();
    }

    private static string Roman(int n)
    {
        if (n <= 0 || n >= 4000)
        {
            return n.ToString(CultureInfo.InvariantCulture);
        }

        var values = new[] { 1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1 };
        var symbols = new[] { "M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I" };
        var sb = new StringBuilder();
        for (var i = 0; i < values.Length; i++)
        {
            while (n >= values[i])
            {
                sb.Append(symbols[i]);
                n -= values[i];
            }
        }

        return sb.ToString();
    }

    private static ImageCompressionQuality QualityFrom(int quality) => quality switch
    {
        >= 95 => ImageCompressionQuality.Best,
        >= 85 => ImageCompressionQuality.VeryHigh,
        >= 70 => ImageCompressionQuality.High,
        >= 50 => ImageCompressionQuality.Medium,
        _ => ImageCompressionQuality.Low,
    };

    private static double MmToPt(double mm) => mm * 72.0 / 25.4;

    private static QColor Hex(string? hex, string fallback) => ThemeDrawing.Hex(hex, fallback);

    private static bool IsDark(string? hex) => ThemeDrawing.IsDark(hex);

    private static string[] FontChain(string family) => ThemeDrawing.FontChain(family);

    private static string? NormalizeHex(string? hex) => ThemeDrawing.NormalizeHex(hex);

    [GeneratedRegex(@"\{(?<tok>[a-zA-Z]+)\}|(?<lit>[^{]+|\{)")]
    private static partial Regex TemplateToken();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex StripTags();
}

/// <summary>Colour and font helpers shared by the themed renderers, so every renderer reads theme values the same way.</summary>
public static class ThemeDrawing
{
    /// <summary>
    /// Parses a theme colour (<c>#RGB</c>, <c>#RRGGBB</c> or <c>#AARRGGBB</c>) into a layout engine colour; null, blank or
    /// unparseable values give <paramref name="fallback"/> (which must itself be valid hex).
    /// </summary>
    public static QColor Hex(string? hex, string fallback)
    {
        var value = string.IsNullOrWhiteSpace(hex) ? fallback : hex.Trim();
        try
        {
            return QColor.FromHex(value);
        }
        catch (Exception)
        {
            return QColor.FromHex(fallback);
        }
    }

    /// <summary>True when the colour's relative luminance is below 0.45 (light text reads better on it); false for null or unparseable values.</summary>
    public static bool IsDark(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }

        var s = hex.Trim().TrimStart('#');
        if (s.Length == 8)
        {
            s = s[2..];
        }

        if (s.Length == 3)
        {
            s = string.Concat(s.Select(ch => new string(ch, 2)));
        }

        if (s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            return false;
        }

        var r = (rgb >> 16) & 0xFF;
        var g = (rgb >> 8) & 0xFF;
        var b = rgb & 0xFF;
        var luminance = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;
        return luminance < 0.45;
    }

    /// <summary>
    /// Layout engine font family list for a theme font: the family itself, then the bundled sans as fallback for missing
    /// glyphs (unless the family is the bundled sans).
    /// </summary>
    public static string[] FontChain(string family)
    {
        var list = new List<string> { family };
        if (!string.Equals(family, BundledFonts.Sans, StringComparison.OrdinalIgnoreCase))
        {
            list.Add(BundledFonts.Sans);
        }

        return list.ToArray();
    }

    /// <summary>#RGB / #RRGGBB / #AARRGGBB → #RRGGBB (for SVG); null when unparseable.</summary>
    internal static string? NormalizeHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return null;
        }

        var s = hex.Trim().TrimStart('#');
        if (s.Length == 8)
        {
            s = s[2..];
        }

        if (s.Length == 3)
        {
            s = string.Concat(s.Select(ch => new string(ch, 2)));
        }

        return s.Length == 6 && s.All(char.IsAsciiHexDigit) ? "#" + s : null;
    }
}
