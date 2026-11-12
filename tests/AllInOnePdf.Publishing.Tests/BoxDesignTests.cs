using AllInOnePdf.Core.Rendering;
using AllInOnePdf.Core.Themes;
using AllInOnePdf.Publishing.Model;
using AllInOnePdf.Publishing.Rendering;
using AllInOnePdf.Publishing.Validation;
using SkiaSharp;

namespace AllInOnePdf.Publishing.Tests;

/// <summary>Box design: the Position fields (offset, Z level), rounded corners and borders in the PDF.</summary>
public class BoxDesignTests
{
    private static readonly PublicationRenderer Renderer = new();

    [Theory]
    [InlineData(-1000, -400)]
    [InlineData(1000, 400)]
    [InlineData(-37.5, -37.5)]
    [InlineData(0, 0)]
    [InlineData(double.NaN, 0)]
    [InlineData(double.PositiveInfinity, 0)]
    public void Validator_clamps_offsets(double value, double expected)
    {
        var p = WithBox(new BoxDesign { OffsetXPt = value, OffsetYPt = value });
        PublicationValidator.Normalize(p);
        var design = p.Sections[0].Design;
        Assert.Equal(expected, design.OffsetXPt);
        Assert.Equal(expected, design.OffsetYPt);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(7, 7)]
    [InlineData(250, 100)]
    public void Validator_clamps_the_z_level(int value, int expected)
    {
        var p = WithBox(new BoxDesign { ZIndex = value });
        p.Sections[0].Rows[0].Design = new BoxDesign { ZIndex = value };
        p.Sections[0].Rows[0].Columns[0].Design = new BoxDesign { ZIndex = value };
        p.Sections[0].Rows[0].Columns[0].Modules[0].Design = new ModuleDesign { ZIndex = value };
        PublicationValidator.Normalize(p);
        Assert.Equal(expected, p.Sections[0].Design.ZIndex);
        Assert.Equal(expected, p.Sections[0].Rows[0].Design.ZIndex);
        Assert.Equal(expected, p.Sections[0].Rows[0].Columns[0].Design.ZIndex);
        Assert.Equal(expected, p.Sections[0].Rows[0].Columns[0].Modules[0].Design.ZIndex);
    }

    [Fact]
    public void Position_round_trips_through_json()
    {
        var p = WithBox(new BoxDesign { OffsetXPt = 12.5, OffsetYPt = -30, ZIndex = 3 });
        p.Sections[0].Rows[0].Columns[0].Modules[0].Design = new ModuleDesign { OffsetXPt = -4, OffsetYPt = 8, ZIndex = 100, FontScale = 1.5 };

        var back = Publication.FromJson(p.ToJson());

        var section = back.Sections[0].Design;
        Assert.Equal((12.5, -30.0, 3), (section.OffsetXPt, section.OffsetYPt, section.ZIndex));
        var module = back.Sections[0].Rows[0].Columns[0].Modules[0].Design;
        Assert.Equal((-4.0, 8.0, 100, 1.5), (module.OffsetXPt, module.OffsetYPt, module.ZIndex, module.FontScale));
    }

    [Fact]
    public void Older_files_without_position_load_with_zero()
    {
        var json = WithBox(new BoxDesign { BorderWidthPt = 1 }).ToJson();
        foreach (var name in new[] { "offsetXPt", "offsetYPt", "zIndex", "OffsetXPt", "OffsetYPt", "ZIndex" })
        {
            json = System.Text.RegularExpressions.Regex.Replace(json, $"\\s*\"{name}\"\\s*:\\s*[-0-9.eE]+\\s*,?", string.Empty);
        }

        Assert.DoesNotContain("ffset", json, StringComparison.Ordinal);
        var back = PublicationValidator.Normalize(Publication.FromJson(json));
        var design = back.Sections[0].Design;
        Assert.Equal((0.0, 0.0, 0), (design.OffsetXPt, design.OffsetYPt, design.ZIndex));
        Assert.Equal(1, design.BorderWidthPt);
    }

    [Fact]
    public async Task A_module_box_radius_clips_the_picture()
    {
        var (p, image) = PictureOn();
        image.Design = new ModuleDesign { CornerRadiusPt = 30 };
        await AssertRoundedCornerAsync(p);
    }

    [Fact]
    public async Task The_image_radius_rounds_the_picture_itself()
    {
        var (p, image) = PictureOn();
        image.CornerRadiusPt = 30;
        await AssertRoundedCornerAsync(p);
    }

    [Fact]
    public async Task Without_a_radius_the_picture_has_square_corners()
    {
        var (p, _) = PictureOn();
        using var bitmap = await RenderAsync(p);
        var (left, top) = ContentOrigin(p);
        Assert.True(IsNavy(bitmap.GetPixel(left + 2, top + 2)), "the corner should be part of the picture");
    }

    [Fact]
    public async Task A_border_without_its_own_colour_uses_the_theme_border_colour()
    {
        var p = Plain();
        p.Theme.Colors.Border = "#00AA00";
        p.Sections.Add(new Section
        {
            Rows = [new Row { Columns = [new Column { Modules = [new SpacerModule { HeightPt = 60, Design = new ModuleDesign { BorderWidthPt = 4 } }] }] }],
        });

        using var bitmap = await RenderAsync(p);
        var (left, top) = ContentOrigin(p);
        // The 4 pt border is stroked centred on the box edge: look a few points either side of the left margin.
        var colours = Enumerable.Range(left - 4, 9).Select(x => bitmap.GetPixel(x, top + 30)).ToList();
        Assert.True(colours.Any(c => c.Green > 120 && c.Red < 80 && c.Blue < 80), $"expected the theme's green border, got {string.Join(", ", colours)}");
    }

    // ------------------------------------------------------------------ helpers

    private static Publication WithBox(BoxDesign design)
    {
        var p = Plain();
        p.Sections.Add(new Section { Design = design, Rows = [new Row { Columns = [new Column { Modules = [new TextModule { Markdown = "Text" }] }] }] });
        return p;
    }

    private static (Publication Publication, ImageModule Image) PictureOn()
    {
        var p = Plain();
        using var bitmap = new SKBitmap(200, 200);
        bitmap.Erase(SKColors.Navy);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var asset = new PublicationAsset { FileName = "navy.png", MediaType = "image/png", Data = encoded.ToArray() };
        p.Assets.Add(asset);
        var image = new ImageModule { AssetId = asset.Id, WidthPercent = 40, Align = BlockAlign.Left };
        p.Sections.Add(new Section { Rows = [new Row { Columns = [new Column { Modules = [image] }] }] });
        return (p, image);
    }

    private static async Task AssertRoundedCornerAsync(Publication p)
    {
        using var bitmap = await RenderAsync(p);
        var (left, top) = ContentOrigin(p);

        // 2 pt inside the corner of a 30 pt radius is outside the curve (paper); 30 pt in is the picture.
        Assert.False(IsNavy(bitmap.GetPixel(left + 2, top + 2)), "the top-left corner should be rounded off");
        Assert.True(IsNavy(bitmap.GetPixel(left + 30, top + 30)), "the picture should be drawn inside the curve");
        Assert.True(IsNavy(bitmap.GetPixel(left + 30, top + 2)), "the top edge away from the corner should be the picture");
    }

    private static async Task<SKBitmap> RenderAsync(Publication p)
    {
        var pdf = await Renderer.RenderAsync(p);
        var page = await new PdfiumRasterizer().RasterizePageAsync(pdf, 0, new RasterizeOptions { Dpi = 72 });
        return SKBitmap.Decode(page.Data);
    }

    private static (int Left, int Top) ContentOrigin(Publication p) =>
        ((int)Math.Ceiling(p.Theme.Page.MarginLeftMm * 72 / 25.4), (int)Math.Ceiling(p.Theme.Page.MarginTopMm * 72 / 25.4));

    private static bool IsNavy(SKColor c) => c.Blue > 90 && c.Red < 40 && c.Green < 40;

    private static Publication Plain()
    {
        var theme = BuiltInThemes.Clean();
        theme.Header.Left = theme.Header.Center = theme.Header.Right = null;
        theme.Footer.Left = theme.Footer.Center = theme.Footer.Right = null;
        return new Publication { Name = "Test", Theme = theme };
    }
}
