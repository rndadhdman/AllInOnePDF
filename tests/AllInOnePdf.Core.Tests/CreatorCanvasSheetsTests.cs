using AllInOnePdf.Core.Themes;
using AllInOnePdf.Publishing.Model;
using AllInOnePdf.UI.Services;

namespace AllInOnePdf.Core.Tests;

/// <summary>The design canvas splits the publication into page sheets exactly where the renderer forces a page.</summary>
public class CreatorCanvasSheetsTests
{
    private static Section SectionOf(params Module[] modules) =>
        new() { Rows = [new Row { Columns = [new Column { Modules = [.. modules] }] }] };

    private static Publication PublicationOf(params Section[] sections)
    {
        var p = new Publication { Theme = BuiltInThemes.Clean() };
        p.Sections.AddRange(sections);
        return p;
    }

    [Fact]
    public void Without_breaks_everything_is_one_sheet()
    {
        var p = PublicationOf(SectionOf(new TextModule()), SectionOf(new TextModule()), SectionOf(new TextModule()));
        Assert.Equal([(0, 3)], CreatorCanvasHtml.SheetRanges(p));
    }

    [Fact]
    public void An_empty_publication_still_has_one_sheet()
    {
        Assert.Equal([(0, 0)], CreatorCanvasHtml.SheetRanges(PublicationOf()));
    }

    [Fact]
    public void A_section_that_starts_on_a_new_page_opens_a_sheet_unless_nothing_precedes_it()
    {
        var p = PublicationOf(SectionOf(new TextModule()), SectionOf(new TextModule()), SectionOf(new TextModule()));
        p.Sections[1].PageBreakBefore = true;
        Assert.Equal([(0, 1), (1, 3)], CreatorCanvasHtml.SheetRanges(p));

        p.Sections[1].PageBreakBefore = false;
        p.Sections[0].PageBreakBefore = true;
        Assert.Equal([(0, 3)], CreatorCanvasHtml.SheetRanges(p));
    }

    [Fact]
    public void A_page_break_after_a_section_opens_a_sheet_unless_it_is_the_last()
    {
        var p = PublicationOf(SectionOf(new TextModule()), SectionOf(new TextModule()), SectionOf(new TextModule()));
        p.Sections[0].PageBreakAfter = true;
        Assert.Equal([(0, 1), (1, 3)], CreatorCanvasHtml.SheetRanges(p));

        p.Sections[0].PageBreakAfter = false;
        p.Sections[2].PageBreakAfter = true;
        Assert.Equal([(0, 3)], CreatorCanvasHtml.SheetRanges(p));
    }

    [Fact]
    public void A_page_break_module_ending_a_section_opens_a_sheet_and_is_not_doubled_by_the_section_setting()
    {
        var p = PublicationOf(SectionOf(new TextModule(), new PageBreakModule()), SectionOf(new TextModule()));
        p.Sections[0].PageBreakAfter = true;
        Assert.Equal([(0, 1), (1, 2)], CreatorCanvasHtml.SheetRanges(p));

        // After that break the page is empty, so a "start on a new page" on the next section adds nothing.
        p.Sections[1].PageBreakBefore = true;
        Assert.Equal([(0, 1), (1, 2)], CreatorCanvasHtml.SheetRanges(p));
    }

    [Fact]
    public void A_page_break_module_in_the_middle_of_a_column_does_not_split_the_sheet()
    {
        var p = PublicationOf(SectionOf(new TextModule(), new PageBreakModule(), new TextModule()), SectionOf(new TextModule()));
        Assert.Equal([(0, 2)], CreatorCanvasHtml.SheetRanges(p));
    }

    [Fact]
    public void The_theme_can_start_a_page_before_a_section_that_opens_with_a_level_1_heading()
    {
        var p = PublicationOf(SectionOf(new HeadingModule { Level = 1 }), SectionOf(new HeadingModule { Level = 1 }), SectionOf(new HeadingModule { Level = 2 }));
        p.Theme.Headings.PageBreakBeforeH1 = true;
        Assert.Equal([(0, 1), (1, 3)], CreatorCanvasHtml.SheetRanges(p));

        p.Theme.Headings.PageBreakBeforeH1 = false;
        Assert.Equal([(0, 3)], CreatorCanvasHtml.SheetRanges(p));
    }

    [Fact]
    public void Empty_sections_carry_no_content_for_break_decisions()
    {
        var p = PublicationOf(new Section(), SectionOf(new TextModule()), new Section(), SectionOf(new TextModule()));
        p.Sections[1].PageBreakBefore = true; // nothing before it: ignored
        p.Sections[3].PageBreakBefore = true; // content before it: a new sheet
        Assert.Equal([(0, 3), (3, 4)], CreatorCanvasHtml.SheetRanges(p));
    }
}
