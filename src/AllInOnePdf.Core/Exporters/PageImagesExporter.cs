using System.Globalization;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Exporters.Images;
using AllInOnePdf.Core.Importers.Pdf;
using AllInOnePdf.Core.Rendering;
using AllInOnePdf.Core.Themes;
using SkiaSharp;

namespace AllInOnePdf.Core.Exporters;

/// <summary>
/// Document → one image per page. A document imported from a PDF that kept its original bytes
/// (<see cref="Documents.Document.SourceFile"/>) is rasterised unchanged; anything else is first laid out with the
/// theme, like the PDF exporter, and the result rasterised. Registered once per format (PNG, JPEG, WebP).
/// </summary>
public abstract class PageImagesExporter : IExporter
{
    public const string OptDpi = "dpi";
    public const string OptPages = "pages";
    public const string OptFirstPageOnly = "firstPageOnly";
    public const string OptQuality = "quality";
    public const string OptBackground = "background";
    public const string OptTransparent = "transparent";
    public const string OptNamePattern = "namePattern";
    public const string OptMaxPages = "maxPages";

    private const string GroupImage = "Image";
    private const string GroupPages = "Pages";
    private const string GroupFiles = "Files";
    private const string GroupAppearance = "Appearance";
    private const string PdfMediaType = "application/pdf";

    private static readonly IReadOnlyList<OptionChoice> ThemeOverride =
    [
        new("theme", "Use theme setting"),
        new("yes", "Always"),
        new("no", "Never"),
    ];

    private readonly IPdfRenderer _renderer;
    private readonly IPdfRasterizer _rasterizer;

    protected PageImagesExporter(IPdfRenderer renderer, IPdfRasterizer rasterizer, FileFormat format, ImageEncoding encoding)
    {
        _renderer = renderer;
        _rasterizer = rasterizer;
        Format = format;
        Encoding = encoding;
        Options = BuildOptions(encoding);
    }

    public FileFormat Format { get; }

    /// <summary>Pixel encoding of the produced files.</summary>
    public ImageEncoding Encoding { get; }

    public bool SupportsTheme => true;

    public IReadOnlyList<OptionDefinition> Options { get; }

    private static List<OptionDefinition> BuildOptions(ImageEncoding encoding)
    {
        var lossy = encoding != ImageEncoding.Png;
        var options = new List<OptionDefinition>
        {
            OptionDefinition.Int(OptDpi, "Resolution", 150, 36, 600, "Pixels per inch. 96 for screens, 150 for documents, 300 for print.", GroupImage, unit: "dpi"),
            OptionDefinition.Int(OptQuality, "Quality", 90, 1, 100,
                lossy ? "Higher keeps more detail; lower makes smaller files." : "Not used for PNG, which is lossless.",
                GroupImage, unit: "%", advanced: !lossy),
            new(OptBackground, "Background", OptionType.Color, "#FFFFFF", "Colour painted behind transparent parts of the page. Themed pages use the theme's page colour.", GroupImage),
        };

        if (encoding == ImageEncoding.Png)
        {
            options.Add(OptionDefinition.Bool(OptTransparent, "Transparent background", false, "Leave the page background transparent instead of painting a colour (also drops the theme's page colour; areas a PDF fills itself stay opaque).", GroupImage));
        }

        options.AddRange(
        [
            OptionDefinition.PageRange(OptPages, "Pages", null, "Pages to export, such as 1-3,7. Leave empty for all pages.", GroupPages),
            OptionDefinition.Bool(OptFirstPageOnly, "First page only", false, "Export just the first page (handy for thumbnails and covers).", GroupPages),
            OptionDefinition.Int(OptMaxPages, "Maximum pages", 500, 1, 10000, "Pages after this many are skipped, with a warning.", GroupPages, advanced: true),
            OptionDefinition.Text(OptNamePattern, "File names", PageImageNaming.DefaultPattern, "{name} is the document name, {page} the page number (zero-padded), {count} the page count.", GroupFiles),
            OptionDefinition.Theme(PdfExporter.OptTheme, "Theme", BuiltInThemes.DefaultId, "Fonts, colours and page setup used to lay out the pages. Not used when the source is a PDF: its pages are drawn as they are.", GroupAppearance),
            OptionDefinition.Choice(PdfExporter.OptCover, "Cover page", "theme", ThemeOverride, "Add a title page before the content (not for PDF sources).", GroupAppearance),
            OptionDefinition.Choice(PdfExporter.OptToc, "Table of contents", "theme", ThemeOverride, "Add a contents page built from the headings (not for PDF sources).", GroupAppearance),
        ]);
        return options;
    }

    public async Task<ExportResult> ExportAsync(ExportContext context, CancellationToken cancellationToken = default)
    {
        var options = context.Options.WithDefaults(Options);
        var result = new ExportResult();
        var doc = context.Document;

        byte[] pdf;
        if (doc.SourceFile is { MediaType: PdfMediaType } source)
        {
            pdf = source.Data;
        }
        else
        {
            var renderOptions = new PdfRenderOptions
            {
                IncludeCover = Tri(options.GetString(PdfExporter.OptCover)),
                IncludeToc = Tri(options.GetString(PdfExporter.OptToc)),
                EmbedMetadata = false,
                FallbackTitle = context.BaseName,
            };
            var theme = context.Theme ?? BuiltInThemes.Clean();
            if (Encoding == ImageEncoding.Png && options.GetBool(OptTransparent))
            {
                // The theme paints every page; make that paint invisible so the PNG keeps an alpha background.
                theme = theme.Clone();
                theme.Page.Background = "#00FFFFFF";
            }

            pdf = await _renderer.RenderAsync(doc, theme, renderOptions, context.Progress, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        int pageCount;
        try
        {
            pageCount = _rasterizer.GetPageCount(pdf);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            throw new InvalidDataException($"The PDF pages could not be read for rasterising ({ex.Message}).", ex);
        }

        if (pageCount <= 0)
        {
            throw new InvalidDataException("The document has no pages to export.");
        }

        IReadOnlyList<int> pages = options.GetBool(OptFirstPageOnly)
            ? [1]
            : PageRangeParser.Parse(options.GetString(OptPages), pageCount);

        var maxPages = Math.Max(1, options.GetInt(OptMaxPages, 500));
        if (pages.Count > maxPages)
        {
            result.Warnings.Add(string.Create(CultureInfo.InvariantCulture,
                $"Only the first {maxPages} of {pages.Count} pages were exported. Raise \"Maximum pages\" in the advanced options or choose a page range."));
            pages = pages.Take(maxPages).ToList();
        }

        var rasterize = new RasterizeOptions
        {
            Dpi = Math.Clamp(options.GetInt(OptDpi, 150), 36, 600),
            Background = ResolveBackground(options, result),
            Encoding = Encoding,
            JpegQuality = Math.Clamp(options.GetInt(OptQuality, 90), 1, 100),
        };

        var pattern = options.GetString(OptNamePattern);
        var extension = Format.PrimaryExtension;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < pages.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var number = pages[i];
            var image = await _rasterizer.RasterizePageAsync(pdf, number - 1, rasterize, cancellationToken);

            var name = PageImageNaming.Build(pattern, context.BaseName, number, pageCount, pages.Count > 1, extension);
            var stem = Path.GetFileNameWithoutExtension(name);
            for (var n = 2; !used.Add(name); n++)
            {
                name = string.Create(CultureInfo.InvariantCulture, $"{stem}-{n}{extension}");
            }

            result.Files.Add(new OutputFile(name, image.Data, Format.MediaType, IsPrimary: i == 0));
            context.Report((double)(i + 1) / pages.Count, string.Create(CultureInfo.InvariantCulture, $"Page {number} of {pageCount}"));
        }

        return result;
    }

    private string ResolveBackground(OptionValues options, ExportResult result)
    {
        if (Encoding == ImageEncoding.Png && options.GetBool(OptTransparent))
        {
            return "#00FFFFFF";
        }

        var value = options.GetString(OptBackground)?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return "#FFFFFF";
        }

        if (!value.StartsWith('#'))
        {
            value = "#" + value;
        }

        if (!SKColor.TryParse(value, out var color))
        {
            result.Warnings.Add($"The background colour \"{value}\" is not a valid hex colour; white was used.");
            return "#FFFFFF";
        }

        // JPEG has no alpha channel; force the colour opaque so a translucent value does not turn black.
        if (Encoding == ImageEncoding.Jpeg && color.Alpha != 255)
        {
            color = color.WithAlpha(255);
        }

        return $"#{color.Alpha:X2}{color.Red:X2}{color.Green:X2}{color.Blue:X2}";
    }

    private static bool? Tri(string? value) => value?.ToLowerInvariant() switch
    {
        "yes" or "true" => true,
        "no" or "false" => false,
        _ => null,
    };
}
