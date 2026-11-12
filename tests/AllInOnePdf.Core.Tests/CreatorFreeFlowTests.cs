using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Rendering;
using AllInOnePdf.Core.Storage;
using AllInOnePdf.Core.Themes;
using AllInOnePdf.Publishing.Content;
using AllInOnePdf.Publishing.Model;
using AllInOnePdf.Publishing.Rendering;
using AllInOnePdf.Publishing.Storage;
using AllInOnePdf.Publishing.Templates;
using AllInOnePdf.UI.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AllInOnePdf.Core.Tests;

/// <summary>Free flow in the Creator: item operations and undo, layout conversion, sheets, canvas HTML, geometry.</summary>
public sealed class CreatorFreeFlowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aio-ff-" + Guid.NewGuid().ToString("N"));

    // ------------------------------------------------------------------ state

    [Fact]
    public void Add_section_and_add_page_use_the_default_layout()
    {
        var state = Blank();
        state.SetDefaultLayout(SectionLayout.FreeFlow, convertExisting: false);
        Assert.Equal(SectionLayout.Structure, state.Working!.Sections[0].Layout);

        var section = state.AddSection()!;
        Assert.Equal(SectionLayout.FreeFlow, section.Layout);
        Assert.Empty(section.Rows);
        Assert.Null(state.AddRow(section.Id));

        var page = state.AddPage()!;
        Assert.Equal(SectionLayout.FreeFlow, page.Layout);
        Assert.True(page.PageBreakBefore);
    }

    [Fact]
    public void Add_item_places_it_in_front_selects_it_and_locates_it()
    {
        var (state, section) = Canvas();
        var a = state.AddItem(section.Id, new TextModule(), 10, 20, 240, 80)!;
        var b = state.AddItem(section.Id, new ImageModule(), 30, 40, 200, 150)!;

        Assert.Equal(b.Module.Id, state.Selection.Id);
        Assert.True(b.Z > a.Z);
        Assert.True(b.LockAspect);
        Assert.False(a.LockAspect);
        var location = state.Locate(a.Module.Id)!;
        Assert.Equal(CreatorSelectionKind.Module, location.Kind);
        Assert.Same(a, location.Item);
        Assert.Null(location.Column);
        Assert.Same(section, location.Section);
        Assert.Null(state.AddItem(section.Id, new PageBreakModule(), 0, 0, 10, 10));
    }

    [Fact]
    public void Move_and_resize_are_one_undo_step_each_and_clamp()
    {
        var (state, section) = Canvas();
        var item = state.AddItem(section.Id, new TextModule(), 10, 20, 240, 80)!;
        var id = item.Module.Id;

        Assert.True(state.MoveItem(id, 100, 200));
        Assert.True(state.ResizeItem(id, 100, 200, 2, 99999));
        var now = state.FindItem(id)!;
        Assert.Equal(PlacedModule.MinSizePt, now.WidthPt);
        Assert.Equal(PlacedModule.MaxPt, now.HeightPt);

        state.Undo();
        now = state.FindItem(id)!;
        Assert.Equal((100.0, 200.0, 240.0, 80.0), (now.XPt, now.YPt, now.WidthPt, now.HeightPt));
        state.Undo();
        now = state.FindItem(id)!;
        Assert.Equal((10.0, 20.0), (now.XPt, now.YPt));
    }

    [Fact]
    public void Held_arrow_nudges_coalesce_into_one_step()
    {
        var (state, section) = Canvas();
        var id = state.AddItem(section.Id, new TextModule(), 10, 10, 100, 50)!.Module.Id;
        state.NudgeItem(id, 1, 0);
        state.NudgeItem(id, 1, 0);
        state.NudgeItem(id, 0, 10);
        Assert.Equal((12.0, 20.0), (state.FindItem(id)!.XPt, state.FindItem(id)!.YPt));
        state.Undo();
        Assert.Equal((10.0, 10.0), (state.FindItem(id)!.XPt, state.FindItem(id)!.YPt));
    }

    [Fact]
    public void Stacking_moves_change_the_paint_order()
    {
        var (state, section) = Canvas();
        var a = state.AddItem(section.Id, new TextModule(), 0, 0, 50, 50)!;
        var b = state.AddItem(section.Id, new TextModule(), 0, 0, 50, 50)!;
        var c = state.AddItem(section.Id, new TextModule(), 0, 0, 50, 50)!;
        string[] Order() => [.. CreatorFreeFlow.PaintOrder(state.Working!.Sections[^1]).Select(i => i.Module.Id)];
        Assert.Equal([a.Module.Id, b.Module.Id, c.Module.Id], Order());

        Assert.True(state.BringForward(a.Module.Id));
        Assert.Equal([b.Module.Id, a.Module.Id, c.Module.Id], Order());
        Assert.True(state.BringToFront(a.Module.Id));
        Assert.Equal([b.Module.Id, c.Module.Id, a.Module.Id], Order());
        Assert.False(state.BringForward(a.Module.Id));
        Assert.True(state.SendToBack(a.Module.Id));
        Assert.Equal([a.Module.Id, b.Module.Id, c.Module.Id], Order());
        Assert.True(state.SendBackward(c.Module.Id));
        Assert.Equal([a.Module.Id, c.Module.Id, b.Module.Id], Order());

        // Alt+Up in the tree (front to back) brings forward.
        Assert.True(state.MoveBy(a.Module.Id, -1));
        Assert.Equal([c.Module.Id, a.Module.Id, b.Module.Id], Order());

        state.Undo();
        Assert.Equal([a.Module.Id, c.Module.Id, b.Module.Id], Order());
        Assert.True(state.SetItemZ(a.Module.Id, 100));
        Assert.Equal(a.Module.Id, Order()[^1]);
    }

    [Fact]
    public void Reorder_by_front_index_and_across_canvases()
    {
        var (state, section) = Canvas();
        var a = state.AddItem(section.Id, new TextModule(), 0, 0, 50, 50)!;
        var b = state.AddItem(section.Id, new TextModule(), 0, 0, 50, 50)!;
        var c = state.AddItem(section.Id, new TextModule(), 0, 0, 50, 50)!;
        string[] Front(Section s) => [.. CreatorFreeFlow.FrontToBack(s).Select(i => i.Module.Id)];
        Assert.Equal([c.Module.Id, b.Module.Id, a.Module.Id], Front(section));

        Assert.True(state.ReorderItem(a.Module.Id, section.Id, 0));
        Assert.Equal([a.Module.Id, c.Module.Id, b.Module.Id], Front(section));
        Assert.False(state.ReorderItem(a.Module.Id, section.Id, 1));   // where it already is
        Assert.True(state.ReorderItem(a.Module.Id, section.Id, 3));
        Assert.Equal([c.Module.Id, b.Module.Id, a.Module.Id], Front(section));

        var other = state.AddSection()!;
        Assert.True(state.ReorderItem(b.Module.Id, other.Id, 0));
        Assert.Single(other.Items);
        Assert.Equal(2, section.Items.Count);
    }

    [Fact]
    public void Duplicate_offsets_the_copy_and_delete_selects_the_section_with_undo()
    {
        var (state, section) = Canvas();
        var item = state.AddItem(section.Id, new HeadingModule { Text = "Hi" }, 40, 50, 300, 48)!;
        state.Duplicate(item.Module.Id);
        var copy = state.FindItem(state.Selection.Id)!;
        Assert.NotEqual(item.Module.Id, copy.Module.Id);
        Assert.Equal((52.0, 62.0, 300.0, 48.0), (copy.XPt, copy.YPt, copy.WidthPt, copy.HeightPt));
        Assert.Equal(item.Module.Id, CreatorFreeFlow.PaintOrder(section)[0].Module.Id);   // the copy paints above

        state.Delete(copy.Module.Id);
        Assert.Equal(CreatorSelectionKind.Section, state.Selection.Kind);
        Assert.Single(state.Working!.Sections[^1].Items);
        state.Undo();
        Assert.Equal(2, state.Working!.Sections[^1].Items.Count);
    }

    [Fact]
    public void Modules_move_between_columns_and_canvases()
    {
        var state = Blank();
        var column = state.Working!.Sections[0].Rows[0].Columns[0];
        var text = new TextModule { Markdown = "x" };
        state.AddModule(text, column.Id);
        state.SetDefaultLayout(SectionLayout.FreeFlow, convertExisting: false);
        var canvas = state.AddSection()!;

        Assert.True(CreatorDrops.Place(state, "module", text.Id, canvas.Id, 30, 40));
        var item = state.FindItem(text.Id)!;
        Assert.Equal((30.0, 40.0, 240.0, 80.0), (item.XPt, item.YPt, item.WidthPt, item.HeightPt));
        Assert.Empty(state.Working.Sections[0].Rows[0].Columns[0].Modules);

        Assert.True(state.Move(text.Id, state.Working.Sections[0].Rows[0].Columns[0].Id, 0));
        Assert.Null(state.FindItem(text.Id));
        Assert.Same(text, state.Working.Sections[0].Rows[0].Columns[0].Modules[0]);

        Assert.True(CreatorDrops.Place(state, "new", "image", canvas.Id, 5, 6));
        var image = state.FindItem(state.Selection.Id)!;
        Assert.Equal((200.0, 150.0), (image.WidthPt, image.HeightPt));
        Assert.True(image.LockAspect);
        Assert.False(CreatorDrops.Place(state, "new", "pagebreak", canvas.Id, 5, 6));
    }

    [Fact]
    public void Insert_with_a_canvas_selected_places_an_item()
    {
        var (state, section) = Canvas();
        state.Select(CreatorSelectionKind.Section, section.Id);
        Assert.True(state.InsertTargetIsFreeFlow);
        state.InsertModule(new TextModule());
        Assert.Single(section.Items);
        state.InsertModule(new PageBreakModule());
        Assert.Single(section.Items);
    }

    [Fact]
    public void Set_section_layout_converts_both_ways_with_one_undo_step_each()
    {
        var state = Blank();
        var section = state.Working!.Sections[0];
        var column = section.Rows[0].Columns[0];
        state.AddModule(new HeadingModule { Text = "Title" }, column.Id);
        state.AddModule(new TextModule { Markdown = "Body" }, column.Id);
        state.AddModule(new PageBreakModule(), column.Id);

        // No page map: the modules stack down the canvas, full width; the page break goes.
        Assert.True(state.SetSectionLayout(section.Id, SectionLayout.FreeFlow));
        section = state.Working.Sections[0];
        Assert.Equal(SectionLayout.FreeFlow, section.Layout);
        Assert.Empty(section.Rows);
        Assert.Equal(2, section.Items.Count);
        var width = CreatorFreeFlow.CanvasWidthPt(state.Working.Theme, section);
        Assert.All(section.Items, i => Assert.Equal(width, i.WidthPt, 3));
        Assert.True(section.Items[1].YPt > section.Items[0].YPt);
        Assert.NotNull(section.CanvasHeightPt);   // the converted section keeps the content's height

        Assert.True(state.SetSectionLayout(section.Id, SectionLayout.Structure));
        section = state.Working.Sections[0];
        Assert.Equal(SectionLayout.Structure, section.Layout);
        Assert.Empty(section.Items);
        Assert.Equal(2, section.Rows.Count);

        state.Undo();
        Assert.Equal(SectionLayout.FreeFlow, state.Working.Sections[0].Layout);
        state.Undo();
        Assert.Equal(SectionLayout.Structure, state.Working.Sections[0].Layout);
        Assert.Equal(3, state.Working.Sections[0].Rows[0].Columns[0].Modules.Count);
    }

    [Fact]
    public void Default_layout_with_convert_all_is_one_step()
    {
        var state = Load("newsletter");
        var count = state.Working!.Sections.Count;
        var modules = CreatorFreeFlow.ModulesOf(state.Working).Count(m => m is not PageBreakModule);
        state.SetDefaultLayout(SectionLayout.FreeFlow, convertExisting: true);
        Assert.Equal(SectionLayout.FreeFlow, state.Working.DefaultLayout);
        Assert.All(state.Working.Sections, s => Assert.Equal(SectionLayout.FreeFlow, s.Layout));
        Assert.Equal(modules, state.Working.Sections.Sum(s => s.Items.Count));
        state.Undo();
        Assert.Equal(SectionLayout.Structure, state.Working.DefaultLayout);
        Assert.All(state.Working.Sections, s => Assert.Equal(SectionLayout.Structure, s.Layout));
        Assert.Equal(count, state.Working.Sections.Count);
    }

    [Fact]
    public void Section_duplicates_get_new_item_ids()
    {
        var (state, section) = Canvas();
        var item = state.AddItem(section.Id, new TextModule(), 0, 0, 50, 50)!;
        state.Duplicate(section.Id);
        var copy = state.Working!.Sections[^1];
        Assert.NotSame(section, copy);
        Assert.NotEqual(item.Module.Id, copy.Items[0].Module.Id);
    }

    // ------------------------------------------------------------------ sheets and canvas HTML

    [Fact]
    public void A_full_page_canvas_is_a_sheet_of_its_own_but_a_sized_canvas_follows_the_pdf()
    {
        var p = new Publication { Theme = BuiltInThemes.Clean() };
        p.Sections.Add(new Section { Rows = [new Row()] });
        p.Sections.Add(new Section { Layout = SectionLayout.FreeFlow, Rows = [] });
        p.Sections.Add(new Section { Rows = [new Row()] });

        // Without a page map a full-page canvas (no height of its own) breaks before and after itself.
        Assert.Equal([(0, 1), (1, 2), (2, 3)], CreatorCanvasHtml.SheetRanges(p));
        var noMap = CreatorCanvasSheets.Build(p, null);
        Assert.Equal(3, noMap.Count);
        Assert.True(noMap[1].Sections.Single().FreeFlow);

        // A canvas with a height sits in the flow like any section: one sheet until the PDF says otherwise.
        p.Sections[1].CanvasHeightPt = 120;
        Assert.Equal([(0, 3)], CreatorCanvasHtml.SheetRanges(p));
        Assert.Single(CreatorCanvasSheets.Build(p, null));

        // With a page map the canvas is on the page the PDF put it on, whatever its height.
        p.Sections[1].CanvasHeightPt = null;
        var packed = p.Sections.ToDictionary(s => s.Id, _ => new PublicationPageSpan(1, 1), StringComparer.Ordinal);
        var sheets = CreatorCanvasSheets.Build(p, packed);
        Assert.Equal([1], sheets.Select(s => s.Page));
        Assert.Equal([0, 1, 2], sheets[0].Sections.Select(s => s.Index));

        var spread = new Dictionary<string, PublicationPageSpan>(StringComparer.Ordinal)
        {
            [p.Sections[0].Id] = new(1, 1),
            [p.Sections[1].Id] = new(2, 2),
            [p.Sections[2].Id] = new(3, 3),
        };
        sheets = CreatorCanvasSheets.Build(p, spread);
        Assert.Equal([1, 2, 3], sheets.Select(s => s.Page));
        Assert.Equal([0, 1, 2], sheets.Select(s => s.Sections.Single().Index));

        // A full-page canvas the render has not placed yet (just added) cannot share the sheet it would land on.
        p.Sections.Add(new Section { Layout = SectionLayout.FreeFlow, Rows = [] });
        sheets = CreatorCanvasSheets.Build(p, spread);
        Assert.Equal([1, 2, 3, 4], sheets.Select(s => s.Page));
    }

    [Fact]
    public void Item_and_canvas_styles_carry_the_box_and_z()
    {
        var item = new PlacedModule { XPt = 12.5, YPt = -4, WidthPt = 200, HeightPt = 3, Z = 7 };
        Assert.Equal("left:12.5pt;top:-4pt;width:200pt;height:8pt;z-index:7;", CreatorCanvasHtml.ItemStyle(item));
        Assert.DoesNotContain("z-index", CreatorCanvasHtml.ItemStyle(item, withZ: false));

        var clip = CreatorCanvasHtml.ItemClipStyle(new ModuleDesign { OffsetXPt = 30, ZIndex = 4, Background = "#FF0000" });
        Assert.Contains("overflow:hidden", clip);
        Assert.Contains("background:", clip);
        Assert.DoesNotContain("left:", clip);
        Assert.DoesNotContain("z-index", clip);

        var theme = BuiltInThemes.Clean();
        var section = new Section { Layout = SectionLayout.FreeFlow, Design = new BoxDesign { Padding = Spacing.All(10) } };
        var css = CreatorCanvasHtml.CanvasStyle(theme, section);
        Assert.Contains("width:" + CreatorCanvasHtml.ItemNumber(CreatorFreeFlow.ContentWidthPt(theme) - 20) + "pt", css);
    }

    // ------------------------------------------------------------------ geometry

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Body_height_matches_the_pdf_composer(bool headerAndFooter)
    {
        var theme = BuiltInThemes.Clean();
        theme.Header.Enabled = headerAndFooter;
        theme.Header.Left = headerAndFooter ? "Title" : "";
        theme.Footer.Enabled = headerAndFooter;
        theme.Footer.Center = headerAndFooter ? "{page}" : "";
        var composer = new ThemedComposer(theme, new Document());
        Assert.Equal(composer.BodyHeightPt, CreatorFreeFlow.BodyHeightPt(theme), 3);
        Assert.Equal(composer.ContentWidthPt, CreatorFreeFlow.ContentWidthPt(theme), 3);
    }

    [Fact]
    public void Canvas_height_is_a_full_page_minus_insets_or_the_custom_height_capped()
    {
        var theme = BuiltInThemes.Clean();
        var section = new Section { Layout = SectionLayout.FreeFlow, Design = new BoxDesign { Margin = Spacing.Vertical(10), BorderWidthPt = 1, Padding = Spacing.All(5) } };
        var full = CreatorFreeFlow.BodyHeightPt(theme) - 20 - 2 - 10;
        Assert.Equal(full, CreatorFreeFlow.CanvasHeightPt(theme, section), 3);
        section.CanvasHeightPt = 120;
        Assert.Equal(120, CreatorFreeFlow.CanvasHeightPt(theme, section));
        section.CanvasHeightPt = 5000;
        Assert.Equal(full, CreatorFreeFlow.CanvasHeightPt(theme, section), 3);

        // The converter's canvas box (what ConvertSection passes as the origin) moves the section's box in by its insets.
        var boxes = new Dictionary<string, IReadOnlyList<PublicationBox>>(StringComparer.Ordinal) { [section.Id] = [new PublicationBox(2, 50, 60, 400, 300)] };
        Assert.Equal(new PublicationBox(2, 56, 76, 388, 268), SectionLayoutConverter.CanvasBox(section, boxes));
    }

    [Theory]
    [InlineData("text", 240, 80)]
    [InlineData("heading", 300, 48)]
    [InlineData("image", 200, 150)]
    [InlineData("divider", 240, 12)]
    [InlineData("spacer", 100, 24)]
    [InlineData("quote", 260, 90)]
    [InlineData("callout", 260, 100)]
    [InlineData("code", 300, 120)]
    [InlineData("table", 320, 120)]
    [InlineData("button", 140, 36)]
    [InlineData("contents", 260, 200)]
    public void Default_sizes_per_type(string type, double w, double h) => Assert.Equal((w, h), CreatorFreeFlow.DefaultSize(type));

    [Fact]
    public void Union_of_block_boxes()
    {
        var u = CreatorFreeFlow.Union([new PublicationBox(1, 10, 20, 100, 10), new PublicationBox(1, 5, 40, 50, 20)]);
        Assert.Equal(new PublicationBox(1, 5, 20, 105, 40), u);
    }

    // ------------------------------------------------------------------ snapping

    [Fact]
    public void Move_snaps_to_canvas_centre_and_other_edges_within_tolerance()
    {
        var others = new[] { new CanvasBox(300, 100, 50, 50) };

        // Box centre 2 pt left of the canvas centre (250): snaps there.
        var r = CreatorSnap.Move(new CanvasBox(198, 400, 100, 40), 500, 700, others, 3);
        Assert.Equal(200, r.Box.X);
        Assert.Equal(250, r.GuideX);

        // Top 1 pt below another item's bottom (150): snaps to it.
        r = CreatorSnap.Move(new CanvasBox(10, 151, 40, 40), 500, 700, others, 3);
        Assert.Equal(150, r.Box.Y);
        Assert.Equal(150, r.GuideY);

        // Too far: no snap.
        r = CreatorSnap.Move(new CanvasBox(20, 160, 40, 40), 500, 700, others, 3);
        Assert.Equal(new CanvasBox(20, 160, 40, 40), r.Box);
        Assert.Null(r.GuideX);
        Assert.Null(r.GuideY);
    }

    [Fact]
    public void Resize_keeps_the_aspect_on_corners_and_the_minimum()
    {
        var start = new CanvasBox(100, 100, 200, 100);
        Assert.Equal(new CanvasBox(100, 100, 260, 100), CreatorSnap.Resize(start, "e", 60, 30, keepAspect: true));
        Assert.Equal(new CanvasBox(100, 100, 260, 130), CreatorSnap.Resize(start, "se", 60, 0, keepAspect: true));
        Assert.Equal(new CanvasBox(100, 100, 260, 100), CreatorSnap.Resize(start, "se", 60, 0, keepAspect: false));
        var nw = CreatorSnap.Resize(start, "nw", 500, 500, keepAspect: false);
        Assert.Equal(new CanvasBox(292, 192, 8, 8), nw);   // the opposite corner stays put

        var snapped = CreatorSnap.SnapResize(new CanvasBox(100, 100, 148, 50), "e", 500, 700, [], 3);
        Assert.Equal(150, snapped.Box.Width);
        Assert.Equal(250, snapped.GuideX);
    }

    // ------------------------------------------------------------------ helpers

    private (CreatorState State, Section Section) Canvas()
    {
        var state = Blank();
        state.SetDefaultLayout(SectionLayout.FreeFlow, convertExisting: false);
        return (state, state.AddSection()!);
    }

    private CreatorState Blank() => Load(BuiltInTemplates.BlankId);

    private CreatorState Load(string template)
    {
        var publications = new FilePublicationStore(new AppPaths(Path.Combine(_root, "data")), NullLogger<FilePublicationStore>.Instance);
        var thumbnails = new CreatorThumbnailCache(new ServiceCollection().BuildServiceProvider(), new PdfiumRasterizer(), publications);
        var state = new CreatorState(publications, thumbnails);
        state.Load(BuiltInTemplates.Find(template)!.Create(), isNew: true, routeId: "new:" + template);
        return state;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
