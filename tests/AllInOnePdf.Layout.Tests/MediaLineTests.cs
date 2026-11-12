using AllInOnePdf.Layout.Fluent;
using AllInOnePdf.Layout.Infrastructure;

namespace AllInOnePdf.Layout.Tests;

public class MediaLineTests
{
    [Fact]
    public void A_horizontal_line_is_as_tall_as_its_thickness_and_spans_the_width()
    {
        var root = new MediaRoot();
        root.LineHorizontal(2).LineColor(Color.FromHex("#FF0000")).LineDashPattern([3, 1]);
        var canvas = new MediaRecordingCanvas();
        MediaHarness.Start(root, canvas);

        Assert.Equal(SpacePlan.FullRender(100, 2), root.Measure(new Size(100, 50)));
        Assert.Equal(SpacePlanType.Wrap, root.Measure(new Size(100, 1)).Type);
        Assert.Equal(SpacePlan.FullRender(0, 2), root.Measure(new Size(Size.Infinity, 50)));

        root.Draw(new Size(100, 2));
        var line = Assert.Single(canvas.Ops);
        Assert.Equal("line", line.Kind);
        Assert.Equal(new Position(0, 1), line.At);
        Assert.Equal(new Position(100, 1), line.To);
        Assert.Equal(2, line.Thickness);
        Assert.Equal(new Color(255, 0, 0), line.Color);
        Assert.Equal([3f, 1f], line.Dash!);
    }

    [Fact]
    public void A_vertical_line_is_as_wide_as_its_thickness()
    {
        var root = new MediaRoot();
        root.LineVertical(3);
        var canvas = new MediaRecordingCanvas();
        MediaHarness.Start(root, canvas);

        Assert.Equal(SpacePlan.FullRender(3, 50), root.Measure(new Size(100, 50)));
        Assert.Equal(SpacePlanType.Wrap, root.Measure(new Size(2, 50)).Type);
        root.Draw(new Size(3, 50));
        var line = Assert.Single(canvas.Ops);
        Assert.Equal((new Position(1.5f, 0), new Position(1.5f, 50), Color.Black, (float[]?)null), (line.At, line.To, line.Color, line.Dash));
    }

    [Fact]
    public void Dash_patterns_are_validated_and_odd_lists_repeat()
    {
        var root = new MediaRoot();
        var line = root.LineHorizontal(1);
        Assert.Throws<ArgumentException>(() => line.LineDashPattern([]));
        Assert.Throws<ArgumentException>(() => line.LineDashPattern([-1, 2]));

        line.LineDashPattern([1, 2, 3]);
        var canvas = new MediaRecordingCanvas();
        MediaHarness.Start(root, canvas);
        root.Draw(new Size(10, 1));
        Assert.Equal([1f, 2, 3, 1, 2, 3], canvas.Ops.Single().Dash!);
    }
}
