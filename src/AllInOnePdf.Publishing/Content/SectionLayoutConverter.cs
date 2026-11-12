using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Rendering;
using AllInOnePdf.Core.Themes;
using AllInOnePdf.Publishing.Model;
using AllInOnePdf.Publishing.Rendering;
using AllInOnePdf.Publishing.Validation;

namespace AllInOnePdf.Publishing.Content;

/// <summary>
/// Switches a section between <see cref="SectionLayout.Structure"/> and <see cref="SectionLayout.FreeFlow"/> while
/// keeping its modules. Structure → Free flow places every module where the last render painted it when the render's
/// boxes are given, otherwise stacks them with estimated heights; Free flow → Structure reads the canvas as bands of
/// items that overlap vertically (rows) split into items that overlap horizontally (columns).
/// </summary>
public static class SectionLayoutConverter
{
    /// <summary>Vertical gap between stacked modules when no render boxes are available.</summary>
    public const double StackGapPt = 12;

    /// <summary>
    /// The free-flow canvas of <paramref name="section"/> on the page where the last render started drawing it: the
    /// section's first box (<see cref="PublicationRenderResult.Boxes"/>[section.Id][0]) less its margin, border and
    /// padding (the canvas starts inside the border). Pass it as <c>canvasOrigin</c> to
    /// <see cref="ToFreeFlow(Section, double, PublicationBox?, IReadOnlyDictionary{string, IReadOnlyList{PublicationBox}}?, Publication?)"/>
    /// and its width as <c>canvasWidthPt</c>. Null when the render has no box for the section.
    /// </summary>
    /// <param name="section">The section (its design gives the margin, border and padding).</param>
    /// <param name="boxes">Boxes from the last render, by id.</param>
    public static PublicationBox? CanvasBox(Section section, IReadOnlyDictionary<string, IReadOnlyList<PublicationBox>>? boxes)
    {
        ArgumentNullException.ThrowIfNull(section);
        if (boxes is null || !boxes.TryGetValue(section.Id, out var drawn) || drawn.Count == 0)
        {
            return null;
        }

        var first = drawn[0];
        var design = section.Design ?? new BoxDesign();
        return new PublicationBox(
            first.Page,
            first.XPt + BoxMetrics.Left(design),
            first.YPt + BoxMetrics.Top(design),
            Math.Max(0, first.WidthPt - BoxMetrics.Horizontal(design)),
            Math.Max(0, first.HeightPt - BoxMetrics.Vertical(design)));
    }

    /// <summary>
    /// Turns a Structure section into Free flow. Modules are taken from the rows in reading order and placed on the
    /// canvas; the rows are emptied, page-break modules are dropped (a canvas has no page breaks). Each module's
    /// Position moves onto its item: offsets are baked into the item's place and reset to 0, the Z level becomes the
    /// item's <see cref="PlacedModule.Z"/>. A null <see cref="Section.CanvasHeightPt"/> is set to the height the
    /// content needs (at least the section's height in the render), so converting does not push later content to the
    /// next page. Does nothing when the section is already free flow.
    /// </summary>
    /// <param name="section">The section to convert.</param>
    /// <param name="canvasWidthPt">Width of the section's canvas (its content box) in points.</param>
    /// <param name="canvasOrigin">
    /// Where the canvas's top-left lies on the page, in page coordinates (<see cref="CanvasBox"/>), when
    /// <paramref name="boxes"/> is given; module boxes are made relative to it. Its height, when above 0, is the
    /// least canvas height.
    /// </param>
    /// <param name="boxes">
    /// Boxes from the last render (<see cref="PublicationRenderResult.Boxes"/>), by module id, or null. A module drawn
    /// on several pages keeps its place on its first page and the sum of its heights; a module that starts on a later
    /// page than the canvas is stacked under the items before it.
    /// </param>
    /// <param name="publication">
    /// The publication the section belongs to, for the theme and assets the height estimates use when a module has no
    /// render box (null: the Clean theme).
    /// </param>
    public static void ToFreeFlow(
        Section section,
        double canvasWidthPt,
        PublicationBox? canvasOrigin = null,
        IReadOnlyDictionary<string, IReadOnlyList<PublicationBox>>? boxes = null,
        Publication? publication = null)
    {
        ArgumentNullException.ThrowIfNull(section);
        if (section.Layout == SectionLayout.FreeFlow)
        {
            return;
        }

        var width = double.IsFinite(canvasWidthPt) && canvasWidthPt > PlacedModule.MinSizePt ? canvasWidthPt : PlacedModule.DefaultWidthPt;
        var theme = publication?.Theme ?? BuiltInThemes.Clean();
        var trimmed = TrimmedAtTop(section, theme);
        Estimates? estimates = null;
        var items = new List<PlacedModule>();
        var y = 0.0;
        foreach (var module in PublicationFlattener.Modules(section).ToList())
        {
            if (module is PageBreakModule)
            {
                continue;
            }

            var design = module.Design ??= new ModuleDesign();
            var item = new PlacedModule { Module = module, Z = Math.Clamp(design.ZIndex, 0, BoxDesign.MaxZIndex), LockAspect = module is ImageModule };
            if (canvasOrigin is { } origin && boxes is not null && boxes.TryGetValue(module.Id, out var drawn) && drawn.Count > 0)
            {
                // The render already includes every offset (the module's, its column's, row's and section's).
                var page = drawn.Min(b => b.Page);
                var onFirst = drawn.Where(b => b.Page == page).ToList();
                var left = onFirst.Min(b => b.XPt);
                var top = onFirst.Min(b => b.YPt);
                var right = onFirst.Max(b => b.XPt + b.WidthPt);
                var height = onFirst.Max(b => b.YPt + b.HeightPt) - top
                    + drawn.Where(b => b.Page != page).GroupBy(b => b.Page).Sum(g => g.Max(b => b.YPt + b.HeightPt) - g.Min(b => b.YPt));

                // A free-flow item draws its module exactly as a structure column of that width does, a heading's
                // space-before included, so the drawn box is the item box. The one exception: a heading the structure
                // render pulled up inside its own box (a boxed module at the top of a row whose columns open with
                // different heading spaces) drew above its box; the item starts where that text started.
                var lead = trimmed.Contains(module.Id) && !PublicationLayout.IsFlowModule(module, ModuleBlocks.ToBlocks(module, publication ?? new Publication()))
                    ? LeadingSpace(module, theme, publication)
                    : 0;
                if (lead > 0)
                {
                    top -= lead;
                    height += lead;
                }

                item.XPt = left - origin.XPt;
                item.YPt = page == origin.Page ? top - origin.YPt : y;
                item.WidthPt = right - left;
                item.HeightPt = height;
            }
            else
            {
                estimates ??= new Estimates(publication);
                item.XPt = Offset(design.OffsetXPt);
                item.YPt = y + Offset(design.OffsetYPt);
                item.WidthPt = width;
                item.HeightPt = estimates.Height(module, width);
            }

            item.WidthPt = Math.Clamp(item.WidthPt, PlacedModule.MinSizePt, PlacedModule.MaxPt);
            item.HeightPt = Math.Clamp(item.HeightPt, PlacedModule.MinSizePt, PlacedModule.MaxPt);
            design.OffsetXPt = 0;
            design.OffsetYPt = 0;
            design.ZIndex = 0;

            y = Math.Max(y, item.YPt + item.HeightPt + StackGapPt);
            items.Add(item);
        }

        if (section.CanvasHeightPt is null)
        {
            var bottom = items.Count == 0 ? 0 : items.Max(i => i.YPt + i.HeightPt);
            var least = canvasOrigin is { HeightPt: > 0 and var h } && double.IsFinite(h) ? h : 0;
            section.CanvasHeightPt = Math.Clamp(Math.Ceiling(Math.Max(bottom, least)), PublicationValidator.MinCanvasHeightPt, PlacedModule.MaxPt);
        }

        section.Items = items;
        section.Rows = [];
        section.Layout = SectionLayout.FreeFlow;
    }

    /// <summary>
    /// Turns a Free flow section into Structure. The canvas is read as bands of items whose vertical ranges overlap
    /// (top to bottom), each band a row; inside a band items whose horizontal ranges overlap share a column (left to
    /// right, read top to bottom), column widths in proportion to the columns' extents. A band with more than
    /// <see cref="PublicationValidator.MaxColumns"/> columns merges its narrowest neighbours. An item's Z level becomes
    /// its module's <see cref="BoxDesign.ZIndex"/>. The items are emptied. Does nothing when the section is already structure.
    /// </summary>
    /// <param name="section">The section to convert.</param>
    public static void ToStructure(Section section)
    {
        ArgumentNullException.ThrowIfNull(section);
        if (section.Layout == SectionLayout.Structure)
        {
            return;
        }

        var rows = new List<Row>();
        var placed = (section.Items ?? []).Where(i => i?.Module is not null).ToList();
        foreach (var band in FreeFlowOrder.Bands(placed, i => i, PublicationValidator.MaxColumns))
        {
            var total = band.Sum(c => c.Width);
            var row = new Row { Columns = [], Fractions = [] };
            foreach (var column in band)
            {
                row.Columns.Add(new Column { Modules = [.. column.Items.Select(TakeModule)] });
                row.Fractions.Add(column.Width / total);
            }

            rows.Add(row);
        }

        section.Rows = rows;
        section.Items = [];
        section.Layout = SectionLayout.Structure;
    }

    private static Module TakeModule(PlacedModule item)
    {
        var module = item.Module;
        if (item.Z > 0)
        {
            (module.Design ??= new ModuleDesign()).ZIndex = Math.Min(item.Z, BoxDesign.MaxZIndex);
        }

        return module;
    }

    /// <summary>
    /// The theme space-before of the heading a text or heading module opens with (what a structure row may trim), or 0.
    /// Only text and heading modules trim, like the renderer.
    /// </summary>
    private static double LeadingSpace(Module module, PdfTheme theme, Publication? publication)
    {
        if (module is not (HeadingModule or TextModule))
        {
            return 0;
        }

        var blocks = ModuleBlocks.ToBlocks(module, publication ?? new Publication());
        var space = blocks.Count > 0 && blocks[0] is HeadingBlock h ? theme.Headings.Level(h.Level).SpaceBeforePt : 0;
        return double.IsFinite(space) && space > 0 ? space : 0;
    }

    /// <summary>
    /// Modules the structure render already drew without their leading heading space: the first visible module of
    /// every column of a multi-column row whose columns open with different heading spaces (see the renderer's row
    /// alignment).
    /// </summary>
    private static HashSet<string> TrimmedAtTop(Section section, PdfTheme theme)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in section.Rows ?? [])
        {
            var firsts = (row?.Columns ?? []).Select(c => (c?.Modules ?? []).FirstOrDefault(m => m is not null && !m.Hidden)).ToList();
            if (firsts.Count < 2)
            {
                continue;
            }

            var spaces = firsts.Select(m => m is null ? 0 : LeadingOf(m, theme)).Distinct().Count();
            if (spaces > 1)
            {
                foreach (var m in firsts.OfType<Module>())
                {
                    ids.Add(m.Id);
                }
            }
        }

        return ids;
    }

    private static double LeadingOf(Module module, PdfTheme theme)
    {
        var blocks = ModuleBlocks.ToBlocks(module, new Publication());
        return blocks.Count > 0 && blocks[0] is HeadingBlock h ? theme.Headings.Level(h.Level).SpaceBeforePt : 0;
    }

    private static double Offset(double value) => double.IsFinite(value) ? Math.Clamp(value, -BoxDesign.MaxOffsetPt, BoxDesign.MaxOffsetPt) : 0;

    /// <summary>Module heights the way the renderer estimates them, for modules without a render box.</summary>
    private sealed class Estimates
    {
        /// <summary>Extra room on top of the estimate so text the heuristic undercounts is not cut off at once.</summary>
        private const double SlackPt = 4;

        private readonly Publication _publication;
        private readonly LayoutEstimator _estimator;
        private readonly List<HeadingBlock> _headings;

        public Estimates(Publication? publication)
        {
            _publication = publication ?? new Publication();
            var theme = _publication.Theme ?? BuiltInThemes.Clean();
            var composer = new ThemedComposer(theme, new Core.Documents.Document());
            _estimator = new LayoutEstimator(theme, composer.BodyHeightPt);
            _headings = (_publication.Sections ?? []).Where(s => s is not null).SelectMany(PublicationFlattener.Modules)
                .Where(m => !m.Hidden)
                .SelectMany(m => Block.EnumerateRecursive(ModuleBlocks.ToBlocks(m, _publication))).OfType<HeadingBlock>().ToList();
        }

        public double Height(Module module, double width)
        {
            var design = module.Design ?? new ModuleDesign();
            var inner = Math.Max(10, width - BoxMetrics.Horizontal(design));
            var entries = module is ContentsModule contents ? _headings.Count(h => h.Level <= Math.Clamp(contents.Depth, 1, 6)) : 0;
            var content = _estimator.Module(module, ModuleBlocks.ToBlocks(module, _publication), inner, _publication, showPlaceholders: true, entries);
            var height = BoxMetrics.Vertical(design) + content + SlackPt;
            return double.IsFinite(height) ? Math.Ceiling(height) : PlacedModule.DefaultHeightPt;
        }
    }
}
