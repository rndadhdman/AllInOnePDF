using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Themes;
using AllInOnePdf.Publishing.Model;

namespace AllInOnePdf.Publishing.Rendering;

/// <summary>
/// Rough block heights in points, with the same text-length heuristic as <c>ThemedComposer</c>'s keep-with-next
/// estimates (average glyph advance about 0.52 em). Used to decide whether a row is kept on one page, to size
/// free-flow canvases (<see cref="CanvasHeight"/>) and to stack modules when a section is converted to free flow.
/// </summary>
internal sealed class LayoutEstimator(PdfTheme theme, double bodyHeightPt)
{
    private readonly PdfTheme _t = theme;

    /// <summary>Height of one body line.</summary>
    public double BodyLine => _t.Typography.BodySizePt * _t.Typography.LineHeight;

    /// <summary>Lines a text of this length takes at <paramref name="sizePt"/> in <paramref name="widthPt"/>.</summary>
    public static double Lines(string text, double sizePt, double widthPt) =>
        Math.Max(1, Math.Ceiling(text.Length * sizePt * 0.52 / Math.Max(40, widthPt)));

    /// <summary>A heading of <paramref name="level"/> with spacing and rule.</summary>
    public double Heading(int level, string text, double widthPt)
    {
        var style = _t.Headings.Level(level);
        var lines = Lines(text, style.SizePt, widthPt);
        return style.SpaceBeforePt + style.SpaceAfterPt + (lines * style.SizePt * _t.Headings.LineHeight) + (style.Underline ? 3 + style.RuleWidthPt : 0);
    }

    /// <summary>The body height the estimates were made for (page content height minus header and footer).</summary>
    public double BodyHeightPt => bodyHeightPt;

    /// <summary>
    /// Height of a free-flow section's canvas: <see cref="Section.CanvasHeightPt"/>, or the whole body height when it
    /// is null, never more than what one page leaves after the section's own margins, borders and padding (so the
    /// layout engine can always place it), never less than 1 pt.
    /// </summary>
    public static double CanvasHeight(Section section, double bodyHeightPt)
    {
        var design = section.Design ?? new BoxDesign();
        var max = Math.Max(1, bodyHeightPt - BoxMetrics.Vertical(design));
        var wanted = section.CanvasHeightPt is { } h && double.IsFinite(h) && h > 0 ? h : max;
        return Math.Clamp(wanted, 1, max);
    }

    /// <summary>Height of a free-flow section on the page: its canvas plus its box (margins, borders, padding).</summary>
    public double FreeFlowSection(Section section) => CanvasHeight(section, bodyHeightPt) + BoxMetrics.Vertical(section.Design ?? new BoxDesign());

    /// <summary>
    /// Height of a module's content in <paramref name="widthPt"/> (inside its own box), with the renderer's rules for
    /// spacers, dividers, buttons, contents lists and images.
    /// </summary>
    /// <param name="module">The module.</param>
    /// <param name="blocks">Its blocks (<c>ModuleBlocks.ToBlocks</c>).</param>
    /// <param name="widthPt">Width inside the module's box.</param>
    /// <param name="publication">The publication (assets).</param>
    /// <param name="showPlaceholders">Images without an asset draw a placeholder.</param>
    /// <param name="contentsEntries">Headings a contents module lists.</param>
    public double Module(Module module, IReadOnlyList<Block> blocks, double widthPt, Publication publication, bool showPlaceholders, int contentsEntries)
    {
        var scale = module.Design?.FontScale is > 0 and var s ? s : 1;
        switch (module)
        {
            case SpacerModule spacer:
                return Math.Max(0, spacer.HeightPt);
            case DividerModule divider:
                return (2 * Math.Max(6, _t.Typography.ParagraphSpacingPt)) + divider.ThicknessPt;
            case ButtonModule:
                return (_t.Typography.BodySizePt * scale * 1.2) + 12 + _t.Typography.ParagraphSpacingPt;
            case ContentsModule:
                return (_t.Headings.H1.SizePt * 0.8 * 1.3) + _t.Headings.H1.SpaceAfterPt + (contentsEntries * ((_t.Typography.BodySizePt * _t.Typography.LineHeight) + 7));
            case PageBreakModule:
                return 0;
            case ImageModule image:
            {
                var asset = publication.FindAsset(image.AssetId);
                if (asset?.Data is not { Length: > 0 } && !showPlaceholders)
                {
                    return 0;
                }

                var (pw, ph) = asset is null ? (3, 2)
                    : asset.PixelWidth is > 0 && asset.PixelHeight is > 0 ? (asset.PixelWidth.Value, asset.PixelHeight.Value)
                    : Core.Rendering.ImageHeader.ReadPixelSize(asset.Data);
                var boxWidth = widthPt * Math.Clamp(image.WidthPercent, 5, 100) / 100;
                var height = asset is null ? PlaceholderHeight(boxWidth, double.MaxValue) : pw > 0 && ph > 0 ? boxWidth * ph / pw : boxWidth * 0.66;
                height = Math.Min(height, Math.Min(image.MaxHeightPt ?? double.MaxValue, bodyHeightPt * MaxImageShare));
                var caption = string.IsNullOrWhiteSpace(image.Caption) ? 0 : (_t.Images.CaptionSizePt * 1.6) + 4;
                return height + caption + _t.Images.SpacingPt;
            }

            default:
                return Blocks(blocks, widthPt, scale);
        }
    }

    /// <summary>Images and placeholders never get taller than this share of the body height (unbreakable content).</summary>
    public const double MaxImageShare = 0.9;

    /// <summary>Image placeholders never get taller than this share of the body height.</summary>
    public const double PlaceholderShare = 0.32;

    /// <summary>Height of an image placeholder <paramref name="boxWidth"/> wide: 3:2, capped.</summary>
    public double PlaceholderHeight(double boxWidth, double maxHeight) =>
        Math.Max(10, Math.Min(boxWidth / 1.5, Math.Min(maxHeight, bodyHeightPt * PlaceholderShare)));

    /// <summary>Sum of the blocks' heights.</summary>
    public double Blocks(IEnumerable<Block> blocks, double widthPt, double scale = 1) => blocks.Sum(b => Block(b, widthPt, scale, nested: false));

    private double Block(Block block, double widthPt, double scale, bool nested)
    {
        var size = _t.Typography.BodySizePt * scale;
        var line = size * _t.Typography.LineHeight;
        var spacing = nested ? Math.Min(_t.Typography.ParagraphSpacingPt, 4) : _t.Typography.ParagraphSpacingPt;
        switch (block)
        {
            case HeadingBlock h:
                return Heading(h.Level, h.ToPlainText(), widthPt);
            case ParagraphBlock p:
                return (Lines(p.ToPlainText(), size, widthPt) * line) + spacing;
            case ListBlock list:
            {
                var inner = widthPt - _t.Lists.IndentPt;
                var items = list.Items.Sum(i => i.Blocks.Sum(b => Block(b, inner, scale, nested: true)) + _t.Lists.ItemSpacingPt);
                return items + spacing;
            }

            case QuoteBlock quote:
                return 24 + quote.Blocks.Sum(b => Block(b, widthPt - 30, scale, nested: true)) + (quote.Attribution is null ? 0 : line) + spacing;
            case CalloutBlock callout:
                return 30 + line + callout.Blocks.Sum(b => Block(b, widthPt - 40, scale, nested: true)) + spacing;
            case CodeBlock code:
            {
                var lines = code.Code.Count(ch => ch == '\n') + 1;
                return (lines * _t.Code.SizePt * _t.Code.LineHeight) + (2 * _t.Code.PaddingPt) + (code.Caption is null && code.Language is null ? 0 : _t.Code.SizePt * 1.4) + spacing;
            }

            case TableBlock table:
            {
                var columns = Math.Max(1, table.ColumnCount);
                var cellWidth = (widthPt / columns) - (2 * _t.Tables.CellPaddingPt);
                var cellSize = _t.Typography.BodySizePt * _t.Tables.FontScale;
                var rows = table.Rows.Sum(r =>
                {
                    var lines = r.Cells.Select(c => Lines(c.ToPlainText(), cellSize, cellWidth)).DefaultIfEmpty(1).Max();
                    return (lines * cellSize * 1.3) + (2 * _t.Tables.CellPaddingPt);
                });
                return rows + (table.Caption is null ? 0 : cellSize * 1.6) + spacing;
            }

            case ImageBlock:
                return bodyHeightPt * 0.5;
            case ThematicBreakBlock:
                return 2 * Math.Max(6, _t.Typography.ParagraphSpacingPt);
            case PageBreakBlock:
                return double.PositiveInfinity;
            default:
                return 2 * line;
        }
    }
}
