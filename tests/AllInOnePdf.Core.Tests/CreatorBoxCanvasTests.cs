using AllInOnePdf.Publishing.Model;
using AllInOnePdf.UI.Services;

namespace AllInOnePdf.Core.Tests;

/// <summary>The design canvas draws corner radius, clipping and position like the PDF. (The inspector's number inputs are the shared ones; see NumberInputMathTests.)</summary>
public class CreatorBoxCanvasTests
{
    [Fact]
    public void A_radius_rounds_the_box_and_clips_the_content_wrapper()
    {
        var design = new BoxDesign { CornerRadiusPt = 12 };
        Assert.Contains("border-radius:12pt;", CreatorCanvasHtml.BoxStyle(design), StringComparison.Ordinal);
        Assert.Equal("overflow:hidden;border-radius:12pt 12pt 12pt 12pt / 12pt 12pt 12pt 12pt;", CreatorCanvasHtml.ClipStyle(design));
        Assert.Equal("overflow:hidden;", CreatorCanvasHtml.SelfClipStyle(design));
    }

    [Fact]
    public void The_clip_radius_shrinks_by_the_border_and_padding_on_each_side()
    {
        var design = new BoxDesign { CornerRadiusPt = 20, BorderWidthPt = 2, Padding = new Spacing { TopPt = 4, RightPt = 30, BottomPt = 0, LeftPt = 8 } };
        // Horizontal radii: left 20-2-8, right 0 (padding is wider than the radius); vertical: top 20-2-4, bottom 20-2.
        Assert.Equal("overflow:hidden;border-radius:10pt 0pt 0pt 10pt / 14pt 14pt 18pt 18pt;", CreatorCanvasHtml.ClipStyle(design));
    }

    [Fact]
    public void No_radius_means_no_clipping()
    {
        Assert.Equal(string.Empty, CreatorCanvasHtml.ClipStyle(new BoxDesign()));
        Assert.Equal(string.Empty, CreatorCanvasHtml.SelfClipStyle(new BoxDesign()));
        Assert.Equal(string.Empty, CreatorCanvasHtml.ClipStyle(null));
        Assert.DoesNotContain("border-radius", CreatorCanvasHtml.BoxStyle(new BoxDesign()), StringComparison.Ordinal);
        Assert.Equal(string.Empty, CreatorCanvasHtml.ClipStyle(new BoxDesign { CornerRadiusPt = 6, Padding = Spacing.All(10) }));
    }

    [Fact]
    public void A_module_clip_undoes_the_text_size_zoom()
    {
        var design = new ModuleDesign { CornerRadiusPt = 20, FontScale = 2 };
        Assert.Equal("overflow:hidden;border-radius:10pt 10pt 10pt 10pt / 10pt 10pt 10pt 10pt;", CreatorCanvasHtml.ClipStyle(design));
    }

    [Fact]
    public void Position_moves_the_box_relatively_and_sets_its_z_level()
    {
        var style = CreatorCanvasHtml.BoxStyle(new BoxDesign { OffsetXPt = 12.5, OffsetYPt = -8, ZIndex = 4 });
        Assert.Contains("position:relative;left:12.5pt;top:-8pt;z-index:4;", style, StringComparison.Ordinal);

        var moduleStyle = CreatorCanvasHtml.ModuleStyle(new ModuleDesign { OffsetYPt = 20 });
        Assert.Contains("position:relative;left:0pt;top:20pt;", moduleStyle, StringComparison.Ordinal);
        Assert.DoesNotContain("z-index", moduleStyle, StringComparison.Ordinal);

        var zOnly = CreatorCanvasHtml.BoxStyle(new BoxDesign { ZIndex = 2 });
        Assert.Contains("position:relative;z-index:2;", zOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("left:", zOnly, StringComparison.Ordinal);
    }

    [Fact]
    public void Position_zero_adds_nothing_and_out_of_range_values_are_clamped()
    {
        Assert.DoesNotContain("position", CreatorCanvasHtml.BoxStyle(new BoxDesign()), StringComparison.Ordinal);
        var style = CreatorCanvasHtml.BoxStyle(new BoxDesign { OffsetXPt = 9999, OffsetYPt = double.NaN, ZIndex = 500 });
        Assert.Contains("left:400pt;top:0pt;z-index:100;", style, StringComparison.Ordinal);
    }
}
