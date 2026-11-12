using System.Text;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Rendering;
using AllInOnePdf.Core.Themes;
using AllInOnePdf.Publishing.Content;
using AllInOnePdf.Publishing.Model;
using AllInOnePdf.Layout.Fluent;
using AllInOnePdf.Layout.Infrastructure;
using QColor = AllInOnePdf.Layout.Infrastructure.Color;
using QDocument = AllInOnePdf.Layout.Fluent.Document;
using QMetadata = AllInOnePdf.Layout.Infrastructure.DocumentMetadata;

namespace AllInOnePdf.Publishing.Rendering;

/// <summary>
/// One layout pass of a publication (one PDF): builds the module blocks once, registers their headings with a
/// <see cref="ThemedComposer"/> and composes sections, rows, columns and modules on a single layout engine page description.
/// </summary>
internal sealed class PublicationLayout
{
    /// <summary>Multi-column rows estimated below this share of the body height are kept on one page.</summary>
    private const double KeepTogetherShare = 0.8;

    /// <summary>Single-column rows estimated below this share of the body height are kept on one page.</summary>
    private const double SingleColumnKeepTogetherShare = 0.35;

    /// <summary>Images and placeholders never get taller than this share of the body height (unbreakable content).</summary>
    private const double MaxImageShare = LayoutEstimator.MaxImageShare;

    private readonly Publication _publication;
    private readonly PublicationRenderOptions _options;
    private readonly CancellationToken _ct;
    private readonly bool _keepRowsTogether;
    private readonly PdfTheme _theme;
    private readonly Core.Documents.Document _context;
    private readonly ThemedComposer _composer;
    private readonly List<SectionPlan> _sections;
    private readonly List<HeadingBlock> _headings;
    private readonly LayoutEstimator _estimator;
    private readonly Dictionary<Block, string> _blockModule = new(ReferenceEqualityComparer.Instance);

    public PublicationLayout(Publication publication, PublicationRenderOptions options, ISyntaxHighlighter? highlighter, bool keepRowsTogether, CancellationToken ct)
    {
        _publication = publication;
        _options = options;
        _ct = ct;
        _keepRowsTogether = keepRowsTogether;
        _sections = Plan(publication);

        _theme = publication.Theme ?? BuiltInThemes.Clean();
        _context = PublicationFlattener.ToDocument(publication);
        _composer = new ThemedComposer(_theme, _context, highlighter, new ThemedComposerOptions
        {
            Draft = options.Draft,
            CancellationToken = ct,
            FallbackTitle = publication.Name,
        });

        // Headings in reading order: structure sections by rows and columns, free-flow canvases top to bottom, then
        // left to right (the order the flattener and the exports use).
        _headings = _sections.SelectMany(s => s.Section.Layout == SectionLayout.FreeFlow
                ? FreeFlowOrder.ReadingOrder(s.Items, i => i.Item).Select(i => i.Module)
                : s.Rows.SelectMany(r => r.Columns).SelectMany(c => c.Modules))
            .SelectMany(m => Block.EnumerateRecursive(m.Blocks)).OfType<HeadingBlock>().ToList();
        _composer.RegisterHeadings(_headings);
        _estimator = new LayoutEstimator(_theme, _composer.BodyHeightPt);
    }

    /// <summary>Lays the publication out: the PDF plus the pages of every section, row, column and module (tracked by id).</summary>
    public PublicationRenderResult Generate()
    {
        var t = _theme;
        var pdf = QDocument.Create(container => container.Page(page =>
        {
            _composer.ConfigurePage(page);

            if (t.Header.HasContent)
            {
                var header = t.Header.ShowOnFirstPage ? page.Header() : page.Header().SkipOnce();
                header.Element(_composer.ComposeHeader);
            }

            if (t.Footer.HasContent)
            {
                var footer = t.Footer.ShowOnFirstPage ? page.Footer() : page.Footer().SkipOnce();
                footer.Element(_composer.ComposeFooter);
            }

            page.Content().Column(ComposeBody);
        }));

        if (_options.EmbedMetadata)
        {
            var meta = _context.Metadata;
            pdf = pdf.WithMetadata(new QMetadata
            {
                Title = _composer.Title,
                Author = string.Join(", ", meta.Authors),
                Subject = meta.Description ?? meta.Subtitle ?? string.Empty,
                Keywords = string.Join(", ", meta.Keywords),
                Creator = "AllInOne PDF Creator",
                Producer = "AllInOne PDF",
                Language = string.IsNullOrWhiteSpace(meta.Language) ? "en" : meta.Language,
                CreationDate = DateTime.Now,
                ModifiedDate = DateTime.Now,
            });
        }

        var bytes = pdf.WithSettings(_composer.CreateDocumentSettings(Math.Clamp(_options.ImageQuality, 1, 100))).GeneratePdf(out var info);

        // Tracking keys carry a kind prefix ("s:", "r:", "c:", "m:"); ids are unique across kinds, so the id alone is the key.
        var pages = new Dictionary<string, PublicationPageSpan>(StringComparer.Ordinal);
        foreach (var (key, span) in info.TrackedPages)
        {
            var id = IdOf(key);
            pages[id] = pages.TryGetValue(id, out var existing)
                ? new PublicationPageSpan(Math.Min(existing.First, span.First), Math.Max(existing.Last, span.Last))
                : new PublicationPageSpan(span.First, span.Last);
        }

        var boxes = new Dictionary<string, List<PublicationBox>>(StringComparer.Ordinal);
        foreach (var (key, drawn) in info.TrackedBoxes)
        {
            var id = IdOf(key);
            if (!boxes.TryGetValue(id, out var list))
            {
                boxes[id] = list = [];
            }

            list.AddRange(drawn.Select(b => new PublicationBox(b.Page, b.X, b.Y, b.Width, b.Height)));
        }

        // A free-flow item whose content does not even start in its box (too small for one line, or for the two lines
        // orphan control asks for) draws nothing, so the engine tracked nothing: report the box it was given anyway.
        foreach (var plan in _sections.Where(s => s.Section.Layout == SectionLayout.FreeFlow && s.Items.Count > 0))
        {
            if (!boxes.TryGetValue(plan.Section.Id, out var sectionBoxes) || sectionBoxes.Count == 0)
            {
                continue;
            }

            var section = sectionBoxes[0];
            var design = plan.Section.Design ?? new BoxDesign();
            var x0 = section.XPt + BoxMetrics.Left(design);
            var y0 = section.YPt + BoxMetrics.Top(design);
            foreach (var (item, m) in plan.Items)
            {
                if (!boxes.ContainsKey(m.Module.Id))
                {
                    boxes[m.Module.Id] = [new PublicationBox(section.Page, x0 + Coordinate(item.XPt), y0 + Coordinate(item.YPt), Size(item.WidthPt, PlacedModule.DefaultWidthPt), Size(item.HeightPt, PlacedModule.DefaultHeightPt))];
                    pages.TryAdd(m.Module.Id, new PublicationPageSpan(section.Page, section.Page));
                }
            }
        }

        return new PublicationRenderResult(bytes, info.PageCount, pages)
        {
            Boxes = boxes.ToDictionary(p => p.Key, p => (IReadOnlyList<PublicationBox>)p.Value, StringComparer.Ordinal),
            Overflowing = new HashSet<string>(info.Overflowing.Select(IdOf), StringComparer.Ordinal),
        };
    }

    /// <summary>The element id of a tracking key ("m:abc" → "abc").</summary>
    private static string IdOf(string key) => key.Length > 2 && key[1] == ':' ? key[2..] : key;

    /// <summary>The tracking key of a block composed in a flow run: its module's id.</summary>
    private string? BlockKey(Block block) => _blockModule.TryGetValue(block, out var id) ? "m:" + id : null;

    // ---------------------------------------------------------------- plan

    private sealed record ModulePlan(Module Module, IReadOnlyList<Block> Blocks);

    private sealed record ColumnPlan(Column Column, double Fraction, List<ModulePlan> Modules);

    private sealed record RowPlan(Row Row, List<ColumnPlan> Columns);

    private sealed record ItemPlan(PlacedModule Item, ModulePlan Module);

    private sealed record SectionPlan(Section Section, List<RowPlan> Rows, List<ItemPlan> Items);

    /// <summary>Visible modules with their blocks, built once so the composed blocks are the registered ones.</summary>
    private static List<SectionPlan> Plan(Publication publication)
    {
        var sections = new List<SectionPlan>();
        foreach (var section in publication.Sections ?? [])
        {
            if (section is null)
            {
                continue;
            }

            var rows = new List<RowPlan>();
            var items = new List<ItemPlan>();
            if (section.Layout == SectionLayout.FreeFlow)
            {
                // Free flow: the items in list order (the paint order among equal z); rows are ignored.
                foreach (var item in section.Items ?? [])
                {
                    if (item?.Module is null || item.Module.Hidden || item.Module is PageBreakModule)
                    {
                        continue;
                    }

                    items.Add(new ItemPlan(item, new ModulePlan(item.Module, ModuleBlocks.ToBlocks(item.Module, publication))));
                }

                sections.Add(new SectionPlan(section, rows, items));
                continue;
            }

            foreach (var row in section.Rows ?? [])
            {
                if (row is null)
                {
                    continue;
                }

                var columns = new List<ColumnPlan>();
                var count = row.Columns?.Count ?? 0;
                var fractions = Fractions(row.Fractions, count);
                for (var i = 0; i < count; i++)
                {
                    var column = row.Columns![i] ?? new Column();
                    var modules = new List<ModulePlan>();
                    foreach (var module in column.Modules ?? [])
                    {
                        if (module is null || module.Hidden)
                        {
                            continue;
                        }

                        modules.Add(new ModulePlan(module, ModuleBlocks.ToBlocks(module, publication)));
                    }

                    columns.Add(new ColumnPlan(column, fractions[i], modules));
                }

                if (columns.Count > 0)
                {
                    rows.Add(new RowPlan(row, columns));
                }
            }

            sections.Add(new SectionPlan(section, rows, items));
        }

        return sections;
    }

    /// <summary>Positive fractions summing to 1, one per column (repairs mismatches the validator would fix).</summary>
    private static double[] Fractions(List<double>? fractions, int count)
    {
        var result = new double[count];
        double sum = 0;
        for (var i = 0; i < count; i++)
        {
            var f = fractions is not null && i < fractions.Count && double.IsFinite(fractions[i]) && fractions[i] > 0 ? fractions[i] : 1.0 / Math.Max(1, count);
            result[i] = f;
            sum += f;
        }

        for (var i = 0; i < count; i++)
        {
            result[i] = sum > 0 ? result[i] / sum : 1.0 / count;
        }

        return result;
    }

    // ---------------------------------------------------------------- body

    private void ComposeBody(ColumnDescriptor col)
    {
        col.Spacing(0);
        var hasContent = false;   // something was placed since the last page break
        var anyContent = false;   // something was placed at all

        for (var s = 0; s < _sections.Count; s++)
        {
            _ct.ThrowIfCancellationRequested();
            var plan = _sections[s];
            var section = plan.Section;

            if ((section.PageBreakBefore || StartsWithBreakingH1(plan)) && hasContent)
            {
                col.Item().PageBreak();
                hasContent = false;
            }

            if (section.Layout == SectionLayout.FreeFlow)
            {
                // A free-flow section is always drawn, empty or not: it is a fixed canvas (one page by default).
                ComposeFreeFlow(col.Item(), plan);
                hasContent = true;
                anyContent = true;
            }
            else if (plan.Rows.Count > 0)
            {
                var design = section.Design ?? new BoxDesign();
                var innerWidth = _composer.ContentWidthPt - Horizontal(design);
                Box(Positioned(col.Item(), design, "s:" + section.Id), design).Column(sc =>
                {
                    foreach (var row in plan.Rows)
                    {
                        ComposeRow(sc.Item(), row, innerWidth);
                    }
                });
                hasContent = true;
                anyContent = true;
                if (EndsWithPageBreak(plan))
                {
                    hasContent = false;
                }
            }

            if (section.PageBreakAfter && hasContent && s < _sections.Count - 1)
            {
                col.Item().PageBreak();
                hasContent = false;
            }
        }

        if (_context.Footnotes.Count > 0)
        {
            _composer.ComposeFootnotes(col.Item());
        }
        else if (!anyContent)
        {
            // The layout engine needs some content to emit a page.
            col.Item().Height(1);
        }
    }

    /// <summary>The theme's "level-1 headings start a page" applies to a section that opens with a level-1 heading module.</summary>
    private bool StartsWithBreakingH1(SectionPlan plan) =>
        _theme.Headings.PageBreakBeforeH1
        && plan.Rows.FirstOrDefault()?.Columns.FirstOrDefault()?.Modules.FirstOrDefault()?.Module is HeadingModule { Level: 1 };

    private static bool EndsWithPageBreak(SectionPlan plan) =>
        plan.Rows.Count > 0 && plan.Rows[^1].Columns.Count == 1 && plan.Rows[^1].Columns[0].Modules.LastOrDefault()?.Module is PageBreakModule;

    private void ComposeRow(IContainer container, RowPlan plan, double availableWidth)
    {
        var row = plan.Row;
        var design = row.Design ?? new BoxDesign();
        var inner = Math.Max(20, availableWidth - Horizontal(design));
        var gutter = Math.Clamp(double.IsFinite(row.GutterPt) ? row.GutterPt : 16, 0, 100);
        var columns = plan.Columns;
        var spacing = columns.Count > 1 ? Math.Min(gutter, inner / (columns.Count * 2)) : 0;
        var share = inner - (spacing * (columns.Count - 1));
        var widths = columns.Select(c => share * c.Fraction).ToArray();

        if (_keepRowsTogether && row.KeepTogether)
        {
            // Side-by-side columns are worth keeping whole up to most of a page; a single column of running text is
            // better split than pushed to the next page with a gap above it, so only short single-column rows are kept.
            var estimate = Vertical(design) + columns.Select((c, i) => EstimateColumn(c, widths[i])).DefaultIfEmpty(0).Max();
            var keepShare = columns.Count > 1 ? KeepTogetherShare : SingleColumnKeepTogetherShare;
            if (estimate < _composer.BodyHeightPt * keepShare)
            {
                container = container.PreventPageBreak();
            }
        }

        // Columns that open with different heading space-before (a heading next to an image or plain text, an H1 next
        // to an H2) would start at different heights: drop the leading space so the row's columns line up at the top.
        var trimTop = columns.Count > 1 && columns.Select(LeadingSpace).Distinct().Skip(1).Any();

        Box(Positioned(container, design, "r:" + row.Id), design).Row(r =>
        {
            r.Spacing((float)spacing);
            for (var i = 0; i < columns.Count; i++)
            {
                var item = r.RelativeItem((float)columns[i].Fraction);
                item = row.Align switch
                {
                    VerticalAlign.Middle => item.AlignMiddle(),
                    VerticalAlign.Bottom => item.AlignBottom(),
                    _ => item,
                };
                ComposeColumn(item, columns[i], widths[i], trimTop);
            }
        });
    }

    /// <summary>The theme space-before of the heading a column opens with, or 0.</summary>
    private double LeadingSpace(ColumnPlan column) =>
        column.Modules is [{ Blocks: [HeadingBlock h, ..] }, ..] ? _theme.Headings.Level(h.Level).SpaceBeforePt : 0;

    private void ComposeColumn(IContainer container, ColumnPlan plan, double width, bool trimTop)
    {
        var design = plan.Column.Design ?? new BoxDesign();
        var inner = Math.Max(10, width - Horizontal(design));
        Box(Positioned(container, design, "c:" + plan.Column.Id), design).Column(cc =>
        {
            // Runs of plain flow modules go through one ComposeBlocks call so headings keep with the next module and
            // paragraphs get orphan control across module boundaries; each block is page-tracked under its module.
            var run = new List<Block>();
            var runAtTop = false;
            void Flush()
            {
                if (run.Count > 0)
                {
                    var blocks = run.ToArray();
                    _composer.ComposeBlocks(TrimLeadingSpace(cc.Item(), blocks, runAtTop), blocks, inner, null, BlockKey);
                    run.Clear();
                }
            }

            var modules = plan.Modules;
            for (var i = 0; i < modules.Count; i++)
            {
                var m = modules[i];
                ApplyAlignment(m);
                if (IsFlowModule(m))
                {
                    // A level-1 heading only page-breaks inside a flow; start a new flow so it never does mid-column.
                    if (m.Module is HeadingModule { Level: 1 })
                    {
                        Flush();
                    }

                    if (run.Count == 0)
                    {
                        runAtTop = trimTop && i == 0;
                    }

                    foreach (var block in m.Blocks)
                    {
                        _blockModule[block] = m.Module.Id;
                    }

                    run.AddRange(m.Blocks);
                    continue;
                }

                Flush();
                var next = i + 1 < modules.Count ? modules[i + 1] : null;
                ComposeModule(cc, m, inner, next, atTop: trimTop && i == 0);
            }

            Flush();
        });
    }

    /// <summary>
    /// A heading at the very top of a column drops its theme space-before (negative padding cancels it), so columns of
    /// a row line up at the top.
    /// </summary>
    private IContainer TrimLeadingSpace(IContainer item, IReadOnlyList<Block> blocks, bool atTop)
    {
        if (!atTop || blocks.Count == 0 || blocks[0] is not HeadingBlock h)
        {
            return item;
        }

        var space = _theme.Headings.Level(h.Level).SpaceBeforePt;
        return space > 0 && double.IsFinite(space) ? item.PaddingTop(-(float)space) : item;
    }

    /// <summary>Text-like modules without any box or text styling of their own.</summary>
    private static bool IsFlowModule(ModulePlan m) => IsFlowModule(m.Module, m.Blocks);

    /// <summary>
    /// Text-like modules without any box, text styling or Position of their own: a structure column composes a run of
    /// them as one flow (tracked per block); every other module is composed on its own, inside its tracked box.
    /// </summary>
    internal static bool IsFlowModule(Module module, IReadOnlyList<Block> blocks)
    {
        if (module is not (TextModule or HeadingModule or QuoteModule or CalloutModule or CodeModule or TableModule) || blocks.Count == 0)
        {
            return false;
        }

        var d = module.Design;
        if (d is null)
        {
            return true;
        }

        return IsZero(d.Margin) && IsZero(d.Padding) && string.IsNullOrWhiteSpace(d.Background) && !HasBorder(d) && d.CornerRadiusPt <= 0
            && string.IsNullOrWhiteSpace(d.TextColor) && Math.Abs(d.FontScale - 1) < 0.001 && !HasPosition(d);
    }

    /// <summary>Text alignment override: written onto the module's own paragraphs and headings.</summary>
    private static void ApplyAlignment(ModulePlan m)
    {
        if (m.Module.Design?.Alignment is not { } align || m.Module is ButtonModule)
        {
            return;
        }

        foreach (var p in Paragraphs(m.Blocks))
        {
            p.Alignment = align;
        }

        foreach (var h in m.Blocks.OfType<HeadingBlock>())
        {
            h.Alignment = align;
        }
    }

    private static IEnumerable<ParagraphBlock> Paragraphs(IEnumerable<Block> blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case ParagraphBlock p:
                    yield return p;
                    break;
                case QuoteBlock or CalloutBlock or ListBlock:
                    foreach (var inner in Paragraphs(block.Children))
                    {
                        yield return inner;
                    }

                    break;
            }
        }
    }

    // ------------------------------------------------------------- modules

    private void ComposeModule(ColumnDescriptor cc, ModulePlan m, double columnWidth, ModulePlan? next, bool atTop)
    {
        var module = m.Module;
        if (module is PageBreakModule)
        {
            cc.Item().PageBreak();
            return;
        }

        var design = module.Design ?? new ModuleDesign();
        var width = Math.Max(10, columnWidth - Horizontal(design));
        var item = cc.Item();

        // A heading drawn on its own keeps with the module after it: it needs room for itself and two lines.
        if (module is HeadingModule heading && next is not null && _theme.Headings.KeepWithNext)
        {
            var need = _estimator.Heading(heading.Level, PlainText(m.Blocks), width) + (2 * _estimator.BodyLine) + Vertical(design);
            item = item.EnsureSpace((float)Math.Min(need, _composer.BodyHeightPt * 0.6));
        }

        ComposeModuleContent(Box(Positioned(item, design, "m:" + module.Id), design), m, width, atTop);
    }

    /// <summary>A module's content inside its (already drawn) box, <paramref name="width"/> wide.</summary>
    private void ComposeModuleContent(IContainer item, ModulePlan m, double width, bool atTop)
    {
        var module = m.Module;
        var design = module.Design ?? new ModuleDesign();
        var scale = Math.Clamp(double.IsFinite(design.FontScale) && design.FontScale > 0 ? design.FontScale : 1, 0.5, 3);
        var color = string.IsNullOrWhiteSpace(design.TextColor) ? null : design.TextColor;
        var textStyle = color is not null || Math.Abs(scale - 1) > 0.001 ? new BlockTextStyle(scale, color) : null;

        if (module is HeadingModule or TextModule)
        {
            item = TrimLeadingSpace(item, m.Blocks, atTop);
        }

        switch (module)
        {
            case SpacerModule spacer:
                item.Height((float)Math.Clamp(spacer.HeightPt, 0, _composer.BodyHeightPt - 2));
                break;

            case DividerModule divider:
                ComposeDivider(item, divider);
                break;

            case ButtonModule button:
                ComposeButton(item, button, scale);
                break;

            case ContentsModule contents:
            {
                var depth = Math.Clamp(contents.Depth, 1, 6);
                var entries = _headings.Where(h => h.Level <= depth).ToList();
                _composer.ComposeToc(item, entries, string.IsNullOrWhiteSpace(contents.Title) ? null : contents.Title, contents.ShowPageNumbers);
                break;
            }

            case ImageModule image:
                ComposeImage(item, image, width);
                break;

            default:
                if (m.Blocks.Count > 0)
                {
                    _composer.ComposeBlocks(item, m.Blocks, width, textStyle);
                }

                break;
        }
    }

    private void ComposeDivider(IContainer item, DividerModule divider)
    {
        var thickness = (float)Math.Clamp(double.IsFinite(divider.ThicknessPt) ? divider.ThicknessPt : 0.75, 0.25, 20);
        var color = Hex(string.IsNullOrWhiteSpace(divider.Color) ? _theme.Colors.Border : divider.Color, "#D1D5DB");
        var gap = (float)Math.Max(6, _theme.Typography.ParagraphSpacingPt);
        Percent(item.PaddingVertical(gap), divider.WidthPercent, divider.Align, c =>
        {
            var line = c.LineHorizontal(thickness).LineColor(color);
            switch (divider.Style)
            {
                case DividerStyle.Dashed:
                    line.LineDashPattern([thickness * 4, thickness * 3]);
                    break;
                case DividerStyle.Dotted:
                    line.LineDashPattern([thickness, thickness * 2]);
                    break;
            }
        });
    }

    private void ComposeButton(IContainer item, ButtonModule button, double scale)
    {
        var accentHex = string.IsNullOrWhiteSpace(button.Color) ? _theme.Colors.Accent : button.Color;
        var accent = Hex(accentHex, "#0F6CBD");
        var radius = (float)Math.Clamp(double.IsFinite(button.CornerRadiusPt) ? button.CornerRadiusPt : 4, 0, 40);
        var label = string.IsNullOrWhiteSpace(button.Text) ? "Button" : button.Text.Trim();

        var box = Align(item.PaddingBottom((float)_theme.Typography.ParagraphSpacingPt), button.Align);
        if (SafeUrl(button.Url) is { } url)
        {
            box = box.Hyperlink(url);
        }

        QColor textColor;
        switch (button.Style)
        {
            case ButtonStyle.Outline:
                box = box.Border(1).BorderColor(accent);
                if (radius > 0)
                {
                    box = box.CornerRadius(radius);
                }

                box = box.PaddingVertical(6).PaddingHorizontal(14);
                textColor = accent;
                break;

            case ButtonStyle.Link:
                box = box.PaddingVertical(2);
                textColor = accent;
                break;

            default:
                box = box.Background(accent);
                if (radius > 0)
                {
                    box = box.CornerRadius(radius);
                }

                box = box.PaddingVertical(6).PaddingHorizontal(14);
                textColor = QColor.FromHex(ThemeDrawing.IsDark(accentHex) ? "#FFFFFF" : "#111111");
                break;
        }

        var span = box.Text(label)
            .FontFamily(ThemeDrawing.FontChain(_theme.Typography.BodyFont))
            .FontSize((float)(_theme.Typography.BodySizePt * scale))
            .SemiBold()
            .FontColor(textColor)
            .LineHeight(1.2f);
        if (button.Style == ButtonStyle.Link && _theme.Links.Underline)
        {
            span.Underline();
        }
    }

    private void ComposeImage(IContainer item, ImageModule image, double width)
    {
        var asset = _publication.FindAsset(image.AssetId);
        var data = asset?.Data is { Length: > 0 } bytes ? bytes : null;
        if (data is null && !_options.ShowPlaceholders)
        {
            return;
        }

        var maxHeight = Math.Min(image.MaxHeightPt is double mh && mh > 0 && double.IsFinite(mh) ? mh : double.MaxValue, _composer.BodyHeightPt * MaxImageShare);
        var percent = Math.Clamp(double.IsFinite(image.WidthPercent) ? image.WidthPercent : 100, 5, 100);
        var radius = (float)Math.Clamp(double.IsFinite(image.CornerRadiusPt) ? image.CornerRadiusPt : 0, 0, 200);
        var muted = Hex(_theme.Colors.Muted, "#6B7280");
        var caption = string.IsNullOrWhiteSpace(image.Caption) || !_theme.Images.ShowCaptions ? null : image.Caption.Trim();

        var isSvg = data is not null && IsSvg(asset!, data);
        var (pw, ph) = data is null ? (3, 2)
            : asset!.PixelWidth is > 0 && asset.PixelHeight is > 0 ? (asset.PixelWidth.Value, asset.PixelHeight.Value)
            : isSvg ? (0, 0) : ImageHeader.ReadPixelSize(data);
        var ratio = pw > 0 && ph > 0 ? (double)pw / ph : 0;
        if (data is null)
        {
            // Placeholders stand in for any picture: 3:2, but never taller than about a third of the body so a template's
            // empty image does not push the rest of the page over.
            ratio = PlaceholderRatio(width * percent / 100, maxHeight);
        }
        else if (ratio > 0 && width * percent / 100 / ratio > maxHeight)
        {
            // Narrow the box so the height fits; keeps the aspect ratio and never overflows the page.
            percent = Math.Max(5, maxHeight * ratio / width * 100);
        }

        item.PaddingBottom((float)_theme.Images.SpacingPt).Column(col =>
        {
            Percent(col.Item(), percent, image.Align, holder =>
            {
                var box = ratio > 0 ? holder.AspectRatio((float)ratio, AspectRatioOption.FitWidth) : holder.MaxHeight((float)maxHeight);
                if (data is null)
                {
                    box.Background(Hex(_theme.Colors.Surface, "#F3F4F6"))
                        .Border(0.75f).BorderColor(Hex(_theme.Colors.Border, "#D1D5DB"))
                        .CornerRadius(Math.Max(radius, 4))
                        .AlignMiddle().AlignCenter()
                        .Text("Image").FontSize((float)(_theme.Typography.BodySizePt * 0.95)).FontColor(muted);
                    return;
                }

                if (radius > 0)
                {
                    box = box.CornerRadius(radius);
                }

                if (isSvg)
                {
                    box.Svg(Encoding.UTF8.GetString(data)).FitArea();
                    return;
                }

                try
                {
                    box.Image(data).FitArea();
                }
                catch (Exception)
                {
                    box.Background(Hex(_theme.Colors.Surface, "#F3F4F6")).Padding(12).AlignCenter()
                        .Text(image.Alt ?? "Unsupported image").Italic().FontColor(muted);
                }
            });

            if (caption is not null)
            {
                var cap = col.Item().PaddingTop(4);
                cap = image.Align switch
                {
                    BlockAlign.Left => cap.AlignLeft(),
                    BlockAlign.Right => cap.AlignRight(),
                    _ => cap.AlignCenter(),
                };
                cap.Text(caption).FontSize((float)_theme.Images.CaptionSizePt).Italic().FontColor(muted);
            }
        });
    }

    private double PlaceholderRatio(double boxWidth, double maxHeight) => boxWidth / _estimator.PlaceholderHeight(boxWidth, maxHeight);

    // ----------------------------------------------------------- free flow

    /// <summary>
    /// A free-flow section: its box (margin, background, border, radius, padding, Position) around a fixed canvas as
    /// wide as the section's content box and <see cref="LayoutEstimator.CanvasHeight"/> tall, the canvas inset by the
    /// border width too (the canvas starts inside the border, as on the design canvas). Each visible item is placed at
    /// its x, y, width, height and z and tracked as "m:" + id; an item whose content does not fit is reported as
    /// overflowing. The canvas never paginates: when it does not fit the rest of a page it moves to the next one.
    /// </summary>
    private void ComposeFreeFlow(IContainer container, SectionPlan plan)
    {
        var section = plan.Section;
        var design = section.Design ?? new BoxDesign();
        var height = LayoutEstimator.CanvasHeight(section, _composer.BodyHeightPt);
        var canvas = Box(Positioned(container, design, "s:" + section.Id), design);
        if (HasBorder(design))
        {
            canvas = canvas.Padding((float)design.BorderWidthPt);
        }

        canvas.Height((float)height).Freeform(f =>
        {
            foreach (var (item, m) in plan.Items)
            {
                var w = Size(item.WidthPt, PlacedModule.DefaultWidthPt);
                var h = Size(item.HeightPt, PlacedModule.DefaultHeightPt);
                var x = Coordinate(item.XPt);
                var y = Coordinate(item.YPt);
                var key = "m:" + m.Module.Id;
                ApplyAlignment(m);
                ComposeFreeModule(f.Item(x, y, w, h, Math.Clamp(item.Z, 0, BoxDesign.MaxZIndex), key).TrackPages(key), m, w, h);
            }
        });
    }

    /// <summary>
    /// A module in a free-flow item box: its design box (the design's offsets and Z level are ignored, the item box
    /// places it) and its content composed exactly as in a structure column of the box's width, a leading heading's
    /// theme space-before included (nothing is trimmed or shifted), so a section converted from structure with the
    /// render's boxes paints every word where the structure render did. Images fill the box (see <see cref="ComposeFreeImage"/>).
    /// </summary>
    private void ComposeFreeModule(IContainer box, ModulePlan m, double width, double height)
    {
        var design = m.Module.Design ?? new ModuleDesign();
        var inner = Box(box, design);
        var innerWidth = Math.Max(1, width - Horizontal(design));
        if (m.Module is ImageModule image)
        {
            ComposeFreeImage(inner, image, innerWidth, Math.Max(1, height - Vertical(design)));
            return;
        }

        ComposeModuleContent(inner, m, innerWidth, atTop: false);
    }

    /// <summary>
    /// An image in a free-flow box: the theme's image spacing stays at the bottom (when the box has room), the picture
    /// (or the placeholder) takes the rest of the height (with a caption: at most the height a structure column gives
    /// it, so the caption sits right under it), and the module's width percent, alignment and maximum height; a
    /// picture keeps its aspect ratio inside that area (<c>FitArea</c>), a placeholder fills it.
    /// </summary>
    private void ComposeFreeImage(IContainer item, ImageModule image, double width, double height)
    {
        var asset = _publication.FindAsset(image.AssetId);
        var data = asset?.Data is { Length: > 0 } bytes ? bytes : null;
        if (data is null && !_options.ShowPlaceholders)
        {
            return;
        }

        var muted = Hex(_theme.Colors.Muted, "#6B7280");
        var radius = (float)Math.Clamp(double.IsFinite(image.CornerRadiusPt) ? image.CornerRadiusPt : 0, 0, 200);
        var percent = Math.Clamp(double.IsFinite(image.WidthPercent) ? image.WidthPercent : 100, 5, 100);
        var caption = string.IsNullOrWhiteSpace(image.Caption) || !_theme.Images.ShowCaptions ? null : image.Caption.Trim();
        var captionHeight = (_theme.Images.CaptionSizePt * 1.25) + 4; // one caption line plus its gap, never more than drawn
        if (caption is not null && height < captionHeight * 2)
        {
            caption = null; // no room for a caption under a picture: the picture wins
        }

        // The theme's image spacing stays under the picture, as in a structure column, when the box has room for it.
        var spacing = Math.Max(0, _theme.Images.SpacingPt);
        if (spacing > 0 && height - (caption is null ? 0 : captionHeight) > spacing * 3)
        {
            height -= spacing;
            item = item.PaddingBottom((float)spacing);
        }

        var pictureHeight = Math.Max(1, height - (caption is null ? 0 : captionHeight));
        var maxHeight = image.MaxHeightPt is double mh && mh > 0 && double.IsFinite(mh) ? mh : double.MaxValue;
        pictureHeight = Math.Min(pictureHeight, maxHeight);
        if (caption is not null)
        {
            // With a caption the picture keeps the height a structure column gives it, so the caption sits right under
            // it as there (a converted section draws it on the same spot); it only shrinks when the box is smaller.
            var boxWidth = width * percent / 100;
            var cap = Math.Min(maxHeight, _composer.BodyHeightPt * MaxImageShare);
            var (pw, ph) = data is null ? (0, 0)
                : asset!.PixelWidth is > 0 && asset.PixelHeight is > 0 ? (asset.PixelWidth.Value, asset.PixelHeight.Value)
                : IsSvg(asset, data) ? (0, 0) : ImageHeader.ReadPixelSize(data);
            var natural = data is null ? _estimator.PlaceholderHeight(boxWidth, cap)
                : pw > 0 && ph > 0 ? Math.Min(boxWidth * ph / pw, cap) : pictureHeight;
            pictureHeight = Math.Max(1, Math.Min(pictureHeight, natural));
        }

        item.Column(col =>
        {
            Percent(col.Item().Height((float)pictureHeight), percent, image.Align, holder =>
            {
                if (data is null)
                {
                    holder.Background(Hex(_theme.Colors.Surface, "#F3F4F6"))
                        .Border(0.75f).BorderColor(Hex(_theme.Colors.Border, "#D1D5DB"))
                        .CornerRadius(Math.Max(radius, 4))
                        .AlignMiddle().AlignCenter()
                        .Text("Image").FontSize((float)(_theme.Typography.BodySizePt * 0.95)).FontColor(muted);
                    return;
                }

                var box = radius > 0 ? holder.CornerRadius(radius) : holder;
                if (IsSvg(asset!, data))
                {
                    box.Svg(Encoding.UTF8.GetString(data)).FitArea();
                    return;
                }

                try
                {
                    box.Image(data).FitArea();
                }
                catch (Exception)
                {
                    box.Background(Hex(_theme.Colors.Surface, "#F3F4F6")).Padding(4).AlignCenter()
                        .Text(image.Alt ?? "Unsupported image").Italic().FontColor(muted);
                }
            });

            if (caption is not null)
            {
                var cap = col.Item().PaddingTop(4);
                cap = image.Align switch
                {
                    BlockAlign.Left => cap.AlignLeft(),
                    BlockAlign.Right => cap.AlignRight(),
                    _ => cap.AlignCenter(),
                };
                cap.Text(caption).FontSize((float)_theme.Images.CaptionSizePt).Italic().FontColor(muted);
            }
        });
    }

    private static float Size(double value, double fallback) =>
        (float)Math.Clamp(double.IsFinite(value) && value > 0 ? value : fallback, PlacedModule.MinSizePt, PlacedModule.MaxPt);

    private static float Coordinate(double value) =>
        double.IsFinite(value) ? (float)Math.Clamp(value, -PlacedModule.MaxPt, PlacedModule.MaxPt) : 0f;

    private static bool IsSvg(PublicationAsset asset, byte[] data)
    {
        if (asset.MediaType?.Contains("svg", StringComparison.OrdinalIgnoreCase) == true
            || asset.FileName?.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        var head = Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 256)).TrimStart('﻿', ' ', '\r', '\n', '\t');
        return head.StartsWith("<svg", StringComparison.OrdinalIgnoreCase)
            || (head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) && Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 1024)).Contains("<svg", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Only http(s) and mailto links are written into the PDF.</summary>
    private static string? SafeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        return uri.Scheme is "http" or "https" or "mailto" ? uri.AbsoluteUri : null;
    }

    // ------------------------------------------------------------ estimates

    private double EstimateColumn(ColumnPlan plan, double width)
    {
        var design = plan.Column.Design ?? new BoxDesign();
        var inner = Math.Max(10, width - Horizontal(design));
        var total = Vertical(design);
        foreach (var m in plan.Modules)
        {
            if (m.Module is PageBreakModule)
            {
                return double.PositiveInfinity; // a forced break: the row cannot stay on one page
            }

            var d = m.Module.Design ?? new ModuleDesign();
            var w = Math.Max(10, inner - Horizontal(d));
            total += Vertical(d) + EstimateModule(m, w);
        }

        return total;
    }

    private double EstimateModule(ModulePlan m, double width)
    {
        var entries = m.Module is ContentsModule contents ? _headings.Count(h => h.Level <= contents.Depth) : 0;
        return _estimator.Module(m.Module, m.Blocks, width, _publication, _options.ShowPlaceholders, entries);
    }

    private static string PlainText(IReadOnlyList<Block> blocks) =>
        string.Join(' ', blocks.OfType<InlineContainerBlock>().Select(b => b.ToPlainText()));

    // --------------------------------------------------------------- boxes

    /// <summary>Margin (outer padding), background, border, corner radius and padding of a design box.</summary>
    private IContainer Box(IContainer container, BoxDesign design)
    {
        var m = design.Margin ?? new Spacing();
        var c = container
            .PaddingTop(Pt(m.TopPt)).PaddingRight(Pt(m.RightPt)).PaddingBottom(Pt(m.BottomPt)).PaddingLeft(Pt(m.LeftPt));

        if (!string.IsNullOrWhiteSpace(design.Background))
        {
            c = c.Background(Hex(design.Background, "#FFFFFF"));
        }

        if (HasBorder(design))
        {
            // No colour of its own: the theme's border colour, as the inspector ("Theme border colour") and the canvas show.
            var color = string.IsNullOrWhiteSpace(design.BorderColor) ? _theme.Colors.Border : design.BorderColor;
            c = c.Border((float)design.BorderWidthPt).BorderColor(Hex(color, "#D1D5DB"));
        }

        if (design.CornerRadiusPt > 0 && double.IsFinite(design.CornerRadiusPt))
        {
            c = c.CornerRadius((float)Math.Min(design.CornerRadiusPt, 200));
        }

        var p = design.Padding ?? new Spacing();
        return c.PaddingTop(Pt(p.TopPt)).PaddingRight(Pt(p.RightPt)).PaddingBottom(Pt(p.BottomPt)).PaddingLeft(Pt(p.LeftPt));
    }

    /// <summary>
    /// Position of a box (<see cref="BoxDesign.OffsetXPt"/>, <see cref="BoxDesign.OffsetYPt"/>, <see cref="BoxDesign.ZIndex"/>)
    /// applied outside everything the box draws, then the page/box tracking under <paramref name="key"/>: the whole box
    /// paints shifted (and, above Z level 0, over the normal content) while the flow, its neighbours and the page
    /// breaks stay where they were, and the tracked box says where it really painted.
    /// </summary>
    private static IContainer Positioned(IContainer container, BoxDesign design, string key)
    {
        var x = Offset(design.OffsetXPt);
        var y = Offset(design.OffsetYPt);
        if (x != 0)
        {
            container = container.TranslateX(x);
        }

        if (y != 0)
        {
            container = container.TranslateY(y);
        }

        if (design.ZIndex > 0)
        {
            container = container.ZIndex(Math.Min(design.ZIndex, BoxDesign.MaxZIndex));
        }

        return container.TrackPages(key);
    }

    private static bool HasPosition(BoxDesign d) => Offset(d.OffsetXPt) != 0 || Offset(d.OffsetYPt) != 0 || d.ZIndex > 0;

    private static float Offset(double value) =>
        double.IsFinite(value) ? (float)Math.Clamp(value, -BoxDesign.MaxOffsetPt, BoxDesign.MaxOffsetPt) : 0f;

    private static bool HasBorder(BoxDesign d) => BoxMetrics.HasBorder(d);

    private static bool IsZero(Spacing? s) => s is null || (s.TopPt <= 0 && s.RightPt <= 0 && s.BottomPt <= 0 && s.LeftPt <= 0);

    /// <summary>Width a design box takes from its container: margins, padding and both borders.</summary>
    private static double Horizontal(BoxDesign d) => BoxMetrics.Horizontal(d);

    private static double Vertical(BoxDesign d) => BoxMetrics.Vertical(d);

    private static float Pt(double? value) => BoxMetrics.Pt(value);

    private static IContainer Align(IContainer c, BlockAlign align) => align switch
    {
        BlockAlign.Center => c.AlignCenter(),
        BlockAlign.Right => c.AlignRight(),
        _ => c.AlignLeft(),
    };

    /// <summary>
    /// A box <paramref name="percent"/> of the container's width, placed by <paramref name="align"/>. Built from relative
    /// row items so it can never be wider than the space the layout engine actually has.
    /// </summary>
    private static void Percent(IContainer container, double percent, BlockAlign align, Action<IContainer> content)
    {
        var pct = (float)Math.Clamp(double.IsFinite(percent) ? percent : 100, 5, 100);
        if (pct >= 99.95f)
        {
            content(container);
            return;
        }

        var rest = 100 - pct;
        container.Row(row =>
        {
            switch (align)
            {
                case BlockAlign.Left:
                    content(row.RelativeItem(pct));
                    row.RelativeItem(rest);
                    break;
                case BlockAlign.Right:
                    row.RelativeItem(rest);
                    content(row.RelativeItem(pct));
                    break;
                default:
                    row.RelativeItem(rest / 2);
                    content(row.RelativeItem(pct));
                    row.RelativeItem(rest / 2);
                    break;
            }
        });
    }

    private static QColor Hex(string? hex, string fallback) => ThemeDrawing.Hex(hex, string.IsNullOrWhiteSpace(fallback) ? "#000000" : fallback);
}
