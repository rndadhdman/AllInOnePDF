using System.Globalization;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Importers.Data;

namespace AllInOnePdf.Core.Importers;

/// <summary>
/// CSV / TSV → one table. RFC 4180 parsing (<see cref="CsvParser"/>), delimiter and header detection, numeric
/// columns right-aligned, encoding from the byte-order mark (UTF-8 / UTF-16) with a Latin-1 fallback.
/// </summary>
public sealed class CsvImporter : IImporter
{
    public const string OptDelimiter = "delimiter";
    public const string OptHasHeader = "hasHeader";
    public const string OptMaxRows = "maxRows";
    public const string OptTrim = "trimWhitespace";
    public const string OptTitle = "title";

    private const string GroupTable = "Table";
    private const string GroupLimits = "Limits";
    private const double NumericShare = 0.8;
    private const long MaxBytes = 256L * 1024 * 1024;

    public FileFormat Format => KnownFormats.Csv;

    public IReadOnlyList<OptionDefinition> Options { get; } =
    [
        OptionDefinition.Choice(OptDelimiter, "Delimiter", "auto",
        [
            new OptionChoice("auto", "Detect", "Pick comma, semicolon, tab or pipe from the first rows."),
            new OptionChoice("comma", "Comma (,)"),
            new OptionChoice("semicolon", "Semicolon (;)", "Common in European spreadsheet exports."),
            new OptionChoice("tab", "Tab", "Tab-separated values."),
            new OptionChoice("pipe", "Pipe (|)"),
        ], "Character that separates the fields.", GroupTable),
        OptionDefinition.Choice(OptHasHeader, "Header row", "auto",
        [
            new OptionChoice("auto", "Detect", "The first row is a header when it is text above numeric columns, or when every column is text and its values are distinct."),
            new OptionChoice("yes", "Yes", "The first row holds the column names."),
            new OptionChoice("no", "No", "Every row is data."),
        ], "Whether the first row is shown as the table header.", GroupTable),
        OptionDefinition.Bool(OptTrim, "Trim whitespace", true, "Remove spaces around each value.", GroupTable),
        OptionDefinition.Text(OptTitle, "Title", null, "Caption and document title. Leave empty to use the file name.", GroupTable),
        OptionDefinition.Int(OptMaxRows, "Maximum rows", 5000, 1, 1_000_000, "Rows after this many are left out, with a warning. Very large tables make very long PDFs.", GroupLimits, unit: "rows"),
    ];

    public async Task<Document> ImportAsync(ImportContext context, CancellationToken cancellationToken = default)
    {
        var options = context.Options.WithDefaults(Options);
        var bytes = await TextDecoding.ReadAllBytesAsync(context.Input, MaxBytes, "The CSV file is larger than 256 MB and cannot be converted.", cancellationToken);
        var text = TextDecoding.Decode(bytes, out var latin1);

        var document = new Document();
        document.Metadata.SourceFormat = Format.Id;
        document.Metadata.SourcePath = context.SourcePath;
        if (latin1)
        {
            document.ImportWarnings.Add(TextDecoding.Latin1Warning);
        }

        var delimiter = options.GetString(OptDelimiter)?.ToLowerInvariant() switch
        {
            "comma" => ',',
            "semicolon" => ';',
            "tab" => '\t',
            "pipe" => '|',
            _ => CsvParser.DetectDelimiter(text),
        };

        var trim = options.GetBool(OptTrim, true);
        var maxRows = Math.Max(1, options.GetInt(OptMaxRows, 5000));
        var headerMode = options.GetString(OptHasHeader)?.ToLowerInvariant() ?? "auto";

        // Keep at most maxRows data rows plus a possible header; count the rest without storing them.
        var rows = new List<List<string>>();
        var total = 0;
        foreach (var record in CsvParser.Parse(text, delimiter))
        {
            if (CsvParser.IsBlank(record))
            {
                continue;
            }

            total++;
            if (rows.Count <= maxRows)
            {
                if (trim)
                {
                    for (var c = 0; c < record.Count; c++)
                    {
                        record[c] = record[c].Trim();
                    }
                }

                rows.Add(record);
            }

            if ((total & 0x3FF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                context.Report(Math.Min(0.9, (double)total / Math.Max(maxRows, 1)), $"Row {total:N0}");
            }
        }

        var title = options.GetString(OptTitle);
        title = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(context.FileName) : title.Trim();
        document.Metadata.Title = title;

        if (rows.Count == 0)
        {
            document.ImportWarnings.Add("The CSV file has no rows.");
            document.Blocks.Add(new ParagraphBlock("(empty table)"));
            return document;
        }

        var columns = rows.Max(r => r.Count);
        foreach (var row in rows)
        {
            while (row.Count < columns)
            {
                row.Add(string.Empty);
            }
        }

        var hasHeader = headerMode switch
        {
            "yes" or "true" => true,
            "no" or "false" => false,
            _ => DetectHeader(rows),
        };

        var dataStart = hasHeader ? 1 : 0;
        var dataTotal = total - dataStart;
        if (rows.Count - dataStart > maxRows)
        {
            rows.RemoveRange(dataStart + maxRows, rows.Count - dataStart - maxRows);
        }

        if (dataTotal > maxRows)
        {
            document.ImportWarnings.Add(string.Create(CultureInfo.InvariantCulture,
                $"Only the first {maxRows:N0} of {dataTotal:N0} rows were imported. Raise \"Maximum rows\" to include more."));
        }

        var table = new TableBlock { Caption = title };
        for (var c = 0; c < columns; c++)
        {
            table.Alignments.Add(IsNumericColumn(rows, c, dataStart) ? ColumnAlignment.Right : ColumnAlignment.Default);
        }

        for (var r = 0; r < rows.Count; r++)
        {
            var row = new TableRow { IsHeader = hasHeader && r == 0 };
            foreach (var value in rows[r])
            {
                row.Cells.Add(value.Length == 0 ? new TableCell() : new TableCell(value));
            }

            table.Rows.Add(row);
        }

        document.Blocks.Add(table);
        context.Report(1, "Done");
        return document;
    }

    /// <summary>
    /// Header when the first row has no numbers but some column below it is numeric; when every column is text,
    /// header when the first row's values are all present and distinct.
    /// </summary>
    internal static bool DetectHeader(List<List<string>> rows)
    {
        if (rows.Count < 2)
        {
            return false;
        }

        var first = rows[0];
        if (first.Any(CsvParser.IsNumeric))
        {
            return false;
        }

        var columns = first.Count;
        var anyNumericColumn = Enumerable.Range(0, columns).Any(c => IsNumericColumn(rows, c, 1));
        if (anyNumericColumn)
        {
            return true;
        }

        return first.All(v => !string.IsNullOrWhiteSpace(v))
            && first.Distinct(StringComparer.OrdinalIgnoreCase).Count() == columns;
    }

    internal static bool IsNumericColumn(List<List<string>> rows, int column, int start)
    {
        var filled = 0;
        var numeric = 0;
        for (var r = start; r < rows.Count; r++)
        {
            var value = column < rows[r].Count ? rows[r][column] : null;
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            filled++;
            if (CsvParser.IsNumeric(value))
            {
                numeric++;
            }
        }

        return filled > 0 && numeric >= NumericShare * filled;
    }
}
