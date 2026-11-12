using System.Text;
using System.Text.RegularExpressions;
using AllInOnePdf.Core.Documents;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace AllInOnePdf.Core.Importers.Xlsx;

/// <summary>What the importer needs from a cell style (<c>cellXfs</c> entry): number format, bold, fill, alignment.</summary>
internal readonly record struct XlsxCellStyle(int NumberFormatId, string? FormatCode, bool Bold, bool SolidFill, ColumnAlignment Alignment)
{
    public bool IsDate => XlsxNumberFormat.IsDateFormat(NumberFormatId, FormatCode);
}

/// <summary>A shared or inline string: concatenated run text, and whether every run is bold.</summary>
internal readonly record struct XlsxRichText(string Text, bool AllBold);

/// <summary>Workbook-wide lookups: shared strings, cell styles and the date system.</summary>
internal sealed partial class XlsxWorkbookInfo
{
    private readonly List<XlsxRichText> _sharedStrings = [];
    private readonly List<XlsxCellStyle> _styles = [];

    public XlsxWorkbookInfo(WorkbookPart workbookPart)
    {
        Date1904 = workbookPart.Workbook?.WorkbookProperties?.Date1904?.Value ?? false;
        LoadSharedStrings(workbookPart.SharedStringTablePart);
        LoadStyles(workbookPart.WorkbookStylesPart?.Stylesheet);
    }

    public bool Date1904 { get; }

    public XlsxRichText SharedString(int index) =>
        index >= 0 && index < _sharedStrings.Count ? _sharedStrings[index] : new XlsxRichText(string.Empty, false);

    public XlsxCellStyle Style(int index) =>
        index >= 0 && index < _styles.Count ? _styles[index] : default;

    /// <summary>Text of a shared-string item or inline string: plain text, or the runs concatenated (phonetic runs excluded).</summary>
    public static XlsxRichText ReadRichText(OpenXmlElement? item)
    {
        if (item is null)
        {
            return new XlsxRichText(string.Empty, false);
        }

        var runs = item.Elements<Run>().ToList();
        if (runs.Count == 0)
        {
            return new XlsxRichText(Decode(item.GetFirstChild<Text>()?.Text), false);
        }

        var sb = new StringBuilder();
        var allBold = true;
        var any = false;
        foreach (var run in runs)
        {
            var text = run.Text?.Text ?? string.Empty;
            sb.Append(text);
            if (text.Trim().Length == 0)
            {
                continue;
            }

            any = true;
            if (!IsOn(run.RunProperties?.GetFirstChild<Bold>()))
            {
                allBold = false;
            }
        }

        return new XlsxRichText(Decode(sb.ToString()), any && allBold);
    }

    /// <summary>Excel escapes control characters as <c>_xHHHH_</c>.</summary>
    public static string Decode(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text.Contains("_x", StringComparison.Ordinal)
            ? EscapedChar().Replace(text, m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString())
            : text;
    }

    private static bool IsOn(BooleanPropertyType? flag) => flag is not null && (flag.Val is null || flag.Val.Value);

    private void LoadSharedStrings(SharedStringTablePart? part)
    {
        if (part is null)
        {
            return;
        }

        using var reader = OpenXmlReader.Create(part);
        if (!reader.Read())
        {
            return;
        }

        while (!reader.EOF)
        {
            if (reader.IsStartElement && reader.ElementType == typeof(SharedStringItem))
            {
                _sharedStrings.Add(ReadRichText(reader.LoadCurrentElement()));
                continue;
            }

            if (!reader.Read())
            {
                break;
            }
        }
    }

    private void LoadStyles(Stylesheet? stylesheet)
    {
        if (stylesheet is null)
        {
            return;
        }

        var codes = new Dictionary<int, string>();
        foreach (var nf in stylesheet.NumberingFormats?.Elements<NumberingFormat>() ?? [])
        {
            if (nf.NumberFormatId?.Value is { } id && nf.FormatCode?.Value is { } code)
            {
                codes[(int)id] = code;
            }
        }

        var fonts = stylesheet.Fonts?.Elements<Font>().Select(f => IsOn(f.Bold)).ToList() ?? [];
        var fills = stylesheet.Fills?.Elements<Fill>()
            .Select(f => f.PatternFill?.PatternType?.Value == PatternValues.Solid)
            .ToList() ?? [];

        foreach (var xf in stylesheet.CellFormats?.Elements<CellFormat>() ?? [])
        {
            var numFmt = (int)(xf.NumberFormatId?.Value ?? 0);
            var fontId = (int)(xf.FontId?.Value ?? 0);
            var fillId = (int)(xf.FillId?.Value ?? 0);
            var horizontal = xf.Alignment?.Horizontal?.Value;
            var alignment = ColumnAlignment.Default;
            if (horizontal == HorizontalAlignmentValues.Center || horizontal == HorizontalAlignmentValues.CenterContinuous)
            {
                alignment = ColumnAlignment.Center;
            }
            else if (horizontal == HorizontalAlignmentValues.Right)
            {
                alignment = ColumnAlignment.Right;
            }
            else if (horizontal == HorizontalAlignmentValues.Left)
            {
                alignment = ColumnAlignment.Left;
            }

            _styles.Add(new XlsxCellStyle(
                numFmt,
                codes.GetValueOrDefault(numFmt),
                fontId < fonts.Count && fonts[fontId],
                fillId < fills.Count && fills[fillId],
                alignment));
        }
    }

    [GeneratedRegex("_x([0-9A-Fa-f]{4})_")]
    private static partial Regex EscapedChar();
}
