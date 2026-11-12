using AllInOnePdf.Publishing.Model;
using AllInOnePdf.Publishing.Rendering;

namespace AllInOnePdf.UI.Services;

/// <summary>One page sheet of the design canvas: the parts of the sections the PDF puts on that page.</summary>
public sealed class CanvasSheet
{
    /// <summary>1-based page number.</summary>
    public int Page { get; init; }

    /// <summary>Section parts on this sheet, in order.</summary>
    public List<CanvasSectionPart> Sections { get; } = [];
}

/// <summary>The part of a section on one sheet; a section the PDF splits across pages has one part per sheet.</summary>
public sealed class CanvasSectionPart
{
    /// <summary>The section.</summary>
    public required Section Section { get; init; }

    /// <summary>Index of the section in the publication.</summary>
    public int Index { get; init; }

    /// <summary>0 for the section's first part, then 1, 2, …</summary>
    public int Part { get; init; }

    /// <summary>Row parts on this sheet.</summary>
    public List<CanvasRowPart> Rows { get; } = [];

    /// <summary>True when more of the section follows on the next sheet.</summary>
    public bool Continues { get; set; }

    /// <summary>True when a further part of this section is drawn on a later sheet.</summary>
    public bool HasNextPart { get; set; }

    /// <summary>True for a free-flow section: one part, drawn as a fixed canvas with its items (<see cref="Rows"/> is empty).</summary>
    public bool FreeFlow => Section.Layout == SectionLayout.FreeFlow;
}

/// <summary>The part of a row on one sheet. Only single-column rows split; a multi-column row stays whole on the sheet it starts on.</summary>
public sealed class CanvasRowPart
{
    /// <summary>The row.</summary>
    public required Row Row { get; init; }

    /// <summary>Index of the row in its section.</summary>
    public int Index { get; init; }

    /// <summary>0 for the row's first part, then 1, 2, …</summary>
    public int Part { get; init; }

    /// <summary>Column parts on this sheet.</summary>
    public List<CanvasColumnPart> Columns { get; } = [];

    /// <summary>True when the row goes on, on the next page (as a further part, or inside the PDF for a row that cannot split here).</summary>
    public bool Continues { get; set; }

    /// <summary>True when a further part of this row is drawn on a later sheet (single-column rows only).</summary>
    public bool HasNextPart { get; set; }
}

/// <summary>A column on a sheet with the range of its modules shown there.</summary>
public sealed class CanvasColumnPart
{
    /// <summary>The column.</summary>
    public required Column Column { get; init; }

    /// <summary>Index of the column in its row.</summary>
    public int Index { get; init; }

    /// <summary>0 for the column's first part, then 1, 2, …</summary>
    public int Part { get; init; }

    /// <summary>First module index shown in this part.</summary>
    public int ModuleStart { get; init; }

    /// <summary>One past the last module index shown in this part.</summary>
    public int ModuleEnd { get; set; }
}

/// <summary>
/// Splits a publication into the page sheets the design canvas draws. With a page map from the PDF (the latest
/// render) a new sheet starts wherever the PDF starts a new page, down to the module: sections split between rows,
/// single-column rows split between modules, and a multi-column row or a module the PDF splits inside stays on the
/// sheet it starts on and is flagged as continuing. Without a map only the explicit breaks split
/// (<see cref="CreatorCanvasHtml.SheetRanges"/>).
/// </summary>
public static class CreatorCanvasSheets
{
    /// <summary>Builds the sheets for <paramref name="publication"/>.</summary>
    /// <param name="publication">The publication.</param>
    /// <param name="pages">Pages by element id from the latest render, or null before the first render.</param>
    public static IReadOnlyList<CanvasSheet> Build(Publication publication, IReadOnlyDictionary<string, PublicationPageSpan>? pages)
    {
        ArgumentNullException.ThrowIfNull(publication);
        return pages is null ? FromExplicitBreaks(publication) : FromPages(publication, pages);
    }

    private static List<CanvasSheet> FromExplicitBreaks(Publication publication)
    {
        var sheets = new List<CanvasSheet>();
        var ranges = CreatorCanvasHtml.SheetRanges(publication);
        for (var i = 0; i < ranges.Count; i++)
        {
            var sheet = new CanvasSheet { Page = i + 1 };
            for (var si = ranges[i].Start; si < ranges[i].End; si++)
            {
                sheet.Sections.Add(WholeSection(publication.Sections[si], si));
            }

            sheets.Add(sheet);
        }

        return sheets;
    }

    private static CanvasSectionPart WholeSection(Section section, int index)
    {
        var part = new CanvasSectionPart { Section = section, Index = index };
        for (var ri = 0; ri < section.Rows.Count; ri++)
        {
            part.Rows.Add(WholeRow(section.Rows[ri], ri));
        }

        return part;
    }

    private static CanvasRowPart WholeRow(Row row, int index)
    {
        var part = new CanvasRowPart { Row = row, Index = index };
        for (var ci = 0; ci < row.Columns.Count; ci++)
        {
            var column = row.Columns[ci];
            part.Columns.Add(new CanvasColumnPart { Column = column, Index = ci, ModuleStart = 0, ModuleEnd = column.Modules.Count });
        }

        return part;
    }

    private static List<CanvasSheet> FromPages(Publication publication, IReadOnlyDictionary<string, PublicationPageSpan> pages)
    {
        int First(string id, int fallback) => pages.TryGetValue(id, out var span) && span.First > fallback ? span.First : fallback;
        int Last(string id, int fallback) => pages.TryGetValue(id, out var span) && span.Last > fallback ? span.Last : fallback;

        var sheets = new List<CanvasSheet>();
        var sheet = new CanvasSheet { Page = 1 };
        sheets.Add(sheet);

        CanvasSheet NewSheet(int page)
        {
            sheet = new CanvasSheet { Page = page };
            sheets.Add(sheet);
            return sheet;
        }

        for (var si = 0; si < publication.Sections.Count; si++)
        {
            var section = publication.Sections[si];

            if (section.Layout == SectionLayout.FreeFlow)
            {
                // A canvas never splits, and it sits on the page the PDF put it on: a canvas with a height of its own
                // shares a page with what is before and after it, like any other section. Only a full-page canvas the
                // render has not placed yet (just added) is known to need a sheet of its own.
                var page = First(section.Id, sheet.Page);
                if (!pages.ContainsKey(section.Id) && section.CanvasHeightPt is null && sheet.Sections.Count > 0)
                {
                    page = sheet.Page + 1;
                }

                if (page > sheet.Page)
                {
                    NewSheet(page);
                }

                sheet.Sections.Add(new CanvasSectionPart { Section = section, Index = si });
                continue;
            }

            // The page the section's first content lands on: its own box, else its first row, else that row's first module.
            var start = First(section.Id, sheet.Page);
            if (section.Rows.Count > 0)
            {
                var firstRow = section.Rows[0];
                start = First(firstRow.Id, start);
                if (firstRow.Columns.Count == 1 && firstRow.Columns[0].Modules.Count > 0)
                {
                    start = First(firstRow.Columns[0].Modules[0].Id, start);
                }
            }

            if (start > sheet.Page)
            {
                NewSheet(start);
            }

            var sectionPart = new CanvasSectionPart { Section = section, Index = si };
            sheet.Sections.Add(sectionPart);

            for (var ri = 0; ri < section.Rows.Count; ri++)
            {
                var row = section.Rows[ri];
                var single = row.Columns.Count == 1;
                var rowStart = First(row.Id, sheet.Page);
                if (single && row.Columns[0].Modules.Count > 0)
                {
                    rowStart = First(row.Columns[0].Modules[0].Id, rowStart);
                }

                if (rowStart > sheet.Page)
                {
                    sectionPart.Continues = true;
                    sectionPart.HasNextPart = true;
                    NewSheet(rowStart);
                    sectionPart = new CanvasSectionPart { Section = section, Index = si, Part = sectionPart.Part + 1 };
                    sheet.Sections.Add(sectionPart);
                }

                var rowPart = new CanvasRowPart { Row = row, Index = ri };
                sectionPart.Rows.Add(rowPart);

                if (!single)
                {
                    for (var ci = 0; ci < row.Columns.Count; ci++)
                    {
                        var column = row.Columns[ci];
                        rowPart.Columns.Add(new CanvasColumnPart { Column = column, Index = ci, ModuleStart = 0, ModuleEnd = column.Modules.Count });
                    }

                    rowPart.Continues = Last(row.Id, sheet.Page) > sheet.Page;
                    continue;
                }

                var only = row.Columns[0];
                var columnPart = new CanvasColumnPart { Column = only, Index = 0, ModuleStart = 0, ModuleEnd = only.Modules.Count };
                rowPart.Columns.Add(columnPart);
                for (var mi = 1; mi < only.Modules.Count; mi++)
                {
                    var moduleStart = First(only.Modules[mi].Id, sheet.Page);
                    if (moduleStart <= sheet.Page)
                    {
                        continue;
                    }

                    columnPart.ModuleEnd = mi;
                    rowPart.Continues = true;
                    rowPart.HasNextPart = true;
                    sectionPart.Continues = true;
                    sectionPart.HasNextPart = true;
                    NewSheet(moduleStart);
                    sectionPart = new CanvasSectionPart { Section = section, Index = si, Part = sectionPart.Part + 1 };
                    sheet.Sections.Add(sectionPart);
                    rowPart = new CanvasRowPart { Row = row, Index = ri, Part = rowPart.Part + 1 };
                    sectionPart.Rows.Add(rowPart);
                    columnPart = new CanvasColumnPart { Column = only, Index = 0, Part = columnPart.Part + 1, ModuleStart = mi, ModuleEnd = only.Modules.Count };
                    rowPart.Columns.Add(columnPart);
                }
            }
        }

        return sheets;
    }
}
