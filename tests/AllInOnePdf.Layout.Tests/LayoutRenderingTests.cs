using AllInOnePdf.Layout.Drawing;
using AllInOnePdf.Layout.Fluent;
using AllInOnePdf.Layout.Infrastructure;
using SkiaSharp;

namespace AllInOnePdf.Layout.Tests;

public class LayoutRenderingTests
{
    private static readonly Color Red = Color.FromHex("#FF0000");
    private static readonly Color Blue = Color.FromHex("#0000FF");
    private static readonly Color Green = Color.FromHex("#00FF00");

    /// <summary>Lays <paramref name="compose"/> out on a 200 × 200 white bitmap through <see cref="SkiaCanvas"/>.</summary>
    private static SKBitmap Render(Action<IContainer> compose)
    {
        var bitmap = new SKBitmap(200, 200);
        using var surface = new SKCanvas(bitmap);
        surface.Clear(SKColors.White);
        using var canvas = new SkiaCanvas(surface);
        var root = TestRoot.Build(compose).Init(canvas);
        var size = new Size(200, 200);
        Assert.True(root.Measure(size).HasContent);
        root.Draw(size);
        surface.Flush();
        return bitmap;
    }

    private static void AssertPixel(SKBitmap bitmap, int x, int y, Color expected)
    {
        var actual = bitmap.GetPixel(x, y);
        Assert.True(
            Math.Abs(actual.Red - expected.Red) < 8 && Math.Abs(actual.Green - expected.Green) < 8 && Math.Abs(actual.Blue - expected.Blue) < 8,
            $"pixel ({x}, {y}) is {actual}, expected {expected}");
    }

    [Fact]
    public void Coloured_boxes_land_where_the_layout_puts_them()
    {
        using var bitmap = Render(c => c.Padding(20).Column(col =>
        {
            col.Spacing(10);
            col.Item().Row(row =>
            {
                row.Spacing(10);
                row.RelativeItem().Background(Red).Height(50);
                row.ConstantItem(50).Background(Blue).Height(100);
            });
            col.Item().AlignCenter().Width(40).Height(30).Background(Green);
        }));

        AssertPixel(bitmap, 10, 10, Color.White);       // padding
        AssertPixel(bitmap, 40, 100, Red);              // the red item stretches to the row height (100)
        AssertPixel(bitmap, 125, 60, Color.White);      // row spacing (x 120..130)
        AssertPixel(bitmap, 150, 60, Blue);             // constant item at x 130..180
        AssertPixel(bitmap, 100, 145, Green);           // centred box at x 80..120, y 130..160
        AssertPixel(bitmap, 70, 145, Color.White);
        AssertPixel(bitmap, 100, 165, Color.White);
    }

    [Fact]
    public void Corner_radius_clips_and_borders_are_stroked_on_the_edges()
    {
        using var bitmap = Render(c => c.AlignLeft().AlignTop().Width(100).Height(100).Background(Red).CornerRadius(50).Border(4).BorderColor(Blue));

        AssertPixel(bitmap, 50, 50, Red);    // centre of the circle
        AssertPixel(bitmap, 3, 3, Color.White); // outside the rounded corner
        AssertPixel(bitmap, 50, 1, Blue);    // the border straddles the top edge
        AssertPixel(bitmap, 150, 50, Color.White);
    }

    [Fact]
    public void Layers_paint_in_order()
    {
        using var bitmap = Render(c => c.Height(60).Layers(layers =>
        {
            layers.Layer().Background(Red);
            layers.PrimaryLayer().PaddingLeft(100).Background(Blue);
        }));

        AssertPixel(bitmap, 50, 30, Red);
        AssertPixel(bitmap, 150, 30, Blue);
        AssertPixel(bitmap, 150, 100, Color.White);
    }
}
