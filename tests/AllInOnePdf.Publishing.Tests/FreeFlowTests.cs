using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Rendering;
using AllInOnePdf.Core.Themes;
using AllInOnePdf.Publishing.Content;
using AllInOnePdf.Publishing.Model;
using AllInOnePdf.Publishing.Rendering;
using AllInOnePdf.Publishing.Templates;
using AllInOnePdf.Publishing.Validation;
using SkiaSharp;
using UglyToad.PdfPig;

namespace AllInOnePdf.Publishing.Tests;

/// <summary>Position (offsets, Z level) and Free flow in the PDF, the render result's boxes, the flattener and the converter.</summary>
public class FreeFlowTests
{
    private const double Tolerance = 0.05;

    private static readonly PublicationRenderer Renderer = new();

    // ------------------------------------------------------------------ Position

    [Fact]
    public async Task Offset_moves_the_module_box_but_not_its_neighbours()
    {
        async Task<(PublicationRenderResult Result, string Before, string Shifted, string After)> Render(double dx, double dy)
        {
            var publication = Plain();
            var before = new TextModule { Markdown = "Before the shifted box." };
            var shifted = new TextModule { Markdown = "The shifted box.", Design = new ModuleDesign { Background = "#FFF4CC", OffsetXPt = dx, OffsetYPt = dy } };
            var after = new TextModule { Markdown = "After the shifted box." };
            publication.Sections.Add(Single(before, shifted, after));
            return (await Renderer.RenderWithPagesAsync(publication), before.Id, shifted.Id, after.Id);
        }

        var still = await Render(0, 0);
        var moved = await Render(30, 20);

        var a0 = Assert.Single(still.Result.Boxes[still.Before]);
        var a1 = Assert.Single(moved.Result.Boxes[moved.Before]);
        var b0 = Assert.Single(still.Result.Boxes[still.Shifted]);
        var b1 = Assert.Single(moved.Result.Boxes[moved.Shifted]);
        var c0 = Assert.Single(still.Result.Boxes[still.After]);
        var c1 = Assert.Single(moved.Result.Boxes[moved.After]);

        AssertBox(a0, a1);
        AssertBox(c0, c1);
        AssertBox(b0 with { XPt = b0.XPt + 30, YPt = b0.YPt + 20 }, b1);
    }

    [Fact]
    public async Task Row_and_section_offsets_move_everything_inside()
    {
        var publication = Plain();
        var module = new TextModule { Markdown = "Inside" };
        var section = Single(module);
        section.Design.OffsetYPt = 12;
        section.Rows[0].Design.OffsetXPt = 8;
        publication.Sections.Add(section);
        var moved = await Renderer.RenderWithPagesAsync(publication);

        section.Design.OffsetYPt = 0;
        section.Rows[0].Design.OffsetXPt = 0;
        var still = await Renderer.RenderWithPagesAsync(publication);

        var m0 = Assert.Single(still.Boxes[module.Id]);
        AssertBox(m0 with { XPt = m0.XPt + 8, YPt = m0.YPt + 12 }, Assert.Single(moved.Boxes[module.Id]));
        var r0 = Assert.Single(still.Boxes[section.Rows[0].Id]);
        AssertBox(r0 with { XPt = r0.XPt + 8, YPt = r0.YPt + 12 }, Assert.Single(moved.Boxes[section.Rows[0].Id]));
    }

    [Fact]
    public async Task Higher_z_level_paints_over_a_later_box()
    {
        async Task<SKColor> Overlap(int zOfFirst)
        {
            var publication = Plain();
            var red = new SpacerModule { HeightPt = 60, Design = new ModuleDesign { Background = "#FF0000", ZIndex = zOfFirst } };
            var blue = new SpacerModule { HeightPt = 60, Design = new ModuleDesign { Background = "#0000FF", OffsetYPt = -30 } };
            publication.Sections.Add(Single(red, blue));
            var result = await Renderer.RenderWithPagesAsync(publication);

            // Red is 0..60, blue paints at 30..90: sample the middle of the overlap (page points = pixels at 72 dpi).
            var box = Assert.Single(result.Boxes[red.Id]);
            using var bitmap = await Raster(result.Pdf);
            return bitmap.GetPixel((int)(box.XPt + (box.WidthPt / 2)), (int)(box.YPt + 45));
        }

        var normal = await Overlap(0);
        Assert.True(normal.Blue > 200 && normal.Red < 60, $"later box should win at z 0, got {normal}");

        var raised = await Overlap(5);
        Assert.True(raised.Red > 200 && raised.Blue < 60, $"z 5 should paint over the later box, got {raised}");
    }

    // ----------------------------------------------------------------- Free flow

    [Fact]
    public async Task Free_flow_section_renders_one_page_with_every_item_where_it_was_placed()
    {
        var publication = Plain();
        var section = new Section
        {
            Layout = SectionLayout.FreeFlow,
            Design = new BoxDesign { Margin = Spacing.All(10), Padding = Spacing.All(6), BorderWidthPt = 2, Background = "#F5F5F5" },
            Items =
            [
                new PlacedModule { Module = new HeadingModule { Text = "Placed heading", Level = 2 }, XPt = 20, YPt = 0, WidthPt = 300, HeightPt = 56 },
                new PlacedModule { Module = new ImageModule { Alt = "Picture" }, XPt = 0, YPt = 60, WidthPt = 250, HeightPt = 160, Z = 1 },
                new PlacedModule { Module = new TextModule { Markdown = "Next to the picture." }, XPt = 260, YPt = 60, WidthPt = 200, HeightPt = 80 },
                new PlacedModule { Module = new ButtonModule { Text = "Go" }, XPt = 40, YPt = 400, WidthPt = 120, HeightPt = 36 },
                new PlacedModule { Module = new TextModule { Markdown = "Hidden", Hidden = true }, XPt = 0, YPt = 0 },
            ],
        };
        publication.Sections.Add(section);
        PublicationValidator.Normalize(publication);

        var result = await Renderer.RenderWithPagesAsync(publication, new PublicationRenderOptions { ShowPlaceholders = true });
        Assert.Equal(1, result.PageCount);
        Assert.Empty(result.Overflowing);

        var canvas = SectionLayoutConverter.CanvasBox(section, result.Boxes);
        Assert.NotNull(canvas);
        var sectionBox = Assert.Single(result.Boxes[section.Id]);
        Assert.Equal(sectionBox.XPt + 18, canvas.Value.XPt, 3);
        Assert.Equal(sectionBox.YPt + 18, canvas.Value.YPt, 3);

        foreach (var item in section.Items.Where(i => !i.Module.Hidden))
        {
            var box = Assert.Single(result.Boxes[item.Module.Id]);
            AssertBox(new PublicationBox(1, canvas.Value.XPt + item.XPt, canvas.Value.YPt + item.YPt, item.WidthPt, item.HeightPt), box);
            Assert.Equal(new PublicationPageSpan(1, 1), result.Pages[item.Module.Id]);
        }

        Assert.False(result.Boxes.ContainsKey(section.Items[^1].Module.Id));

        using var doc = PdfDocument.Open(result.Pdf);
        var text = doc.GetPage(1).Text;
        Assert.Contains("Placed heading", text);
        Assert.Contains("Next to the picture", text);
        Assert.DoesNotContain("Hidden", text);
    }

    [Fact]
    public async Task Canvas_height_defaults_to_the_body_and_never_exceeds_a_page()
    {
        var publication = Plain();
        var composer = new ThemedComposer(publication.Theme, new Core.Documents.Document());
        var body = composer.BodyHeightPt;

        var tall = new Section { Layout = SectionLayout.FreeFlow, CanvasHeightPt = 2000, Design = new BoxDesign { Margin = Spacing.Vertical(20), Padding = Spacing.All(10) } };
        var full = new Section { Layout = SectionLayout.FreeFlow };
        var shortOne = new Section { Layout = SectionLayout.FreeFlow, CanvasHeightPt = 120 };
        publication.Sections.AddRange([tall, full, shortOne]);

        var result = await Renderer.RenderWithPagesAsync(publication);
        Assert.Equal(3, result.PageCount);

        var tallBox = Assert.Single(result.Boxes[tall.Id]);
        Assert.Equal(1, tallBox.Page);
        Assert.Equal(body, tallBox.HeightPt, 1);
        Assert.Equal(body - 60, LayoutEstimator.CanvasHeight(tall, body), 3);

        var fullBox = Assert.Single(result.Boxes[full.Id]);
        Assert.Equal(2, fullBox.Page);
        Assert.Equal(body, fullBox.HeightPt, 1);

        var shortBox = Assert.Single(result.Boxes[shortOne.Id]);
        Assert.Equal(3, shortBox.Page);
        Assert.Equal(120, shortBox.HeightPt, 1);

        var estimator = new LayoutEstimator(publication.Theme, body);
        Assert.Equal(body, estimator.FreeFlowSection(tall), 3);
        Assert.Equal(120, estimator.FreeFlowSection(shortOne), 3);
    }

    [Fact]
    public async Task Free_flow_section_that_does_not_fit_moves_to_the_next_page_and_breaks_still_work()
    {
        var publication = Plain();
        publication.Sections.Add(Single(new TextModule { Markdown = "Some text first." }));
        var canvas = new Section { Layout = SectionLayout.FreeFlow, Items = [new PlacedModule { Module = new TextModule { Markdown = "On the canvas" } }] };
        publication.Sections.Add(canvas);
        var small = new Section { Layout = SectionLayout.FreeFlow, CanvasHeightPt = 100, PageBreakBefore = true };
        publication.Sections.Add(small);
        publication.Sections.Add(Single(new TextModule { Markdown = "After" }));

        var result = await Renderer.RenderWithPagesAsync(publication);
        Assert.Equal(new PublicationPageSpan(2, 2), result.Pages[canvas.Id]);
        Assert.Equal(new PublicationPageSpan(3, 3), result.Pages[small.Id]);
        Assert.Equal(3, result.Pages[publication.Sections[^1].Id].First);
        Assert.Equal(3, result.PageCount);
    }

    [Fact]
    public async Task Only_a_free_flow_section_renders_exactly_one_page()
    {
        var publication = Plain();
        publication.Sections.Add(new Section { Layout = SectionLayout.FreeFlow });
        var result = await Renderer.RenderWithPagesAsync(publication);
        Assert.Equal(1, result.PageCount);

        // The theme's header and footer leave less room: the full-page canvas still fits one page.
        var themed = new Publication { Name = "Themed", Theme = BuiltInThemes.Clean() };
        themed.Theme.Header.Center = "{title}";
        themed.Theme.Header.ShowOnFirstPage = true;
        themed.Theme.Footer.Center = "{page}";
        themed.Theme.Footer.ShowOnFirstPage = true;
        themed.Sections.Add(new Section { Layout = SectionLayout.FreeFlow, Items = [new PlacedModule { Module = new TextModule { Markdown = "x" }, YPt = 700, HeightPt = 40 }] });
        Assert.Equal(1, (await Renderer.RenderWithPagesAsync(themed)).PageCount);
    }

    [Fact]
    public async Task Item_too_small_for_its_text_is_reported_as_overflowing()
    {
        var publication = Plain();
        var cramped = new TextModule { Markdown = string.Join(' ', Enumerable.Repeat("Far too many words for this box.", 20)) };
        var roomy = new TextModule { Markdown = "Fits." };
        publication.Sections.Add(new Section
        {
            Layout = SectionLayout.FreeFlow,
            Items =
            [
                new PlacedModule { Module = cramped, WidthPt = 120, HeightPt = 30 },
                new PlacedModule { Module = roomy, YPt = 100, WidthPt = 200, HeightPt = 60 },
            ],
        });

        var result = await Renderer.RenderWithPagesAsync(publication);
        Assert.Contains(cramped.Id, result.Overflowing);
        Assert.DoesNotContain(roomy.Id, result.Overflowing);
        var box = Assert.Single(result.Boxes[cramped.Id]);
        Assert.Equal(120, box.WidthPt, 3);
        Assert.Equal(30, box.HeightPt, 3);
    }

    [Fact]
    public async Task Higher_item_z_paints_over_list_order()
    {
        async Task<SKColor> Sample(int zOfFirst)
        {
            var publication = Plain();
            var first = new SpacerModule { HeightPt = 10, Design = new ModuleDesign { Background = "#FF0000" } };
            var second = new SpacerModule { HeightPt = 10, Design = new ModuleDesign { Background = "#0000FF" } };
            publication.Sections.Add(new Section
            {
                Layout = SectionLayout.FreeFlow,
                Items =
                [
                    new PlacedModule { Module = first, XPt = 0, YPt = 0, WidthPt = 100, HeightPt = 100, Z = zOfFirst },
                    new PlacedModule { Module = second, XPt = 50, YPt = 50, WidthPt = 100, HeightPt = 100 },
                ],
            });
            var result = await Renderer.RenderWithPagesAsync(publication);
            var box = result.Boxes[first.Id][0];
            using var bitmap = await Raster(result.Pdf);
            return bitmap.GetPixel((int)(box.XPt + 75), (int)(box.YPt + 75));
        }

        var listOrder = await Sample(0);
        Assert.True(listOrder.Blue > 200 && listOrder.Red < 60, $"the later item should win, got {listOrder}");
        var raised = await Sample(3);
        Assert.True(raised.Red > 200 && raised.Blue < 60, $"the higher z should win, got {raised}");
    }

    // ------------------------------------------------------------- flattener

    [Fact]
    public void Flattener_reads_a_canvas_top_to_bottom_then_left_to_right()
    {
        var publication = new Publication();
        publication.Sections.Add(new Section
        {
            Layout = SectionLayout.FreeFlow,
            Rows = [new Row { Columns = [new Column { Modules = [new TextModule { Markdown = "Ignored row" }] }] }],
            Items =
            [
                new PlacedModule { Module = new TextModule { Markdown = "Footer" }, XPt = 0, YPt = 500, WidthPt = 400, HeightPt = 40 },
                new PlacedModule { Module = new TextModule { Markdown = "Right column" }, XPt = 300, YPt = 10, WidthPt = 200, HeightPt = 300 },
                new PlacedModule { Module = new TextModule { Markdown = "Left body" }, XPt = 0, YPt = 80, WidthPt = 250, HeightPt = 200 },
                new PlacedModule { Module = new HeadingModule { Text = "Left title", Level = 2 }, XPt = 0, YPt = 0, WidthPt = 250, HeightPt = 60 },
                new PlacedModule { Module = new TextModule { Markdown = "Hidden", Hidden = true }, XPt = 0, YPt = 450 },
            ],
        });

        var texts = PublicationFlattener.ToDocument(publication).Blocks.OfType<InlineContainerBlock>().Select(b => b.ToPlainText()).ToList();
        Assert.Equal(["Left title", "Left body", "Right column", "Footer"], texts);
    }

    // ------------------------------------------------------------- converter

    [Fact]
    public void Structure_to_free_flow_and_back_keeps_every_module_in_order()
    {
        var section = TwoRowSection(out var modules);
        var publication = Plain();
        publication.Sections.Add(section);

        SectionLayoutConverter.ToFreeFlow(section, 480, publication: publication);
        Assert.Equal(SectionLayout.FreeFlow, section.Layout);
        Assert.Empty(section.Rows);
        Assert.Equal(modules.Select(m => m.Id), section.Items.Select(i => i.Module.Id));

        // Stacked by estimated height: no overlaps, a long text gets more room than a button, nothing at the 80 pt default.
        for (var i = 1; i < section.Items.Count; i++)
        {
            Assert.True(section.Items[i].YPt >= section.Items[i - 1].YPt + section.Items[i - 1].HeightPt);
        }

        var longText = section.Items.Single(i => i.Module is TextModule t && t.Markdown.Length > 200);
        var button = section.Items.Single(i => i.Module is ButtonModule);
        Assert.True(longText.HeightPt > button.HeightPt * 2);
        Assert.DoesNotContain(section.Items, i => i.HeightPt == PlacedModule.DefaultHeightPt);
        Assert.NotNull(section.CanvasHeightPt);

        SectionLayoutConverter.ToStructure(section);
        Assert.Equal(SectionLayout.Structure, section.Layout);
        Assert.Empty(section.Items);
        Assert.Equal(modules.Select(m => m.Id), section.Rows.SelectMany(r => r.Columns).SelectMany(c => c.Modules).Select(m => m.Id));
        Assert.All(section.Rows, r => Assert.Equal(r.Columns.Count, r.Fractions.Count));
    }

    [Fact]
    public async Task Converting_with_render_boxes_keeps_every_module_where_the_pdf_drew_it()
    {
        var publication = Plain();
        var section = TwoRowSection(out var modules, withBreak: false);
        section.Design = new BoxDesign { Margin = Spacing.All(8), Padding = Spacing.All(10), BorderWidthPt = 1.5, Background = "#FAFAFA" };
        publication.Sections.Add(section);
        PublicationValidator.Normalize(publication);
        var options = new PublicationRenderOptions { ShowPlaceholders = true };

        var structure = await Renderer.RenderWithPagesAsync(publication, options);
        var canvas = SectionLayoutConverter.CanvasBox(section, structure.Boxes)!.Value;
        SectionLayoutConverter.ToFreeFlow(section, canvas.WidthPt, canvas, structure.Boxes, publication);
        Assert.Equal(modules.Select(m => m.Id), section.Items.Select(i => i.Module.Id));

        var free = await Renderer.RenderWithPagesAsync(publication, options);
        Assert.Equal(1, free.PageCount);
        Assert.Empty(free.Overflowing);

        // The free-flow canvas starts where the structure content would start once the border is inside the box.
        Assert.Equal(canvas.XPt, SectionLayoutConverter.CanvasBox(section, free.Boxes)!.Value.XPt, 3);
        Assert.Equal(canvas.YPt, SectionLayoutConverter.CanvasBox(section, free.Boxes)!.Value.YPt, 3);

        foreach (var module in modules.Where(m => m is not HeadingModule))
        {
            var before = Union(structure.Boxes[module.Id]);
            AssertBox(before, Assert.Single(free.Boxes[module.Id]), 0.5);
        }

        // Text lands on the same spot (the heading's box loses its space-before, its words do not move).
        using var a = PdfDocument.Open(structure.Pdf);
        using var b = PdfDocument.Open(free.Pdf);
        foreach (var word in new[] { "Overview", "Paragraph", "Left", "Visit" })
        {
            var wa = a.GetPage(1).GetWords().First(w => w.Text.StartsWith(word, StringComparison.Ordinal)).BoundingBox;
            var wb = b.GetPage(1).GetWords().First(w => w.Text.StartsWith(word, StringComparison.Ordinal)).BoundingBox;
            Assert.Equal(wa.Left, wb.Left, 0.5);
            Assert.Equal(wa.Bottom, wb.Bottom, 0.5);
        }

        SectionLayoutConverter.ToStructure(section);
        Assert.Equal(modules.Select(m => m.Id), section.Rows.SelectMany(r => r.Columns).SelectMany(c => c.Modules).Select(m => m.Id));
        Assert.Equal(2, section.Rows.Count(r => r.Columns.Count == 1));
        var split = Assert.Single(section.Rows, r => r.Columns.Count == 2);
        Assert.Equal(0.5, split.Fractions[0], 1);
    }

    [Theory]
    [InlineData("newsletter", 0, false)]
    [InlineData("newsletter", 1, false)]
    [InlineData("newsletter", 1, true)]
    [InlineData("newsletter", 2, false)]
    [InlineData("newsletter", 3, false)]
    [InlineData("flyer", 0, true)]
    [InlineData("flyer", 1, false)]
    [InlineData("flyer", 2, false)]
    [InlineData("report", 0, false)]
    [InlineData("menu", 0, false)]
    [InlineData("resume", 0, false)]
    [InlineData("invoice", 0, false)]
    [InlineData("invoice", 1, false)]
    public async Task Converted_template_section_paints_every_module_where_the_structure_render_did(string templateId, int sectionIndex, bool placeholders)
    {
        // Exactly what the Creator does: union boxes per module on the canvas page, CanvasBox as the origin.
        var publication = BuiltInTemplates.Find(templateId)!.Create();
        var options = new PublicationRenderOptions { Draft = true, ShowPlaceholders = placeholders };
        var structure = await Renderer.RenderWithPagesAsync(publication, options);
        var section = publication.Sections[sectionIndex];
        var origin = SectionLayoutConverter.CanvasBox(section, structure.Boxes)!.Value;

        // Modules the PDF drew on the canvas page; an image without picture or placeholder draws a 0 pt box, which no
        // item can have (8 pt at least) and which paints nothing either way.
        var modules = PublicationFlattener.Modules(section)
            .Where(m => structure.Boxes.TryGetValue(m.Id, out var drawn) && drawn.Any(b => b.Page == origin.Page && b.HeightPt > 0))
            .ToList();
        var unions = modules.ToDictionary(m => m.Id, m => Union([.. structure.Boxes[m.Id].Where(b => b.Page == origin.Page)]));
        var boxes = unions.ToDictionary(p => p.Key, p => (IReadOnlyList<PublicationBox>)[p.Value]);

        SectionLayoutConverter.ToFreeFlow(section, origin.WidthPt, origin, boxes, publication);
        var free = await Renderer.RenderWithPagesAsync(publication, options);

        var problems = new List<string>();
        foreach (var module in modules)
        {
            var before = unions[module.Id];
            var after = free.Boxes[module.Id][0];
            if (Math.Abs(before.XPt - after.XPt) > 0.5 || Math.Abs(before.YPt - after.YPt) > 0.5
                || Math.Abs(before.WidthPt - after.WidthPt) > 0.5 || Math.Abs(before.HeightPt - after.HeightPt) > 0.5)
            {
                problems.Add($"{module.GetType().Name} box {before} -> {after}");
            }

            if (free.Overflowing.Contains(module.Id))
            {
                problems.Add($"{module.GetType().Name} is cut off");
            }
        }

        // Every word drawn inside the section's modules on that page lands on the same spot (same baseline, same left).
        using var a = PdfDocument.Open(structure.Pdf);
        using var b = PdfDocument.Open(free.Pdf);
        var pageHeight = a.GetPage(origin.Page).Height;
        var wordsAfter = b.GetPage(origin.Page).GetWords().ToList();
        foreach (var word in a.GetPage(origin.Page).GetWords())
        {
            var top = pageHeight - word.BoundingBox.Top;
            var inside = unions.Values.Any(u => word.BoundingBox.Left >= u.XPt - 1 && word.BoundingBox.Left <= u.XPt + u.WidthPt && top >= u.YPt - 1 && top <= u.YPt + u.HeightPt);
            if (inside && !wordsAfter.Any(w => w.Text == word.Text && Math.Abs(w.BoundingBox.Bottom - word.BoundingBox.Bottom) <= 0.5 && Math.Abs(w.BoundingBox.Left - word.BoundingBox.Left) <= 0.5))
            {
                var near = wordsAfter.Where(w => w.Text == word.Text).OrderBy(w => Math.Abs(w.BoundingBox.Bottom - word.BoundingBox.Bottom)).FirstOrDefault();
                problems.Add($"word '{word.Text}' at {word.BoundingBox} moved or is missing (nearest: {near?.BoundingBox.ToString() ?? "none"})");
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public async Task Converted_boxed_heading_the_row_pulled_up_keeps_its_words_in_place()
    {
        // A heading with a box of its own at the top of a row whose columns open differently is drawn with its
        // space-before pulled up inside its box; the converted item starts where that text started.
        var publication = Plain();
        var heading = new HeadingModule { Text = "Boxed title", Level = 1, Design = new ModuleDesign { Background = "#EEF6FF", Padding = Spacing.All(6) } };
        var text = new TextModule { Markdown = "Plain words on the right." };
        var section = new Section { Rows = [new Row { Fractions = [0.5, 0.5], Columns = [new Column { Modules = [heading] }, new Column { Modules = [text] }] }] };
        publication.Sections.Add(section);
        var structure = await Renderer.RenderWithPagesAsync(publication);
        var origin = SectionLayoutConverter.CanvasBox(section, structure.Boxes)!.Value;
        SectionLayoutConverter.ToFreeFlow(section, origin.WidthPt, origin, structure.Boxes, publication);
        var free = await Renderer.RenderWithPagesAsync(publication);
        Assert.Empty(free.Overflowing);
        AssertBox(Union(structure.Boxes[text.Id]), Assert.Single(free.Boxes[text.Id]), 0.5);

        using var a = PdfDocument.Open(structure.Pdf);
        using var b = PdfDocument.Open(free.Pdf);
        foreach (var word in new[] { "Boxed", "title", "Plain" })
        {
            var wa = a.GetPage(1).GetWords().First(w => w.Text == word).BoundingBox;
            var wb = b.GetPage(1).GetWords().First(w => w.Text == word).BoundingBox;
            Assert.Equal(wa.Left, wb.Left, 0.5);
            Assert.Equal(wa.Bottom, wb.Bottom, 0.5);
        }
    }

    [Fact]
    public void To_structure_groups_side_by_side_items_into_columns_and_caps_them_at_twenty()
    {
        var section = new Section { Layout = SectionLayout.FreeFlow };
        for (var i = 0; i < 25; i++)
        {
            section.Items.Add(new PlacedModule { Module = new TextModule { Markdown = "c" + i }, XPt = i * 20, YPt = 0, WidthPt = 18, HeightPt = 40 });
        }

        section.Items.Add(new PlacedModule { Module = new TextModule { Markdown = "under" }, XPt = 0, YPt = 60, WidthPt = 300, HeightPt = 40 });
        section.Items.Add(new PlacedModule { Module = new TextModule { Markdown = "left" }, XPt = 0, YPt = 120, WidthPt = 300, HeightPt = 40 });
        section.Items.Add(new PlacedModule { Module = new TextModule { Markdown = "right" }, XPt = 320, YPt = 110, WidthPt = 100, HeightPt = 40 });
        var ids = section.Items.Select(i => i.Module.Id).ToList();

        SectionLayoutConverter.ToStructure(section);
        Assert.Equal(3, section.Rows.Count);
        Assert.Equal(PublicationValidator.MaxColumns, section.Rows[0].Columns.Count);
        Assert.Equal(1, section.Rows[0].Fractions.Sum(), 6);
        Assert.Single(section.Rows[1].Columns);
        Assert.Equal(["left", "right"], section.Rows[2].Columns.Select(c => ((TextModule)c.Modules.Single()).Markdown));
        Assert.Equal(ids.Count, section.Rows.SelectMany(r => r.Columns).SelectMany(c => c.Modules).Count());
        Assert.Equal(ids.Take(25), section.Rows[0].Columns.SelectMany(c => c.Modules).Select(m => m.Id));
    }

    // -------------------------------------------------------------- template

    [Fact]
    public async Task Poster_template_is_one_free_flow_page_with_a_layered_quote()
    {
        var poster = BuiltInTemplates.Find("poster")!.Create();
        var section = Assert.Single(poster.Sections);
        Assert.Equal(SectionLayout.FreeFlow, section.Layout);
        Assert.Null(section.CanvasHeightPt);
        Assert.Equal(PageSizeName.A4, poster.Theme.Page.Size);
        Assert.Equal(PageOrientation.Portrait, poster.Theme.Page.Orientation);

        var picture = section.Items.Single(i => i.Module is ImageModule { AssetId: null });
        var quote = section.Items.Single(i => i.Module is QuoteModule or CalloutModule);
        Assert.True(quote.Z > picture.Z);
        Assert.True(quote.YPt < picture.YPt + picture.HeightPt && quote.YPt + quote.HeightPt > picture.YPt + picture.HeightPt, "the quote overlaps the picture's bottom edge");
        Assert.Contains(section.Items, i => i.Module is HeadingModule { Level: 1 });
        Assert.Contains(section.Items, i => i.Module is ButtonModule);

        var result = await Renderer.RenderWithPagesAsync(poster, new PublicationRenderOptions { ShowPlaceholders = true });
        Assert.Equal(1, result.PageCount);
        var cut = section.Items.Where(i => result.Overflowing.Contains(i.Module.Id)).Select(i => i.Module.GetType().Name + " " + i.WidthPt + "x" + i.HeightPt + " got " + string.Join(";", result.Boxes.GetValueOrDefault(i.Module.Id) ?? []));
        Assert.True(result.Overflowing.Count == 0, "cut off: " + string.Join(", ", cut));

        // Where the quote overlaps the picture, the quote's white box is on top of the grey placeholder.
        var q = result.Boxes[quote.Module.Id][0];
        var p = result.Boxes[picture.Module.Id][0];
        using var bitmap = await Raster(result.Pdf);
        var x = (int)(q.XPt + 6);
        var y = (int)((q.YPt + p.YPt + p.HeightPt) / 2);
        var color = bitmap.GetPixel(x, y);
        Assert.True(color.Red > 250 && color.Green > 250 && color.Blue > 250, $"expected the quote's white background over the picture, got {color}");
        var grey = bitmap.GetPixel((int)(p.XPt + 20), (int)(p.YPt + 20));
        Assert.True(grey.Red < 250, $"expected the grey placeholder, got {grey}");
    }

    // ---------------------------------------------------------------- helpers

    private static Section TwoRowSection(out List<Module> modules, bool withBreak = true)
    {
        var heading = new HeadingModule { Text = "Overview", Level = 1 };
        var text = new TextModule { Markdown = "Paragraph " + string.Join(' ', Enumerable.Repeat("words that wrap over several lines of the column", 8)) };
        var left = new TextModule { Markdown = "Left column text." };
        var image = new ImageModule { Alt = "Picture", WidthPercent = 100 };
        var button = new ButtonModule { Text = "Visit", Url = "https://example.com" };
        modules = [heading, text, left, image, button];
        return new Section
        {
            Rows =
            [
                new Row { Columns = [new Column { Modules = [heading, text] }] },
                new Row { Fractions = [0.5, 0.5], Columns = [new Column { Modules = [left] }, new Column { Modules = withBreak ? [image, button, new PageBreakModule()] : [image, button] }] },
            ],
        };
    }

    private static PublicationBox Union(IReadOnlyList<PublicationBox> boxes)
    {
        var left = boxes.Min(b => b.XPt);
        var top = boxes.Min(b => b.YPt);
        return new PublicationBox(boxes[0].Page, left, top, boxes.Max(b => b.XPt + b.WidthPt) - left, boxes.Max(b => b.YPt + b.HeightPt) - top);
    }

    private static void AssertBox(PublicationBox expected, PublicationBox actual, double tolerance = Tolerance)
    {
        Assert.Equal(expected.Page, actual.Page);
        Assert.True(Math.Abs(expected.XPt - actual.XPt) <= tolerance, $"x: expected {expected}, got {actual}");
        Assert.True(Math.Abs(expected.YPt - actual.YPt) <= tolerance, $"y: expected {expected}, got {actual}");
        Assert.True(Math.Abs(expected.WidthPt - actual.WidthPt) <= tolerance, $"width: expected {expected}, got {actual}");
        Assert.True(Math.Abs(expected.HeightPt - actual.HeightPt) <= tolerance, $"height: expected {expected}, got {actual}");
    }

    private static async Task<SKBitmap> Raster(byte[] pdf)
    {
        var page = await new PdfiumRasterizer().RasterizePageAsync(pdf, 0, new RasterizeOptions { Dpi = 72 });
        return SKBitmap.Decode(page.Data);
    }

    private static Publication Plain()
    {
        var theme = BuiltInThemes.Clean();
        theme.Header.Left = theme.Header.Center = theme.Header.Right = null;
        theme.Footer.Left = theme.Footer.Center = theme.Footer.Right = null;
        return new Publication { Name = "Test", Theme = theme };
    }

    private static Section Single(params Module[] modules) =>
        new() { Rows = [new Row { Columns = [new Column { Modules = [.. modules] }] }] };
}
