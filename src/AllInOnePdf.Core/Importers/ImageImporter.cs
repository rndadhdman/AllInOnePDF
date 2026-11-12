using System.Globalization;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Importers.Data;
using AllInOnePdf.Core.Themes;
using SkiaSharp;

namespace AllInOnePdf.Core.Importers;

/// <summary>
/// A picture (PNG, JPEG, WebP, GIF, BMP, TIFF) → a document holding one image. Decoded with SkiaSharp to validate it
/// and read its size; EXIF orientation is applied. PNG and JPEG bytes are kept unchanged when already upright; other
/// formats are stored as PNG so every exporter can use them.
/// </summary>
public sealed class ImageImporter : IImporter
{
    public const string OptFit = "fit";
    public const string OptDpi = "dpi";
    public const string OptCaption = "caption";
    public const string OptCaptionText = "captionText";
    public const string OptTranscode = "transcodeToPng";
    public const string OptMaxMegapixels = "maxMegapixels";

    private const string GroupLayout = "Layout";
    private const string GroupCaption = "Caption";
    private const string GroupAdvanced = "Advanced";
    private const long MaxBytes = 512L * 1024 * 1024;

    public FileFormat Format => KnownFormats.Image;

    public ConverterStatus Status => ConverterStatus.Ready;

    public IReadOnlyList<OptionDefinition> Options { get; } =
    [
        OptionDefinition.Choice(OptFit, "Size", "fit-width",
        [
            new OptionChoice("fit-width", "Fit to width", "Scale the picture to the full text width."),
            new OptionChoice("fit-page", "Fit to page", "Full width, limited to the page height by the theme."),
            new OptionChoice("actual-size", "Actual size", "Print at the resolution below; larger pictures are scaled down to fit."),
        ], "How large the picture is placed on the page.", GroupLayout),
        OptionDefinition.Int(OptDpi, "Resolution", 96, 72, 600, "Pixels per inch used for \"Actual size\" (96 is screen size, 300 is print size).", GroupLayout, unit: "dpi"),
        OptionDefinition.Choice(OptCaption, "Caption", "none",
        [
            new OptionChoice("none", "None"),
            new OptionChoice("file-name", "File name"),
            new OptionChoice("custom", "Custom text", "Use the caption text below."),
        ], "Text shown under the picture when the theme shows captions.", GroupCaption),
        OptionDefinition.Text(OptCaptionText, "Caption text", null, "Used when Caption is \"Custom text\".", GroupCaption),
        OptionDefinition.Bool(OptTranscode, "Convert WebP to PNG", true, "Store WebP pictures as PNG for the widest compatibility. GIF, BMP and TIFF are always converted to PNG.", GroupAdvanced, advanced: true),
        OptionDefinition.Int(OptMaxMegapixels, "Maximum size", 80, 1, 1000, "Pictures with more megapixels than this are refused, to limit memory use.", GroupAdvanced, unit: "MP", advanced: true),
    ];

    public bool CanImport(string fileName) => Format.Matches(fileName);

    public async Task<Document> ImportAsync(ImportContext context, CancellationToken cancellationToken = default)
    {
        var options = context.Options.WithDefaults(Options);
        var bytes = await TextDecoding.ReadAllBytesAsync(context.Input, MaxBytes, "The image file is larger than 512 MB and cannot be converted.", cancellationToken);
        context.Report(0.2, "Decoding");
        return await Task.Run(() => Import(bytes, context, options, cancellationToken), cancellationToken);
    }

    private Document Import(byte[] bytes, ImportContext context, OptionValues options, CancellationToken ct)
    {
        var document = new Document();
        document.Metadata.SourceFormat = Format.Id;
        document.Metadata.SourcePath = context.SourcePath;
        document.Metadata.Title = Path.GetFileNameWithoutExtension(context.FileName);

        using var codec = SKCodec.Create(new MemoryStream(bytes, writable: false))
            ?? throw new InvalidDataException(IsTiff(bytes)
                ? $"\"{context.FileName}\" is a TIFF image, which the built-in image decoder cannot read. Save it as PNG or JPEG and convert that."
                : $"\"{context.FileName}\" could not be read as an image; it may be damaged or in an unsupported format.");

        var info = codec.Info;
        var maxMp = Math.Max(1, options.GetInt(OptMaxMegapixels, 80));
        var megapixels = (double)info.Width * info.Height / 1_000_000;
        if (megapixels > maxMp)
        {
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                $"The image is {info.Width} × {info.Height} pixels ({megapixels:0.#} megapixels), above the {maxMp} megapixel limit. Scale it down or raise \"Maximum size\" in the advanced options."));
        }

        var origin = codec.EncodedOrigin;
        var format = codec.EncodedFormat;
        var upright = origin == SKEncodedOrigin.TopLeft;
        var transcodeWebp = options.GetBool(OptTranscode, true);

        if (format == SKEncodedImageFormat.Gif && codec.FrameCount > 1)
        {
            document.ImportWarnings.Add($"\"{context.FileName}\" is animated; only the first frame is used.");
        }

        string mediaType;
        byte[] data;
        int width, height;
        if (upright && format is SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg
            || upright && format == SKEncodedImageFormat.Webp && !transcodeWebp)
        {
            data = bytes;
            mediaType = format switch
            {
                SKEncodedImageFormat.Jpeg => "image/jpeg",
                SKEncodedImageFormat.Webp => "image/webp",
                _ => "image/png",
            };
            (width, height) = (info.Width, info.Height);
        }
        else
        {
            ct.ThrowIfCancellationRequested();
            using var decoded = SKBitmap.Decode(codec)
                ?? throw new InvalidDataException($"\"{context.FileName}\" could not be decoded; it may be damaged.");
            var oriented = ImageOrientation.Apply(decoded, origin);
            try
            {
                // Rotated photos stay JPEG (PNG would be several times larger); everything else becomes PNG.
                var asJpeg = format == SKEncodedImageFormat.Jpeg;
                using var image = SKImage.FromBitmap(oriented);
                using var encoded = image.Encode(asJpeg ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png, asJpeg ? 92 : 100)
                    ?? throw new InvalidDataException($"\"{context.FileName}\" could not be converted to PNG.");
                data = encoded.ToArray();
                mediaType = asJpeg ? "image/jpeg" : "image/png";
                (width, height) = (oriented.Width, oriented.Height);
            }
            finally
            {
                if (!ReferenceEquals(oriented, decoded))
                {
                    oriented.Dispose();
                }
            }
        }

        ct.ThrowIfCancellationRequested();
        var resource = document.AddResource(mediaType, data, ResourceFileName(context.FileName, mediaType));

        var block = new ImageBlock
        {
            Source = resource.Reference,
            AltText = context.FileName,
            PixelWidth = width,
            PixelHeight = height,
        };

        switch (options.GetString(OptFit)?.ToLowerInvariant())
        {
            case "actual-size":
                block.WidthPercent = ActualSizePercent(width, Math.Clamp(options.GetInt(OptDpi, 96), 72, 600));
                break;
            case "fit-page":
                block.WidthPercent = 100;
                document.ImportWarnings.Add("\"Fit to page\" uses the full text width; whether a tall picture is also limited to the page height depends on the theme.");
                break;
            default:
                block.WidthPercent = 100;
                break;
        }

        var captionMode = options.GetString(OptCaption)?.ToLowerInvariant();
        block.Title = captionMode switch
        {
            "file-name" => context.FileName,
            "custom" => string.IsNullOrWhiteSpace(options.GetString(OptCaptionText)) ? null : options.GetString(OptCaptionText)!.Trim(),
            _ => null,
        };

        // Renderers print Title ?? AltText as the caption, so "no caption" must also leave the alt text empty.
        if (captionMode is null or "none" || block.Title is null)
        {
            block.AltText = null;
        }

        document.Blocks.Add(block);
        context.Report(1, "Done");
        return document;
    }

    /// <summary>
    /// Width as a percentage of the default theme's text width when printed at <paramref name="dpi"/>, capped at 100.
    /// </summary>
    internal static double ActualSizePercent(int pixelWidth, int dpi)
    {
        var page = BuiltInThemes.Clean().Page;
        var (pageWidthMm, _) = page.GetSizeMm();
        var contentMm = pageWidthMm - page.MarginLeftMm - page.MarginRightMm;
        if (contentMm <= 0 || dpi <= 0)
        {
            return 100;
        }

        var imageMm = pixelWidth / (double)dpi * 25.4;
        return Math.Round(Math.Clamp(imageMm / contentMm * 100, 1, 100), 2);
    }

    private static bool IsTiff(byte[] b) =>
        b.Length >= 4 && ((b[0] == 'I' && b[1] == 'I' && b[2] == 42 && b[3] == 0) || (b[0] == 'M' && b[1] == 'M' && b[2] == 0 && b[3] == 42));

    private static string ResourceFileName(string fileName, string mediaType)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = mediaType switch
        {
            "image/jpeg" => Path.GetExtension(fileName) is { Length: > 0 } e && (e.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) || e.Equals(".jpg", StringComparison.OrdinalIgnoreCase)) ? e : ".jpg",
            "image/webp" => ".webp",
            _ => ".png",
        };
        return stem + ext;
    }
}
