using System.Globalization;
using System.Text;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Importers.Xlsx;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace AllInOnePdf.Core.Exporters.Xlsx;

/// <summary>Typed view of the <see cref="XlsxExporter"/> options.</summary>
internal sealed record XlsxExportSettings(bool FreezeHeader, bool AutoFilter, bool IncludeHeadings, bool NumbersAsValues, bool Metadata);

/// <summary>Range covered by a sheet's auto filter (1-based, inclusive).</summary>
internal readonly record struct XlsxFilterRange(int Row1, int Column1, int Row2, int Column2);

/// <summary>Writes one worksheet: a table (title row, header, spans, typed values, widths, freeze pane, filter) or prose.</summary>
internal sealed class XlsxSheetWriter(XlsxThemeStyles styles, XlsxExportSettings settings)
{
    private const double MinWidth = 8;
    private const double MaxWidth = 60;
    private const int MaxCellContribution = 60;
    private const double ProseWidth = 100;

    private sealed record Placement(TableCell Cell, int Row, int Column, int RowSpan, int ColumnSpan);

    /// <summary>Writes <paramref name="table"/>; returns the auto-filter range when one was added.</summary>
    public XlsxFilterRange? WriteTable(WorksheetPart part, TableBlock table, string? title, bool selected)
    {
        // Lay the cells out on a grid: row spans from above push later cells to the right.
        var rowCount = table.Rows.Count;
        var occupied = new List<HashSet<int>>();
        for (var r = 0; r < rowCount; r++)
        {
            occupied.Add([]);
        }

        var anchors = new Dictionary<(int Row, int Column), Placement>();
        var owner = new Dictionary<(int Row, int Column), Placement>();
        var width = 0;
        for (var r = 0; r < rowCount; r++)
        {
            var c = 0;
            foreach (var cell in table.Rows[r].Cells)
            {
                while (occupied[r].Contains(c))
                {
                    c++;
                }

                var rs = Math.Clamp(cell.RowSpan, 1, rowCount - r);
                var cs = Math.Max(1, cell.ColumnSpan);
                var placement = new Placement(cell, r, c, rs, cs);
                anchors[(r, c)] = placement;
                for (var rr = r; rr < r + rs; rr++)
                {
                    for (var cc = c; cc < c + cs; cc++)
                    {
                        occupied[rr].Add(cc);
                        owner[(rr, cc)] = placement;
                    }
                }

                c += cs;
                width = Math.Max(width, c);
            }
        }

        width = Math.Max(1, width);
        // A single-column title row could not be told apart from data, so titles need at least two columns to merge across.
        var hasTitle = settings.IncludeHeadings && !string.IsNullOrWhiteSpace(title) && width > 1;
        var offset = hasTitle ? 1 : 0;
        var leadingHeaders = table.Rows.TakeWhile(r => r.IsHeader).Count();

        var sheetData = new SheetData();
        var merges = new List<string>();
        var links = new List<(string Reference, string Url)>();
        var widths = new double[width];

        if (hasTitle)
        {
            var titleRow = new Row { RowIndex = 1 };
            var titleStyle = styles.Title();
            titleRow.Append(TextCell(XlsxCellReference.Format(1, 1), title!.Trim(), titleStyle));
            for (var c = 1; c < width; c++)
            {
                titleRow.Append(new Cell { CellReference = XlsxCellReference.Format(1, c + 1), StyleIndex = titleStyle });
            }

            sheetData.Append(titleRow);
            if (width > 1)
            {
                merges.Add(XlsxCellReference.Range(1, 1, 1, width));
            }
        }

        var dataIndex = -1;
        for (var r = 0; r < rowCount; r++)
        {
            var source = table.Rows[r];
            if (!source.IsHeader)
            {
                dataIndex++;
            }

            var excelRow = r + 1 + offset;
            var row = new Row { RowIndex = (uint)excelRow };
            for (var c = 0; c < width; c++)
            {
                var reference = XlsxCellReference.Format(excelRow, c + 1);
                var role = new XlsxCellRole(source.IsHeader, Math.Max(0, dataIndex), table.GetAlignment(c));
                if (anchors.TryGetValue((r, c), out var anchor))
                {
                    row.Append(WriteCell(anchor, reference, role, links, widths));
                    if (anchor.RowSpan > 1 || anchor.ColumnSpan > 1)
                    {
                        merges.Add(XlsxCellReference.Range(excelRow, c + 1, excelRow + anchor.RowSpan - 1, c + anchor.ColumnSpan));
                    }
                }
                else
                {
                    // Covered by a span (or missing in a ragged row): an empty cell keeps borders and fills continuous.
                    var headerRole = owner.TryGetValue((r, c), out var o) ? role with { Header = table.Rows[o.Row].IsHeader } : role;
                    row.Append(new Cell { CellReference = reference, StyleIndex = styles.Cell(headerRole) });
                }
            }

            sheetData.Append(row);
        }

        var lastRow = rowCount + offset;
        var worksheet = new Worksheet();
        worksheet.Append(new SheetDimension { Reference = XlsxCellReference.Range(1, 1, Math.Max(1, lastRow), width) });

        var frozen = settings.FreezeHeader && leadingHeaders > 0 && leadingHeaders < rowCount ? offset + leadingHeaders : 0;
        worksheet.Append(SheetViews(selected, frozen));
        worksheet.Append(new SheetFormatProperties { DefaultRowHeight = 15 });

        var columns = new Columns();
        for (var c = 0; c < width; c++)
        {
            var w = Math.Clamp(widths[c] * 1.1 + 2, MinWidth, MaxWidth);
            columns.Append(new Column { Min = (uint)(c + 1), Max = (uint)(c + 1), Width = Math.Round(w, 2), CustomWidth = true });
        }

        worksheet.Append(columns);
        worksheet.Append(sheetData);

        XlsxFilterRange? filter = null;
        if (settings.AutoFilter && leadingHeaders > 0 && leadingHeaders < rowCount)
        {
            filter = new XlsxFilterRange(offset + leadingHeaders, 1, lastRow, width);
            worksheet.Append(new AutoFilter { Reference = XlsxCellReference.Range(filter.Value.Row1, 1, lastRow, width) });
        }

        AppendMergesAndLinks(worksheet, part, merges, links);
        worksheet.Append(Margins());
        part.Worksheet = worksheet;
        return filter;
    }

    /// <summary>One row per block for documents without tables: headings bold and larger, list items with markers, code line by line.</summary>
    public void WriteProse(WorksheetPart part, Document document, bool selected, CancellationToken ct)
    {
        var lines = new List<(string Text, XlsxProseKind Kind)>();
        foreach (var block in document.Blocks)
        {
            ct.ThrowIfCancellationRequested();
            Prose(block, 0, lines);
        }

        if (lines.Count == 0)
        {
            lines.Add((string.Empty, XlsxProseKind.Body));
        }

        var sheetData = new SheetData();
        for (var i = 0; i < lines.Count; i++)
        {
            var (text, kind) = lines[i];
            var reference = XlsxCellReference.Format(i + 1, 1);
            var row = new Row { RowIndex = (uint)(i + 1) };
            row.Append(text.Length == 0
                ? new Cell { CellReference = reference, StyleIndex = styles.Prose(kind) }
                : TextCell(reference, text, styles.Prose(kind)));
            sheetData.Append(row);
        }

        var worksheet = new Worksheet();
        worksheet.Append(new SheetDimension { Reference = XlsxCellReference.Range(1, 1, lines.Count, 1) });
        worksheet.Append(SheetViews(selected, 0));
        worksheet.Append(new SheetFormatProperties { DefaultRowHeight = 15 });
        worksheet.Append(new Columns(new Column { Min = 1, Max = 1, Width = ProseWidth, CustomWidth = true }));
        worksheet.Append(sheetData);
        worksheet.Append(Margins());
        part.Worksheet = worksheet;
    }

    private Cell WriteCell(Placement placement, string reference, XlsxCellRole role, List<(string Reference, string Url)> links, double[] widths)
    {
        var lines = CellLines(placement.Cell);
        var text = string.Join("\n", lines);
        if (placement.ColumnSpan == 1)
        {
            var longest = lines.Count == 0 ? 0 : lines.Max(l => l.Length);
            widths[placement.Column] = Math.Max(widths[placement.Column], Math.Min(MaxCellContribution, longest));
        }

        var url = SingleLink(placement.Cell);
        if (url is not null)
        {
            links.Add((reference, url));
        }

        role = role with
        {
            Bold = !role.Header && IsFullyBold(placement.Cell),
            Link = url is not null,
            Wrap = lines.Count > 1,
        };

        if (text.Length == 0)
        {
            return new Cell { CellReference = reference, StyleIndex = styles.Cell(role) };
        }

        var value = settings.NumbersAsValues && !role.Header && lines.Count == 1 && url is null
            ? XlsxCellValue.Parse(text)
            : new XlsxCellValue(XlsxValueKind.Text, text);

        switch (value.Kind)
        {
            case XlsxValueKind.Number:
            {
                var id = value.FormatCode is { } code ? styles.Book.NumberFormat(code) : value.NumberFormatId;
                return new Cell
                {
                    CellReference = reference,
                    StyleIndex = styles.Cell(role with { NumberFormatId = id }),
                    CellValue = new CellValue(XlsxStyleBook.Number(value.Number)),
                };
            }

            case XlsxValueKind.Boolean:
                return new Cell
                {
                    CellReference = reference,
                    StyleIndex = styles.Cell(role),
                    DataType = CellValues.Boolean,
                    CellValue = new CellValue(value.Number != 0 ? "1" : "0"),
                };
            default:
                return TextCell(reference, text, styles.Cell(role));
        }
    }

    private static Cell TextCell(string reference, string text, uint style) => new()
    {
        CellReference = reference,
        StyleIndex = style,
        DataType = CellValues.InlineString,
        InlineString = new InlineString(new Text(Clean(text)) { Space = SpaceProcessingModeValues.Preserve }),
    };

    /// <summary>Strips characters XML cannot carry; Excel caps a cell at 32,767 characters.</summary>
    private static string Clean(string text)
    {
        var sb = new StringBuilder(Math.Min(text.Length, 32767));
        foreach (var ch in text)
        {
            if (sb.Length >= 32767)
            {
                break;
            }

            if (ch is '\n' or '\t' || (!char.IsControl(ch) && ch is not '\uFFFE' and not '\uFFFF'))
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }

    private static SheetViews SheetViews(bool selected, int frozenRows)
    {
        var view = new SheetView { WorkbookViewId = 0 };
        if (selected)
        {
            view.TabSelected = true;
        }

        if (frozenRows > 0)
        {
            var topLeft = XlsxCellReference.Format(frozenRows + 1, 1);
            view.Append(new Pane
            {
                VerticalSplit = frozenRows,
                TopLeftCell = topLeft,
                ActivePane = PaneValues.BottomLeft,
                State = PaneStateValues.Frozen,
            });
            view.Append(new Selection { Pane = PaneValues.BottomLeft, ActiveCell = topLeft, SequenceOfReferences = new ListValue<StringValue> { InnerText = topLeft } });
        }

        return new SheetViews(view);
    }

    private static PageMargins Margins() => new() { Left = 0.7, Right = 0.7, Top = 0.75, Bottom = 0.75, Header = 0.3, Footer = 0.3 };

    private static void AppendMergesAndLinks(Worksheet worksheet, WorksheetPart part, List<string> merges, List<(string Reference, string Url)> links)
    {
        if (merges.Count > 0)
        {
            var mergeCells = new MergeCells { Count = (uint)merges.Count };
            foreach (var m in merges)
            {
                mergeCells.Append(new MergeCell { Reference = m });
            }

            worksheet.Append(mergeCells);
        }

        var hyperlinks = new Hyperlinks();
        foreach (var (reference, url) in links)
        {
            if (url.StartsWith('#'))
            {
                if (url.Length > 1)
                {
                    hyperlinks.Append(new Hyperlink { Reference = reference, Location = url[1..] });
                }
            }
            else if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                var rel = part.AddHyperlinkRelationship(uri, true);
                hyperlinks.Append(new Hyperlink { Reference = reference, Id = rel.Id });
            }
        }

        if (hyperlinks.HasChildren)
        {
            worksheet.Append(hyperlinks);
        }
    }

    /// <summary>The cell's text, one entry per paragraph-like block (nested lists and code flattened).</summary>
    internal static List<string> CellLines(TableCell cell)
    {
        var lines = new List<string>();
        foreach (var block in cell.Blocks)
        {
            Flatten(block, lines);
        }

        return lines;
    }

    private static void Flatten(Block block, List<string> lines)
    {
        switch (block)
        {
            case InlineContainerBlock icb:
                lines.Add(icb.ToPlainText().Trim());
                break;
            case CodeBlock code:
                lines.AddRange(code.Code.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n'));
                break;
            case ListBlock list:
                var n = list.Start;
                foreach (var item in list.Items)
                {
                    var marker = list.Ordered ? (n++).ToString(CultureInfo.InvariantCulture) + ". " : "\u2022 ";
                    var itemLines = new List<string>();
                    foreach (var child in item.Blocks)
                    {
                        Flatten(child, itemLines);
                    }

                    for (var i = 0; i < itemLines.Count; i++)
                    {
                        lines.Add((i == 0 ? marker : "  ") + itemLines[i]);
                    }
                }

                break;
            case TableBlock nested:
                foreach (var row in nested.Rows)
                {
                    lines.Add(string.Join(" | ", row.Cells.Select(c => c.ToPlainText())));
                }

                break;
            case ImageBlock image when !string.IsNullOrWhiteSpace(image.AltText):
                lines.Add(image.AltText!);
                break;
            case MathBlock math:
                lines.Add(math.Latex.Trim());
                break;
            default:
                foreach (var child in block.Children)
                {
                    Flatten(child, lines);
                }

                break;
        }
    }

    private static void Prose(Block block, int depth, List<(string Text, XlsxProseKind Kind)> lines)
    {
        var indent = new string(' ', depth * 2);
        switch (block)
        {
            case HeadingBlock h:
                lines.Add((h.ToPlainText().Trim(), h.Level switch
                {
                    1 => XlsxProseKind.Heading1,
                    2 => XlsxProseKind.Heading2,
                    _ => XlsxProseKind.Heading,
                }));
                break;
            case ParagraphBlock p:
                lines.Add((indent + p.ToPlainText().Trim(), XlsxProseKind.Body));
                break;
            case CodeBlock code:
                foreach (var line in code.Code.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n'))
                {
                    lines.Add((line, XlsxProseKind.Code));
                }

                break;
            case ListBlock list:
                var n = list.Start;
                foreach (var item in list.Items)
                {
                    var marker = list.Ordered ? (n++).ToString(CultureInfo.InvariantCulture) + ". " : "\u2022 ";
                    var first = true;
                    foreach (var child in item.Blocks)
                    {
                        if (first && child is InlineContainerBlock icb)
                        {
                            lines.Add((indent + marker + icb.ToPlainText().Trim(), XlsxProseKind.Body));
                        }
                        else
                        {
                            Prose(child, depth + 1, lines);
                        }

                        first = false;
                    }
                }

                break;
            case CalloutBlock callout:
                if (!string.IsNullOrWhiteSpace(callout.Title))
                {
                    lines.Add((indent + callout.Title.Trim(), XlsxProseKind.Heading));
                }

                foreach (var child in callout.Blocks)
                {
                    Prose(child, depth, lines);
                }

                break;
            case QuoteBlock quote:
                foreach (var child in quote.Blocks)
                {
                    Prose(child, depth, lines);
                }

                if (!string.IsNullOrWhiteSpace(quote.Attribution))
                {
                    lines.Add((indent + "\u2014 " + quote.Attribution.Trim(), XlsxProseKind.Body));
                }

                break;
            case ImageBlock image:
                if (image.ResolveCaption() is { } caption)
                {
                    lines.Add((indent + caption, XlsxProseKind.Body));
                }

                break;
            case MathBlock math:
                lines.Add((indent + math.Latex.Trim(), XlsxProseKind.Code));
                break;
            case ThematicBreakBlock or PageBreakBlock:
                lines.Add((string.Empty, XlsxProseKind.Body));
                break;
            case HtmlBlock:
                break;
            default:
                foreach (var child in block.Children)
                {
                    Prose(child, depth, lines);
                }

                break;
        }
    }

    /// <summary>True when every visible character of the cell is inside a bold run.</summary>
    internal static bool IsFullyBold(TableCell cell)
    {
        var any = false;
        foreach (var block in cell.Blocks)
        {
            if (block is not InlineContainerBlock icb)
            {
                return false;
            }

            if (!AllBold(icb.Inlines, false, ref any))
            {
                return false;
            }
        }

        return any;
    }

    private static bool AllBold(IEnumerable<Inline> inlines, bool bold, ref bool any)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TextInline t:
                    if (t.Text.Trim().Length > 0)
                    {
                        if (!bold)
                        {
                            return false;
                        }

                        any = true;
                    }

                    break;
                case StyledInline s:
                    if (!AllBold(s.Inlines, bold || s.Style.HasFlag(InlineStyle.Bold), ref any))
                    {
                        return false;
                    }

                    break;
                case CodeInline:
                    return false;
                default:
                    if (!AllBold(inline.Children, bold, ref any))
                    {
                        return false;
                    }

                    break;
            }
        }

        return true;
    }

    /// <summary>The URL when the cell is exactly one paragraph holding one link (whitespace aside).</summary>
    internal static string? SingleLink(TableCell cell)
    {
        if (cell.Blocks.Count != 1 || cell.Blocks[0] is not ParagraphBlock p)
        {
            return null;
        }

        LinkInline? found = null;
        foreach (var inline in Unwrap(p.Inlines))
        {
            if (inline is LinkInline link && found is null)
            {
                found = link;
            }
            else if (inline is not TextInline { Text: var t } || t.Trim().Length > 0)
            {
                return null;
            }
        }

        return found?.Url;
    }

    /// <summary>Top-level inlines with styled wrappers (bold link etc.) looked through.</summary>
    private static IEnumerable<Inline> Unwrap(IEnumerable<Inline> inlines)
    {
        foreach (var inline in inlines)
        {
            if (inline is StyledInline s)
            {
                foreach (var inner in Unwrap(s.Inlines))
                {
                    yield return inner;
                }
            }
            else
            {
                yield return inline;
            }
        }
    }
}
