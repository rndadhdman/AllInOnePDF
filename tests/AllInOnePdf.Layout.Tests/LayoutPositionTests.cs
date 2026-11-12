using AllInOnePdf.Layout.Drawing;
using AllInOnePdf.Layout.Fluent;
using AllInOnePdf.Layout.Infrastructure;
using SkiaSharp;

namespace AllInOnePdf.Layout.Tests;

/// <summary>TranslateX/Y, ZIndex, Freeform and tracked boxes, through the real page loop rendered onto bitmaps (1 px per pt).</summary>
public sealed class LayoutPositionTests
{
    private static readonly Color Red = Color.FromHex("#FF0000");
    private static readonly Color Blue = Color.FromHex("#0000FF");
    private static readonly Color Green = Color.FromHex("#00FF00");

    /// <summary>Collects every page as a white bitmap of the page size.</summary>
    private sealed class BitmapSink : IPageSink, IDisposable
    {
        private SKCanvas? _canvas;

        public List<SKBitmap> Pages { get; } = [];

        public SKCanvas BeginPage(float width, float height)
        {
            var bitmap = new SKBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height));
            Pages.Add(bitmap);
            _canvas = new SKCanvas(bitmap);
            _canvas.Clear(SKColors.White);
            return _canvas;
        }

        public void EndPage()
        {
            _canvas?.Flush();
            _canvas?.Dispose();
            _canvas = null;
        }

        public void Close() => EndPage();

        public void Dispose()
        {
            foreach (var page in Pages)
            {
                page.Dispose();
            }
        }
    }

    private static (BitmapSink Sink, DocumentLayoutInfo Info) Render(float width, float height, float margin, Action<PageDescriptor> compose)
    {
        var page = new PageDescriptor();
        page.Size(width, height);
        page.Margin(margin);
        compose(page);
        var sink = new BitmapSink();
        var info = PageLayout.Generate([page], sink);
        return (sink, info);
    }

    private static void AssertPixel(SKBitmap bitmap, int x, int y, Color expected)
    {
        var actual = bitmap.GetPixel(x, y);
        Assert.True(
            Math.Abs(actual.Red - expected.Red) < 8 && Math.Abs(actual.Green - expected.Green) < 8 && Math.Abs(actual.Blue - expected.Blue) < 8,
            $"pixel ({x}, {y}) is {actual}, expected {expected}");
    }

    private static void AssertBox(TrackedBox expected, TrackedBox actual)
    {
        Assert.Equal(expected.Page, actual.Page);
        Assert.Equal(expected.X, actual.X, 3);
        Assert.Equal(expected.Y, actual.Y, 3);
        Assert.Equal(expected.Width, actual.Width, 3);
        Assert.Equal(expected.Height, actual.Height, 3);
    }

    // ------------------------------------------------------------------ translate

    [Fact]
    public void Translate_keeps_the_measured_size()
    {
        var root = TestRoot.Build(c => c.TranslateX(50).TranslateY(-20).Width(30).Height(20)).Init();
        Assert.Equal(SpacePlan.FullRender(30, 20), root.Measure(new Size(100, 100)));
    }

    [Fact]
    public void Translate_shifts_the_drawing_and_nothing_else()
    {
        var (sink, info) = Render(200, 200, 10, page => page.Content().Column(col =>
        {
            col.Item().Height(50).TrackPages("a");
            col.Item().TranslateX(10).TranslateX(20).TranslateY(20).TrackPages("b").Height(40).Background(Blue);
            col.Item().Height(10).TrackPages("c");
        }));
        using var _ = sink;

        AssertBox(new TrackedBox(1, 10, 10, 180, 50), info.TrackedBoxes["a"].Single());
        AssertBox(new TrackedBox(1, 40, 80, 180, 40), info.TrackedBoxes["b"].Single()); // slot at (10, 60), shifted by (30, 20)
        AssertBox(new TrackedBox(1, 10, 100, 180, 10), info.TrackedBoxes["c"].Single()); // the next item does not move

        var bitmap = sink.Pages.Single();
        AssertPixel(bitmap, 20, 100, Color.White);  // the slot's own left part is empty
        AssertPixel(bitmap, 100, 70, Color.White);  // above the shifted box
        AssertPixel(bitmap, 100, 100, Blue);
        AssertPixel(bitmap, 195, 100, Blue);        // painted outside the slot (and into the margin), not clipped
    }

    // ------------------------------------------------------------------- z-index

    [Fact]
    public void ZIndex_keeps_the_measured_size()
    {
        var root = TestRoot.Build(c => c.ZIndex(3).Width(30).Height(20)).Init();
        Assert.Equal(SpacePlan.FullRender(30, 20), root.Measure(new Size(100, 100)));
    }

    [Fact]
    public void A_raised_layer_declared_first_paints_on_top()
    {
        var (sink, _) = Render(100, 100, 0, page => page.Content().Layers(layers =>
        {
            layers.Layer().ZIndex(2).PaddingLeft(50).Background(Green);
            layers.Layer().ZIndex(1).Background(Red);
            layers.PrimaryLayer().Background(Blue);
        }));
        using var _ = sink;

        var bitmap = sink.Pages.Single();
        AssertPixel(bitmap, 25, 50, Red);    // z 1 over the z 0 primary
        AssertPixel(bitmap, 75, 50, Green);  // z 2 over z 1
    }

    [Fact]
    public void ZIndex_zero_and_below_draws_in_place()
    {
        var (sink, _) = Render(100, 100, 0, page => page.Content().Layers(layers =>
        {
            layers.Layer().ZIndex(0).Background(Red);
            layers.PrimaryLayer().ZIndex(-1).Background(Blue);
        }));
        using var _ = sink;

        AssertPixel(sink.Pages.Single(), 50, 50, Blue);
    }

    [Fact]
    public void A_raised_element_paints_over_the_footer()
    {
        void Compose(PageDescriptor page, int z)
        {
            page.Footer().Height(20).Background(Blue);
            page.Content().Column(col => col.Item().Height(10).TranslateY(85).ZIndex(z).Background(Red));
        }

        var (flat, _) = Render(100, 100, 0, page => Compose(page, 0));
        var (raised, _) = Render(100, 100, 0, page => Compose(page, 1));
        using var a = flat;
        using var b = raised;

        AssertPixel(flat.Pages.Single(), 50, 90, Blue);   // in place: the footer draws after the content
        AssertPixel(raised.Pages.Single(), 50, 90, Red);  // deferred: after the footer
    }

    [Fact]
    public void Deferred_drawing_keeps_its_translation_and_clip()
    {
        var (sink, info) = Render(100, 100, 0, page => page.Content()
            .Padding(10).AlignLeft().AlignTop().Width(40).Height(40).CornerRadius(20).Background(Blue)
            .ZIndex(1).TrackPages("z").Background(Red));
        using var _ = sink;

        var bitmap = sink.Pages.Single();
        AssertPixel(bitmap, 30, 30, Red);           // the raised box paints at the padded position
        AssertPixel(bitmap, 12, 12, Color.White);   // and stays inside the rounded clip
        AssertPixel(bitmap, 60, 30, Color.White);
        AssertBox(new TrackedBox(1, 10, 10, 40, 40), info.TrackedBoxes["z"].Single());
    }

    [Fact]
    public void ZIndex_works_on_every_page_and_draws_once_per_pass()
    {
        var block = new Block(0, 50, Red);
        var (sink, info) = Render(100, 100, 0, page => page.Content().Column(col =>
        {
            col.Item().Height(100).Background(Green);
            col.Item().Layers(layers =>
            {
                layers.Layer().ZIndex(1).TrackPages("z").SetChild(block);
                layers.PrimaryLayer().Height(50).Background(Blue);
            });
        }));
        using var _ = sink;

        Assert.Equal(2, info.PageCount);
        Assert.Equal([(2, true), (2, false)], block.Draws);
        AssertPixel(sink.Pages[0], 50, 50, Green);
        AssertPixel(sink.Pages[1], 50, 25, Red);
        AssertBox(new TrackedBox(2, 0, 0, 100, 50), info.TrackedBoxes["z"].Single());
    }

    [Fact]
    public void A_raised_element_split_across_pages_is_deferred_per_page()
    {
        var (sink, info) = Render(100, 100, 0, page => page.Content().ZIndex(1).TrackPages("lines").SetChild(new Lines(15, 10)));
        using var _ = sink;

        Assert.Equal(2, info.PageCount);
        Assert.Equal(new PageSpan(1, 2), info.TrackedPages["lines"]);
        var boxes = info.TrackedBoxes["lines"];
        Assert.Equal(2, boxes.Count);
        AssertBox(new TrackedBox(1, 0, 0, 100, 100), boxes[0]);
        AssertBox(new TrackedBox(2, 0, 0, 100, 100), boxes[1]); // the page content is drawn into the whole content area
    }

    [Fact]
    public void Documents_with_layers_are_deterministic()
    {
        static byte[] Make() => Document.Create(doc => doc.Page(page =>
        {
            page.Size(200, 200);
            page.Content().Layers(layers =>
            {
                layers.Layer().ZIndex(1).TranslateX(15).Background(Red);
                layers.PrimaryLayer().Freeform(f => f.Item(10, 10, 50, 50, z: 1).Background(Blue));
            });
        })).GeneratePdf();

        Assert.Equal(Make(), Make());
    }

    // ------------------------------------------------------------------ freeform

    [Fact]
    public void Freeform_takes_the_offered_space_and_wraps_without_room()
    {
        var root = TestRoot.Build(c => c.Freeform(f => f.Item(0, 0, 10, 10))).Init();
        Assert.Equal(SpacePlan.FullRender(120, 80), root.Measure(new Size(120, 80)));
        Assert.True(root.Measure(new Size(0, 80)).IsWrap);
        Assert.True(root.Measure(new Size(120, 0)).IsWrap);
    }

    [Fact]
    public void Freeform_places_paints_in_z_order_and_clips()
    {
        var (sink, info) = Render(200, 200, 0, page => page.Content().AlignLeft().AlignTop().Width(100).Height(100).Freeform(f =>
        {
            f.Item(10, 10, 30, 30).Background(Red);
            f.Item(20, 20, 30, 30, z: -1).Background(Blue);        // added later, but lower: under the red one
            f.Item(80, 80, 50, 50).Background(Green);              // past the canvas: clipped at 100
            f.Item(60, 10, 10, 10).TranslateX(5).Background(Red);  // its content is clipped to its own box
            f.Item(50, 50, 20, 20).TrackPages("t");
        }));
        using var _ = sink;

        var bitmap = sink.Pages.Single();
        AssertPixel(bitmap, 25, 25, Red);
        AssertPixel(bitmap, 45, 45, Blue);
        AssertPixel(bitmap, 90, 90, Green);
        AssertPixel(bitmap, 110, 110, Color.White);
        AssertPixel(bitmap, 67, 15, Red);
        AssertPixel(bitmap, 72, 15, Color.White);
        AssertPixel(bitmap, 62, 15, Color.White);
        AssertBox(new TrackedBox(1, 50, 50, 20, 20), info.TrackedBoxes["t"].Single());
    }

    [Fact]
    public void Freeform_reports_items_whose_content_does_not_fit()
    {
        var (sink, info) = Render(200, 200, 10, page => page.Content().Freeform(f =>
        {
            f.Item(0, 0, 50, 20, key: "partial").SetChild(new Lines(5, 10));   // two of five lines fit
            f.Item(0, 30, 50, 5, key: "wrap").SetChild(new Lines(1, 10));      // not even one line
            f.Item(0, 40, 50, 20, key: "fits").SetChild(new Lines(2, 10));
            f.Item(0, 70, 50, 5).SetChild(new Lines(3, 10));                   // no key: not reported
        }));
        using var _ = sink;

        Assert.Equal(new HashSet<string> { "partial", "wrap" }, info.Overflowing.ToHashSet());
    }

    [Fact]
    public void Freeform_items_report_tracked_boxes_in_page_coordinates()
    {
        var (sink, info) = Render(300, 300, 20, page => page.Content().PaddingTop(30).Freeform(f =>
        {
            f.Item(-10, 15, 40, 25).TrackPages("left");
            f.Item(100, 100, 60, 60, z: 2).PaddingLeft(5).TrackPages("inner");
        }));
        using var _ = sink;

        AssertBox(new TrackedBox(1, 10, 65, 40, 25), info.TrackedBoxes["left"].Single());
        AssertBox(new TrackedBox(1, 125, 150, 55, 60), info.TrackedBoxes["inner"].Single());
    }

    // ------------------------------------------------------------ tracked boxes

    [Fact]
    public void Tracked_boxes_include_margins_header_and_paddings()
    {
        var (sink, info) = Render(300, 400, 20, page =>
        {
            page.Header().Height(30);
            page.Content().Padding(10).Column(col =>
            {
                col.Item().Height(50).TrackPages("a");
                col.Item().PaddingLeft(15).TrackPages("b").Height(40);
            });
        });
        using var _ = sink;

        AssertBox(new TrackedBox(1, 30, 60, 240, 50), info.TrackedBoxes["a"].Single());
        AssertBox(new TrackedBox(1, 45, 110, 225, 40), info.TrackedBoxes["b"].Single());
        Assert.Equal(new PageSpan(1, 1), info.TrackedPages["a"]);
        Assert.Empty(info.Overflowing);
    }

    [Fact]
    public void Tracked_boxes_list_every_page_of_split_content_and_reset_per_document()
    {
        var document = Document.Create(doc => doc.Page(page =>
        {
            page.Size(100, 100);
            page.Margin(10);
            page.Content().Column(col => col.Item().TrackPages("lines").SetChild(new Lines(12, 10)));
        }));

        document.GeneratePdf(out var first);
        document.GeneratePdf(out var second);

        foreach (var info in new[] { first, second })
        {
            var boxes = info.TrackedBoxes["lines"];
            Assert.Equal(2, boxes.Count);
            AssertBox(new TrackedBox(1, 10, 10, 80, 80), boxes[0]);
            AssertBox(new TrackedBox(2, 10, 10, 80, 40), boxes[1]);
        }
    }
}
