using AllInOnePdf.Layout.Fluent;
using AllInOnePdf.Layout.Infrastructure;
using SkiaSharp;

namespace AllInOnePdf.Layout.Tests;

/// <summary>
/// The shapes in which ThemedComposer and PublicationLayout call the media API (minus the core and text calls that
/// do not exist in this clone), so a signature change breaks here first.
/// </summary>
public class MediaCallSiteTests
{
    private static readonly float[] LeaderDash = [0.9f, 2.7f];

    [Fact]
    public void Renderer_call_shapes_compile_and_lay_out()
    {
        var root = new MediaRoot();
        root.Table(t =>
        {
            t.ColumnsDefinition(cd =>
            {
                for (var i = 0; i < 3; i++)
                {
                    cd.RelativeColumn();
                }
            });

            void HeaderCells(Func<ITableCellContainer> newCell)
            {
                var c = newCell();
                c = c.ColumnSpan((uint)2);
                c.LineHorizontal(0.9f).LineColor(Color.FromHex("#6B7280")).LineDashPattern(LeaderDash);
                newCell().Svg("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 16 16' width='16' height='16'><path d='M3.6 8.4 L6.6 11.4 L12.4 4.8' stroke='#fff' stroke-width='2.2' fill='none'/></svg>").FitArea();
            }

            t.Header(h => HeaderCells(h.Cell));
            HeaderCells(t.Cell);

            var thickness = 0.75f;
            var line = t.Cell().ColumnSpan(3).LineHorizontal(thickness).LineColor(Color.FromHex("#D1D5DB"));
            line.LineDashPattern([thickness * 4, thickness * 3]);

            var c = t.Cell();
            c = c.RowSpan((uint)2);
            c.Image(MediaHarness.Picture(4, 2, SKColors.Gray)).FitArea();
        });

        MediaHarness.Start(root, FreeCanvas.Instance);
        var plan = root.Measure(new Size(300, 400));
        Assert.Equal(SpacePlanType.FullRender, plan.Type);
        root.Draw(new Size(300, 400));
    }
}
