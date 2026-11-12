using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Importers.Data;
using AllInOnePdf.Core.Importers.Rtf;

namespace AllInOnePdf.Core.Importers;

/// <summary>
/// Rich Text Format (.rtf) → document model with a hand-written byte tokenizer: headings from outline levels,
/// heading styles or (optionally) bold/large paragraphs, Word and WordPad lists, tables with merged cells, PNG/JPEG
/// pictures, hyperlink fields, footnotes, code from monospace fonts, page breaks and the info block. Text is decoded
/// per font character set, including double-byte East Asian code pages. Pure managed code, offline.
/// </summary>
public sealed class RtfImporter : IImporter
{
    public const string OptDetectHeadings = "detectHeadings";
    public const string OptDetectLists = "detectLists";
    public const string OptDetectCode = "detectCode";
    public const string OptPageBreaks = "pageBreaks";
    public const string OptFootnotes = "footnotes";
    public const string OptExtractImages = "extractImages";
    public const string OptMaxImageMb = "maxImageMb";

    private const string GroupStructure = "Structure";
    private const string GroupContent = "Content";
    private const string GroupImages = "Images";

    private const long MaxBytes = 256L * 1024 * 1024;

    public FileFormat Format => KnownFormats.Rtf;

    public ConverterStatus Status => ConverterStatus.Ready;

    public IReadOnlyList<OptionDefinition> Options { get; } =
    [
        OptionDefinition.Bool(OptDetectHeadings, "Detect headings", true, "Without heading styles, short bold or large paragraphs become headings.", GroupStructure),
        OptionDefinition.Bool(OptDetectLists, "Detect typed lists", true, "Paragraphs that start with a typed bullet or number and a hanging indent become list items.", GroupStructure),
        OptionDefinition.Bool(OptDetectCode, "Detect code", true, "Paragraphs set entirely in a monospace font become code blocks; monospace words become inline code.", GroupStructure),
        OptionDefinition.Choice(OptPageBreaks, "Page breaks", "marker",
        [
            new OptionChoice("marker", "Keep", "Manual page breaks and \"page break before\" become page breaks."),
            new OptionChoice("none", "Ignore", "Join everything into one continuous document."),
        ], "Whether manual page breaks in the RTF file are kept.", GroupStructure),
        OptionDefinition.Bool(OptFootnotes, "Footnotes", true, "Keep footnotes and endnotes with their references. Off leaves them out.", GroupContent),
        OptionDefinition.Bool(OptExtractImages, "Extract images", true, "Keep the PNG and JPEG pictures embedded in the document.", GroupImages),
        OptionDefinition.Int(OptMaxImageMb, "Largest image", 25, 1, 1024, "Pictures larger than this are left out.", GroupImages, unit: "MB", advanced: true, visibleWhen: OptExtractImages),
    ];

    public async Task<Document> ImportAsync(ImportContext context, CancellationToken cancellationToken = default)
    {
        var bytes = await TextDecoding.ReadAllBytesAsync(context.Input, MaxBytes,
            "The RTF file is larger than 256 MB, the largest size that can be converted.", cancellationToken);
        var settings = RtfImportSettings.From(context.Options.WithDefaults(Options));
        return await Task.Run(() => Import(bytes, context, settings, cancellationToken), cancellationToken);
    }

    private Document Import(byte[] bytes, ImportContext context, RtfImportSettings settings, CancellationToken ct)
    {
        var start = FindStart(bytes)
            ?? throw new InvalidDataException("The file is not an RTF document; it may be damaged or have the wrong extension.");

        context.Report(0, "Reading RTF");
        var document = new Document();
        document.Metadata.SourceFormat = Format.Id;
        document.Metadata.SourcePath = context.SourcePath;
        new RtfDocumentBuilder(bytes, start, document, settings, context, ct).Build();
        context.Report(1, "Done");
        return document;
    }

    /// <summary>Offset of the opening <c>{\rtf</c>, after an optional byte-order mark and white space.</summary>
    internal static int? FindStart(byte[] bytes)
    {
        var i = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            i = 3;
        }

        while (i < bytes.Length && bytes[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            i++;
        }

        ReadOnlySpan<byte> magic = "{\\rtf"u8;
        return bytes.Length - i >= magic.Length && bytes.AsSpan(i, magic.Length).SequenceEqual(magic) ? i : null;
    }
}
