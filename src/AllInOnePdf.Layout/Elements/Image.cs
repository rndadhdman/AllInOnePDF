using AllInOnePdf.Layout.Infrastructure;
using SkiaSharp;

namespace AllInOnePdf.Layout.Elements;

/// <summary>
/// A raster image. It never paginates: it answers FullRender at the size its <see cref="Scaling"/> gives, or Wrap
/// when that size does not fit. It draws at the top-left of the space it is drawn with.
/// </summary>
/// <remarks>
/// How the picture ends up in the PDF is Skia's decision (SkPDFBitmap): an image created from JPEG data keeps that
/// data and is embedded as DCTDecode untouched; any other image is embedded as JPEG at the document's encoding
/// quality when every pixel is opaque, and losslessly (Flate colour plus a Flate SMask) when any pixel is
/// transparent. <see cref="CompressionQuality"/> re-encodes an opaque image here, before Skia sees it, so a single
/// image can use another quality than the document; transparent images are never re-encoded.
/// </remarks>
internal sealed class Image : Element
{
    private readonly object _gate = new();
    private SKImage? _prepared;

    public Image(SKImage source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Source = source;
    }

    /// <summary>The decoded picture.</summary>
    public SKImage Source { get; }

    public ImageScaling Scaling { get; set; } = ImageScaling.FitWidth;

    /// <summary>When true, <see cref="CompressionQuality"/> is ignored and the decoded source is drawn as is.</summary>
    public bool UseOriginalImage { get; set; }

    /// <summary>Per-image JPEG quality for opaque images; null leaves it to the document setting.</summary>
    public ImageCompressionQuality? CompressionQuality { get; set; }

    /// <summary>Width / height of the source in pixels.</summary>
    public float AspectRatio => Source.Width / (float)Source.Height;

    /// <summary>Decodes <paramref name="data"/>; throws <see cref="DocumentComposeException"/> for anything Skia cannot read.</summary>
    public static Image FromEncoded(byte[] data)
    {
        if (data is null || data.Length == 0)
        {
            throw new DocumentComposeException("The image has no data.");
        }

        SKImage? image;
        try
        {
            image = SKImage.FromEncodedData(data);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new DocumentComposeException("The image data could not be decoded.", ex);
        }

        if (image is null || image.Width <= 0 || image.Height <= 0)
        {
            image?.Dispose();
            throw new DocumentComposeException("The image data could not be decoded (unsupported or corrupt format).");
        }

        return new Image(image);
    }

    public override SpacePlan Measure(Size availableSpace) => ImageFit.Measure(Scaling, AspectRatio, availableSpace);

    public override void Draw(Size availableSpace)
    {
        var target = ImageFit.TargetSize(Scaling, AspectRatio, availableSpace);
        if (target.Width <= 0 || target.Height <= 0)
        {
            return;
        }

        // The measuring pass draws on a canvas without a native surface: skip the (possibly costly) re-encoding.
        var image = Canvas.Native is null ? Source : Prepared();
        Canvas.DrawImage(image, Position.Zero, target);
    }

    /// <summary>The image handed to the canvas: the source, or an opaque source re-encoded at <see cref="CompressionQuality"/>.</summary>
    private SKImage Prepared()
    {
        if (UseOriginalImage || CompressionQuality is not { } quality)
        {
            return Source;
        }

        lock (_gate)
        {
            return _prepared ??= Reencode(Source, Quality(quality)) ?? Source;
        }
    }

    /// <summary>The JPEG quality Skia's PDF backend uses for each setting (same mapping as the document setting).</summary>
    public static int Quality(ImageCompressionQuality quality) => quality switch
    {
        ImageCompressionQuality.Best => 100,
        ImageCompressionQuality.VeryHigh => 95,
        ImageCompressionQuality.High => 90,
        ImageCompressionQuality.Medium => 80,
        ImageCompressionQuality.Low => 65,
        _ => 40,
    };

    private static SKImage? Reencode(SKImage source, int quality)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        if (!source.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0) || !IsOpaque(bitmap))
        {
            // Transparent pixels: Skia keeps the image lossless; never turn it into a JPEG.
            return null;
        }

        using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, quality);
        return data is null ? null : SKImage.FromEncodedData(data);
    }

    private static bool IsOpaque(SKBitmap bitmap)
    {
        var bytes = bitmap.GetPixelSpan();
        for (var i = 3; i < bytes.Length; i += 4)
        {
            if (bytes[i] != 255)
            {
                return false;
            }
        }

        return true;
    }
}
