using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Rendering;
using AllInOnePdf.Core.Themes;

namespace AllInOnePdf.Core.Exporters;

/// <summary>Document model → PDF via <see cref="IPdfRenderer"/>, styled by a <see cref="PdfTheme"/>.</summary>
public sealed class PdfExporter : IExporter
{
    public const string OptTheme = "theme";
    public const string OptCover = "cover";
    public const string OptToc = "toc";
    public const string OptTitle = "title";
    public const string OptAuthor = "author";
    public const string OptMetadata = "embedMetadata";
    public const string OptImageQuality = "imageQuality";

    private static readonly IReadOnlyList<OptionChoice> ThemeOverride =
    [
        new("theme", "Use theme setting"),
        new("yes", "Always"),
        new("no", "Never"),
    ];

    private readonly IPdfRenderer _renderer;

    public PdfExporter(IPdfRenderer renderer)
    {
        _renderer = renderer;
    }

    public FileFormat Format => KnownFormats.Pdf;

    public bool SupportsTheme => true;

    public IReadOnlyList<OptionDefinition> Options { get; } =
    [
        OptionDefinition.Theme(OptTheme, "Theme", BuiltInThemes.DefaultId, "Fonts, colours, spacing and page setup of the PDF. Edit or add themes on the Themes page.", "Appearance"),
        OptionDefinition.Choice(OptCover, "Cover page", "theme", ThemeOverride, "Add a title page before the content.", "Structure"),
        OptionDefinition.Choice(OptToc, "Table of contents", "theme", ThemeOverride, "Add a contents page built from the headings.", "Structure"),
        OptionDefinition.Text(OptTitle, "Title override", null, "Replaces the document title on the cover and in PDF metadata.", "Metadata"),
        OptionDefinition.Text(OptAuthor, "Author override", null, "Replaces the author(s) from the source.", "Metadata"),
        OptionDefinition.Bool(OptMetadata, "Embed metadata", true, "Write title, author and keywords into the PDF properties.", "Metadata", advanced: true),
        OptionDefinition.Int(OptImageQuality, "Image quality", 85, 30, 100, "JPEG quality for re-encoded images. Lower makes smaller files.", "Images", unit: "%", advanced: true),
    ];

    public async Task<ExportResult> ExportAsync(ExportContext context, CancellationToken cancellationToken = default)
    {
        var theme = context.Theme ?? BuiltInThemes.Clean();
        var doc = context.Document;

        var title = context.Options.GetString(OptTitle);
        if (!string.IsNullOrWhiteSpace(title))
        {
            doc.Metadata.Title = title.Trim();
        }

        var author = context.Options.GetString(OptAuthor);
        if (!string.IsNullOrWhiteSpace(author))
        {
            doc.Metadata.Authors.Clear();
            doc.Metadata.Authors.AddRange(author.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        var options = new PdfRenderOptions
        {
            IncludeCover = Tri(context.Options.GetString(OptCover)),
            IncludeToc = Tri(context.Options.GetString(OptToc)),
            EmbedMetadata = context.Options.GetBool(OptMetadata, true),
            ImageQuality = context.Options.GetInt(OptImageQuality, 85),
            FallbackTitle = context.BaseName,
        };

        var bytes = await _renderer.RenderAsync(doc, theme, options, context.Progress, cancellationToken);
        return ExportResult.Single(context.BaseName + ".pdf", bytes, Format.MediaType);
    }

    private static bool? Tri(string? value) => value?.ToLowerInvariant() switch
    {
        "yes" or "true" => true,
        "no" or "false" => false,
        _ => null,
    };
}
