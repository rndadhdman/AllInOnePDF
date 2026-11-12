using AllInOnePdf.Core.Themes;
using AllInOnePdf.Publishing.Model;
using AllInOnePdf.Publishing.Rendering;
using AllInOnePdf.UI.Services;

namespace AllInOnePdf.Core.Tests;

/// <summary>The design canvas splits its sheets where the PDF's page map says a new page starts.</summary>
public class CreatorCanvasSheetsBuildTests
{
    private static Section SectionOf(params Row[] rows) => new() { Rows = [.. rows] };

    private static Row RowOf(params Column[] columns) => new() { Columns = [.. columns], Fractions = [.. Enumerable.Repeat(1.0 / columns.Length, columns.Length)] };

    private static Column ColumnOf(int modules) => new() { Modules = [.. Enumerable.Range(0, modules).Select(_ => (Module)new TextModule { Markdown = "x" })] };

    private static Publication PublicationOf(params Section[] sections)
    {
        var p = new Publication { Theme = BuiltInThemes.Clean() };
        p.Sections.AddRange(sections);
        return p;
    }

    private static Dictionary<string, PublicationPageSpan> Map(params (string Id, int First, int Last)[] entries) =>
        entries.ToDictionary(e => e.Id, e => new PublicationPageSpan(e.First, e.Last), StringComparer.Ordinal);

    [Fact]
    public void Without_a_map_only_explicit_breaks_split()
    {
        var p = PublicationOf(SectionOf(RowOf(ColumnOf(2))), SectionOf(RowOf(ColumnOf(1))));
        p.Sections[1].PageBreakBefore = true;
        var sheets = CreatorCanvasSheets.Build(p, null);
        Assert.Equal([1, 2], sheets.Select(s => s.Page));
        Assert.Equal([0], sheets[0].Sections.Select(s => s.Index));
        Assert.Equal([1], sheets[1].Sections.Select(s => s.Index));
    }

    [Fact]
    public void A_section_the_pdf_puts_on_page_2_opens_a_second_sheet()
    {
        var p = PublicationOf(SectionOf(RowOf(ColumnOf(1))), SectionOf(RowOf(ColumnOf(1))));
        var map = Map((p.Sections[0].Id, 1, 1), (p.Sections[1].Id, 2, 2));
        var sheets = CreatorCanvasSheets.Build(p, map);
        Assert.Equal(2, sheets.Count);
        Assert.Equal(2, sheets[1].Page);
        Assert.Equal(1, sheets[1].Sections.Single().Index);
        Assert.False(sheets[0].Sections.Single().Continues);
    }

    [Fact]
    public void A_section_splits_between_rows_and_both_parts_know_it()
    {
        var p = PublicationOf(SectionOf(RowOf(ColumnOf(1)), RowOf(ColumnOf(1))));
        var section = p.Sections[0];
        var map = Map((section.Id, 1, 2), (section.Rows[0].Id, 1, 1), (section.Rows[1].Id, 2, 2));
        var sheets = CreatorCanvasSheets.Build(p, map);
        Assert.Equal(2, sheets.Count);
        var first = sheets[0].Sections.Single();
        var second = sheets[1].Sections.Single();
        Assert.Same(section, first.Section);
        Assert.Same(section, second.Section);
        Assert.Equal(0, first.Part);
        Assert.Equal(1, second.Part);
        Assert.True(first.Continues);
        Assert.True(first.HasNextPart);
        Assert.Equal([0], first.Rows.Select(r => r.Index));
        Assert.Equal([1], second.Rows.Select(r => r.Index));
    }

    [Fact]
    public void A_single_column_row_splits_between_modules()
    {
        var p = PublicationOf(SectionOf(RowOf(ColumnOf(4))));
        var row = p.Sections[0].Rows[0];
        var modules = row.Columns[0].Modules;
        var map = Map((row.Id, 1, 3), (modules[0].Id, 1, 1), (modules[1].Id, 1, 2), (modules[2].Id, 2, 2), (modules[3].Id, 3, 3));
        var sheets = CreatorCanvasSheets.Build(p, map);
        Assert.Equal([1, 2, 3], sheets.Select(s => s.Page));
        var parts = sheets.Select(s => s.Sections.Single().Rows.Single()).ToList();
        Assert.Equal([0, 1, 2], parts.Select(r => r.Part));
        Assert.Equal([(0, 2), (2, 3), (3, 4)], parts.Select(r => (r.Columns.Single().ModuleStart, r.Columns.Single().ModuleEnd)));
        Assert.True(parts[0].HasNextPart);
        Assert.True(parts[1].HasNextPart);
        Assert.False(parts[2].HasNextPart);
    }

    [Fact]
    public void A_multi_column_row_stays_whole_and_is_marked_as_continuing()
    {
        var p = PublicationOf(SectionOf(RowOf(ColumnOf(3), ColumnOf(3)), RowOf(ColumnOf(1))));
        var section = p.Sections[0];
        var wide = section.Rows[0];
        var map = Map((section.Id, 1, 2), (wide.Id, 1, 2), (wide.Columns[0].Modules[2].Id, 2, 2), (section.Rows[1].Id, 2, 2));
        var sheets = CreatorCanvasSheets.Build(p, map);
        Assert.Equal(2, sheets.Count);
        var first = sheets[0].Sections.Single().Rows.Single();
        Assert.Equal(2, first.Columns.Count);
        Assert.Equal((0, 3), (first.Columns[0].ModuleStart, first.Columns[0].ModuleEnd));
        Assert.True(first.Continues);
        Assert.False(first.HasNextPart);
        Assert.Equal(1, sheets[1].Sections.Single().Rows.Single().Index);
    }

    [Fact]
    public void Ids_missing_from_the_map_stay_on_the_current_sheet_and_earlier_pages_never_pull_back()
    {
        var p = PublicationOf(SectionOf(RowOf(ColumnOf(1))), SectionOf(RowOf(ColumnOf(1))), SectionOf(RowOf(ColumnOf(1))));
        var map = Map((p.Sections[0].Id, 1, 1), (p.Sections[1].Id, 3, 3), (p.Sections[2].Id, 2, 2));
        var sheets = CreatorCanvasSheets.Build(p, map);
        Assert.Equal([1, 3], sheets.Select(s => s.Page));
        Assert.Equal([1, 2], sheets[1].Sections.Select(s => s.Index));

        var unknown = CreatorCanvasSheets.Build(p, Map());
        Assert.Single(unknown);
        Assert.Equal(3, unknown[0].Sections.Count);
    }
}
