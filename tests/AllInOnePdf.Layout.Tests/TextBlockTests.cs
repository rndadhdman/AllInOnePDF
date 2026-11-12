using AllInOnePdf.Layout.Fluent;
using AllInOnePdf.Layout.Infrastructure;
using AllInOnePdf.Layout.Text;
using SkiaSharp;

namespace AllInOnePdf.Layout.Tests;

public class TextBlockTests
{
    private const string Lorem = "The quick brown fox jumps over the lazy dog while the five boxing wizards jump quickly and pack my box with five dozen liquor jugs.";

    private static TextBlock Block(Action<TextDescriptor> content, PageContext? context = null, IDrawingCanvas? canvas = null) =>
        TextHarness.Block(content, context, canvas);

    private static Action<TextDescriptor> Plain(string text, float size = 12, float lineHeight = 1.2f) => t =>
    {
        t.DefaultTextStyle(s => s.FontFamily("Inter").FontSize(size).LineHeight(lineHeight));
        t.Span(text);
    };

    // ------------------------------------------------------------------ measurement

    [Fact]
    public void Height_is_lines_times_size_times_line_height_and_width_fits()
    {
        var block = Block(Plain(Lorem));
        var lines = block.GetLineTexts(200);
        Assert.True(lines.Count >= 3);

        var plan = block.Measure(new Size(200, 1000));
        Assert.Equal(SpacePlanType.FullRender, plan.Type);
        Assert.Equal(lines.Count * 12 * 1.2f, plan.Height, 2);
        Assert.True(plan.Width <= 200);
        Assert.True(plan.Width > 150);
        Assert.Equal(Lorem, string.Concat(lines));
    }

    [Fact]
    public void Measuring_is_repeatable_and_agrees_with_drawing()
    {
        var canvas = new TextRecordingCanvas();
        var block = Block(Plain(Lorem), canvas: canvas);
        var first = block.Measure(new Size(180, 1000));
        var second = block.Measure(new Size(180, 1000));
        Assert.Equal(first, second);

        block.Draw(new Size(180, 1000));
        var lineCount = block.GetLineTexts(180).Count;
        var baselines = canvas.GlyphRuns.Select(r => r.Origin.Y).Distinct().ToList();
        Assert.Equal(lineCount, baselines.Count);
        Assert.True(baselines.Max() < first.Height);
        Assert.Equal(SpacePlanType.Empty, block.Measure(new Size(180, 1000)).Type);
    }

    [Fact]
    public void A_short_line_measures_its_own_width()
    {
        var plan = Block(Plain("Hi")).Measure(new Size(500, 100));
        Assert.True(plan.Width is > 5 and < 30);
        Assert.Equal(14.4f, plan.Height, 2);
    }

    [Fact]
    public void Baseline_splits_the_extra_line_height_evenly_above_and_below()
    {
        // QuestPDF sets half leading: the line box minus the font's ascent + descent is shared equally.
        var canvas = new TextRecordingCanvas();
        var block = Block(Plain("Hello", 10, 2f), canvas: canvas);
        block.Draw(new Size(300, 100));
        var inter = FontRegistry.FindFamily("Inter", 400, false)!.Face.Metrics;
        var expected = (10f * inter.Ascent) + ((20f - (10f * (inter.Ascent + inter.Descent))) / 2);
        Assert.Equal(expected, canvas.GlyphRuns[0].Origin.Y, 2);
    }

    [Fact]
    public void Blocks_without_spans_are_empty_and_whitespace_blocks_are_one_line()
    {
        Assert.Equal(SpacePlanType.Empty, Block(_ => { }).Measure(new Size(100, 100)).Type);

        var space = Block(Plain(" ")).Measure(new Size(100, 100));
        Assert.Equal(SpacePlanType.FullRender, space.Type);
        Assert.Equal(0, space.Width);
        Assert.Equal(14.4f, space.Height, 2);

        var empty = Block(t => t.Span(string.Empty)).Measure(new Size(100, 100));
        Assert.Equal(SpacePlanType.FullRender, empty.Type);
        Assert.True(empty.Height > 0);

        Assert.Equal(SpacePlanType.Wrap, Block(Plain(" ")).Measure(new Size(100, 5)).Type);
    }

    [Fact]
    public void A_glyph_wider_than_the_space_wraps()
    {
        Assert.Equal(SpacePlanType.Wrap, Block(Plain("W", 40)).Measure(new Size(10, 100)).Type);
        Assert.Equal(SpacePlanType.Wrap, Block(Plain("W")).Measure(new Size(0, 100)).Type);
    }

    // ------------------------------------------------------------------ breaking inside blocks

    [Fact]
    public void Lines_break_at_spaces_and_after_hyphens()
    {
        var block = Block(Plain("alpha beta-gamma delta"));
        var wide = block.GetLineTexts(1000);
        Assert.Equal(["alpha beta-gamma delta"], wide);

        var narrow = block.GetLineTexts(Width("alpha beta-") + 1);
        Assert.Equal("alpha beta-", narrow[0]);
        Assert.StartsWith("gamma", narrow[1]);
    }

    [Fact]
    public void A_soft_hyphen_is_invisible_unless_the_line_breaks_there()
    {
        var text = "extra­ordinary";
        var block = Block(Plain(text));
        Assert.Equal(["extraordinary"], block.GetLineTexts(1000));

        var lines = block.GetLineTexts(Width("ordinary") + 1);
        Assert.Equal(["extra-", "ordinary"], lines);

        var canvas = new TextRecordingCanvas();
        var drawn = Block(Plain(text), canvas: canvas);
        drawn.Draw(new Size(Width("ordinary") + 1, 100));
        Assert.Equal(6 + 8, canvas.GlyphRuns.Sum(r => r.Run.GlyphIds.Length));
    }

    [Fact]
    public void Long_words_break_anywhere_as_a_last_resort()
    {
        var lines = Block(Plain("Supercalifragilisticexpialidocious")).GetLineTexts(60);
        Assert.True(lines.Count > 2);
        Assert.Equal("Supercalifragilisticexpialidocious", string.Concat(lines));
        Assert.All(lines, l => Assert.True(Width(l) <= 60.01f));
    }

    [Fact]
    public void BreakAnywhere_fills_lines_to_the_edge()
    {
        var text = "let value = compute(alpha, beta, gamma);";
        var normal = Block(Plain(text)).GetLineTexts(100);
        var anywhere = Block(t =>
        {
            t.DefaultTextStyle(s => s.FontFamily("Inter").BreakAnywhere());
            t.Span(text);
        }).GetLineTexts(100);
        Assert.True(Width(anywhere[0]) > Width(normal[0].TrimEnd()));
        Assert.Equal(text, string.Concat(anywhere));
    }

    [Theory]
    // The space after ")" overflows and hangs: the line keeps ")" (QuestPDF: "… = default)" | "=> await").
    [InlineData(new[] { "ct = default) => x" }, 13, new[] { "ct = default) ", "=> x" })]
    // ";" and the space fit but "/" does not: no break before a space or before "/" after a space, so the line
    // ends after ")" (QuestPDF: "…(path, ct)" | "; // a long").
    [InlineData(new[] { "(path, ct); ", "// a" }, 12, new[] { "(path, ct)", "; // a" })]
    // A span's first character keeps the ordinary rules: no break between "abc" and "d", so "c" moves too.
    [InlineData(new[] { "abc", "d" }, 3, new[] { "ab", "cd" })]
    public void BreakAnywhere_breaks_like_QuestPDF_around_spaces(string[] spans, int columns, string[] expected)
    {
        var lines = Block(t =>
        {
            t.DefaultTextStyle(s => s.FontFamily("JetBrains Mono").FontSize(10).BreakAnywhere());
            foreach (var span in spans)
            {
                t.Span(span);
            }
        }).GetLineTexts((columns * 6) + 0.5f);
        Assert.Equal(expected, lines);
    }

    [Fact]
    public void Hard_breaks_end_lines_and_tabs_are_four_spaces()
    {
        var block = Block(t =>
        {
            t.Span("one\ntwo");
            t.Span("\n");
            t.Span("\tthree");
        });
        Assert.Equal(["one\n", "two\n", "    three"], block.GetLineTexts(500));
        Assert.Equal(3 * 14.4f, block.Measure(new Size(500, 500)).Height, 2);
    }

    [Fact]
    public void A_trailing_hard_break_adds_an_empty_line()
    {
        var block = Block(Plain("end\n"));
        Assert.Equal(2, block.GetLineTexts(500).Count);
        Assert.Equal(2 * 14.4f, block.Measure(new Size(500, 500)).Height, 2);
    }

    // ------------------------------------------------------------------ alignment

    [Fact]
    public void Justify_spreads_every_line_but_the_last_to_the_full_width()
    {
        var canvas = new TextRecordingCanvas();
        var block = Block(t =>
        {
            t.Justify();
            t.DefaultTextStyle(s => s.FontFamily("Inter"));
            t.Span(Lorem);
        }, canvas: canvas);
        block.Draw(new Size(200, 1000));

        var rights = canvas.GlyphRuns.GroupBy(r => r.Origin.Y).OrderBy(g => g.Key).Select(g => g.Max(r => r.Origin.X + r.Run.Width)).ToList();
        Assert.True(rights.Count >= 3, string.Join(" | ", block.GetLineTexts(200).Select(l => l + "=" + Width(l))) + " :: " + string.Join(",", rights));
        foreach (var right in rights.Take(rights.Count - 1))
        {
            Assert.True(Math.Abs(200 - right) < 0.1f, string.Join(" | ", block.GetLineTexts(200).Select(l => l + "=" + Width(l))) + " :: " + string.Join(",", rights));
        }

        Assert.True(rights[^1] < 190);
    }

    [Fact]
    public void Justify_makes_every_word_gap_the_same_width_whatever_the_font_of_its_space()
    {
        // "aa bb cc dd" with a monospaced "bb cc": Skia's paragraph (QuestPDF) gives each gap the same width.
        var canvas = new TextRecordingCanvas();
        var block = Block(t =>
        {
            t.Justify();
            t.DefaultTextStyle(s => s.FontFamily("Inter").FontSize(10));
            t.Span("aa ");
            t.Span("bb cc").FontFamily("JetBrains Mono");
            t.Span(" dd ee ff gg hh ii jj kk ll mm nn oo pp qq rr ss tt uu vv ww xx yy zz");
        }, canvas: canvas);
        block.Draw(new Size(150, 1000));

        // Every glyph of the first line with its character and absolute x; a gap runs from a space glyph (the end of
        // the word before it) to the next word's first glyph.
        var y = canvas.GlyphRuns[0].Origin.Y;
        var glyphs = canvas.GlyphRuns.Where(r => r.Origin.Y == y)
            .SelectMany(r =>
            {
                var source = GlyphRunText.Of(r.Run)!;
                return r.Run.Positions.Select((p, i) => (Char: source.Text[source.Clusters[i]], X: r.Origin.X + p.X));
            })
            .OrderBy(g => g.X)
            .ToList();
        var gaps = new List<float>();
        for (var i = 1; i < glyphs.Count; i++)
        {
            if (glyphs[i].Char != ' ' && glyphs[i - 1].Char == ' ')
            {
                gaps.Add(glyphs[i].X - glyphs[i - 1].X);
            }
        }

        Assert.True(gaps.Count >= 4, string.Join(",", gaps));
        Assert.All(gaps, g => Assert.Equal(gaps[0], g, 2));
    }

    [Fact]
    public void Advances_use_the_size_truncated_to_a_64th_like_Skia()
    {
        // JetBrains Mono: 600 units per 1000; at 9.9 pt Skia advances 600 × 9.890625 / 1000.
        var options = new ShapingOptions(FontRegistry.ResolveChain(["JetBrains Mono"], 400, false), 9.9f, 0, [], 400, false);
        var glyph = TextShaper.Shape("a", options)[0].Glyphs[0];
        Assert.Equal(0.6f * 9.890625f, glyph.Advance, 4);
        Assert.Equal(9.890625f, TextShaper.AdvanceSize(9.9f));
        Assert.Equal(11f, TextShaper.AdvanceSize(11f));
    }

    [Fact]
    public void Justify_leaves_lines_ending_in_a_hard_break_alone()
    {
        var canvas = new TextRecordingCanvas();
        var block = Block(t =>
        {
            t.Justify();
            t.Span("short line\nanother one here");
        }, canvas: canvas);
        block.Draw(new Size(300, 1000));
        var firstRight = canvas.GlyphRuns.Where(r => r.Origin.Y == canvas.GlyphRuns[0].Origin.Y).Max(r => r.Origin.X + r.Run.Width);
        Assert.True(firstRight < 150);
    }

    [Theory]
    [InlineData("center")]
    [InlineData("right")]
    public void Center_and_right_alignment_offset_the_line(string alignment)
    {
        var canvas = new TextRecordingCanvas();
        var block = Block(t =>
        {
            if (alignment == "center")
            {
                t.AlignCenter();
            }
            else
            {
                t.AlignRight();
            }

            t.Span("Hi");
        }, canvas: canvas);
        block.Draw(new Size(300, 100));
        var run = Assert.Single(canvas.GlyphRuns);
        var expectedLeft = alignment == "center" ? (300 - Width("Hi")) / 2 : 300 - Width("Hi");
        Assert.Equal(expectedLeft, run.Origin.X, 1);
    }

    [Fact]
    public void The_first_line_indent_shifts_paragraph_first_lines()
    {
        var canvas = new TextRecordingCanvas();
        var block = Block(t =>
        {
            t.ParagraphFirstLineIndentation(20);
            t.Span(Lorem);
        }, canvas: canvas);
        block.Draw(new Size(200, 1000));
        var byLine = canvas.GlyphRuns.GroupBy(r => r.Origin.Y).OrderBy(g => g.Key).Select(g => g.Min(r => r.Origin.X)).ToList();
        Assert.Equal(20, byLine[0], 2);
        Assert.Equal(0, byLine[1], 2);
    }

    // ------------------------------------------------------------------ clamping

    [Fact]
    public void ClampLines_keeps_the_limit_and_ends_with_an_ellipsis_when_text_was_cut()
    {
        var block = Block(t =>
        {
            t.ClampLines(2);
            t.Span(Lorem);
        });
        var lines = block.GetLineTexts(150);
        Assert.Equal(2, lines.Count);
        Assert.EndsWith("…", lines[1]);
        Assert.True(Width(lines[1]) <= 150.01f);
        Assert.Equal(2 * 14.4f, block.Measure(new Size(150, 1000)).Height, 2);

        var uncut = Block(t =>
        {
            t.ClampLines(1);
            t.Span("short");
        }).GetLineTexts(150);
        Assert.Equal(["short"], uncut);
    }

    [Fact]
    public void A_single_clamped_code_line_is_cut_mid_word()
    {
        var lines = Block(t =>
        {
            t.ClampLines(1, "...");
            t.Span("averyveryverylongidentifier andmore");
        }).GetLineTexts(80);
        var line = Assert.Single(lines);
        Assert.EndsWith("...", line);
        Assert.StartsWith("avery", line);
    }

    // ------------------------------------------------------------------ pagination

    [Fact]
    public void Pagination_draws_whole_lines_and_continues_with_the_rest()
    {
        var canvas = new TextRecordingCanvas();
        var block = Block(Plain(Lorem), canvas: canvas);
        var total = block.GetLineTexts(150).Count;
        Assert.True(total >= 4);

        Assert.Equal(SpacePlanType.Wrap, block.Measure(new Size(150, 10)).Type);

        var space = new Size(150, 14.4f * 2.5f);
        var plan = block.Measure(space);
        Assert.Equal(SpacePlanType.PartialRender, plan.Type);
        Assert.Equal(2 * 14.4f, plan.Height, 2);
        block.Draw(space);
        Assert.Equal(2, canvas.GlyphRuns.Select(r => r.Origin.Y).Distinct().Count());

        canvas.GlyphRuns.Clear();
        var rest = block.Measure(new Size(150, 1000));
        Assert.Equal(SpacePlanType.FullRender, rest.Type);
        Assert.Equal((total - 2) * 14.4f, rest.Height, 2);
        block.Draw(new Size(150, 1000));
        Assert.Equal(total - 2, canvas.GlyphRuns.Select(r => r.Origin.Y).Distinct().Count());
        Assert.True(canvas.GlyphRuns.Min(r => r.Origin.Y) < 14.4f);
        Assert.Equal(SpacePlanType.Empty, block.Measure(new Size(150, 1000)).Type);

        block.Reset();
        Assert.Equal(SpacePlanType.FullRender, block.Measure(new Size(150, 1000)).Type);
    }

    // ------------------------------------------------------------------ page numbers and links

    [Fact]
    public void Page_number_placeholders_are_reshaped_per_page()
    {
        var context = TextHarness.NewContext(page: 3);
        var block = Block(t =>
        {
            t.Span("Page ");
            t.CurrentPageNumber();
            t.Span(" of ");
            t.TotalPages();
            t.Span(" / intro ");
            t.BeginPageNumberOfSection("intro");
        }, context);
        Assert.Equal(["Page 3 of ? / intro ?"], block.GetLineTexts(1000));

        context.SetSectionPage("intro", 2);
        context.BeginPage();
        Assert.Equal(["Page 4 of ? / intro 2"], block.GetLineTexts(1000));

        context.BeginSecondPass(12);
        context.BeginPage();
        block.Initialize(context, FreeCanvas.Instance);
        Assert.Equal(["Page 1 of 12 / intro 2"], block.GetLineTexts(1000));
    }

    [Fact]
    public void Links_mark_one_rectangle_per_line_fragment()
    {
        var canvas = new TextRecordingCanvas();
        var block = Block(t =>
        {
            t.Span("See ");
            t.Hyperlink("the documentation website", "https://example.com");
            t.Span(" or ");
            t.SectionLink("intro", "intro-section");
        }, canvas: canvas);
        block.Draw(new Size(Width("See the documentation") + 1, 1000));

        var external = canvas.Links.Where(l => l.External).ToList();
        Assert.Equal(2, external.Count);
        Assert.All(external, l => Assert.Equal("https://example.com", l.Target));
        Assert.Equal(14.4f, external[0].Size.Height, 2);
        Assert.Equal(14.4f, external[1].Position.Y, 2);
        Assert.Contains(canvas.Links, l => !l.External && l.Target == "intro-section");
    }

    // ------------------------------------------------------------------ inline elements and scripts

    [Fact]
    public void An_inline_element_sits_on_the_baseline_and_raises_the_line()
    {
        var box = new FixedBox(10, 30);
        var canvas = new TextRecordingCanvas();
        var block = Block(t =>
        {
            t.Span("Icon ");
            t.Element().SetChild(box);
            t.Span(" here");
        }, canvas: canvas);
        var plan = block.Measure(new Size(300, 100));
        Assert.True(plan.Height > 30);

        block.Draw(new Size(300, 100));
        Assert.Equal(1, box.Draws);
        var baseline = canvas.GlyphRuns[0].Origin.Y;
        Assert.Equal(30, baseline, 2);
        Assert.Contains(canvas.Translations, p => Math.Abs(p.Y) < 0.01f && p.X > 0);
    }

    [Fact]
    public void Element_only_blocks_keep_the_default_line_metrics()
    {
        var block = Block(t => t.Element().SetChild(new FixedBox(10, 5)));
        Assert.Equal(14.4f, block.Measure(new Size(300, 100)).Height, 1);
    }

    [Fact]
    public void Superscript_and_subscript_shift_the_baseline_and_shrink_the_glyphs()
    {
        var canvas = new TextRecordingCanvas();
        var block = Block(t =>
        {
            t.Span("x");
            t.Span("2").Superscript();
            t.Span("y");
            t.Span("i").Subscript();
        }, canvas: canvas);
        var plain = Block(Plain("xy")).Measure(new Size(300, 100)).Height;
        var plan = block.Measure(new Size(300, 100));
        Assert.True(plan.Height > plain);

        block.Draw(new Size(300, 100));
        var normal = canvas.GlyphRuns[0];
        var sup = canvas.GlyphRuns[1];
        var sub = canvas.GlyphRuns[3];
        Assert.Equal(normal.Origin.Y - (12 * 0.35f), sup.Origin.Y, 2);
        Assert.Equal(normal.Origin.Y + (12 * 0.25f), sub.Origin.Y, 2);
        Assert.Equal(12 * 0.6f, sup.Run.Font.Size, 2);
    }

    // ------------------------------------------------------------------ decorations

    [Fact]
    public void Backgrounds_underlines_and_strikethroughs_are_drawn_for_their_spans()
    {
        var canvas = new TextRecordingCanvas();
        var block = Block(t =>
        {
            t.Span("plain ");
            t.Span("code").BackgroundColor(Color.FromHex("#EEEEEE"));
            t.Span(" under").Underline();
            t.Span(" struck").Strikethrough();
        }, canvas: canvas);
        block.Draw(new Size(500, 100));

        var rect = Assert.Single(canvas.Rectangles);
        Assert.Equal(Width("code"), rect.Size.Width, 1);
        Assert.Equal(14.4f, rect.Size.Height, 2);
        Assert.Equal(0, rect.Position.Y, 2);

        Assert.Equal(2, canvas.Lines.Count);
        var baseline = canvas.GlyphRuns[0].Origin.Y;
        var underline = canvas.Lines[0];
        var strike = canvas.Lines[1];
        Assert.True(underline.From.Y > baseline);
        Assert.True(strike.From.Y < baseline);
        Assert.InRange(underline.Thickness, 0.5f, 1.2f);
    }

    [Fact]
    public void Style_resolution_layers_span_over_block_over_enclosing()
    {
        var previous = TextBlock.EnclosingStyleResolver;
        try
        {
            TextBlock.EnclosingStyleResolver = _ => TextStyle.Default.FontSize(20).FontColor(Color.FromHex("#FF0000"));
            var canvas = new TextRecordingCanvas();
            var block = Block(t =>
            {
                t.DefaultTextStyle(s => s.FontSize(10));
                t.Span("a");
                t.Span("b").FontSize(30);
            }, canvas: canvas);
            block.Draw(new Size(300, 100));
            Assert.Equal(10, canvas.GlyphRuns[0].Run.Font.Size, 2);
            Assert.Equal(30, canvas.GlyphRuns[1].Run.Font.Size, 2);
            Assert.Equal(Color.FromHex("#FF0000"), canvas.GlyphRuns[0].Run.Color);
        }
        finally
        {
            TextBlock.EnclosingStyleResolver = previous;
        }
    }

    // ------------------------------------------------------------------ pixels

    [Fact]
    public void A_paragraph_renders_ink_where_the_lines_are()
    {
        using var bitmap = new SKBitmap(300, 100);
        using (var surface = new SKCanvas(bitmap))
        {
            surface.Clear(SKColors.White);
            var block = Block(t =>
            {
                t.DefaultTextStyle(s => s.FontSize(20).FontColor(Color.Black));
                t.Span("Hello ink");
                t.Span("\nline two").Bold();
            }, canvas: new BitmapCanvas(surface));
            block.Draw(new Size(300, 100));
        }

        var path = Path.Combine(Path.GetTempPath(), "allinonepdf-text-ink.png");
        using (var file = File.Create(path))
        {
            bitmap.Encode(file, SKEncodedImageFormat.Png, 100);
        }

        int Ink(int x0, int y0, int x1, int y1)
        {
            var count = 0;
            for (var y = y0; y < y1; y++)
            {
                for (var x = x0; x < x1; x++)
                {
                    if (bitmap.GetPixel(x, y).Red < 128)
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        var helloWidth = (int)Math.Ceiling(Width("Hello ink", 20));
        Assert.True(Ink(0, 0, helloWidth, 24) > 50, "first line has ink");
        Assert.True(Ink(0, 24, 120, 48) > 50, "second line has ink");
        Assert.Equal(0, Ink(helloWidth + 4, 0, 300, 24));
        Assert.Equal(0, Ink(0, 50, 300, 100));
    }

    [Fact]
    public void Blocks_lay_out_on_parallel_threads()
    {
        Parallel.For(0, 16, i =>
        {
            var block = Block(Plain(Lorem + i, 9 + (i % 4)));
            var plan = block.Measure(new Size(120 + i, 1000));
            Assert.Equal(SpacePlanType.FullRender, plan.Type);
            block.Draw(new Size(120 + i, 1000));
        });
    }

    private static float Width(string text, float size = 12) =>
        TextShaper.Shape(text, new ResolvedTextStyle(TextStyle.Default.FontSize(size)).Shaping).Sum(r => r.Width);
}
