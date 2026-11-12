using AllInOnePdf.Layout.Fluent;
using AllInOnePdf.Layout.Infrastructure;

namespace AllInOnePdf.Layout.Tests;

public class MediaTableTests
{
    private static (MediaRoot Root, MediaRecordingCanvas Canvas, PageContext Context) Build(Action<TableDescriptor> table)
    {
        var root = new MediaRoot();
        root.Table(table);
        var canvas = new MediaRecordingCanvas();
        var context = MediaHarness.Start(root, canvas);
        return (root, canvas, context);
    }

    private static MediaBox Box(TableDescriptor t, float height, string name, Func<ITableCellContainer, ITableCellContainer>? place = null)
    {
        var box = new MediaBox(10, height, name);
        (place?.Invoke(t.Cell()) ?? t.Cell()).SetChild(box);
        return box;
    }

    [Fact]
    public void Constant_columns_come_first_and_relative_columns_share_the_rest_by_weight()
    {
        var (root, canvas, _) = Build(t =>
        {
            t.ColumnsDefinition(c =>
            {
                c.ConstantColumn(50);
                c.RelativeColumn();
                c.RelativeColumn(3);
            });
            Box(t, 10, "a");
            Box(t, 10, "b");
            Box(t, 10, "c");
        });

        var plan = root.Measure(new Size(450, 500));
        Assert.Equal(SpacePlan.FullRender(450, 10), plan);
        root.Draw(new Size(450, 500));

        var marks = canvas.Marks.ToDictionary(m => m.Name);
        Assert.Equal(0, marks["a"].At.X);
        Assert.Equal(50, marks["b"].At.X);
        Assert.Equal(150, marks["c"].At.X);
        Assert.Equal(new Size(50, 10), marks["a"].Size);
        Assert.Equal(new Size(100, 10), marks["b"].Size);
        Assert.Equal(new Size(300, 10), marks["c"].Size);
    }

    [Fact]
    public void Constant_columns_wider_than_the_space_wrap()
    {
        var (root, _, _) = Build(t =>
        {
            t.ColumnsDefinition(c => c.ConstantColumn(200));
            Box(t, 10, "a");
        });

        Assert.Equal(SpacePlanType.Wrap, root.Measure(new Size(100, 100)).Type);
    }

    [Fact]
    public void Cells_flow_into_the_first_free_slot_around_spans_and_pinned_cells()
    {
        var (root, canvas, _) = Build(t =>
        {
            t.ColumnsDefinition(c =>
            {
                c.RelativeColumn();
                c.RelativeColumn();
                c.RelativeColumn();
            });
            Box(t, 10, "a", c => c.ColumnSpan(2));  // row 1, columns 1-2
            Box(t, 10, "b");                         // row 1, column 3
            Box(t, 10, "c");                         // row 2, column 1
            Box(t, 10, "d", c => c.ColumnSpan(3));  // does not fit after c: row 3
            Box(t, 10, "p", c => c.Row(2).Column(3)); // pinned
            Box(t, 10, "e");                         // the cursor is past row 2's free slot: row 4
        });

        root.Measure(new Size(300, 500));
        root.Draw(new Size(300, 500));
        var at = canvas.Marks.ToDictionary(m => m.Name, m => (m.At, m.Size));

        Assert.Equal((new Position(0, 0), new Size(200, 10)), at["a"]);
        Assert.Equal((new Position(200, 0), new Size(100, 10)), at["b"]);
        Assert.Equal((new Position(0, 10), new Size(100, 10)), at["c"]);
        Assert.Equal((new Position(200, 10), new Size(100, 10)), at["p"]);
        Assert.Equal((new Position(0, 20), new Size(300, 10)), at["d"]);
        Assert.Equal((new Position(0, 30), new Size(100, 10)), at["e"]);
    }

    [Fact]
    public void A_row_is_as_tall_as_its_tallest_cell_and_every_cell_is_drawn_that_tall()
    {
        var (root, canvas, _) = Build(t =>
        {
            t.ColumnsDefinition(c =>
            {
                c.RelativeColumn();
                c.RelativeColumn();
                c.RelativeColumn();
            });
            Box(t, 10, "a");
            Box(t, 30, "b");
            Box(t, 20, "c");
            Box(t, 5, "d");
        });

        Assert.Equal(SpacePlan.FullRender(300, 35), root.Measure(new Size(300, 500)));
        root.Draw(new Size(300, 500));
        var marks = canvas.Marks.ToDictionary(m => m.Name);
        Assert.All(new[] { "a", "b", "c" }, n => Assert.Equal(30, marks[n].Size.Height));
        Assert.Equal(new Position(0, 30), marks["d"].At);
        Assert.Equal(5, marks["d"].Size.Height);
    }

    [Fact]
    public void Cells_are_measured_with_their_span_width_and_the_height_left()
    {
        MediaBox? box = null;
        var widths = new List<Size>();
        var (root, _, _) = Build(t =>
        {
            t.ColumnsDefinition(c =>
            {
                c.ConstantColumn(40);
                c.RelativeColumn();
            });
            var probe = new MeasureProbe(widths);
            t.Cell().ColumnSpan(2).SetChild(probe);
            box = Box(t, 10, "x");
        });

        root.Measure(new Size(140, 50));
        Assert.Equal(new Size(140, 50), widths[0]);
        Assert.NotNull(box);
    }

    [Fact]
    public void A_table_taller_than_a_page_breaks_between_rows_and_repeats_the_header()
    {
        var (root, canvas, context) = Build(t =>
        {
            t.ColumnsDefinition(c => c.RelativeColumn());
            t.Header(h => h.Cell().SetChild(new MediaBox(10, 20, "header")));
            for (var i = 0; i < 10; i++)
            {
                Box(t, 30, $"row{i}");
            }
        });

        var page = new Size(100, 200);

        // Page 1: header 20 + six rows of 30 = 200.
        Assert.Equal(SpacePlan.PartialRender(100, 200), root.Measure(page));
        Assert.Equal(SpacePlan.PartialRender(100, 200), root.Measure(page)); // measuring is repeatable
        root.Draw(page);
        var first = canvas.Marks.ToList();
        Assert.Equal(["header", "row0", "row1", "row2", "row3", "row4", "row5"], first.Select(m => m.Name));
        Assert.Equal(0, first[0].At.Y);
        Assert.Equal(20, first[1].At.Y);
        Assert.Equal(170, first[6].At.Y);

        // Page 2: the header again, then the four remaining rows.
        context.BeginPage();
        canvas.Ops.Clear();
        Assert.Equal(SpacePlan.FullRender(100, 140), root.Measure(page));
        root.Draw(page);
        var second = canvas.Marks.ToList();
        Assert.Equal(["header", "row6", "row7", "row8", "row9"], second.Select(m => m.Name));
        Assert.Equal(0, second[0].At.Y);
        Assert.Equal(20, second[1].At.Y);
        Assert.Equal(110, second[4].At.Y);

        // Done.
        context.BeginPage();
        Assert.Equal(SpacePlanType.Empty, root.Measure(page).Type);

        // Reset replays the table from the first row.
        root.Reset();
        Assert.Equal(SpacePlan.PartialRender(100, 200), root.Measure(page));
    }

    [Fact]
    public void A_row_that_does_not_fit_moves_whole_to_the_next_page()
    {
        var (root, _, _) = Build(t =>
        {
            t.ColumnsDefinition(c => c.RelativeColumn());
            Box(t, 30, "a");
        });

        Assert.Equal(SpacePlanType.Wrap, root.Measure(new Size(100, 29)).Type);
        Assert.Equal(SpacePlan.FullRender(100, 30), root.Measure(new Size(100, 30)));
    }

    [Fact]
    public void A_row_taller_than_a_page_is_drawn_clipped_and_the_table_continues()
    {
        var (root, canvas, context) = Build(t =>
        {
            t.ColumnsDefinition(c => c.RelativeColumn());
            Box(t, 50, "a");
            Box(t, 500, "huge");
            Box(t, 50, "c");
        });

        var page = new Size(100, 200);
        Assert.Equal(SpacePlan.PartialRender(100, 50), root.Measure(page));
        root.Draw(page);

        // Page 2: the table continues at the top and the 500 pt row cannot fit any page: clipped, no exception.
        context.BeginPage();
        canvas.Ops.Clear();
        Assert.Equal(SpacePlan.PartialRender(100, 200), root.Measure(page));
        root.Draw(page);
        Assert.Contains(canvas.Ops, o => o.Kind == "clip" && o.Size == new Size(100, 200));
        var huge = Assert.Single(canvas.Marks);
        Assert.Equal(new Size(100, 500), huge.Size);

        context.BeginPage();
        Assert.Equal(SpacePlan.FullRender(100, 50), root.Measure(page));
        root.Draw(page);
        context.BeginPage();
        Assert.Equal(SpacePlanType.Empty, root.Measure(page).Type);
    }

    [Fact]
    public void A_first_row_taller_than_a_page_wraps_once_then_draws_clipped()
    {
        var (root, _, context) = Build(t =>
        {
            t.ColumnsDefinition(c => c.RelativeColumn());
            Box(t, 500, "huge");
        });

        var page = new Size(100, 200);
        Assert.Equal(SpacePlanType.Wrap, root.Measure(page).Type);
        Assert.Equal(SpacePlanType.Wrap, root.Measure(page).Type); // same page, same answer
        context.BeginPage();
        Assert.Equal(SpacePlan.FullRender(100, 200), root.Measure(page));
        root.Draw(page);
        context.BeginPage();
        Assert.Equal(SpacePlanType.Empty, root.Measure(page).Type);
    }

    [Fact]
    public void A_row_span_covers_its_rows_and_stretches_them_when_taller()
    {
        var (root, canvas, _) = Build(t =>
        {
            t.ColumnsDefinition(c =>
            {
                c.RelativeColumn();
                c.RelativeColumn();
            });
            Box(t, 10, "span", c => c.RowSpan(2));
            Box(t, 20, "b");
            Box(t, 30, "c");
            Box(t, 5, "d");
        });

        Assert.Equal(SpacePlan.FullRender(200, 55), root.Measure(new Size(200, 500)));
        root.Draw(new Size(200, 500));
        var marks = canvas.Marks.ToDictionary(m => m.Name);
        Assert.Equal((new Position(0, 0), new Size(100, 50)), (marks["span"].At, marks["span"].Size));
        Assert.Equal((new Position(100, 0), new Size(100, 20)), (marks["b"].At, marks["b"].Size));
        Assert.Equal((new Position(100, 20), new Size(100, 30)), (marks["c"].At, marks["c"].Size));
        Assert.Equal(new Position(0, 50), marks["d"].At);
    }

    [Fact]
    public void A_tall_row_span_pushes_its_last_row_down()
    {
        var (root, canvas, _) = Build(t =>
        {
            t.ColumnsDefinition(c =>
            {
                c.RelativeColumn();
                c.RelativeColumn();
            });
            Box(t, 80, "span", c => c.RowSpan(2));
            Box(t, 20, "b");
            Box(t, 30, "c");
        });

        Assert.Equal(SpacePlan.FullRender(200, 80), root.Measure(new Size(200, 500)));
        root.Draw(new Size(200, 500));
        var marks = canvas.Marks.ToDictionary(m => m.Name);
        Assert.Equal(new Size(100, 60), marks["c"].Size);
    }

    [Fact]
    public void Rows_joined_by_a_row_span_move_to_the_next_page_together()
    {
        var (root, canvas, _) = Build(t =>
        {
            t.ColumnsDefinition(c =>
            {
                c.RelativeColumn();
                c.RelativeColumn();
            });
            Box(t, 30, "a");
            Box(t, 30, "b");
            Box(t, 10, "span", c => c.RowSpan(2));
            Box(t, 30, "c");
            Box(t, 30, "d");
        });

        // 30 + (30 + 30) = 90 does not fit in 70: only the first row goes on this page.
        Assert.Equal(SpacePlan.PartialRender(200, 30), root.Measure(new Size(200, 70)));
        root.Draw(new Size(200, 70));
        Assert.Equal(["a", "b"], canvas.Marks.Select(m => m.Name));
    }

    [Fact]
    public void Extend_last_cells_stretches_short_columns_to_the_table_bottom()
    {
        var (root, canvas, _) = Build(t =>
        {
            t.ColumnsDefinition(c =>
            {
                c.RelativeColumn();
                c.RelativeColumn();
            });
            t.ExtendLastCellsToTableBottom();
            Box(t, 10, "a");
            Box(t, 10, "b");
            Box(t, 40, "c", c => c.Row(2).Column(1));
        });

        root.Measure(new Size(200, 500));
        root.Draw(new Size(200, 500));
        var marks = canvas.Marks.ToDictionary(m => m.Name);
        Assert.Equal(new Size(100, 50), marks["b"].Size);
        Assert.Equal(new Size(100, 40), marks["c"].Size);
    }

    [Fact]
    public void Empty_tables_measure_empty()
    {
        var (root, _, _) = Build(t => t.ColumnsDefinition(c => c.RelativeColumn()));
        Assert.Equal(SpacePlanType.Empty, root.Measure(new Size(100, 100)).Type);
    }

    [Fact]
    public void A_cell_that_does_not_fit_splits_and_its_row_continues_on_the_next_page()
    {
        var lines = new SplitBox(5, 10, "lines");
        var (root, canvas, context) = Build(t =>
        {
            t.ColumnsDefinition(c =>
            {
                c.RelativeColumn();
                c.RelativeColumn();
            });
            t.Header(h => h.Cell().ColumnSpan(2).SetChild(new MediaBox(10, 10, "header")));
            Box(t, 10, "first");
            Box(t, 10, "second");
            t.Cell().SetChild(lines);
            Box(t, 20, "short");
        });

        // Header 10 + first row 10 leave 30: three of the five lines fit and the row is drawn 30 tall.
        var page = new Size(200, 50);
        Assert.Equal(SpacePlan.PartialRender(200, 50), root.Measure(page));
        root.Draw(page);
        var marks = canvas.Marks.ToDictionary(m => m.Name);
        Assert.Equal((new Position(100, 20), new Size(100, 30)), (marks["short"].At, marks["short"].Size));
        Assert.Equal([3], lines.DrawnLines);

        // Next page: the header, then the continuing row: the unfinished cell with its last two lines, and the
        // finished cell laid out again (stateless content draws again; text would measure empty and draw only its box).
        context.BeginPage();
        canvas.Ops.Clear();
        Assert.Equal(SpacePlan.FullRender(200, 30), root.Measure(page));
        root.Draw(page);
        Assert.Equal(["header", "lines", "short"], canvas.Marks.Select(m => m.Name));
        Assert.Equal([3, 2], lines.DrawnLines);
    }

    /// <summary>Lines of a fixed height that paginate like a text block.</summary>
    private sealed class SplitBox(int lines, float lineHeight, string name) : Element
    {
        private int _drawn;

        public List<int> DrawnLines { get; } = [];

        public override void Reset() => _drawn = 0;

        public override SpacePlan Measure(Size availableSpace)
        {
            var left = lines - _drawn;
            if (left <= 0)
            {
                return SpacePlan.Empty();
            }

            var fit = (int)Math.Floor((availableSpace.Height + 0.001f) / lineHeight);
            return fit <= 0 ? SpacePlan.Wrap()
                : fit >= left ? SpacePlan.FullRender(availableSpace.Width, left * lineHeight)
                : SpacePlan.PartialRender(availableSpace.Width, fit * lineHeight);
        }

        public override void Draw(Size availableSpace)
        {
            var plan = Measure(availableSpace);
            var count = (int)Math.Round(plan.Height / lineHeight);
            _drawn += count;
            DrawnLines.Add(count);
            if (Canvas is MediaRecordingCanvas recording)
            {
                recording.Mark(name, availableSpace);
            }
        }
    }

    private sealed class MeasureProbe(List<Size> seen) : Element
    {
        public override SpacePlan Measure(Size availableSpace)
        {
            seen.Add(availableSpace);
            return SpacePlan.FullRender(availableSpace.Width, 5);
        }

        public override void Draw(Size availableSpace)
        {
        }
    }
}
