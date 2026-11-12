using AllInOnePdf.Layout.Fluent;
using AllInOnePdf.Layout.Infrastructure;

namespace AllInOnePdf.Layout.Tests;

public class LayoutElementTests
{
    private static readonly Color Red = Color.FromHex("#FF0000");
    private static readonly Color Blue = Color.FromHex("#0000FF");

    // ------------------------------------------------------------------ column

    [Fact]
    public void Column_stacks_items_with_spacing_between_them()
    {
        var canvas = new RecordingCanvas();
        var root = TestRoot.Build(c => c.Column(col =>
        {
            col.Spacing(5);
            col.Item().SetChild(new Block(0, 10, Red));
            col.Item().SetChild(new Block(0, 20, Red));
            col.Item().SetChild(new Block(0, 30, Red));
        })).Init(canvas);

        Assert.Equal(SpacePlan.FullRender(100, 70), root.Measure(new Size(100, 500)));
        root.Draw(new Size(100, 500));
        Assert.Equal([0f, 15f, 40f], canvas.Rectangles.Select(r => r.Position.Y));
        Assert.True(root.Measure(new Size(100, 500)).IsEmpty);
    }

    [Fact]
    public void Column_paginates_item_by_item_and_moves_wrapping_items_to_the_next_page()
    {
        var blocks = Enumerable.Range(0, 5).Select(_ => new Block(0, 40)).ToArray();
        var root = TestRoot.Build(c => c.Column(col =>
        {
            foreach (var b in blocks)
            {
                col.Item().SetChild(b);
            }
        })).Init();

        var page = new Size(100, 100);
        Assert.Equal(SpacePlan.PartialRender(100, 80), root.Measure(page));
        root.Draw(page);
        Assert.Equal(SpacePlan.PartialRender(100, 80), root.Measure(page));
        root.Draw(page);
        Assert.Equal(SpacePlan.FullRender(100, 40), root.Measure(page));
        root.Draw(page);
        Assert.All(blocks, b => Assert.Single(b.Pages));
    }

    [Fact]
    public void Column_draws_a_partial_item_and_continues_it()
    {
        var root = TestRoot.Build(c => c.Column(col =>
        {
            col.Item().SetChild(new Block(0, 50));
            col.Item().SetChild(new Lines(10, 10));
        })).Init();

        var page = new Size(100, 100);
        Assert.Equal(SpacePlan.PartialRender(100, 100), root.Measure(page));
        root.Draw(page);
        Assert.Equal(SpacePlan.FullRender(100, 50), root.Measure(page));
    }

    [Fact]
    public void Column_wraps_when_the_first_item_does_not_fit()
    {
        var root = TestRoot.Build(c => c.Column(col => col.Item().SetChild(new Block(0, 150)))).Init();
        Assert.True(root.Measure(new Size(100, 100)).IsWrap);
    }

    [Fact]
    public void Column_does_not_add_spacing_for_empty_items()
    {
        var root = TestRoot.Build(c => c.Column(col =>
        {
            col.Spacing(10);
            col.Item().SetChild(new Block(0, 20));
            col.Item();
            col.Item().SetChild(new Block(0, 20));
        })).Init();

        Assert.Equal(SpacePlan.FullRender(100, 50), root.Measure(new Size(100, 500)));
    }

    // --------------------------------------------------------------------- row

    [Fact]
    public void Row_shares_width_between_constant_auto_and_relative_items()
    {
        var canvas = new RecordingCanvas();
        var root = TestRoot.Build(c => c.Row(row =>
        {
            row.Spacing(10);
            row.ConstantItem(50).Background(Red);
            row.AutoItem().SetChild(new Block(40, 10));
            row.RelativeItem().Background(Red);
            row.RelativeItem(2).Background(Red);
        })).Init(canvas);

        var plan = root.Measure(new Size(300, 100));
        Assert.Equal(SpacePlan.FullRender(300, 10), plan);
        root.Draw(new Size(300, 100));

        var fills = canvas.Rectangles.Where(r => r.Color == Red).ToList();
        Assert.Equal([0f, 110f, 180f], fills.Select(r => r.Position.X));
        Assert.Equal([50f, 60f, 120f], fills.Select(r => r.Size.Width));
        Assert.All(fills, f => Assert.Equal(10f, f.Size.Height));
    }

    [Fact]
    public void Row_draws_every_item_with_the_height_of_the_tallest()
    {
        var canvas = new RecordingCanvas();
        var root = TestRoot.Build(c => c.Row(row =>
        {
            row.RelativeItem().Background(Red).SetChild(new Block(0, 10));
            row.RelativeItem().Background(Blue).SetChild(new Block(0, 30));
        })).Init(canvas);

        Assert.Equal(30f, root.Measure(new Size(100, 100)).Height);
        root.Draw(new Size(100, 100));
        Assert.Equal(30f, canvas.Rectangles.Single(r => r.Color == Red).Size.Height);
        Assert.Equal(30f, canvas.Rectangles.Single(r => r.Color == Blue).Size.Height);
    }

    [Fact]
    public void Row_continues_partial_items_and_does_not_redraw_finished_ones()
    {
        var block = new Block(0, 20);
        var root = TestRoot.Build(c => c.Row(row =>
        {
            row.RelativeItem().SetChild(new Lines(10, 10));
            row.RelativeItem().SetChild(block);
        })).Init();

        var page = new Size(100, 50);
        Assert.Equal(SpacePlan.PartialRender(100, 50), root.Measure(page));
        root.Draw(page);
        Assert.Equal(SpacePlan.FullRender(100, 50), root.Measure(page));
        root.Draw(page);
        Assert.Single(block.Pages);
        Assert.True(root.Measure(page).IsEmpty);
    }

    [Fact]
    public void Row_wraps_when_an_item_wraps()
    {
        var root = TestRoot.Build(c => c.Row(row =>
        {
            row.RelativeItem().SetChild(new Block(0, 10));
            row.RelativeItem().SetChild(new Block(0, 200));
        })).Init();

        Assert.True(root.Measure(new Size(100, 100)).IsWrap);
    }

    // ------------------------------------------------------------- box model

    [Fact]
    public void Padding_insets_the_child_and_accepts_negative_values()
    {
        var block = new Block(0, 20, Red);
        var canvas = new RecordingCanvas();
        var root = TestRoot.Build(c => c.Padding(10).SetChild(block)).Init(canvas);

        Assert.Equal(SpacePlan.FullRender(100, 40), root.Measure(new Size(100, 100)));
        root.Draw(new Size(100, 100));
        Assert.Equal(new Position(10, 10), canvas.Rectangles[0].Position);
        Assert.Equal(80f, canvas.Rectangles[0].Size.Width);

        var negative = TestRoot.Build(c => c.PaddingTop(-5).PaddingLeft(2).SetChild(new Block(0, 20))).Init();
        Assert.Equal(SpacePlan.FullRender(100, 15), negative.Measure(new Size(100, 100)));
    }

    [Fact]
    public void Chained_paddings_merge_into_one_element()
    {
        var root = TestRoot.Build(c => c.PaddingVertical(6).PaddingHorizontal(14).SetChild(new Block(0, 10))).Init();
        Assert.Equal(SpacePlan.FullRender(100, 22), root.Measure(new Size(100, 100)));
        Assert.Single(root.GetChildren());
        Assert.IsAssignableFrom<IContainer>(root.Child);
        Assert.IsType<Block>(((IContainer)root.Child!).Child);
    }

    [Theory]
    [InlineData("center", "middle", 40, 20)]
    [InlineData("right", "bottom", 80, 40)]
    [InlineData("left", "top", 0, 0)]
    public void Alignment_places_the_child_at_its_natural_size(string horizontal, string vertical, float x, float y)
    {
        var canvas = new RecordingCanvas();
        var root = TestRoot.Build(c =>
        {
            var h = horizontal switch { "center" => c.AlignCenter(), "right" => c.AlignRight(), _ => c.AlignLeft() };
            var v = vertical switch { "middle" => h.AlignMiddle(), "bottom" => h.AlignBottom(), _ => h.AlignTop() };
            v.SetChild(new Block(20, 10, Red));
        }).Init(canvas);

        Assert.Equal(SpacePlan.FullRender(20, 10), root.Measure(new Size(100, 50)));
        root.Draw(new Size(100, 50));
        Assert.Equal(new Position(x, y), canvas.Rectangles.Single().Position);
    }

    [Fact]
    public void Width_and_height_constrain_and_wrap_when_too_large()
    {
        var available = new Size(100, 100);
        Assert.Equal(SpacePlan.FullRender(50, 10), TestRoot.Build(c => c.Width(50).SetChild(new Block(0, 10))).Init().Measure(available));
        Assert.True(TestRoot.Build(c => c.Width(200).SetChild(new Block(0, 10))).Init().Measure(available).IsWrap);
        Assert.Equal(SpacePlan.FullRender(0, 30), TestRoot.Build(c => c.Height(30)).Init().Measure(available));
        Assert.True(TestRoot.Build(c => c.Height(200)).Init().Measure(available).IsWrap);
        Assert.True(TestRoot.Build(c => c.MaxHeight(20).SetChild(new Block(0, 30))).Init().Measure(available).IsWrap);
        Assert.Equal(SpacePlan.FullRender(100, 40), TestRoot.Build(c => c.MinHeight(40).SetChild(new Block(0, 10))).Init().Measure(available));
        Assert.Equal(SpacePlan.FullRender(64, 5), TestRoot.Build(c => c.Width(64).Height(5).Background(Red)).Init().Measure(available));
        Assert.Equal(SpacePlan.FullRender(30, 10), TestRoot.Build(c => c.MaxWidth(30).SetChild(new Block(0, 10))).Init().Measure(available));
    }

    [Fact]
    public void Aspect_ratio_sizes_by_ratio()
    {
        static SpacePlan Measure(float ratio, AspectRatioOption option, Size available) =>
            TestRoot.Build(c => c.AspectRatio(ratio, option).Background(Color.Black)).Init().Measure(available);

        Assert.Equal(SpacePlan.FullRender(100, 50), Measure(2, AspectRatioOption.FitWidth, new Size(100, 100)));
        Assert.Equal(SpacePlan.FullRender(60, 30), Measure(2, AspectRatioOption.FitHeight, new Size(100, 30)));
        Assert.Equal(SpacePlan.FullRender(40, 40), Measure(1, AspectRatioOption.FitArea, new Size(100, 40)));
        Assert.Equal(SpacePlan.FullRender(50, 50), Measure(1, AspectRatioOption.FitArea, new Size(50, 400)));
        Assert.True(Measure(0.5f, AspectRatioOption.FitWidth, new Size(100, 100)).IsWrap);
    }

    [Fact]
    public void Styled_box_fills_under_the_child_strokes_borders_over_it_and_clips_to_the_radius()
    {
        var canvas = new RecordingCanvas();
        var root = TestRoot.Build(c => c.Background(Red).Border(2).BorderColor(Blue).CornerRadius(4).SetChild(new Block(0, 20, Color.Black))).Init(canvas);

        root.Measure(new Size(100, 20));
        root.Draw(new Size(100, 20));
        Assert.Equal(Red, canvas.Rectangles[0].Color);
        Assert.Equal(4f, canvas.Rectangles[0].Radius);
        Assert.Equal(Color.Black, canvas.Rectangles[1].Color);
        Assert.Single(canvas.Clips);
        Assert.Equal(2f, Assert.Single(canvas.Outlines).Thickness);

        var sides = new RecordingCanvas();
        var bottom = TestRoot.Build(c => c.BorderBottom(1).BorderLeft(3).SetChild(new Block(0, 20))).Init(sides);
        bottom.Draw(new Size(100, 20));
        // Square borders are filled bands centred on the edges (QuestPDF): bottom spans the outer width, left fills above it.
        Assert.Empty(sides.Lines);
        Assert.Contains(sides.Rectangles, r => r.Position == new Position(-1.5f, 19.5f) && r.Size == new Size(101.5f, 1));
        Assert.Contains(sides.Rectangles, r => r.Position == new Position(-1.5f, 0) && r.Size == new Size(3, 19.5f));
    }

    [Fact]
    public void Layers_draw_in_declaration_order_in_the_primary_layer_size()
    {
        var canvas = new RecordingCanvas();
        var root = TestRoot.Build(c => c.Layers(layers =>
        {
            layers.Layer().Background(Red);
            layers.PrimaryLayer().SetChild(new Block(0, 25, Blue));
            layers.Layer().AlignBottom().Height(2).Background(Color.Black);
        })).Init(canvas);

        Assert.Equal(SpacePlan.FullRender(100, 25), root.Measure(new Size(100, 100)));
        root.Draw(new Size(100, 25));
        Assert.Equal([Red, Blue, Color.Black], canvas.Rectangles.Select(r => r.Color));
        Assert.Equal(25f, canvas.Rectangles[0].Size.Height);
        Assert.Equal(23f, canvas.Rectangles[2].Position.Y);
    }

    [Fact]
    public void Layers_require_one_primary_layer()
    {
        var root = new TestRoot();
        Assert.Throws<InvalidOperationException>(() => root.Layers(l => l.Layer()));
    }

    // -------------------------------------------------------------- pagination

    [Fact]
    public void Page_break_ends_the_page_once()
    {
        var after = new Block(0, 10);
        var root = TestRoot.Build(c => c.Column(col =>
        {
            col.Item().SetChild(new Block(0, 10));
            col.Item().PageBreak();
            col.Item().SetChild(after);
        })).Init();

        var page = new Size(100, 100);
        Assert.Equal(SpacePlan.PartialRender(100, 10), root.Measure(page));
        root.Draw(page);
        Assert.Empty(after.Pages);
        Assert.Equal(SpacePlan.FullRender(100, 10), root.Measure(page));
    }

    [Fact]
    public void Ensure_space_moves_content_that_would_start_too_low()
    {
        Assert.True(TestRoot.Build(c => c.EnsureSpace(50).SetChild(new Lines(10, 10))).Init().Measure(new Size(100, 30)).IsWrap);
        Assert.True(TestRoot.Build(c => c.EnsureSpace(50).SetChild(new Lines(10, 10))).Init().Measure(new Size(100, 60)).IsPartial);
        Assert.True(TestRoot.Build(c => c.EnsureSpace(50).SetChild(new Block(0, 10))).Init().Measure(new Size(100, 30)).IsFull);

        // Enough space offered, but only a 30 pt heading would be drawn before the unbreakable part: QuestPDF moves it.
        static void Group(IContainer c) => c.EnsureSpace(50).Column(col =>
        {
            col.Item().SetChild(new Block(0, 30));
            col.Item().PreventPageBreak().SetChild(new Lines(10, 10));
        });

        Assert.True(TestRoot.Build(Group).Init().Measure(new Size(100, 60)).IsWrap);
    }

    [Fact]
    public void Prevent_page_break_only_draws_content_that_fits_whole()
    {
        Assert.True(TestRoot.Build(c => c.PreventPageBreak().SetChild(new Lines(10, 10))).Init().Measure(new Size(100, 50)).IsWrap);
        Assert.True(TestRoot.Build(c => c.ShowEntire().SetChild(new Lines(10, 10))).Init().Measure(new Size(100, 200)).IsFull);
    }

    [Fact]
    public void Skip_once_hides_the_content_the_first_time()
    {
        var block = new Block(0, 10) { Once = false };
        var root = TestRoot.Build(c => c.SkipOnce().SetChild(block)).Init();

        Assert.True(root.Measure(new Size(100, 100)).IsEmpty);
        root.Draw(new Size(100, 100));
        Assert.Empty(block.Pages);
        Assert.True(root.Measure(new Size(100, 100)).IsFull);
        root.Draw(new Size(100, 100));
        Assert.Single(block.Pages);

        root.Reset();
        Assert.True(root.Measure(new Size(100, 100)).IsEmpty);
    }

    [Fact]
    public void Element_overloads_run_on_the_container()
    {
        var block = new Block(0, 10);
        var root = TestRoot.Build(c => c.Element(e => e.Padding(5)).Element(e => e.SetChild(block))).Init();
        Assert.Equal(SpacePlan.FullRender(100, 20), root.Measure(new Size(100, 100)));
    }

    [Fact]
    public void Hyperlink_marks_the_drawn_area()
    {
        var canvas = new RecordingCanvas();
        var root = TestRoot.Build(c => c.Padding(5).Hyperlink("https://example.com/").SetChild(new Block(0, 10))).Init(canvas);
        root.Draw(new Size(100, 20));
        var link = Assert.Single(canvas.Links);
        Assert.Equal(new Position(5, 5), link.Position);
        Assert.Equal(new Size(90, 10), link.Size);
    }

    // ------------------------------------------------------------- text style

    [Fact]
    public void Default_text_styles_inherit_from_enclosing_scopes()
    {
        var probe = new StyleProbe();
        var outside = new StyleProbe();
        TestRoot.Build(c => c.Column(col =>
        {
            col.Item().DefaultTextStyle(s => s.FontSize(20).FontColor(Red)).PaddingTop(2).DefaultTextStyle(s => s.Bold()).SetChild(probe);
            col.Item().SetChild(outside);
        })).Init();

        Assert.Equal(20f, probe.Seen!.Size);
        Assert.Equal(FontWeight.Bold, probe.Seen.FontWeight);
        Assert.Equal(Red, probe.Seen.Color);
        Assert.Equal(["Inter"], probe.Seen.FontFamilies);
        Assert.Equal(TextStyle.Default, outside.Seen);
    }
}
