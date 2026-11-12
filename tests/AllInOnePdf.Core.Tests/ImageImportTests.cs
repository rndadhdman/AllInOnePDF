using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Importers;
using AllInOnePdf.Core.Importers.Data;
using SkiaSharp;

namespace AllInOnePdf.Core.Tests;

public sealed class ImageImportTests
{
    [Fact]
    public void Importer_declares_its_format_and_options()
    {
        IImporter importer = new ImageImporter();
        Assert.Equal(ConverterStatus.Ready, importer.Status);
        Assert.Equal("image", importer.Format.Id);
        foreach (var name in new[] { "a.png", "b.JPG", "c.jpeg", "d.webp", "e.gif", "f.bmp" })
        {
            Assert.True(importer.CanImport(name), name);
        }

        // TIFF is not decodable without a native codec, so it is not offered as an input format.
        Assert.False(importer.CanImport("g.tif"));

        Assert.False(importer.CanImport("x.pdf"));
        var keys = importer.Options.Select(o => o.Key).ToList();
        Assert.Equal(["fit", "dpi", "caption", "captionText", "transcodeToPng", "maxMegapixels"], keys);
        Assert.Equal(80, importer.Options.Single(o => o.Key == "maxMegapixels").DefaultValue);
        Assert.True(importer.Options.Single(o => o.Key == "maxMegapixels").Advanced);
        Assert.All(importer.Options, o => Assert.False(string.IsNullOrWhiteSpace(o.Description)));
    }

    [Fact]
    public async Task Png_is_kept_byte_for_byte_with_its_size()
    {
        var png = ImageTestData.Encode(300, 200, SKEncodedImageFormat.Png);
        var doc = await ImageTestData.ImportImageAsync(png, "photo.png");

        var resource = Assert.Single(doc.Resources);
        Assert.Equal("image/png", resource.MediaType);
        Assert.Equal(png, resource.Data);
        var image = Assert.IsType<ImageBlock>(Assert.Single(doc.Blocks));
        Assert.Equal(resource.Reference, image.Source);
        Assert.Equal(300, image.PixelWidth);
        Assert.Equal(200, image.PixelHeight);
        Assert.Equal(100, image.WidthPercent);
        Assert.Null(image.Title);
        Assert.Equal("photo", doc.Metadata.Title);
        Assert.Equal("image", doc.Metadata.SourceFormat);
        Assert.Empty(doc.ImportWarnings);
    }

    [Fact]
    public async Task Jpeg_is_kept_as_is()
    {
        var jpeg = ImageTestData.Encode(64, 48, SKEncodedImageFormat.Jpeg);
        var doc = await ImageTestData.ImportImageAsync(jpeg, "shot.jpg");
        var resource = Assert.Single(doc.Resources);
        Assert.Equal("image/jpeg", resource.MediaType);
        Assert.Equal(jpeg, resource.Data);
        Assert.Equal("shot.jpg", resource.FileName);
    }

    [Fact]
    public async Task Webp_is_transcoded_to_png_unless_turned_off()
    {
        var webp = ImageTestData.Encode(80, 40, SKEncodedImageFormat.Webp);

        var doc = await ImageTestData.ImportImageAsync(webp, "pic.webp");
        var resource = Assert.Single(doc.Resources);
        Assert.Equal("image/png", resource.MediaType);
        Assert.Equal(0x89, resource.Data[0]);
        Assert.Equal((80, 40), ImageTestData.Decode(resource.Data));

        var kept = await ImageTestData.ImportImageAsync(webp, "pic.webp", o => o.Set(ImageImporter.OptTranscode, false));
        Assert.Equal("image/webp", Assert.Single(kept.Resources).MediaType);
        Assert.Equal(webp, kept.Resources[0].Data);
    }

    [Fact]
    public async Task Bmp_and_gif_become_png_with_their_dimensions()
    {
        var bmp = await ImageTestData.ImportImageAsync(ImageTestData.CreateBmp(31, 17), "scan.bmp");
        Assert.Equal("image/png", bmp.Resources[0].MediaType);
        Assert.Equal((31, 17), ImageTestData.Decode(bmp.Resources[0].Data));
        var block = bmp.Blocks.OfType<ImageBlock>().Single();
        Assert.Equal(31, block.PixelWidth);
        Assert.Equal(17, block.PixelHeight);

        var gif = await ImageTestData.ImportImageAsync(ImageTestData.CreateGif(10, 6), "anim.gif");
        Assert.Equal("image/png", gif.Resources[0].MediaType);
        Assert.Equal((10, 6), ImageTestData.Decode(gif.Resources[0].Data));
        Assert.Empty(gif.ImportWarnings);
    }

    [Fact]
    public async Task Animated_gif_uses_the_first_frame_with_a_warning()
    {
        var doc = await ImageTestData.ImportImageAsync(ImageTestData.CreateGif(8, 8, frames: 3), "spinner.gif");
        Assert.Equal((8, 8), ImageTestData.Decode(doc.Resources[0].Data));
        Assert.Contains(doc.ImportWarnings, w => w.Contains("first frame", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Fit_modes_set_the_width()
    {
        var png = ImageTestData.Encode(321, 100, SKEncodedImageFormat.Png);

        var actual = await ImageTestData.ImportImageAsync(png, "a.png", o => o.Set(ImageImporter.OptFit, "actual-size"));
        // 321 px at 96 dpi = 84.9 mm of the default theme's 170 mm text width.
        Assert.InRange(actual.Blocks.OfType<ImageBlock>().Single().WidthPercent!.Value, 49, 51);

        var print = await ImageTestData.ImportImageAsync(png, "a.png", o => o.Set(ImageImporter.OptFit, "actual-size").Set(ImageImporter.OptDpi, 300));
        Assert.InRange(print.Blocks.OfType<ImageBlock>().Single().WidthPercent!.Value, 15, 17);

        var huge = await ImageTestData.ImportImageAsync(ImageTestData.Encode(3000, 100, SKEncodedImageFormat.Png), "a.png", o => o.Set(ImageImporter.OptFit, "actual-size"));
        Assert.Equal(100, huge.Blocks.OfType<ImageBlock>().Single().WidthPercent);

        var page = await ImageTestData.ImportImageAsync(png, "a.png", o => o.Set(ImageImporter.OptFit, "fit-page"));
        Assert.Equal(100, page.Blocks.OfType<ImageBlock>().Single().WidthPercent);
        Assert.Contains(page.ImportWarnings, w => w.Contains("theme", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Caption_modes()
    {
        var png = ImageTestData.Encode(20, 20, SKEncodedImageFormat.Png);

        var none = (await ImageTestData.ImportImageAsync(png, "cat.png")).Blocks.OfType<ImageBlock>().Single();
        Assert.Null(none.Title);
        Assert.Null(none.AltText);

        var name = (await ImageTestData.ImportImageAsync(png, "cat.png", o => o.Set(ImageImporter.OptCaption, "file-name"))).Blocks.OfType<ImageBlock>().Single();
        Assert.Equal("cat.png", name.Title);
        Assert.Equal("cat.png", name.AltText);

        var custom = (await ImageTestData.ImportImageAsync(png, "cat.png", o => o.Set(ImageImporter.OptCaption, "custom").Set(ImageImporter.OptCaptionText, " The cat "))).Blocks.OfType<ImageBlock>().Single();
        Assert.Equal("The cat", custom.Title);
    }

    [Fact]
    public async Task Oversize_and_invalid_images_are_refused_clearly()
    {
        var big = ImageTestData.Encode(1200, 1000, SKEncodedImageFormat.Png);
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => ImageTestData.ImportImageAsync(big, "big.png", o => o.Set(ImageImporter.OptMaxMegapixels, 1)));
        Assert.Contains("1200 × 1000", ex.Message);
        Assert.Contains("megapixel", ex.Message);

        var junk = await Assert.ThrowsAsync<InvalidDataException>(() => ImageTestData.ImportImageAsync("not an image"u8.ToArray(), "fake.png"));
        Assert.Contains("fake.png", junk.Message);

        byte[] tiff = [(byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0, 0, 0];
        var tiffError = await Assert.ThrowsAsync<InvalidDataException>(() => ImageTestData.ImportImageAsync(tiff, "scan.tif"));
        Assert.Contains("TIFF", tiffError.Message);
    }

    [Fact]
    public void Orientation_rotates_and_swaps_axes()
    {
        using var source = new SKBitmap(4, 2);
        source.Erase(SKColors.Blue);
        source.SetPixel(0, 0, SKColors.Red);

        using var cw = ImageOrientation.Apply(source, SKEncodedOrigin.RightTop);
        Assert.Equal(2, cw.Width);
        Assert.Equal(4, cw.Height);
        Assert.Equal(SKColors.Red, cw.GetPixel(1, 0));

        using var ccw = ImageOrientation.Apply(source, SKEncodedOrigin.LeftBottom);
        Assert.Equal(SKColors.Red, ccw.GetPixel(0, 3));

        using var flipped = ImageOrientation.Apply(source, SKEncodedOrigin.BottomRight);
        Assert.Equal(4, flipped.Width);
        Assert.Equal(SKColors.Red, flipped.GetPixel(3, 1));

        Assert.Same(source, ImageOrientation.Apply(source, SKEncodedOrigin.TopLeft));
    }
}
