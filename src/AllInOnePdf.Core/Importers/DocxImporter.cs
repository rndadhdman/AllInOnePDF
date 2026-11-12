using System.IO.Packaging;
using System.Xml;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Importers.Docx;
using DocumentFormat.OpenXml.Packaging;

namespace AllInOnePdf.Core.Importers;

/// <summary>
/// Word (.docx) → document model with Open XML SDK: headings by style or outline level, title/subtitle, quotes,
/// code (code styles or monospace runs), callouts, lists from the numbering definitions, tables with merged cells,
/// images, hyperlinks, footnotes/endnotes and page breaks. Pure managed code, offline.
/// </summary>
public sealed class DocxImporter : IImporter
{
    public const string OptDetectCode = "detectCode";
    public const string OptPageBreaks = "pageBreaks";
    public const string OptExtractImages = "extractImages";
    public const string OptIncludeHeadersFooters = "includeHeadersFooters";
    public const string OptFootnotes = "footnotes";
    public const string OptSkipToc = "skipToc";
    public const string OptMaxImageMb = "maxImageMb";

    private const string GroupStructure = "Structure";
    private const string GroupContent = "Content";
    private const string GroupImages = "Images";

    /// <summary>OLE compound file signature: encrypted .docx files (and legacy .doc) start with it.</summary>
    private static readonly byte[] CompoundFileSignature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    public FileFormat Format => KnownFormats.Docx;

    public ConverterStatus Status => ConverterStatus.Ready;

    public IReadOnlyList<OptionDefinition> Options { get; } =
    [
        OptionDefinition.Bool(OptDetectCode, "Detect code", true, "Paragraphs in a code style or set entirely in a monospace font become code blocks; monospace words become inline code.", GroupStructure),
        OptionDefinition.Choice(OptPageBreaks, "Page breaks", "marker",
        [
            new OptionChoice("marker", "Keep", "Manual page breaks and \"page break before\" become page breaks."),
            new OptionChoice("none", "Ignore", "Join everything into one continuous document."),
        ], "Whether manual page breaks in the Word file are kept.", GroupStructure),
        OptionDefinition.Bool(OptSkipToc, "Skip table of contents", true, "Leave out Word's table of contents field; it can be rebuilt from the headings.", GroupStructure),
        OptionDefinition.Bool(OptFootnotes, "Footnotes", true, "Keep footnotes and endnotes with their references. Off leaves them out.", GroupContent),
        OptionDefinition.Bool(OptIncludeHeadersFooters, "Include headers and footers", false, "Add the page header text at the start and the footer text at the end.", GroupContent),
        OptionDefinition.Bool(OptExtractImages, "Extract images", true, "Keep the pictures embedded in the document.", GroupImages),
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

        var settings = DocxImportSettings.From(context.Options.WithDefaults(Options));
        return await Task.Run(() => Import(bytes, context, settings, cancellationToken), cancellationToken);
    }

    private Document Import(byte[] bytes, ImportContext context, DocxImportSettings settings, CancellationToken ct)
    {
        if (bytes.Length >= CompoundFileSignature.Length && bytes.AsSpan(0, CompoundFileSignature.Length).SequenceEqual(CompoundFileSignature))
        {
            throw new InvalidDataException(
                "The Word document is password-protected (or is an old .doc file). Remove the password in Word (File › Info › Protect Document), or save it as .docx, and try again.");
        }

        if (bytes.Length < 4 || bytes[0] != 'P' || bytes[1] != 'K')
        {
            throw new InvalidDataException("The file is not a Word document (.docx); it may be damaged or have the wrong extension.");
        }

        context.Report(0, "Opening Word document");
        WordprocessingDocument package;
        var stream = new MemoryStream(bytes, writable: false);
        try
        {
            package = WordprocessingDocument.Open(stream, false, new OpenSettings { AutoSave = false });
        }
        catch (Exception ex) when (ex is OpenXmlPackageException or FileFormatException or InvalidDataException or XmlException or IOException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            stream.Dispose();
            throw new InvalidDataException($"The file could not be read as a Word document; it may be damaged ({ex.Message}).", ex);
        }

        using (stream)
        using (package)
        {
            var document = new Document();
            document.Metadata.SourceFormat = Format.Id;
            document.Metadata.SourcePath = context.SourcePath;
            try
            {
                new DocxBodyReader(package, document, settings, context, ct).Read();
            }
            catch (Exception ex) when (ex is XmlException or OpenXmlPackageException or FileFormatException)
            {
                throw new InvalidDataException($"The Word document is damaged and could not be read ({ex.Message}).", ex);
            }

            FillMetadata(document.Metadata, package);
            context.Report(1, "Done");
            return document;
        }
    }

    private static void FillMetadata(DocumentMetadata metadata, WordprocessingDocument package)
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

    private static void FillMetadataCore(DocumentMetadata metadata, WordprocessingDocument package)
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
