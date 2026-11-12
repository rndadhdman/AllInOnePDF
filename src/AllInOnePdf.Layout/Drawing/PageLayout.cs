using AllInOnePdf.Layout.Elements;
using AllInOnePdf.Layout.Fluent;
using AllInOnePdf.Layout.Infrastructure;

namespace AllInOnePdf.Layout.Drawing;

/// <summary>
/// The page loop. A document is laid out twice: the first pass measures and "draws" on a canvas that records nothing to
/// learn the page count, section pages and captured positions (and the glyphs each font draws); the second pass draws
/// the same tree onto PDF pages with those numbers known, with subset fonts (<see cref="FontSubsets"/>).
/// </summary>
internal static class PageLayout
{
    /// <summary>A run of this many consecutive zero-height partial pages is treated as layout that makes no progress.</summary>
    private const int MaxBlankPartialPages = 100;

    /// <summary>A hard stop for content that never finishes.</summary>
    private const int MaxPages = 50_000;

    /// <summary>Lays the pages out and writes the PDF; returns the page count and the tracked pages.</summary>
    public static DocumentLayoutInfo Generate(IReadOnlyList<PageDescriptor> descriptors, DocumentMetadata metadata, DocumentSettings settings, Stream output)
    {
        using var writer = new PdfDocumentWriter(output, metadata, settings);
        return Generate(descriptors, writer);
    }

    /// <summary>Lays the pages out onto <paramref name="writer"/> (the PDF writer, or bitmaps in tests) and closes it.</summary>
    public static DocumentLayoutInfo Generate(IReadOnlyList<PageDescriptor> descriptors, IPageSink writer)
    {
        var pages = descriptors.Select(static d => new PageTrees(d)).ToList();
        var context = new PageContext();
        var layers = DocumentState.Of(context).Layers;
        layers.IsActive = true;

        // Pass 1: page numbers, sections and captures.
        context.BeginFirstPass();
        var measuring = new MeasuringCanvas();
        foreach (var page in pages)
        {
            page.Initialize(context, measuring);
        }

        foreach (var page in pages)
        {
            Run(page, context, measuring, writer: null, skia: null);
        }

        var pageCount = context.CurrentPage;
        layers.Clear();
        foreach (var page in pages)
        {
            page.Reset();
        }

        // Pass 2: draw, with the fonts cut down to the glyphs pass 1 drew (disposed after the writer has closed the file).
        context.BeginSecondPass(pageCount);
        using var subsets = FontSubsets.Build(measuring.Glyphs);
        using var skia = new SkiaCanvas { Subsets = subsets };
        foreach (var page in pages)
        {
            page.Initialize(context, skia);
        }

        foreach (var page in pages)
        {
            Run(page, context, skia, writer, skia);
        }

        if (context.CurrentPage == 0)
        {
            // A document without page descriptions: emit one blank A4 page so the file is a valid PDF.
            writer.BeginPage(595.4f, 842f);
            writer.EndPage();
        }

        writer.Close();
        return new DocumentLayoutInfo(Math.Max(1, pageCount), new Dictionary<string, PageSpan>(context.TrackedPages, StringComparer.Ordinal))
        {
            TrackedBoxes = context.TrackedBoxes,
            Overflowing = new HashSet<string>(context.Overflowing, StringComparer.Ordinal),
        };
    }

    private static void Run(PageTrees page, PageContext context, IDrawingCanvas canvas, IPageSink? writer, SkiaCanvas? skia)
    {
        var layers = DocumentState.Of(context).Layers;
        var d = page.Descriptor;
        var width = d.PageWidth - d.Left - d.Right;
        var height = d.PageHeight - d.Top - d.Bottom;
        if (width <= 0 || height <= 0)
        {
            throw new DocumentLayoutException($"The page margins leave no room for content (page {context.CurrentPage + 1}: {d.PageWidth:0.#} × {d.PageHeight:0.#} pt).");
        }

        var full = new Size(width, height);

        // Leading page breaks: content that would start with a page that draws nothing is consumed without a page.
        for (var i = 0; i < MaxBlankPartialPages; i++)
        {
            var probe = page.Content.Measure(full);
            if (!probe.IsPartial || probe.Height > LayoutMath.Epsilon)
            {
                break;
            }

            skia?.SetTarget(null);
            page.Content.Draw(full);
            FlushLayers(layers, canvas);
        }

        var blankStreak = 0;
        while (true)
        {
            context.BeginPage();
            var number = context.CurrentPage;
            if (number > MaxPages)
            {
                throw new DocumentLayoutException($"The layout did not finish within {MaxPages} pages; content is not making progress.");
            }

            page.SoftResetHeaderFooter(context);
            var headerPlan = page.Header.Measure(full);
            var footerPlan = page.Footer.Measure(full);
            if (headerPlan.IsWrap || footerPlan.IsWrap)
            {
                throw new DocumentLayoutException($"The page {(headerPlan.IsWrap ? "header" : "footer")} does not fit on page {number}.");
            }

            var headerHeight = headerPlan.HasContent ? headerPlan.Height : 0;
            var footerHeight = footerPlan.HasContent ? footerPlan.Height : 0;
            var contentHeight = height - headerHeight - footerHeight;
            if (contentHeight < -LayoutMath.Epsilon)
            {
                throw new DocumentLayoutException($"The header and footer leave no room for content on page {number}.");
            }

            var contentSize = new Size(width, Math.Max(0, contentHeight));
            var contentPlan = page.Content.Measure(contentSize);
            if (contentPlan.IsWrap)
            {
                throw new DocumentLayoutException(
                    $"Content does not fit on page {number}: an element that may not break (PreventPageBreak, a fixed height, an image or a row) is larger than the page's content area ({width:0.#} × {contentHeight:0.#} pt).");
            }

            if (contentPlan.IsPartial && contentPlan.Height <= LayoutMath.Epsilon)
            {
                if (++blankStreak > MaxBlankPartialPages)
                {
                    throw new DocumentLayoutException($"Layout makes no progress on page {number}: content keeps answering an empty partial render.");
                }
            }
            else
            {
                blankStreak = 0;
            }

            if (writer is not null)
            {
                skia!.SetTarget(writer.BeginPage(d.PageWidth, d.PageHeight));
            }
            else if (canvas is MeasuringCanvas measuring)
            {
                measuring.ResetOrigin();
            }

            if (d.Color.Alpha > 0)
            {
                canvas.DrawRectangle(Position.Zero, new Size(d.PageWidth, d.PageHeight), d.Color);
            }

            var origin = new Position(d.Left, d.Top);
            canvas.Translate(origin);
            page.Header.Draw(new Size(width, headerHeight));
            canvas.Translate(new Position(0, headerHeight));
            page.Content.Draw(contentSize);
            canvas.Translate(new Position(0, contentSize.Height));
            page.Footer.Draw(new Size(width, footerHeight));
            canvas.Translate(new Position(-d.Left, -(d.Top + headerHeight + contentSize.Height)));
            FlushLayers(layers, canvas);

            if (writer is not null)
            {
                skia!.SetTarget(null);
                writer.EndPage();
            }

            if (!contentPlan.IsPartial)
            {
                break;
            }

            // Content that has nothing left (it ended with a page break) does not produce a trailing blank page.
            if (page.Content.Measure(full).IsEmpty)
            {
                break;
            }
        }
    }

    /// <summary>Paints the drawing <c>ZIndex</c> deferred on this page, from the page origin.</summary>
    private static void FlushLayers(DeferredLayers layers, IDrawingCanvas canvas)
    {
        if (layers.Count == 0)
        {
            return;
        }

        if (canvas is ICanvasStateCapture capture)
        {
            layers.Flush(capture);
        }
    }

    /// <summary>The element trees of one page description, each under the page's default text style.</summary>
    private sealed class PageTrees
    {
        public PageTrees(PageDescriptor descriptor)
        {
            Descriptor = descriptor;
            Header = new TextStyleScope(descriptor.TextStyle) { Child = descriptor.HeaderSlot };
            Content = new TextStyleScope(descriptor.TextStyle) { Child = descriptor.ContentSlot };
            Footer = new TextStyleScope(descriptor.TextStyle) { Child = descriptor.FooterSlot };
        }

        public PageDescriptor Descriptor { get; }

        public Element Header { get; }

        public Element Content { get; }

        public Element Footer { get; }

        public void Initialize(PageContext context, IDrawingCanvas canvas)
        {
            Header.Initialize(context, canvas);
            Content.Initialize(context, canvas);
            Footer.Initialize(context, canvas);
        }

        public void Reset()
        {
            Header.Reset();
            Content.Reset();
            Footer.Reset();
        }

        /// <summary>Re-arms the header and footer for the next page; <see cref="SkipOnceElement"/> keeps its state.</summary>
        public void SoftResetHeaderFooter(PageContext context)
        {
            var state = DocumentState.Of(context);
            state.IsSoftReset = true;
            try
            {
                Header.Reset();
                Footer.Reset();
            }
            finally
            {
                state.IsSoftReset = false;
            }
        }
    }
}
