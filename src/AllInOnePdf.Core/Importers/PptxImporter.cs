using System.Globalization;
using System.IO.Packaging;
using System.Xml;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Importers.Pptx;
using DocumentFormat.OpenXml.Packaging;
using P = DocumentFormat.OpenXml.Presentation;

namespace AllInOnePdf.Core.Importers;

/// <summary>
/// PowerPoint (.pptx) → document model with Open XML SDK: one section per slide (title → heading), body text with
/// bullet and numbered lists, text boxes, monospace shapes as code, tables with merged cells, pictures and optional
/// speaker notes. Pure managed code, offline.
/// </summary>
public sealed class PptxImporter : IImporter
{
    public const string OptPageBreaks = "pageBreaks";
    public const string OptIncludeHidden = "includeHidden";
    public const string OptReadingOrder = "readingOrder";
    public const string OptHeadingLevel = "headingLevel";
    public const string OptUntitledSlides = "untitledSlides";
    public const string OptDetectCode = "detectCode";
    public const string OptSpeakerNotes = "speakerNotes";
    public const string OptExtractImages = "extractImages";
    public const string OptMaxImageMb = "maxImageMb";

    private const string GroupStructure = "Structure";
    private const string GroupContent = "Content";
    private const string GroupImages = "Images";

    /// <summary>OLE compound file signature: encrypted .pptx files (and legacy .ppt) start with it.</summary>
    private static readonly byte[] CompoundFileSignature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    public FileFormat Format => KnownFormats.Pptx;

    public ConverterStatus Status => ConverterStatus.Ready;

    public IReadOnlyList<OptionDefinition> Options { get; } =
    [
        OptionDefinition.Choice(OptPageBreaks, "Between slides", "rule",
        [
            new OptionChoice("rule", "Divider", "A horizontal rule (--- in Markdown) separates the slides."),
            new OptionChoice("marker", "Page break", "Each slide starts on a new page."),
            new OptionChoice("none", "Nothing", "Join the slides into one continuous document."),
        ], "What separates one slide from the next.", GroupStructure),
        OptionDefinition.Choice(OptUntitledSlides, "Slides without a title", "number",
        [
            new OptionChoice("number", "Number them", "Add a \"Slide N\" heading."),
            new OptionChoice("none", "No heading", "Keep only the slide's content."),
        ], "What heading a slide without a title gets.", GroupStructure),
        OptionDefinition.Bool(OptIncludeHidden, "Include hidden slides", false, "Also convert slides that are hidden in the slide show.", GroupStructure),
        OptionDefinition.Choice(OptReadingOrder, "Reading order", "position",
        [
            new OptionChoice("position", "By position", "Top to bottom, then left to right, after the title."),
            new OptionChoice("xml", "As stored", "The order the shapes are stored in (the selection pane order, reversed)."),
        ], "The order in which a slide's shapes are read.", GroupStructure, advanced: true),
        OptionDefinition.Int(OptHeadingLevel, "Slide title level", 1, 1, 6, "Slide titles become headings of this level. 1 is the top level; choose 2 to keep level 1 for a title of your own.", GroupStructure, advanced: true),
        OptionDefinition.Bool(OptDetectCode, "Detect code", true, "Shapes set entirely in a monospace font become code blocks; monospace words become inline code.", GroupContent),
        OptionDefinition.Bool(OptSpeakerNotes, "Speaker notes", false, "Add each slide's speaker notes after its content, for handouts or scripts.", GroupContent),
        OptionDefinition.Bool(OptExtractImages, "Extract images", true, "Keep the pictures placed on the slides.", GroupImages),
        OptionDefinition.Int(OptMaxImageMb, "Largest image", 25, 1, 1024, "Pictures larger than this are left out.", GroupImages, unit: "MB", advanced: true, visibleWhen: OptExtractImages),
    ];

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

        var settings = PptxImportSettings.From(context.Options.WithDefaults(Options));
        return await Task.Run(() => Import(bytes, context, settings, cancellationToken), cancellationToken);
    }

    private Document Import(byte[] bytes, ImportContext context, PptxImportSettings settings, CancellationToken ct)
    {
        if (bytes.Length >= CompoundFileSignature.Length && bytes.AsSpan(0, CompoundFileSignature.Length).SequenceEqual(CompoundFileSignature))
        {
            throw new InvalidDataException(
                "The presentation is password-protected (or is an old .ppt file). Remove the password in PowerPoint (File › Info › Protect Presentation), or save it as .pptx, and try again.");
        }

        if (bytes.Length < 4 || bytes[0] != 'P' || bytes[1] != 'K')
        {
            throw new InvalidDataException("The file is not a PowerPoint presentation (.pptx); it may be damaged or have the wrong extension.");
        }

        context.Report(0, "Opening presentation");
        PresentationDocument package;
        var stream = new MemoryStream(bytes, writable: false);
        try
        {
            package = PresentationDocument.Open(stream, false, new OpenSettings { AutoSave = false });
        }
        catch (Exception ex) when (ex is OpenXmlPackageException or FileFormatException or InvalidDataException or XmlException or IOException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            stream.Dispose();
            throw new InvalidDataException($"The file could not be read as a PowerPoint presentation; it may be damaged ({ex.Message}).", ex);
        }

        using (stream)
        using (package)
        {
            var document = new Document();
            document.Metadata.SourceFormat = Format.Id;
            document.Metadata.SourcePath = context.SourcePath;
            string? firstTitle;
            try
            {
                firstTitle = ReadSlides(package, document, settings, context, ct);
            }
            catch (Exception ex) when (ex is XmlException or OpenXmlPackageException or FileFormatException or InvalidOperationException or InvalidDataException)
            {
                throw new InvalidDataException($"The file is not a valid PowerPoint presentation, or it is damaged ({ex.Message}).", ex);
            }

            FillMetadata(document.Metadata, package);
            document.Metadata.Title ??= firstTitle ?? Clean(Path.GetFileNameWithoutExtension(context.FileName));
            context.Report(1, "Done");
            return document;
        }
    }

    private string? ReadSlides(PresentationDocument package, Document document, PptxImportSettings settings, ImportContext context, CancellationToken ct)
    {
        var presentationPart = package.PresentationPart
            ?? throw new XmlException("the package has no presentation part");
        var presentation = presentationPart.Presentation;
        var slideIds = presentation?.SlideIdList?.Elements<P.SlideId>().ToList() ?? [];
        document.Metadata.SourcePageCount = slideIds.Count;

        var reader = new PptxSlideReader(document, settings, presentation?.SlideSize?.Cx?.Value ?? 12192000, context.Warnings.Add);
        string? firstTitle = null;
        string? previousTitle = null;
        var emitted = 0;
        var hidden = 0;
        for (var i = 0; i < slideIds.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var number = i + 1;
            context.Report((double)i / slideIds.Count, string.Create(CultureInfo.InvariantCulture, $"Slide {number} of {slideIds.Count}"));

            var relId = slideIds[i].RelationshipId?.Value;
            if (string.IsNullOrEmpty(relId))
            {
                continue;
            }

            SlidePart slidePart;
            try
            {
                if (presentationPart.GetPartById(relId) is not SlidePart sp)
                {
                    continue;
                }

                slidePart = sp;
            }
            catch (ArgumentOutOfRangeException)
            {
                continue;
            }

            if (slidePart.Slide?.Show?.Value == false && !settings.IncludeHidden)
            {
                hidden++;
                continue;
            }

            var (blocks, title) = reader.ReadSlide(slidePart, number);
            if (blocks.Count == 0)
            {
                continue;
            }

            // "Title (cont.)": an overflow slide (our exporter makes them) continues the slide before it.
            if (emitted > 0 && title is not null && previousTitle is not null && ContinuationOf(title) is { } baseTitle
                && string.Equals(baseTitle, ContinuationOf(previousTitle) ?? previousTitle, StringComparison.Ordinal)
                && blocks.Count > 0 && blocks[0] is HeadingBlock)
            {
                blocks.RemoveAt(0);
                AppendContinuation(document.Blocks, blocks);
                continue;
            }

            if (emitted > 0)
            {
                switch (settings.Separator)
                {
                    case PptxSlideSeparator.PageBreak:
                        document.Blocks.Add(new PageBreakBlock { SourcePage = number });
                        break;
                    case PptxSlideSeparator.Rule:
                        document.Blocks.Add(new ThematicBreakBlock { SourcePage = number });
                        break;
                }
            }

            document.Blocks.AddRange(blocks);
            emitted++;
            previousTitle = title;
            if (emitted == 1)
            {
                firstTitle = title;
            }
        }

        reader.Finish();
        if (hidden > 0)
        {
            context.Warnings.Add(hidden == 1
                ? "1 hidden slide was left out; turn on \"Include hidden slides\" to keep it."
                : string.Create(CultureInfo.InvariantCulture, $"{hidden} hidden slides were left out; turn on \"Include hidden slides\" to keep them."));
        }

        return firstTitle;
    }

    private const string ContinuedSuffix = " (cont.)";

    /// <summary>"Title (cont.)" → "Title"; null for any other title.</summary>
    internal static string? ContinuationOf(string title) =>
        title.EndsWith(ContinuedSuffix, StringComparison.Ordinal) && title.Length > ContinuedSuffix.Length
            ? title[..^ContinuedSuffix.Length].TrimEnd()
            : null;

    /// <summary>
    /// Appends a continuation slide's blocks to the document. A list split between the slides (the next items, or
    /// the rest of a nested list, at the top of the continuation) is joined back to the list it continues.
    /// </summary>
    internal static void AppendContinuation(List<Block> output, List<Block> blocks)
    {
        if (blocks.Count > 0 && blocks[0] is ListBlock next && output.Count > 0 && output[^1] is ListBlock previous)
        {
            // The deepest open list (the last item's nested list, and so on) that the continuation's numbering fits.
            var chain = new List<ListBlock> { previous };
            while (chain[^1].Items.Count > 0 && chain[^1].Items[^1].Blocks.LastOrDefault() is ListBlock nested)
            {
                chain.Add(nested);
            }

            var target = chain.LastOrDefault(l => l.Ordered == next.Ordered && (!l.Ordered || next.Start == l.Start + l.Items.Count))
                ?? chain.LastOrDefault(l => l.Ordered == next.Ordered);
            if (target is not null)
            {
                target.Items.AddRange(next.Items);
                blocks.RemoveAt(0);
            }
        }

        output.AddRange(blocks);
    }

    private static void FillMetadata(DocumentMetadata metadata, PresentationDocument package)
    {
        try
        {
            FillMetadataCore(metadata, package);
        }
        catch (Exception ex) when (ex is XmlException or OpenXmlPackageException or InvalidOperationException or FormatException)
        {
            // Broken core properties are not worth failing the import over.
        }
    }

    private static void FillMetadataCore(DocumentMetadata metadata, PresentationDocument package)
    {
        var props = package.PackageProperties;
        metadata.Title ??= Clean(props.Title);
        metadata.Subtitle ??= Clean(props.Subject);
        metadata.Description ??= Clean(props.Description);
        metadata.Language ??= Clean(props.Language);
        if (metadata.Authors.Count == 0 && Clean(props.Creator) is { } creator)
        {
            metadata.Authors.AddRange(creator.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        if (metadata.Keywords.Count == 0 && Clean(props.Keywords) is { } keywords)
        {
            metadata.Keywords.AddRange(keywords.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        if (props.Created is { } created)
        {
            var utc = created.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(created, DateTimeKind.Utc) : created.ToUniversalTime();
            metadata.Date = new DateTimeOffset(utc).ToLocalTime();
        }

        if (Clean(props.LastModifiedBy) is { } modifiedBy)
        {
            metadata.Custom["lastModifiedBy"] = modifiedBy;
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
