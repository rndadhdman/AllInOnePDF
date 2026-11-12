using System.Globalization;
using System.IO.Packaging;
using System.Xml;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Importers.Xlsx;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace AllInOnePdf.Core.Importers;

/// <summary>
/// Excel (.xlsx) → one heading and one table per worksheet, cells as Excel displays them (shared and rich strings,
/// number and date formats, booleans, errors, cached formula results), merged cells as spans, header row detection,
/// hidden rows/columns/sheets skipped, hyperlinks kept. Worksheets are streamed row by row with
/// <see cref="DocumentFormat.OpenXml.OpenXmlReader"/>. Pure managed code, offline.
/// </summary>
public sealed class XlsxImporter : IImporter
{
    public const string OptSheets = "sheets";
    public const string OptIncludeHidden = "includeHidden";
    public const string OptSheetHeadings = "sheetHeadings";
    public const string OptPageBreaks = "pageBreaks";
    public const string OptHeaderRow = "headerRow";
    public const string OptFormulasAsValues = "formulasAsValues";
    public const string OptTrimEmpty = "trimEmpty";
    public const string OptMaxRows = "maxRows";

    private const string GroupSheets = "Sheets";
    private const string GroupTable = "Table";
    private const string GroupLimits = "Limits";

    /// <summary>OLE compound file signature: encrypted .xlsx files (and legacy .xls) start with it.</summary>
    private static readonly byte[] CompoundFileSignature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    public FileFormat Format => KnownFormats.Xlsx;

    public ConverterStatus Status => ConverterStatus.Ready;

    public IReadOnlyList<OptionDefinition> Options { get; } =
    [
        OptionDefinition.Text(OptSheets, "Sheets", null, "Sheet names or numbers to import, separated by commas (for example \"Sales, 3\"). Leave empty for every visible sheet. Sheets named here are imported even when hidden.", GroupSheets),
        OptionDefinition.Bool(OptIncludeHidden, "Include hidden content", false, "Also import hidden sheets, rows and columns.", GroupSheets),
        OptionDefinition.Choice(OptSheetHeadings, "Sheet headings", "auto",
        [
            new OptionChoice("auto", "When useful", "A heading with the sheet name above each table when more than one sheet is imported, or when the sheet starts with a merged title row."),
            new OptionChoice("always", "Always", "A heading above every table, even for a single sheet."),
            new OptionChoice("never", "Never", "Tables only; the sheet name stays as the table caption."),
        ], "Whether each sheet starts with a heading.", GroupSheets),
        OptionDefinition.Bool(OptPageBreaks, "Page break between sheets", true, "Start every sheet on a new page.", GroupSheets),
        OptionDefinition.Choice(OptHeaderRow, "Header row", "auto",
        [
            new OptionChoice("auto", "Detect", "The first row is a header when it is all bold or filled, or when it is text above numeric columns."),
            new OptionChoice("yes", "Yes", "The first row of every sheet holds the column names."),
            new OptionChoice("no", "No", "Every row is data."),
        ], "Whether the first row is shown as the table header.", GroupTable),
        OptionDefinition.Bool(OptFormulasAsValues, "Formulas as values", true, "Show the last calculated result of each formula. Turn off to show the formulas themselves (for example \"=SUM(B2:B9)\").", GroupTable),
        OptionDefinition.Bool(OptTrimEmpty, "Trim empty rows and columns", true, "Leave out empty rows and columns around the data and trim spaces in cells. Turn off to keep the sheet's full used range.", GroupTable),
        OptionDefinition.Int(OptMaxRows, "Maximum rows", 5000, 1, 1_000_000, "Rows after this many on each sheet are left out, with a warning. Very large tables make very long PDFs.", GroupLimits, unit: "rows"),
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

        var settings = XlsxImportSettings.From(context.Options.WithDefaults(Options));
        return await Task.Run(() => Import(bytes, context, settings, cancellationToken), cancellationToken);
    }

    private Document Import(byte[] bytes, ImportContext context, XlsxImportSettings settings, CancellationToken ct)
    {
        if (bytes.Length >= CompoundFileSignature.Length && bytes.AsSpan(0, CompoundFileSignature.Length).SequenceEqual(CompoundFileSignature))
        {
            throw new InvalidDataException(
                "The workbook is password-protected (or is an old .xls file). Remove the password in Excel (File › Info › Protect Workbook), or save it as .xlsx, and try again.");
        }

        if (bytes.Length < 4 || bytes[0] != 'P' || bytes[1] != 'K')
        {
            throw new InvalidDataException("The file is not an Excel workbook (.xlsx); it may be damaged or have the wrong extension.");
        }

        context.Report(0, "Opening workbook");
        SpreadsheetDocument package;
        var stream = new MemoryStream(bytes, writable: false);
        try
        {
            package = SpreadsheetDocument.Open(stream, false, new OpenSettings { AutoSave = false });
        }
        catch (Exception ex) when (ex is OpenXmlPackageException or FileFormatException or InvalidDataException or XmlException or IOException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            stream.Dispose();
            throw new InvalidDataException($"The file could not be read as an Excel workbook; it may be damaged ({ex.Message}).", ex);
        }

        using (stream)
        using (package)
        {
            var document = new Document();
            document.Metadata.SourceFormat = Format.Id;
            document.Metadata.SourcePath = context.SourcePath;
            try
            {
                ReadWorkbook(package, document, settings, context, ct);
            }
            catch (Exception ex) when (ex is XmlException or OpenXmlPackageException or FileFormatException or FormatException)
            {
                throw new InvalidDataException($"The workbook is damaged and could not be read ({ex.Message}).", ex);
            }

            FillMetadata(document.Metadata, package);
            document.Metadata.Title ??= Path.GetFileNameWithoutExtension(context.FileName);
            context.Report(1, "Done");
            return document;
        }
    }

    private static void ReadWorkbook(SpreadsheetDocument package, Document document, XlsxImportSettings settings, ImportContext context, CancellationToken ct)
    {
        var workbookPart = package.WorkbookPart
            ?? throw new InvalidDataException("The file has no workbook part; it is not an Excel workbook.");
        var info = new XlsxWorkbookInfo(workbookPart);
        var sheets = workbookPart.Workbook?.Sheets?.Elements<Sheet>().ToList() ?? [];
        var selected = SelectSheets(sheets, settings, document.ImportWarnings);
        var reader = new XlsxSheetReader(info, settings);

        var tables = new List<(string Name, XlsxSheetTable Table)>();
        for (var i = 0; i < selected.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var sheet = selected[i];
            var name = sheet.Name?.Value ?? string.Create(CultureInfo.InvariantCulture, $"Sheet {i + 1}");
            context.Report(0.05 + 0.9 * i / selected.Count, string.Create(CultureInfo.InvariantCulture, $"Sheet {i + 1} of {selected.Count}: {name}"));

            var part = sheet.Id?.Value is { } relId && workbookPart.TryGetPartById(relId, out var p) ? p : null;
            if (part is not WorksheetPart worksheet)
            {
                document.ImportWarnings.Add(part is null
                    ? $"Sheet \"{name}\" is missing from the file and was skipped."
                    : $"Sheet \"{name}\" is a chart or dialog sheet and was skipped.");
                continue;
            }

            var data = reader.Read(worksheet, ct);
            var table = XlsxTableBuilder.Build(data, settings, name, document.ImportWarnings);
            if (table is null)
            {
                if (selected.Count > 1)
                {
                    document.ImportWarnings.Add($"Sheet \"{name}\" is empty and was left out.");
                }

                continue;
            }

            tables.Add((name, table));
        }

        if (tables.Count == 0)
        {
            document.ImportWarnings.Add("The workbook has no data to import.");
            document.Blocks.Add(new ParagraphBlock("(empty workbook)"));
            return;
        }

        for (var i = 0; i < tables.Count; i++)
        {
            var (name, sheetTable) = tables[i];
            if (i > 0 && settings.PageBreaks)
            {
                document.Blocks.Add(new PageBreakBlock());
            }

            var heading = settings.SheetHeadings switch
            {
                XlsxSheetHeadings.Always => true,
                XlsxSheetHeadings.Never => false,
                _ => tables.Count > 1 || sheetTable.Title is not null,
            };
            if (heading)
            {
                var text = sheetTable.Title ?? name;
                document.Blocks.Add(new HeadingBlock(1, text));
                if (string.Equals(sheetTable.Table.Caption, text, StringComparison.Ordinal))
                {
                    // The heading already says it; a caption repeating the sheet name would print twice.
                    sheetTable.Table.Caption = null;
                }
            }

            document.Blocks.Add(sheetTable.Table);
        }
    }

    /// <summary>
    /// Every visible sheet (or every sheet with hidden content included), or the sheets named in the option by name
    /// (case-insensitive) or 1-based position, in the order given.
    /// </summary>
    private static List<Sheet> SelectSheets(List<Sheet> sheets, XlsxImportSettings settings, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(settings.Sheets))
        {
            return sheets.Where(s => settings.IncludeHidden || !IsHidden(s)).ToList();
        }

        var result = new List<Sheet>();
        foreach (var token in settings.Sheets.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = sheets.FirstOrDefault(s => string.Equals(s.Name?.Value?.Trim(), token, StringComparison.OrdinalIgnoreCase));
            if (match is null && int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= sheets.Count)
            {
                match = sheets[n - 1];
            }

            if (match is null)
            {
                warnings.Add($"The workbook has no sheet named \"{token}\"; it was skipped.");
            }
            else if (!result.Contains(match))
            {
                result.Add(match);
            }
        }

        return result;
    }

    private static bool IsHidden(Sheet sheet) =>
        sheet.State?.Value is { } state && (state == SheetStateValues.Hidden || state == SheetStateValues.VeryHidden);

    private static void FillMetadata(DocumentMetadata metadata, SpreadsheetDocument package)
    {
        try
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
        }
        catch (Exception ex) when (ex is XmlException or OpenXmlPackageException or InvalidOperationException or FormatException)
        {
            // Broken core properties are not worth failing the import over.
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
