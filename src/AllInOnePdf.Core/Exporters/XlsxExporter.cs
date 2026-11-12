using System.Globalization;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Exporters.Xlsx;
using AllInOnePdf.Core.Importers.Xlsx;
using AllInOnePdf.Core.Themes;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Document = AllInOnePdf.Core.Documents.Document;

namespace AllInOnePdf.Core.Exporters;

/// <summary>
/// Document model → Excel (.xlsx) with Open XML SDK. Every table becomes a worksheet named after its caption or the
/// heading above it, with theme-styled header rows and borders, typed numbers/booleans/dates, merged spans, auto-fit
/// column widths, a frozen header and an optional auto filter. Documents without tables become one "Document" sheet
/// with a row per paragraph.
/// </summary>
public sealed class XlsxExporter : IExporter
{
    public const string OptTheme = PdfExporter.OptTheme;
    public const string OptFreezeHeader = "freezeHeader";
    public const string OptAutoFilter = "autoFilter";
    public const string OptIncludeHeadings = "includeHeadings";
    public const string OptNumbersAsValues = "numbersAsValues";
    public const string OptMetadata = PdfExporter.OptMetadata;

    private const string FilterDatabaseName = "_xlnm._FilterDatabase";

    public FileFormat Format => KnownFormats.Xlsx;

    public ConverterStatus Status => ConverterStatus.Ready;

    public bool SupportsTheme => true;

    public IReadOnlyList<OptionDefinition> Options { get; } =
    [
        OptionDefinition.Theme(OptTheme, "Theme", BuiltInThemes.DefaultId, "Fonts, header colours and table borders come from the theme.", "Appearance"),
        OptionDefinition.Bool(OptIncludeHeadings, "Title rows from headings", true, "Write the heading above each table as a bold title row at the top of its sheet.", "Sheets"),
        OptionDefinition.Bool(OptFreezeHeader, "Freeze header row", true, "Keep the header row in view while scrolling.", "Sheets"),
        OptionDefinition.Bool(OptAutoFilter, "Filter buttons", false, "Add filter and sort buttons to the header row.", "Sheets"),
        OptionDefinition.Bool(OptNumbersAsValues, "Numbers as values", true, "Store numbers, percentages, TRUE/FALSE and ISO dates (yyyy-mm-dd) as values Excel can calculate with. Turn off to keep every cell as text.", "Cells"),
        OptionDefinition.Bool(OptMetadata, "Embed metadata", true, "Write title, author, subject and keywords into the workbook properties.", "Metadata", advanced: true),
    ];

    public Task<ExportResult> ExportAsync(ExportContext context, CancellationToken cancellationToken = default) =>
        Task.Run(() => Export(context, cancellationToken), cancellationToken);

    private ExportResult Export(ExportContext context, CancellationToken ct)
    {
        var theme = context.Theme ?? BuiltInThemes.Clean();
        var doc = context.Document;
        var options = context.Options.WithDefaults(Options);
        var settings = new XlsxExportSettings(
            options.GetBool(OptFreezeHeader, true),
            options.GetBool(OptAutoFilter, false),
            options.GetBool(OptIncludeHeadings, true),
            options.GetBool(OptNumbersAsValues, true),
            options.GetBool(OptMetadata, true));

        var tables = CollectTables(doc);
        var styles = new XlsxThemeStyles(theme);
        var writer = new XlsxSheetWriter(styles, settings);
        var names = new XlsxSheetNames();
        var title = doc.ResolveTitle() ?? context.BaseName;

        context.Report(0, "Building workbook");
        using var stream = new MemoryStream();
        using (var package = SpreadsheetDocument.Create(stream, DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = package.AddWorkbookPart();
            var sheets = new Sheets();
            var definedNames = new DefinedNames();

            if (tables.Count == 0)
            {
                var part = workbookPart.AddNewPart<WorksheetPart>();
                writer.WriteProse(part, doc, selected: true, ct);
                sheets.Append(new Sheet { Name = names.Unique("Document", "Document"), SheetId = 1, Id = workbookPart.GetIdOfPart(part) });
            }

            for (var i = 0; i < tables.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (table, heading, titleRow) = tables[i];
                var name = names.Unique(table.Caption ?? heading, string.Create(CultureInfo.InvariantCulture, $"Table {i + 1}"));
                context.Report(0.05 + 0.85 * i / tables.Count, string.Create(CultureInfo.InvariantCulture, $"Sheet {i + 1} of {tables.Count}: {name}"));

                var part = workbookPart.AddNewPart<WorksheetPart>();
                var filter = writer.WriteTable(part, table, titleRow, selected: i == 0);
                sheets.Append(new Sheet { Name = name, SheetId = (uint)(i + 1), Id = workbookPart.GetIdOfPart(part) });
                if (filter is { } f)
                {
                    definedNames.Append(new DefinedName
                    {
                        Name = FilterDatabaseName,
                        LocalSheetId = (uint)i,
                        Hidden = true,
                        Text = "'" + name.Replace("'", "''", StringComparison.Ordinal) + "'!" + XlsxCellReference.AbsoluteRange(f.Row1, f.Column1, f.Row2, f.Column2),
                    });
                }
            }

            var stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
            stylesPart.Stylesheet = styles.Book.Build();

            var workbook = new Workbook();
            workbook.Append(new BookViews(new WorkbookView { ActiveTab = 0 }));
            workbook.Append(sheets);
            if (definedNames.HasChildren)
            {
                workbook.Append(definedNames);
            }

            workbookPart.Workbook = workbook;

            if (settings.Metadata)
            {
                WriteMetadata(package, doc, title);
            }

            context.Report(0.95, "Saving workbook");
        }

        var result = ExportResult.Single(context.BaseName + Format.PrimaryExtension, stream.ToArray(), Format.MediaType);
        context.Report(1, "Workbook written");
        return result;
    }

    /// <summary>
    /// Tables in document order (nested ones included) with the nearest heading before each, and the heading to write
    /// as a title row: only the first table after a heading gets it.
    /// </summary>
    private static List<(TableBlock Table, string? Heading, string? TitleRow)> CollectTables(Document doc)
    {
        var result = new List<(TableBlock, string?, string?)>();
        string? heading = null;
        var headingUsed = false;
        foreach (var block in doc.EnumerateAllBlocks())
        {
            switch (block)
            {
                case HeadingBlock h:
                    var text = h.ToPlainText().Trim();
                    if (text.Length > 0)
                    {
                        heading = text;
                        headingUsed = false;
                    }

                    break;
                case TableBlock t when t.Rows.Count > 0:
                    result.Add((t, heading, headingUsed ? null : heading));
                    headingUsed = true;
                    break;
            }
        }

        return result;
    }

    private static void WriteMetadata(SpreadsheetDocument package, Document doc, string title)
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

        if (m.Date is { } date)
        {
            props.Created = date.UtcDateTime;
        }

        props.Modified = DateTime.UtcNow;
    }
}
