using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Exporters.Epub;
using AllInOnePdf.Core.Rendering;
using AllInOnePdf.Core.Themes;

namespace AllInOnePdf.Core.Exporters;

/// <summary>
/// Document model → EPUB 3.3 (with an NCX and a guide so EPUB 2 readers cope). Chapters are XHTML documents made from
/// <see cref="DocumentHtmlRenderer"/> output, styled by a stylesheet generated from the <see cref="PdfTheme"/>. A text
/// title page is added when the cover is on, plus a cover image rendered from the PDF cover page when a PDF renderer
/// and rasteriser are available.
/// </summary>
public sealed class EpubExporter : IExporter
{
    public const string OptTheme = "theme";
    public const string OptChapterSplit = "chapterSplit";
    public const string OptCover = "cover";
    public const string OptCoverImage = "coverImage";
    public const string OptIncludeNav = "includeNav";
    public const string OptEmbedFonts = "embedFonts";
    public const string OptRespectReaderTheme = "respectReaderTheme";
    public const string OptApplyPageBackground = "applyPageBackground";
    public const string OptLanguage = "language";
    public const string OptIdentifier = "identifier";
    public const string OptAllowRawHtml = "allowRawHtml";

    private const string Appearance = "Appearance";
    private const string Structure = "Structure";
    private const string Metadata = "Metadata";
    private const string Advanced = "Advanced";

    private readonly ISyntaxHighlighter? _highlighter;
    private readonly IPdfRenderer? _renderer;
    private readonly IPdfRasterizer? _rasterizer;

    public EpubExporter()
        : this(null, null, null)
    {
    }

    /// <param name="highlighter">Adds <c>hl-*</c> spans to code blocks; optional.</param>
    /// <param name="renderer">With <paramref name="rasterizer"/>, renders the cover image; optional.</param>
    /// <param name="rasterizer">Rasterises the rendered cover page; optional.</param>
    public EpubExporter(ISyntaxHighlighter? highlighter, IPdfRenderer? renderer, IPdfRasterizer? rasterizer)
    {
        _highlighter = highlighter;
        _renderer = renderer;
        _rasterizer = rasterizer;
    }

    public FileFormat Format => KnownFormats.Epub;

    public bool SupportsTheme => true;

    public IReadOnlyList<OptionDefinition> Options { get; } =
    [
        OptionDefinition.Theme(OptTheme, "Theme", BuiltInThemes.DefaultId, "Fonts, sizes and colours for the book. Readers can still change the font size.", Appearance),
        OptionDefinition.Bool(OptRespectReaderTheme, "Respect the reader's theme", true, "Leave text and background colours to the reading app so night and sepia modes work.", Appearance),
        OptionDefinition.Bool(OptEmbedFonts, "Embed fonts", false, "Include the theme's bundled fonts (SIL Open Font License) so every reader shows them. Makes the file larger.", Appearance),
        OptionDefinition.Bool(OptApplyPageBackground, "Use the theme's page colour", false, "Paint the theme's page background instead of the reader's.", Appearance, advanced: true),

        OptionDefinition.Choice(OptChapterSplit, "Chapters", "h1",
        [
            new OptionChoice("h1", "At level-1 headings", "Each # heading starts a new chapter."),
            new OptionChoice("h2", "At level-1 and level-2 headings", "Shorter chapters; useful for documents with a single title heading."),
            new OptionChoice("page-breaks", "At page breaks", "Explicit page breaks from the source start a new chapter."),
            new OptionChoice("none", "One chapter", "The whole document in a single file."),
        ], "Where the book is split into chapter files. Readers start each chapter on a new page.", Structure),
        OptionDefinition.Choice(OptCover, "Title page", "theme",
        [
            new OptionChoice("theme", "Use theme setting"),
            new OptionChoice("yes", "Always"),
            new OptionChoice("no", "Never"),
        ], "Add a title page with the title, subtitle, author and date.", Structure),
        OptionDefinition.Bool(OptCoverImage, "Cover image", true, "Render the theme's cover page as the book's cover picture (shown in the library).", Structure),
        OptionDefinition.Bool(OptIncludeNav, "Contents page", true, "Show the table of contents as a page in the book. Readers' own contents menus work either way.", Structure),

        OptionDefinition.Text(OptLanguage, "Language", null, "Language tag such as en, de or pt-BR. Empty uses the document's language, then English.", Metadata),
        OptionDefinition.Text(OptIdentifier, "Identifier", null, "Unique book identifier (an ISBN or a URN). Empty generates a new urn:uuid.", Metadata, advanced: true),

        OptionDefinition.Bool(OptAllowRawHtml, "Allow raw HTML", false, "Keep HTML blocks from the source after cleaning them up. Blocks that cannot be made valid XHTML are left out.", Advanced, advanced: true),
    ];

    public async Task<ExportResult> ExportAsync(ExportContext context, CancellationToken cancellationToken = default)
    {
        var result = new ExportResult();
        var warnings = result.Warnings;
        var document = context.Document;
        var theme = context.Theme ?? BuiltInThemes.Clean();
        var options = context.Options.WithDefaults(Options);
        var settings = EpubSettings.From(options, document, theme, warnings);

        context.Report(0, "Preparing the book");
        var info = new EpubBookInfo
        {
            Identifier = settings.Identifier ?? "urn:uuid:" + Guid.NewGuid().ToString("D"),
            Title = Clean(document.ResolveTitle()) ?? Clean(context.BaseName) ?? "Untitled",
            Subtitle = Clean(document.Metadata.Subtitle),
            Authors = document.Metadata.Authors.Select(Clean).Where(a => a is not null).Select(a => a!).ToList(),
            Date = document.Metadata.Date,
            Description = Clean(document.Metadata.Description),
            Keywords = document.Metadata.Keywords.Select(Clean).Where(k => k is not null).Select(k => k!).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Language = settings.Language,
            Modified = TruncateToSeconds(DateTimeOffset.UtcNow),
        };

        // ---- chapters
        context.Report(0.05, "Writing chapters");
        var images = new EpubImageStore(document, warnings);
        var chapters = new EpubContentBuilder(_highlighter).Build(document, theme, settings, images, warnings, cancellationToken);
        images.Flush();

        var package = new EpubPackage();
        var tocTitle = string.IsNullOrWhiteSpace(theme.Toc.Title) ? "Contents" : theme.Toc.Title.Trim();
        var depth = Math.Clamp(theme.Toc.Depth, 1, 6);

        // ---- cover
        if (settings.Cover)
        {
            context.Report(0.5, "Making the title page");
            package.CoverPageHref = EpubCover.PageHref;
            package.Add(new EpubItem("cover", EpubCover.PageHref, "application/xhtml+xml", EpubXml.Utf8.GetBytes(EpubCover.BuildPage(info, theme, settings.Language))));
            if (settings.CoverImage && _renderer is not null && _rasterizer is not null)
            {
                var jpeg = await EpubCover.RenderImageAsync(info, theme, _renderer, _rasterizer, warnings, cancellationToken).ConfigureAwait(false);
                if (jpeg is not null)
                {
                    package.CoverImageId = "cover-image";
                    package.Add(new EpubItem("cover-image", EpubCover.ImageHref, "image/jpeg", jpeg, "cover-image"));
                }
            }

            package.Spine.Add("cover");
        }

        // ---- navigation
        context.Report(0.75, "Building the table of contents");
        package.NavInSpine = settings.IncludeNav;
        package.FirstChapterHref = chapters[0].Href;
        var nav = EpubNavigation.BuildNav(chapters, depth, tocTitle, settings.Language, new EpubNavigation.Targets(package.CoverPageHref, settings.IncludeNav, chapters[0].Href));
        package.Add(new EpubItem("nav", EpubNavigation.NavHref, "application/xhtml+xml", EpubXml.Utf8.GetBytes(nav), "nav"));
        package.Add(new EpubItem("ncx", EpubNavigation.NcxHref, "application/x-dtbncx+xml", EpubXml.Utf8.GetBytes(EpubNavigation.BuildNcx(chapters, depth, info.Identifier, info.Title, info.Authors, settings.Language))));
        if (settings.IncludeNav)
        {
            package.Spine.Add("nav");
        }

        foreach (var chapter in chapters)
        {
            package.Add(new EpubItem(chapter.Id, chapter.Href, "application/xhtml+xml", EpubXml.Utf8.GetBytes(chapter.Xhtml)));
            package.Spine.Add(chapter.Id);
        }

        // ---- styles, fonts, images
        var faces = new List<EpubFontFace>();
        if (settings.EmbedFonts)
        {
            var (fontItems, fontFaces) = EpubFonts.Collect(theme, warnings);
            faces.AddRange(fontFaces);
            fontItems.ForEach(package.Add);
        }

        package.Add(new EpubItem("css", "css/style.css", "text/css", EpubXml.Utf8.GetBytes(EpubStylesheet.Build(theme, settings, faces))));
        images.Items.ForEach(package.Add);

        cancellationToken.ThrowIfCancellationRequested();
        context.Report(0.9, "Packing the EPUB");
        var bytes = package.Write(info);
        result.Files.Add(new OutputFile(context.BaseName + ".epub",bytes, Format.MediaType));
        context.Report(1, "EPUB written");
        return result;
    }

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = EpubContentBuilder.Collapse(EpubXml.StripInvalid(value));
        return text.Length == 0 ? null : text;
    }

    private static DateTimeOffset TruncateToSeconds(DateTimeOffset value) =>
        new(value.Year, value.Month, value.Day, value.Hour, value.Minute, value.Second, TimeSpan.Zero);
}
