using System.Globalization;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Importers.Epub;
using AllInOnePdf.Core.Importers.Html;
using AllInOnePdf.Core.Rendering;

namespace AllInOnePdf.Core.Importers;

/// <summary>
/// EPUB 2/3 → document model. Reads the package (OPF) for metadata and reading order, converts every spine XHTML file
/// with the HTML converter (images come from the archive, never the network) and joins the chapters.
/// DRM-protected books are rejected with a clear message; unsafe archives (path traversal, zip bombs) are refused.
/// </summary>
public sealed class EpubImporter : IImporter
{
    public const string OptChapterBreaks = "chapterBreaks";
    public const string OptIncludeCover = "includeCover";
    public const string OptEmbedImages = "embedImages";
    public const string OptMaxImageMb = "maxImageMb";

    /// <summary>Upper bound for all embedded images together, so a picture book cannot exhaust memory.</summary>
    private const long MaxTotalImageBytes = 512L * 1024 * 1024;

    private const long MaxInputBytes = 1024L * 1024 * 1024;

    private const string ChapterLinkPrefix = "aio-epub-chapter:";

    public FileFormat Format => KnownFormats.Epub;

    public ConverterStatus Status => ConverterStatus.Ready;

    public IReadOnlyList<OptionDefinition> Options { get; } =
    [
        OptionDefinition.Choice(OptChapterBreaks, "Chapter breaks", "marker",
        [
            new OptionChoice("marker", "Page break", "Start every chapter on a new page."),
            new OptionChoice("none", "None", "Run the chapters together."),
            new OptionChoice("headings", "Headings from contents", "Give chapters without a level-1 heading the title from the book's table of contents."),
        ], "How chapter boundaries appear in the document.", "Content"),
        OptionDefinition.Bool(OptIncludeCover, "Include cover", false, "Keep the cover page (its image; a title repeating the book title is dropped).", "Content"),
        OptionDefinition.Bool(OptEmbedImages, "Embed images", true, "Copy the book's pictures into the document.", "Images"),
        OptionDefinition.Int(OptMaxImageMb, "Largest image", 25, 1, 200, "Images larger than this are left out.", "Images", unit: "MB", advanced: true),
    ];

    public async Task<Document> ImportAsync(ImportContext context, CancellationToken cancellationToken = default)
    {
        var options = context.Options.WithDefaults(Options);
        context.Report(0, "Opening EPUB");

        Stream input = context.Input;
        MemoryStream? copy = null;
        if (!input.CanSeek)
        {
            copy = new MemoryStream();
            await HtmlImporter.CopyLimitedAsync(input, copy, MaxInputBytes, cancellationToken);
            copy.Position = 0;
            input = copy;
        }

        try
        {
            using var archive = EpubArchive.Open(input);
            EpubPackage.EnsureNotEncrypted(archive);
            var package = EpubPackage.Load(archive);
            return Convert(archive, package, options, context, cancellationToken);
        }
        finally
        {
            if (copy is not null)
            {
                await copy.DisposeAsync();
            }
        }
    }

    private Document Convert(EpubArchive archive, EpubPackage package, OptionValues options, ImportContext context, CancellationToken cancellationToken)
    {
        var document = new Document();
        var meta = document.Metadata;
        meta.Title = package.Title;
        meta.Subtitle = package.Subtitle;
        meta.Authors.AddRange(package.Creators);
        meta.Language = package.Language;
        meta.Description = package.Description;
        meta.Date = HtmlDocumentConverter.ParseDate(package.Date);
        meta.Keywords.AddRange(package.Subjects);
        meta.SourceFormat = Format.Id;
        meta.Subtitle ??= CoverSubtitle(archive, package);

        var breaks = (options.GetString(OptChapterBreaks) ?? "marker").ToLowerInvariant();
        var includeCover = options.GetBool(OptIncludeCover);
        var maxImageBytes = Math.Max(1, options.GetInt(OptMaxImageMb, 25)) * 1024L * 1024L;
        var settings = new HtmlConversionSettings
        {
            MainContentOnly = false,
            EmbedImages = options.GetBool(OptEmbedImages, true),
            MaxImageBytes = maxImageBytes,
        };

        var converter = new HtmlDocumentConverter(document, settings);
        var spinePaths = new HashSet<string>(package.Spine.Select(s => s.Path), StringComparer.Ordinal);
        var chapterAnchors = new Dictionary<string, string?>(StringComparer.Ordinal);
        long imageBytes = 0;
        var imageCapHit = false;

        var chapters = package.Spine.Where(s => !string.Equals(s.Path, package.NavPath, StringComparison.Ordinal) || package.Spine.Count == 1).ToList();
        var index = 0;
        var emittedChapters = 0;
        foreach (var item in chapters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.Report(0.05 + 0.9 * index++ / Math.Max(1, chapters.Count), "Reading " + Path.GetFileName(item.Path));

            var isCover = string.Equals(item.Path, package.CoverPagePath, StringComparison.Ordinal);
            if (isCover && !includeCover)
            {
                continue;
            }

            var text = archive.ReadText(item.Path);
            if (text is null)
            {
                context.Warnings.Add($"The chapter '{item.Path}' is missing from the EPUB and was skipped.");
                continue;
            }

            var dom = HtmlDocumentConverter.CreateParser().ParseDocument(HtmlDocumentConverter.FixSelfClosingTags(text));
            var chapterPath = item.Path;

            ImageLookup Resolve(string source, long maxBytes)
            {
                var path = EpubArchive.ResolvePath(chapterPath, source);
                if (path is null || archive.GetLength(path) is not { } length)
                {
                    return ImageLookup.NotFound;
                }

                if (length > maxBytes)
                {
                    return new ImageLookup(ImageLookupStatus.TooLarge);
                }

                if (imageBytes + length > MaxTotalImageBytes)
                {
                    imageCapHit = true;
                    return new ImageLookup(ImageLookupStatus.TooLarge);
                }

                var data = archive.ReadBytes(path, maxBytes);
                if (data is null)
                {
                    return ImageLookup.NotFound;
                }

                var mediaType = ImageMediaTypes.Detect(data, path);
                if (mediaType is null)
                {
                    return new ImageLookup(ImageLookupStatus.NotAnImage);
                }

                imageBytes += data.LongLength;
                return new ImageLookup(ImageLookupStatus.Found, data, mediaType, Path.GetFileName(path), path);
            }

            string RewriteLink(string href)
            {
                if (href.StartsWith('#'))
                {
                    return href;
                }

                var target = EpubArchive.ResolvePath(chapterPath, href);
                if (target is null || !spinePaths.Contains(target))
                {
                    return href;
                }

                var hash = href.IndexOf('#', StringComparison.Ordinal);
                return hash >= 0 && hash < href.Length - 1 ? href[hash..] : ChapterLinkPrefix + target;
            }

            var blocks = converter.Convert(dom, Resolve, RewriteLink);
            if (isCover)
            {
                blocks = CleanCover(blocks, package.Title);
                if (!Block.EnumerateRecursive(blocks).OfType<ImageBlock>().Any() && CoverImage(document, archive, package) is { } coverImage)
                {
                    blocks.Insert(0, coverImage);
                }
            }

            if (blocks.Count == 0)
            {
                continue;
            }

            if (!isCover && breaks == "headings" && package.NavTitles.TryGetValue(item.Path, out var navTitle) && !blocks.OfType<HeadingBlock>().Any(h => h.Level == 1))
            {
                blocks.Insert(0, new HeadingBlock(1, navTitle));
            }

            var firstHeading = blocks.OfType<HeadingBlock>().FirstOrDefault();
            if (firstHeading is not null)
            {
                firstHeading.Id ??= "chapter-" + (emittedChapters + 1).ToString(CultureInfo.InvariantCulture) + "-" + DocumentHtmlRenderer.Slugify(firstHeading.ToPlainText());
            }

            chapterAnchors[item.Path] = firstHeading?.Id;

            if (emittedChapters > 0 && breaks == "marker")
            {
                document.Blocks.Add(new PageBreakBlock());
            }

            document.Blocks.AddRange(blocks);
            emittedChapters++;
        }

        if (includeCover && package.CoverPagePath is null && CoverImage(document, archive, package) is { } standalone)
        {
            document.Blocks.Insert(0, standalone);
            if (breaks == "marker" && document.Blocks.Count > 1)
            {
                document.Blocks.Insert(1, new PageBreakBlock());
            }
        }

        FixChapterLinks(document, chapterAnchors);

        converter.AddWarnings(context.Warnings);
        if (imageCapHit)
        {
            context.Warnings.Add("The book's images exceed 512 MB in total; the remaining images were left out.");
        }

        if (emittedChapters == 0)
        {
            context.Warnings.Add("The EPUB has no readable chapters.");
        }

        if (string.IsNullOrWhiteSpace(meta.Title))
        {
            meta.Title = document.Headings.FirstOrDefault(h => h.Level == 1)?.ToPlainText();
        }

        context.Report(1, "EPUB read");
        return document;
    }

    /// <summary>
    /// The subtitle printed on the cover page (<c>.aio-cover-subtitle</c> in our own books, or an element marked
    /// <c>epub:type="subtitle"</c> / <c>class="subtitle"</c>) when the package metadata has none.
    /// </summary>
    private static string? CoverSubtitle(EpubArchive archive, EpubPackage package)
    {
        if (package.CoverPagePath is null || archive.ReadText(package.CoverPagePath) is not { } text)
        {
            return null;
        }

        var dom = HtmlDocumentConverter.CreateParser().ParseDocument(HtmlDocumentConverter.FixSelfClosingTags(text));
        var element = dom.QuerySelector(".aio-cover-subtitle")
            ?? dom.All.FirstOrDefault(e => (e.GetAttribute("epub:type") ?? string.Empty).Split(' ').Contains("subtitle", StringComparer.Ordinal))
            ?? dom.QuerySelector(".subtitle");
        var subtitle = element is null ? null : string.Join(' ', element.TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(subtitle) || string.Equals(subtitle, package.Title, StringComparison.OrdinalIgnoreCase) ? null : subtitle;
    }

    /// <summary>Drops a cover-page heading or paragraph that only repeats the book title.</summary>
    private static List<Block> CleanCover(List<Block> blocks, string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return blocks;
        }

        return blocks.Where(b => b is not InlineContainerBlock icb || !string.Equals(icb.ToPlainText().Trim(), title.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static ImageBlock? CoverImage(Document document, EpubArchive archive, EpubPackage package)
    {
        if (package.CoverImagePath is null)
        {
            return null;
        }

        var data = archive.ReadBytes(package.CoverImagePath, 50L * 1024 * 1024);
        var mediaType = data is null ? null : ImageMediaTypes.Detect(data, package.CoverImagePath);
        if (data is null || mediaType is null)
        {
            return null;
        }

        var resource = document.AddResource(mediaType, data, Path.GetFileName(package.CoverImagePath));
        return new ImageBlock { Source = resource.Reference, AltText = package.Title ?? "Cover" };
    }

    private static void FixChapterLinks(Document document, Dictionary<string, string?> anchors)
    {
        static void Walk(IEnumerable<Inline> inlines, Dictionary<string, string?> anchors)
        {
            foreach (var inline in inlines)
            {
                if (inline is LinkInline link && link.Url.StartsWith(ChapterLinkPrefix, StringComparison.Ordinal))
                {
                    var path = link.Url[ChapterLinkPrefix.Length..];
                    link.Url = anchors.GetValueOrDefault(path) is { } id ? "#" + id : "#";
                }

                Walk(inline.Children, anchors);
            }
        }

        // Table cells, list items and quotes are part of the recursive enumeration.
        foreach (var block in document.EnumerateAllBlocks().Concat(document.Footnotes.SelectMany(f => Block.EnumerateRecursive(f.Blocks))).OfType<InlineContainerBlock>())
        {
            Walk(block.Inlines, anchors);
        }
    }
}
