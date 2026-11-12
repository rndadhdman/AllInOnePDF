using System.Text;
using AllInOnePdf.Layout.Fluent;
using AllInOnePdf.Layout.Infrastructure;
using SkiaSharp;

namespace AllInOnePdf.Layout.Tests;

public class MediaImageTests
{
    // 200 x 100: aspect ratio 2.
    private static readonly byte[] Wide = MediaHarness.Picture(200, 100, SKColors.Red);

    private static SpacePlan Measure(Func<ImageDescriptor, ImageDescriptor> fit, Size available)
    {
        var root = new MediaRoot();
        fit(root.Image(Wide));
        MediaHarness.Start(root, FreeCanvas.Instance);
        return root.Measure(available);
    }

    [Fact]
    public void Fit_width_is_the_default_and_takes_the_available_width()
    {
        Assert.Equal(SpacePlan.FullRender(100, 50), Measure(d => d, new Size(100, 500)));
        Assert.Equal(SpacePlan.FullRender(100, 50), Measure(d => d.FitWidth(), new Size(100, 500)));
    }

    [Fact]
    public void Fit_height_takes_the_available_height()
    {
        Assert.Equal(SpacePlan.FullRender(80, 40), Measure(d => d.FitHeight(), new Size(500, 40)));
    }

    [Fact]
    public void Fit_area_fits_both_dimensions()
    {
        Assert.Equal(SpacePlan.FullRender(100, 50), Measure(d => d.FitArea(), new Size(100, 100)));
        Assert.Equal(SpacePlan.FullRender(200, 100), Measure(d => d.FitArea(), new Size(300, 100)));
        Assert.Equal(SpacePlan.FullRender(100, 50), Measure(d => d.FitArea(), new Size(100, Size.Infinity)));
    }

    [Fact]
    public void Fit_unproportionally_fills_the_space()
    {
        Assert.Equal(SpacePlan.FullRender(70, 90), Measure(d => d.FitUnproportionally(), new Size(70, 90)));
    }

    [Fact]
    public void A_fitted_size_larger_than_the_space_wraps()
    {
        Assert.Equal(SpacePlanType.Wrap, Measure(d => d.FitWidth(), new Size(100, 30)).Type);
        Assert.Equal(SpacePlanType.Wrap, Measure(d => d.FitHeight(), new Size(50, 40)).Type);
        Assert.Equal(SpacePlanType.Wrap, Measure(d => d.FitHeight(), new Size(100, Size.Infinity)).Type);
    }

    [Fact]
    public void Undecodable_data_throws_at_the_call()
    {
        var root = new MediaRoot();
        Assert.Throws<DocumentComposeException>(() => root.Image([1, 2, 3, 4, 5]));
        Assert.Throws<DocumentComposeException>(() => root.Image([]));
        Assert.Throws<DocumentComposeException>(() => root.Image(Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'/>")));
        Assert.Null(root.Child);
    }

    [Fact]
    public void Images_paint_at_the_top_left_of_their_box()
    {
        using var canvas = new MediaBitmapCanvas(100, 100);
        var root = new MediaRoot();
        root.Image(Wide).FitArea();
        MediaHarness.Start(root, canvas);
        Assert.Equal(SpacePlan.FullRender(100, 50), root.Measure(new Size(100, 100)));
        root.Draw(new Size(100, 100));

        Assert.True(MediaHarness.IsNear(canvas.Pixel(50, 25), SKColors.Red), canvas.Pixel(50, 25).ToString());
        Assert.True(MediaHarness.IsNear(canvas.Pixel(50, 75), SKColors.White), canvas.Pixel(50, 75).ToString());
    }

    [Fact]
    public void The_measuring_pass_draws_on_the_canvas_without_preparing_images()
    {
        var canvas = new MediaRecordingCanvas();
        var root = new MediaRoot();
        root.Image(Wide).WithCompressionQuality(ImageCompressionQuality.Low);
        MediaHarness.Start(root, canvas);
        root.Draw(new Size(100, 100));
        var op = Assert.Single(canvas.Ops);
        Assert.Equal(("image", new Size(100, 50)), (op.Kind, op.Size));
    }

    [Fact]
    public void A_compression_quality_reencodes_opaque_images_as_jpeg_and_leaves_transparent_ones_alone()
    {
        SKImage Drawn(byte[] data, Func<ImageDescriptor, ImageDescriptor> options)
        {
            using var canvas = new MediaBitmapCanvas(50, 50);
            var root = new MediaRoot();
            options(root.Image(data));
            MediaHarness.Start(root, canvas);
            root.Draw(new Size(50, 50));
            return Assert.Single(canvas.Images);
        }

        static bool IsJpeg(SKImage image) => image.EncodedData is { } d && d.AsSpan() is [0xFF, 0xD8, ..];
        static bool IsPng(SKImage image) => image.EncodedData is { } d && d.AsSpan() is [0x89, 0x50, ..];

        var opaque = MediaHarness.Picture(20, 20, SKColors.Blue);
        var transparent = MediaHarness.Picture(20, 20, SKColors.Blue, transparentHalf: true);

        Assert.True(IsPng(Drawn(opaque, d => d)));
        Assert.True(IsJpeg(Drawn(opaque, d => d.WithCompressionQuality(ImageCompressionQuality.Medium))));
        Assert.True(IsPng(Drawn(opaque, d => d.WithCompressionQuality(ImageCompressionQuality.Medium).UseOriginalImage())));
        Assert.True(IsPng(Drawn(transparent, d => d.WithCompressionQuality(ImageCompressionQuality.Medium))));
    }

    /// <summary>
    /// Documents what SKDocument (Skia's PDF backend) does with images, which the engine relies on: JPEG data is
    /// embedded as is (DCTDecode); other opaque images become JPEG only when the encoding quality is 100 or less
    /// (Skia's default, 101, is lossless); images with transparent pixels stay lossless with a soft mask.
    /// </summary>
    [Fact]
    public void Skia_pdf_keeps_jpeg_as_is_reencodes_opaque_images_and_keeps_transparent_ones_lossless()
    {
        static string Pdf(byte[] picture, int quality)
        {
            using var stream = new MemoryStream();
            using (var document = SKDocument.CreatePdf(stream, new SKDocumentPdfMetadata { EncodingQuality = quality }))
            {
                var canvas = document.BeginPage(200, 200);
                using var image = SKImage.FromEncodedData(picture);
                canvas.DrawImage(image, SKRect.Create(0, 0, 100, 100), new SKSamplingOptions(SKFilterMode.Linear));
                document.EndPage();
                document.Close();
            }

            return Encoding.Latin1.GetString(stream.ToArray());
        }

        var jpeg = MediaHarness.Picture(64, 64, SKColors.Green, format: SKEncodedImageFormat.Jpeg);
        var opaquePng = MediaHarness.Picture(64, 64, SKColors.Green);
        var transparentPng = MediaHarness.Picture(64, 64, SKColors.Green, transparentHalf: true);

        Assert.Contains("/DCTDecode", Pdf(jpeg, SKDocumentPdfMetadata.DefaultEncodingQuality), StringComparison.Ordinal);
        Assert.Contains("/DCTDecode", Pdf(jpeg, 40), StringComparison.Ordinal);
        Assert.DoesNotContain("/DCTDecode", Pdf(opaquePng, SKDocumentPdfMetadata.DefaultEncodingQuality), StringComparison.Ordinal);
        Assert.Contains("/DCTDecode", Pdf(opaquePng, 80), StringComparison.Ordinal);

        var transparent = Pdf(transparentPng, 80);
        Assert.DoesNotContain("/DCTDecode", transparent, StringComparison.Ordinal);
        Assert.Contains("/SMask", transparent, StringComparison.Ordinal);
    }
}
