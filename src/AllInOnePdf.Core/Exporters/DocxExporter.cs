using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Exporters.Docx;
using AllInOnePdf.Core.Rendering;
using AllInOnePdf.Core.Themes;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using WDocument = DocumentFormat.OpenXml.Wordprocessing.Document;

namespace AllInOnePdf.Core.Exporters;

/// <summary>
/// Document model → Word (.docx) with Open XML SDK. Styles, numbering, page setup, header/footer, cover page and
/// table of contents come from the <see cref="PdfTheme"/>, so a Word file looks like its PDF sibling and stays
/// editable: every element is a named Word style rather than direct formatting.
/// </summary>
public sealed class DocxExporter : IExporter
{
    public const string OptTheme = PdfExporter.OptTheme;
    public const string OptCover = PdfExporter.OptCover;
    public const string OptToc = PdfExporter.OptToc;
    public const string OptTitle = PdfExporter.OptTitle;
    public const string OptAuthor = PdfExporter.OptAuthor;
    public const string OptMetadata = PdfExporter.OptMetadata;

    private static readonly IReadOnlyList<OptionChoice> ThemeOverride =
    [
        new("theme", "Use theme setting"),
        new("yes", "Always"),
        new("no", "Never"),
    ];

    private readonly ISyntaxHighlighter? _highlighter;

    public DocxExporter()
        : this(null)
    {
    }

    public DocxExporter(ISyntaxHighlighter? highlighter)
    {
        _highlighter = highlighter;
    }

    public FileFormat Format => KnownFormats.Docx;

    public ConverterStatus Status => ConverterStatus.Ready;

    public bool SupportsTheme => true;

    public IReadOnlyList<OptionDefinition> Options { get; } =
    [
        OptionDefinition.Theme(OptTheme, "Theme", BuiltInThemes.DefaultId, "Fonts, colours, spacing and page setup become Word styles you can keep editing.", "Appearance"),
        OptionDefinition.Choice(OptCover, "Cover page", "theme", ThemeOverride, "Add a title page before the content.", "Structure"),
        OptionDefinition.Choice(OptToc, "Table of contents", "theme", ThemeOverride, "Add a contents field built from the headings; Word fills it in when the file is opened.", "Structure"),
        OptionDefinition.Text(OptTitle, "Title override", null, "Replaces the document title on the cover and in the file properties.", "Metadata"),
        OptionDefinition.Text(OptAuthor, "Author override", null, "Replaces the author(s) from the source.", "Metadata"),
        OptionDefinition.Bool(OptMetadata, "Embed metadata", true, "Write title, author, subject and keywords into the document properties.", "Metadata", advanced: true),
    ];

    public Task<ExportResult> ExportAsync(ExportContext context, CancellationToken cancellationToken = default) =>
        Task.Run(() => Export(context, cancellationToken), cancellationToken);

    private ExportResult Export(ExportContext context, CancellationToken ct)
    {
        var theme = context.Theme ?? BuiltInThemes.Clean();
        var doc = context.Document;
        var options = context.Options.WithDefaults(Options);

        var title = options.GetString(OptTitle);
        if (!string.IsNullOrWhiteSpace(title))
        {
            doc.Metadata.Title = title.Trim();
        }

        var author = options.GetString(OptAuthor);
        if (!string.IsNullOrWhiteSpace(author))
        {
            doc.Metadata.Authors.Clear();
            doc.Metadata.Authors.AddRange(author.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        var includeCover = Tri(options.GetString(OptCover)) ?? theme.Cover.Enabled;
        var includeToc = (Tri(options.GetString(OptToc)) ?? theme.Toc.Enabled) && doc.Headings.Any();
        var resolvedTitle = theme.Cover.TitleOverride is { Length: > 0 } o ? o : doc.ResolveTitle() ?? context.BaseName;

        var result = new ExportResult();
        context.Report(0, "Building Word styles");
        using var stream = new MemoryStream();
        using (var package = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = package.AddMainDocumentPart();
            var numbering = new DocxNumbering(theme);

            var stylesPart = main.AddNewPart<StyleDefinitionsPart>();
            stylesPart.Styles = DocxStyles.Build(theme, numbering.HeadingNumId, doc.Metadata.Language);
            main.AddNewPart<FontTablePart>().Fonts = DocxPageSetup.FontTable(theme);

            var (widthPt, heightPt) = DocxPageSetup.ContentSize(theme);
            var writer = new DocxWriter(doc, theme, numbering, _highlighter, result.Warnings, widthPt, heightPt);
            var body = new Body();

            if (includeCover)
            {
                body.AppendChild(DocxPageSetup.Cover(writer, theme, doc, resolvedTitle));
            }

            if (includeToc)
            {
                body.AppendChild(DocxPageSetup.TableOfContents(theme));
            }

            var ctx = writer.Root(main);
            for (var i = 0; i < doc.Blocks.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var elements = new List<OpenXmlElement>();
                writer.WriteBlock(doc.Blocks[i], ctx, elements);
                body.Append(elements);
                if (i % 50 == 0)
                {
                    context.Report(0.1 + 0.75 * i / Math.Max(1, doc.Blocks.Count), "Writing content");
                }
            }

            if (doc.Footnotes.Count > 0)
            {
                var footnotesPart = main.AddNewPart<FootnotesPart>();
                footnotesPart.Footnotes = writer.BuildFootnotes(footnotesPart);
            }

            main.AddNewPart<NumberingDefinitionsPart>().Numbering = numbering.Build();
            main.AddNewPart<DocumentSettingsPart>().Settings = DocxPageSetup.Settings(theme, updateFields: includeToc);

            body.AppendChild(DocxPageSetup.Section(main, theme, doc, context.BaseName, includeCover));

            var wdoc = new WDocument();
            if (DocxUnits.Hex(theme.Page.Background) is { } bg && bg != "FFFFFF")
            {
                wdoc.AppendChild(new DocumentBackground { Color = bg });
            }

            wdoc.AppendChild(body);
            main.Document = wdoc;

            if (options.GetBool(OptMetadata, true))
            {
                WriteMetadata(package, doc, resolvedTitle);
            }

            context.Report(0.95, "Saving Word document");
        }

        result.Files.Insert(0, new OutputFile(context.BaseName + Format.PrimaryExtension, stream.ToArray(), Format.MediaType));
        context.Report(1, "Word document written");
        return result;
    }

    private static void WriteMetadata(WordprocessingDocument package, Documents.Document doc, string title)
    {
        var m = doc.Metadata;
        var props = package.PackageProperties;
        props.Title = title;
        if (!string.IsNullOrWhiteSpace(m.Subtitle))
        {
            props.Subject = m.Subtitle;
        }

        if (m.Authors.Count > 0)
        {
            props.Creator = string.Join("; ", m.Authors);
        }

        if (m.Keywords.Count > 0)
        {
            props.Keywords = string.Join(", ", m.Keywords);
        }

        if (!string.IsNullOrWhiteSpace(m.Description))
        {
            props.Description = m.Description;
        }

        if (!string.IsNullOrWhiteSpace(m.Language))
        {
            props.Language = m.Language;
        }

        // "Created" carries the source's date (so it round-trips); a document without one gets only "Modified".
        if (m.Date is { } date)
        {
            props.Created = date.UtcDateTime;
        }

        props.Modified = DateTime.UtcNow;
    }

    private static bool? Tri(string? value) => value?.ToLowerInvariant() switch
    {
        "yes" or "true" => true,
        "no" or "false" => false,
        _ => null,
    };
}
