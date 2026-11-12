using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Importers.Pdf;
using AllInOnePdf.Core.Rendering;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Exceptions;
using UglyToad.PdfPig.Outline;

namespace AllInOnePdf.Core.Importers;

/// <summary>
/// PDF → document model using PdfPig plus layout analysis (<c>Importers/Pdf</c>): words with letter-level font
/// info, lines in reading order, running headers/footers removed, headings from bookmarks or font sizes, lists,
/// code blocks, tables, quotes and callouts, images and links. Pure managed code, offline.
/// </summary>
public sealed class PdfImporter : IImporter
{
    public const string OptPages = "pages";
    public const string OptUseBookmarks = "useBookmarks";
    public const string OptDetectHeadings = "detectHeadings";
    public const string OptHeadingSensitivity = "headingSensitivity";
    public const string OptDetectLists = "detectLists";
    public const string OptDetectCode = "detectCode";
    public const string OptDetectTables = "detectTables";
    public const string OptExtractImages = "extractImages";
    public const string OptMinImageSize = "minImageSizePx";
    public const string OptRemoveHeadersFooters = "removeHeadersFooters";
    public const string OptSkipToc = "skipTableOfContents";
    public const string OptMergeHyphenation = "mergeHyphenation";
    public const string OptStripHeadingNumbers = "stripHeadingNumbers";
    public const string OptPageBreaks = "pageBreaks";
    public const string OptLayout = "layout";
    public const string OptPassword = "password";
    public const string OptKeepSourcePdf = "keepSourcePdf";

    private const string GroupPages = "Pages";
    private const string GroupStructure = "Structure";
    private const string GroupCleanup = "Clean-up";
    private const string GroupContent = "Images";
    private const string GroupSecurity = "Security";

    public FileFormat Format => KnownFormats.Pdf;

    public ConverterStatus Status => ConverterStatus.Ready;

    public IReadOnlyList<OptionDefinition> Options { get; } =
    [
        PageRangeOption(OptPages, "Pages", "Pages to read, such as 1-3,7. Leave empty for all pages.", GroupPages),
        OptionDefinition.Choice(OptPageBreaks, "Page breaks", "none",
        [
            new OptionChoice("none", "None", "Join the pages into one continuous document."),
            new OptionChoice("marker", "Marker", "Insert a page break between pages."),
        ], "Whether the page boundaries of the PDF are kept.", GroupPages),
        OptionDefinition.Choice(OptLayout, "Layout analysis", "auto",
        [
            new OptionChoice("auto", "Automatic", "Detect columns, blocks, lists, code and tables."),
            new OptionChoice("simple", "Simple", "Reading order by position only, no block segmentation. Use it for PDFs that come out jumbled."),
        ], "How the page layout is analysed.", GroupStructure),
        OptionDefinition.Bool(OptUseBookmarks, "Use bookmarks for headings", true, "When the PDF has an outline (bookmarks), its entries decide which lines are headings and their levels.", GroupStructure),
        OptionDefinition.Bool(OptDetectHeadings, "Detect headings", true, "Larger or bold lines become headings; the largest size becomes level 1.", GroupStructure),
        OptionDefinition.Choice(OptHeadingSensitivity, "Heading sensitivity", "normal",
        [
            new OptionChoice("low", "Low", "Only clearly larger text becomes a heading."),
            new OptionChoice("normal", "Normal", "Larger text and short bold lines become headings."),
            new OptionChoice("high", "High", "Slightly larger text and longer bold lines also become headings."),
        ], "How readily lines are treated as headings.", GroupStructure, visibleWhen: OptDetectHeadings),
        OptionDefinition.Bool(OptStripHeadingNumbers, "Strip heading numbers", false, "Remove numbering such as 1.2.3 that the PDF shows in front of headings.", GroupStructure),
        OptionDefinition.Bool(OptDetectLists, "Detect lists", true, "Lines starting with bullets, numbers or checkboxes become (nested) lists and task lists.", GroupStructure),
        OptionDefinition.Bool(OptDetectCode, "Detect code", true, "Blocks in a monospace font become code blocks with their indentation.", GroupStructure),
        OptionDefinition.Bool(OptDetectTables, "Detect tables", true, "Ruled or column-aligned text becomes a table (best effort; unclear cases stay paragraphs).", GroupStructure),
        OptionDefinition.Bool(OptRemoveHeadersFooters, "Remove headers and footers", true, "Drop text repeated at the top or bottom of most pages, and page numbers.", GroupCleanup),
        OptionDefinition.Bool(OptSkipToc, "Skip table of contents", true, "Drop a printed table of contents (entries ending in page numbers).", GroupCleanup),
        OptionDefinition.Bool(OptMergeHyphenation, "Join hyphenated words", true, "Rejoin words split by a hyphen at the end of a line (\"exam-\" + \"ple\").", GroupCleanup),
        OptionDefinition.Bool(OptExtractImages, "Extract images", true, "Keep the pictures embedded in the PDF.", GroupContent),
        OptionDefinition.Int(OptMinImageSize, "Minimum image size", 48, 1, 4096, "Smaller images (icons, bullets, decorations) are skipped.", GroupContent, unit: "px", advanced: true, visibleWhen: OptExtractImages),
        OptionDefinition.Text(OptPassword, "Password", null, "Needed only for a PDF that asks for a password to open. It is used to open the file and is not saved with the output.", GroupSecurity),
        OptionDefinition.Bool(OptKeepSourcePdf, "Keep source PDF", true, "Keep the original PDF inside the document so conversions to images render its pages unchanged.", "Output", advanced: true),
    ];

    /// <summary>Core has no <c>OptionDefinition.PageRange</c> helper yet; this builds the same record.</summary>
    private static OptionDefinition PageRangeOption(string key, string label, string description, string group) =>
        new(key, label, OptionType.PageRange, string.Empty, description, group);

    public async Task<Document> ImportAsync(ImportContext context, CancellationToken cancellationToken = default)
    {
        byte[] bytes;
        if (context.Input is MemoryStream ms && ms.Position == 0)
        {
            bytes = ms.ToArray();
        }
        else
        {
            using var buffer = new MemoryStream();
            await context.Input.CopyToAsync(buffer, cancellationToken);
            bytes = buffer.ToArray();
        }

        var settings = PdfImportSettings.From(context.Options.WithDefaults(Options));
        return await Task.Run(() => Import(bytes, context, settings, cancellationToken), cancellationToken);
    }

    private Document Import(byte[] bytes, ImportContext context, PdfImportSettings settings, CancellationToken ct)
    {
        var parsing = new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true };
        if (!string.IsNullOrEmpty(settings.Password))
        {
            parsing.Password = settings.Password;
        }

        PdfDocument pdf;
        try
        {
            pdf = PdfDocument.Open(bytes, parsing);
        }
        catch (PdfDocumentEncryptedException ex)
        {
            throw new InvalidDataException(string.IsNullOrEmpty(settings.Password)
                ? "The PDF is encrypted; supply the password in options."
                : "The PDF password is not correct. Check the password option and try again.", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            // PdfPig's own message ("Could not find the version header comment…") means nothing to users; it stays
            // available as the inner exception.
            throw new InvalidDataException(bytes.Length == 0
                ? "This file is empty, so there is no PDF to read. Check that the file was saved or downloaded completely."
                : "This file is not a PDF or is damaged. Open it in a PDF viewer to check it, or export it to PDF again.", ex);
        }

        using (pdf)
        {
            var document = new Document();
            if (context.Options.GetBool(OptKeepSourcePdf, true))
            {
                document.AddResource("application/pdf", bytes, context.FileName, Document.SourceFileResourceId);
            }

            FillMetadata(document.Metadata, pdf, context);

            var pageNumbers = PageRangeParser.Parse(settings.Pages, pdf.NumberOfPages);
            var outline = settings.UseBookmarks ? ReadOutline(pdf) : null;

            var pages = new List<PageContent>(pageNumbers.Count);
            for (var i = 0; i < pageNumbers.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var number = pageNumbers[i];
                try
                {
                    pages.Add(PageExtractor.Extract(pdf.GetPage(number), settings));
                }
                catch (PdfDocumentEncryptedException ex)
                {
                    throw new InvalidDataException("The PDF is encrypted; supply the password in options.", ex);
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    throw new InvalidDataException($"Page {number} of the PDF could not be read ({ex.Message}).", ex);
                }

                context.Report(0.85 * (i + 1) / pageNumbers.Count, $"Page {number} of {pdf.NumberOfPages}");
            }

            if (pages.Count > 0 && pages.All(p => !p.HasLetters) && pages.Any(p => p.HasImages))
            {
                throw new NotSupportedException("Scanned PDF with no text layer: OCR is not available yet.");
            }

            // A cover page becomes metadata (title, subtitle, authors, the document date), not content.
            var markers = PdfMarkers.Read(pdf);
            CoverPageFilter.Apply(pages, markers, document.Metadata);

            if (settings.RemoveHeadersFooters)
            {
                HeaderFooterFilter.Apply(pages);
            }

            if (settings.SkipTableOfContents)
            {
                TableOfContentsFilter.Apply(pages);
            }

            ct.ThrowIfCancellationRequested();
            var stats = FontStatistics.Compute(pages);
            var headings = new HeadingDetector(stats, pages, settings, outline);
            var builder = new LayoutBuilder(settings, stats, headings, markers);

            List<Block>? previousPage = null;
            for (var i = 0; i < pages.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var blocks = builder.BuildPage(pages[i]);
                if (settings.PageBreakMarkers)
                {
                    if (document.Blocks.Count > 0 && blocks.Count > 0)
                    {
                        document.Blocks.Add(new PageBreakBlock { SourcePage = pages[i].Number });
                    }
                }
                else if (previousPage is not null && blocks.Count > 0 && pages[i].Number == pages[i - 1].Number + 1)
                {
                    JoinAcrossPages(document.Blocks, blocks, builder, settings.MergeHyphenation);
                }

                document.Blocks.AddRange(blocks);
                if (blocks.Count > 0)
                {
                    previousPage = blocks;
                }

                context.Report(0.85 + 0.15 * (i + 1) / pages.Count, $"Analysing page {pages[i].Number}");
            }

            if (settings.StructureEnabled)
            {
                FootnoteRecovery.Apply(document, builder, stats.BodySize);
            }

            foreach (var (block, image) in builder.Images)
            {
                var resource = document.AddResource(image.MediaType, image.Data);
                block.Source = resource.Reference;
            }

            AssignHeadingIds(document);
            context.Report(1, "Done");
            return document;
        }
    }

    /// <summary>
    /// Re-joins a paragraph or code block that a page break split in two (the first page's last block continues on
    /// the next page). Removes the continuation from <paramref name="next"/>.
    /// </summary>
    private static void JoinAcrossPages(List<Block> output, List<Block> next, LayoutBuilder builder, bool mergeHyphenation)
    {
        if (output.Count == 0 || next.Count == 0)
        {
            return;
        }

        var last = output[^1];
        var first = next[0];
        if (last is TableBlock lt && first is TableBlock ft && TryMergeTables(lt, ft))
        {
            next.RemoveAt(0);
            return;
        }

        if (last is CodeBlock lc && first is CodeBlock fc && (fc.Language is null || fc.Language == lc.Language))
        {
            lc.Code = lc.Code + "\n" + fc.Code;
            next.RemoveAt(0);
            return;
        }

        // A quote or callout continued on the next page arrives as a quote (the bar without the title line).
        if (first is QuoteBlock continuation && last is CalloutBlock or QuoteBlock)
        {
            var target = last is CalloutBlock callout ? callout.Blocks : ((QuoteBlock)last).Blocks;
            var moved = continuation.Blocks.ToList();
            if (target.Count > 0 && moved.Count > 0 && target[^1] is ParagraphBlock tp && moved[0] is ParagraphBlock mp && TryMergeParagraphs(tp, mp, builder, mergeHyphenation))
            {
                moved.RemoveAt(0);
            }

            target.AddRange(moved);
            next.RemoveAt(0);
            return;
        }

        if (last is ParagraphBlock lp && first is ParagraphBlock fp && TryMergeParagraphs(lp, fp, builder, mergeHyphenation))
        {
            next.RemoveAt(0);
        }
    }

    /// <summary>
    /// Appends a table continued on the next page (same column count; the header row repeated at the top of the
    /// page is dropped) to the table before it, which keeps its header and column alignment. Tables with different
    /// headers stay apart.
    /// </summary>
    internal static bool TryMergeTables(TableBlock previous, TableBlock continuation)
    {
        if (previous.Rows.Count == 0 || continuation.Rows.Count == 0 || previous.ColumnCount != continuation.ColumnCount)
        {
            return false;
        }

        static string[] Texts(TableRow row) => row.Cells.Select(c => c.ToPlainText().Trim()).ToArray();

        var header = previous.Rows.TakeWhile(r => r.IsHeader).ToList();
        var repeated = continuation.Rows.TakeWhile(r => r.IsHeader).ToList();
        int skip;
        if (header.Count > 0)
        {
            // The repeated header may come back flagged or not (bold detection), but its text must match.
            var candidate = continuation.Rows.Take(header.Count).ToList();
            if (candidate.Count == header.Count && candidate.Zip(header).All(p => Texts(p.First).SequenceEqual(Texts(p.Second))))
            {
                skip = header.Count;
            }
            else if (repeated.Count == 0)
            {
                skip = 0;
            }
            else
            {
                return false;
            }
        }
        else
        {
            if (repeated.Count > 0)
            {
                return false;
            }

            skip = 0;
        }

        if (skip == continuation.Rows.Count)
        {
            return true;
        }

        // A row split by the page break: its rest has empty cells and does not start like a new row.
        var rest = continuation.Rows[skip];
        var last = previous.Rows[^1];
        if (!last.IsHeader && rest.Cells.Count == last.Cells.Count && rest.Cells.Any(c => c.Blocks.Count == 0)
            && (rest.Cells[0].Blocks.Count == 0 || rest.Cells.First(c => c.Blocks.Count > 0).ToPlainText().TrimStart() is { Length: > 0 } t && char.IsLower(t[0])))
        {
            for (var c = 0; c < rest.Cells.Count; c++)
            {
                var target = last.Cells[c];
                var moved = rest.Cells[c].Blocks.ToList();
                if (moved.Count == 0)
                {
                    continue;
                }

                if (target.Blocks.Count > 0 && target.Blocks[^1] is ParagraphBlock tp && moved[0] is ParagraphBlock mp)
                {
                    tp.Inlines.Add(new TextInline(" "));
                    tp.Inlines.AddRange(mp.Inlines);
                    moved.RemoveAt(0);
                }

                target.Blocks.AddRange(moved);
            }

            skip++;
        }

        foreach (var row in continuation.Rows.Skip(skip))
        {
            row.IsHeader = false;
            previous.Rows.Add(row);
        }

        if (previous.Alignments.Count == 0 && continuation.Alignments.Count > 0)
        {
            previous.Alignments.AddRange(continuation.Alignments);
        }

        return true;
    }

    private static bool TryMergeParagraphs(ParagraphBlock previous, ParagraphBlock continuation, LayoutBuilder builder, bool mergeHyphenation)
    {
        if (!builder.ParagraphSources.TryGetValue(previous, out var lastLines) || !builder.ParagraphSources.TryGetValue(continuation, out var firstLines))
        {
            return false;
        }

        var tail = lastLines[^1].Text.TrimEnd();
        var head = firstLines[0].Text.TrimStart();
        var continues = tail.Length > 0 && head.Length > 0
            && Math.Abs(lastLines[^1].Size - firstLines[0].Size) <= 0.5
            && !".!?:”\"’)".Contains(tail[^1], StringComparison.Ordinal)
            && (char.IsLower(head[0]) || tail[^1] is '-' or ',');
        if (!continues)
        {
            return false;
        }

        var merged = lastLines.Concat(firstLines).ToList();
        previous.Inlines.Clear();
        previous.Inlines.AddRange(InlineBuilder.Build(merged.Select(l => (IReadOnlyList<PdfWord>)l.Words), mergeHyphenation));
        builder.ParagraphSources[previous] = merged;
        return true;
    }

    private static void AssignHeadingIds(Document document)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var heading in document.Headings)
        {
            var slug = DocumentHtmlRenderer.Slugify(heading.ToPlainText());
            var unique = slug;
            var n = 2;
            while (!used.Add(unique))
            {
                unique = $"{slug}-{n++}";
            }

            heading.Id = unique;
        }
    }

    private void FillMetadata(DocumentMetadata metadata, PdfDocument pdf, ImportContext context)
    {
        metadata.SourceFormat = Format.Id;
        metadata.SourcePath = context.SourcePath;
        metadata.SourcePageCount = pdf.NumberOfPages;

        var info = pdf.Information;
        if (info is null)
        {
            return;
        }

        metadata.Title = Clean(info.Title);
        metadata.Description = Clean(info.Subject);
        if (Clean(info.Author) is { } author)
        {
            metadata.Authors.AddRange(author.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        if (Clean(info.Keywords) is { } keywords)
        {
            metadata.Keywords.AddRange(keywords.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        try
        {
            metadata.Date = info.GetCreatedDateTimeOffset();
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
        {
            metadata.Date = null;
        }

        if (Clean(info.Creator) is { } creator)
        {
            metadata.Custom["creator"] = creator;
        }

        if (Clean(info.Producer) is { } producer)
        {
            metadata.Custom["producer"] = producer;
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static List<OutlineEntry>? ReadOutline(PdfDocument pdf)
    {
        try
        {
            if (!pdf.TryGetBookmarks(out var bookmarks) || bookmarks.Roots.Count == 0)
            {
                return null;
            }

            var result = new List<OutlineEntry>();
            void Visit(BookmarkNode node)
            {
                if (!string.IsNullOrWhiteSpace(node.Title))
                {
                    int? page = node is DocumentBookmarkNode d && d.PageNumber > 0 ? d.PageNumber : null;
                    result.Add(new OutlineEntry(node.Title.Trim(), node.Level, page));
                }

                foreach (var child in node.Children)
                {
                    Visit(child);
                }
            }

            foreach (var root in bookmarks.Roots)
            {
                Visit(root);
            }

            return result.Count > 0 ? result : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            // A broken outline is not worth failing the import over; fall back to font heuristics.
            return null;
        }
    }
}
