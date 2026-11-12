using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Rendering;
using AllInOnePdf.Core.Themes;
using AllInOnePdf.Publishing.Content;
using AllInOnePdf.Publishing.Model;
using AllInOnePdf.Publishing.Serialization;

namespace AllInOnePdf.UI.Services;

/// <summary>
/// Builds the design canvas markup: module content as HTML from <see cref="ModuleBlocks.ToBlocks"/> through
/// <see cref="IDocumentHtmlRenderer"/> (one <c>&lt;article class="aio-doc"&gt;</c> per module so the theme CSS from
/// <see cref="ThemeBuilderCssGenerator"/> applies), renderer specials (spacer, divider, button, contents, page break,
/// image placeholders) drawn like the PDF, box designs as inline styles and rows as CSS grids. One instance per
/// canvas: module HTML is cached by content, so typing re-renders only the module being edited.
/// </summary>
public sealed partial class CreatorCanvasHtml
{
    private static readonly JsonSerializerOptions Json = PublishingJson.Options;

    private readonly IDocumentHtmlRenderer _renderer;
    private readonly Dictionary<string, (string Key, string Html)> _cache = new(StringComparer.Ordinal);

    /// <summary>Creates a canvas builder.</summary>
    public CreatorCanvasHtml(IDocumentHtmlRenderer renderer)
    {
        _renderer = renderer;
    }

    /// <summary>
    /// The theme CSS scoped to <paramref name="scopeClass"/> plus the canvas overrides (module text colour, alignment
    /// and heading numbering across modules).
    /// </summary>
    public static string Css(PdfTheme theme, string scopeClass)
    {
        ArgumentNullException.ThrowIfNull(theme);
        var s = "." + scopeClass;
        var css = new StringBuilder(ThemeBuilderCssGenerator.Generate(theme, scopeClass));
        css.Append(s).Append(" .cr-body{counter-reset:tbh1 tbh2 tbh3 tbh4 tbh5 tbh6;}\n");
        css.Append(s).Append(" .cr-body .aio-doc{counter-reset:none;}\n");
        css.Append(s).Append(" .cr-sheet{--cr-border:").Append(ThemeBuilderColor.ToCss(theme.Colors.Border, "#D1D5DB")).Append(";}\n");
        css.Append(s).Append(" :is(.cr-mod,.cr-item)[data-tc] .aio-doc :is(p,.aio-p,li,td,th,blockquote,figcaption,.aio-h,.aio-quote,.tb-toc-entry,.tb-toc-title){color:var(--cr-tc) !important;}\n");
        css.Append(s).Append(" :is(.cr-mod,.cr-item)[data-ta] .aio-doc :is(p,.aio-p,.aio-h,li,figcaption,.aio-figure,caption){text-align:var(--cr-ta) !important;}\n");
        return css.ToString();
    }

    /// <summary>The HTML of a module's content (inside its own article).</summary>
    public string ModuleHtml(Module module, Publication publication)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(publication);
        if (module is ContentsModule contents)
        {
            // Depends on every heading in the publication: cheap, never cached.
            return Article(ContentsHtml(contents, publication));
        }

        var key = CacheKey(module, publication);
        if (_cache.TryGetValue(module.Id, out var cached) && string.Equals(cached.Key, key, StringComparison.Ordinal))
        {
            return cached.Html;
        }

        var html = Article(Build(module, publication));
        _cache[module.Id] = (key, html);
        if (_cache.Count > 2000)
        {
            _cache.Clear();
        }

        return html;
    }

    /// <summary>Inline CSS for a box design (margin, padding, background, border, radius).</summary>
    public static string BoxStyle(BoxDesign? design)
    {
        if (design is null)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(96);
        AppendSpacing(sb, "margin", design.Margin);
        AppendSpacing(sb, "padding", design.Padding);
        if (ThemeBuilderColor.IsValid(design.Background))
        {
            sb.Append("background:").Append(ThemeBuilderColor.ToCss(design.Background, "#FFFFFF")).Append(';');
        }

        if (design.BorderWidthPt > 0)
        {
            sb.Append("border:").Append(N(design.BorderWidthPt)).Append("pt solid ")
              .Append(ThemeBuilderColor.IsValid(design.BorderColor) ? ThemeBuilderColor.ToCss(design.BorderColor, "#D1D5DB") : "var(--cr-border)").Append(';');
        }

        if (Radius(design) is var radius and > 0)
        {
            sb.Append("border-radius:").Append(N(radius)).Append("pt;");
        }

        AppendPosition(sb, design);
        return sb.ToString();
    }

    /// <summary>
    /// Inline CSS that clips a box's content to its rounded corners, like the PDF's styled box clips its child, for a
    /// wrapper inside the box's padding (a module's body, a section's rows, a row's grid): <c>overflow:hidden</c> and
    /// the corner radius reduced by the border and the padding on each side (CSS's inner-radius rule). The wrapper is
    /// used instead of the box itself so the box's own floating toolbar, which sits above its top edge, is not clipped.
    /// Empty without a radius, or when the padding keeps the content clear of the corners.
    /// </summary>
    public static string ClipStyle(BoxDesign? design)
    {
        if (design is null || Radius(design) is not (var r and > 0))
        {
            return string.Empty;
        }

        // A module body carries the text size as CSS zoom, which scales its own radius too: undo that.
        var zoom = design is ModuleDesign { FontScale: var s } && double.IsFinite(s) && Math.Abs(s - 1) > 0.001 ? Math.Clamp(s, 0.5, 3) : 1;

        var border = design.BorderWidthPt > 0 && double.IsFinite(design.BorderWidthPt) ? design.BorderWidthPt : 0;
        var p = design.Padding ?? new Spacing();
        double Inner(double side) => Math.Max(0, r - border - (double.IsFinite(side) ? Math.Max(0, side) : 0)) / zoom;
        double[] h = [Inner(p.LeftPt), Inner(p.RightPt), Inner(p.RightPt), Inner(p.LeftPt)];
        double[] v = [Inner(p.TopPt), Inner(p.TopPt), Inner(p.BottomPt), Inner(p.BottomPt)];
        if (h.All(x => x <= 0) && v.All(x => x <= 0))
        {
            return string.Empty;
        }

        return "overflow:hidden;border-radius:" + string.Join(' ', h.Select(x => N(x) + "pt")) + " / " + string.Join(' ', v.Select(x => N(x) + "pt")) + ";";
    }

    /// <summary>
    /// Inline CSS for a box that clips its own content (a column: it has no toolbar of its own). The browser clips to
    /// the padding edge with the radius reduced by the border, as the PDF does.
    /// </summary>
    public static string SelfClipStyle(BoxDesign? design) =>
        design is not null && Radius(design) > 0 ? "overflow:hidden;" : string.Empty;

    /// <summary>The corner radius the PDF draws (0 when unset or not a number; at most 200 pt).</summary>
    private static double Radius(BoxDesign design) =>
        design.CornerRadiusPt > 0 && double.IsFinite(design.CornerRadiusPt) ? Math.Min(design.CornerRadiusPt, 200) : 0;

    /// <summary>
    /// Position: the offset shifts the box's drawing without moving its neighbours (relative positioning keeps its
    /// place in the flow), and a Z level above 0 paints it over boxes with a lower level.
    /// </summary>
    private static void AppendPosition(StringBuilder sb, BoxDesign design)
    {
        var x = double.IsFinite(design.OffsetXPt) ? Math.Clamp(design.OffsetXPt, -BoxDesign.MaxOffsetPt, BoxDesign.MaxOffsetPt) : 0;
        var y = double.IsFinite(design.OffsetYPt) ? Math.Clamp(design.OffsetYPt, -BoxDesign.MaxOffsetPt, BoxDesign.MaxOffsetPt) : 0;
        var z = Math.Clamp(design.ZIndex, 0, BoxDesign.MaxZIndex);
        if (x == 0 && y == 0 && z == 0)
        {
            return;
        }

        sb.Append("position:relative;");
        if (x != 0 || y != 0)
        {
            sb.Append("left:").Append(N(x)).Append("pt;top:").Append(N(y)).Append("pt;");
        }

        if (z > 0)
        {
            sb.Append("z-index:").Append(z.ToString(CultureInfo.InvariantCulture)).Append(';');
        }
    }

    /// <summary>Inline CSS for a module's box plus its text overrides (as custom properties the canvas CSS reads).</summary>
    public static string ModuleStyle(ModuleDesign? design)
    {
        if (design is null)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(BoxStyle(design));
        if (ThemeBuilderColor.IsValid(design.TextColor))
        {
            sb.Append("--cr-tc:").Append(ThemeBuilderColor.ToCss(design.TextColor, "#000000")).Append(';');
        }

        if (design.Alignment is { } align)
        {
            sb.Append("--cr-ta:").Append(align switch
            {
                TextAlignment.Center => "center",
                TextAlignment.Right => "right",
                TextAlignment.Justify => "justify",
                _ => "left",
            }).Append(';');
        }

        return sb.ToString();
    }

    /// <summary>Inline CSS for the article inside a module (font scale).</summary>
    public static string ArticleStyle(ModuleDesign? design) =>
        design is { FontScale: var scale } && double.IsFinite(scale) && Math.Abs(scale - 1) > 0.001
            ? "zoom:" + N(Math.Clamp(scale, 0.5, 3)) + ";"
            : string.Empty;

    /// <summary>Inline CSS for a row's grid: columns from the fractions, the gutter as gap, vertical alignment.</summary>
    public static string GridStyle(Row row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var fractions = row.Fractions.Count == row.Columns.Count && row.Fractions.Count > 0
            ? row.Fractions
            : [.. Enumerable.Repeat(1.0 / Math.Max(1, row.Columns.Count), Math.Max(1, row.Columns.Count))];
        var columns = string.Join(' ', fractions.Select(f => "minmax(0," + N(Math.Max(0.01, f) * 100) + "fr)"));
        var align = row.Align switch
        {
            VerticalAlign.Middle => "center",
            VerticalAlign.Bottom => "end",
            _ => "start",
        };
        return $"display:grid;grid-template-columns:{columns};column-gap:{N(Math.Max(0, row.GutterPt))}pt;align-items:{align};";
    }

    /// <summary>
    /// Inline CSS for a free-flow section's canvas: a positioned box of the canvas's width and height in points (the
    /// stage's zoom scales points like every other length on the sheet).
    /// </summary>
    public static string CanvasStyle(PdfTheme theme, Section section) =>
        $"position:relative;width:{N(CreatorFreeFlow.CanvasWidthPt(theme, section))}pt;height:{N(CreatorFreeFlow.CanvasHeightPt(theme, section))}pt;";

    /// <summary>
    /// Inline CSS for an item on a free-flow canvas: absolutely positioned at its box, painted at its Z level (ties
    /// paint in document order, which is the list order, like the PDF).
    /// </summary>
    public static string ItemStyle(PlacedModule item, bool withZ = true)
    {
        ArgumentNullException.ThrowIfNull(item);
        static double Size(double v) => double.IsFinite(v) ? Math.Clamp(v, PlacedModule.MinSizePt, PlacedModule.MaxPt) : PlacedModule.DefaultWidthPt;
        var box = $"left:{N(ItemPosition(item.XPt))}pt;top:{N(ItemPosition(item.YPt))}pt;width:{N(Size(item.WidthPt))}pt;height:{N(Size(item.HeightPt))}pt;";
        return withZ ? box + "z-index:" + Math.Clamp(item.Z, 0, BoxDesign.MaxZIndex).ToString(CultureInfo.InvariantCulture) + ";" : box;
    }

    /// <summary>A position as the canvas draws it (clamped to ±2000 pt; not a number → 0), formatted for data attributes.</summary>
    public static string ItemNumber(double value) => N(double.IsFinite(value) ? value : 0);

    private static double ItemPosition(double v) => double.IsFinite(v) ? Math.Clamp(v, -PlacedModule.MaxPt, PlacedModule.MaxPt) : 0;

    /// <summary>
    /// Inline CSS for the clipping box inside an item (the module's own box design without its Position, which the
    /// item's box replaces): it fills the item and cuts off content that does not fit, as the PDF does.
    /// </summary>
    public static string ItemClipStyle(ModuleDesign? design)
    {
        var style = ModuleStyle(design);
        if (design is not null && (design.OffsetXPt != 0 || design.OffsetYPt != 0 || design.ZIndex != 0))
        {
            // Strip the Position part: in free flow the box already says where the module goes.
            var copy = new ModuleDesign
            {
                Margin = design.Margin,
                Padding = design.Padding,
                Background = design.Background,
                BorderColor = design.BorderColor,
                BorderWidthPt = design.BorderWidthPt,
                CornerRadiusPt = design.CornerRadiusPt,
                Alignment = design.Alignment,
                TextColor = design.TextColor,
                FontScale = design.FontScale,
            };
            style = ModuleStyle(copy);
        }

        return "position:absolute;inset:0;overflow:hidden;box-sizing:border-box;" + style;
    }

    /// <summary>
    /// The header or footer strip of the theme snapshot for one sheet, or empty when it is off (or, like the PDF,
    /// on page 1 when it is not shown on the first page). {pages} is <paramref name="pages"/> when known, else "n".
    /// </summary>
    public static string HeaderFooterHtml(Publication publication, bool header, int page = 1, int? pages = null)
    {
        ArgumentNullException.ThrowIfNull(publication);
        var hf = header ? publication.Theme.Header : publication.Theme.Footer;
        if (!hf.HasContent || (page <= 1 && !hf.ShowOnFirstPage))
        {
            return string.Empty;
        }

        var pagesText = pages is > 0 ? pages.Value.ToString(CultureInfo.InvariantCulture) : "n";

        var document = MetadataDocument(publication);
        var title = document.Metadata.Title ?? publication.Name;
        var cls = header ? "tb-header" : "tb-footer";
        var sb = new StringBuilder();
        sb.Append("<div class=\"").Append(cls).Append("-slot\"><div class=\"tb-hf ").Append(cls).Append("\">");
        foreach (var (part, value) in new[] { ("l", hf.Left), ("c", hf.Center), ("r", hf.Right) })
        {
            sb.Append("<div class=\"tb-hf-").Append(part).Append("\">")
              .Append(Encode(ThemeBuilderPreview.ExpandTemplate(value, document, title, Math.Max(1, page).ToString(CultureInfo.InvariantCulture), pagesText)))
              .Append("</div>");
        }

        sb.Append("</div></div>");
        return sb.ToString();
    }

    /// <summary>
    /// The sections on each page sheet the design canvas draws, as [start, end) index ranges. A new sheet starts
    /// wherever <c>PublicationLayout</c> starts a new page for an explicit break: a section that starts on a new page
    /// (or starts with a level-1 heading when the theme breaks before those) after content, a section followed by a
    /// page break, or a page-break module that ends a section. Overflow is not predicted: a sheet grows instead, and
    /// Preview shows the real pages.
    /// </summary>
    public static IReadOnlyList<(int Start, int End)> SheetRanges(Publication publication)
    {
        ArgumentNullException.ThrowIfNull(publication);
        var sections = publication.Sections;
        var ranges = new List<(int Start, int End)>();
        var start = 0;
        var hasContent = false;
        for (var s = 0; s < sections.Count; s++)
        {
            var section = sections[s];

            // A free-flow canvas without a height of its own is a full page: it starts a sheet and the next section
            // starts another. A canvas with a height sits in the flow like any other section (the PDF packs it with
            // its neighbours when it fits; without a page map the sheet grows, like overflow).
            var free = section.Layout == SectionLayout.FreeFlow;
            var fullPage = free && section.CanvasHeightPt is null;
            if (hasContent && (section.PageBreakBefore || fullPage || StartsWithBreakingH1(publication, section)))
            {
                ranges.Add((start, s));
                start = s;
                hasContent = false;
            }

            if (free || section.Rows.Count > 0)
            {
                hasContent = true;
            }

            var endsWithBreak = !free && EndsWithPageBreak(section);
            if (endsWithBreak)
            {
                hasContent = false;
            }

            if ((endsWithBreak || fullPage || (section.PageBreakAfter && hasContent)) && s < sections.Count - 1)
            {
                ranges.Add((start, s + 1));
                start = s + 1;
                hasContent = false;
            }
        }

        ranges.Add((start, sections.Count));
        return ranges;
    }

    private static bool StartsWithBreakingH1(Publication publication, Section section) =>
        publication.Theme.Headings.PageBreakBeforeH1
        && section.Layout == SectionLayout.Structure
        && section.Rows.FirstOrDefault()?.Columns.FirstOrDefault()?.Modules.FirstOrDefault() is HeadingModule { Level: 1 };

    private static bool EndsWithPageBreak(Section section) =>
        section.Rows.Count > 0 && section.Rows[^1].Columns.Count == 1 && section.Rows[^1].Columns[0].Modules.LastOrDefault() is PageBreakModule;

    /// <summary>Accessible name for a canvas element ("Text module 2 of 3").</summary>
    public static string Label(string what, int index, int count) =>
        string.Create(CultureInfo.InvariantCulture, $"{what} {index + 1} of {count}");

    // ------------------------------------------------------------------ modules

    private string Build(Module module, Publication publication)
    {
        switch (module)
        {
            case SpacerModule spacer:
                return $"<div class=\"cr-spacer\" style=\"height:{N(Math.Max(0, spacer.HeightPt))}pt\"><span>Spacer · {N(spacer.HeightPt)} pt</span></div>";

            case PageBreakModule:
                return "<div class=\"cr-break\" role=\"separator\"><span>Page break</span></div>";

            case DividerModule divider:
                return DividerHtml(divider, publication.Theme);

            case ButtonModule button:
                return ButtonHtml(button, publication.Theme);

            case ImageModule image when publication.FindAsset(image.AssetId) is not { Data.Length: > 0 }:
                return ImagePlaceholder(image, publication.Theme);
        }

        var blocks = ModuleBlocks.ToBlocks(module, publication);
        var document = new Document { Metadata = { Language = publication.Metadata.Language } };
        foreach (var block in blocks)
        {
            if (block is CalloutBlock { Title: null } callout)
            {
                var label = publication.Theme.Callouts.Kind(callout.Kind).Label;
                callout.Title = string.IsNullOrWhiteSpace(label) ? callout.Kind.ToString() : label;
            }

            document.Blocks.Add(block);
        }

        if (module is ImageModule img && publication.FindAsset(img.AssetId) is { } asset)
        {
            document.AddResource(asset.MediaType, asset.Data, asset.FileName, asset.Id);
        }

        var html = _renderer.RenderFragment(document, new HtmlRenderOptions
        {
            HeadingAnchors = false,
            BlockIndexes = false,
            ExternalLinksBlank = false,
            ResolveImage = ReaderImageResolver.Create(allowLocalFiles: false),
        });
        html = StripArticle(html);

        if (module is ImageModule image2)
        {
            html = StyleImage(html, image2);
        }

        if (module is TextModule or QuoteModule or CalloutModule && html.Trim().Length == 0)
        {
            html = "<p class=\"aio-p cr-empty-text\">Empty text. Type in the inspector to add content.</p>";
        }

        return html;
    }

    private static string StyleImage(string html, ImageModule image)
    {
        var align = image.Align switch
        {
            BlockAlign.Left => "left",
            BlockAlign.Right => "right",
            _ => "center",
        };
        html = FigureOpen().Replace(html, m => m.Value + $" style=\"text-align:{align}\"", 1);
        var extra = new StringBuilder("max-width:100%;");
        if (image.CornerRadiusPt > 0)
        {
            extra.Append("border-radius:").Append(N(image.CornerRadiusPt)).Append("pt;");
        }

        if (image.MaxHeightPt is { } max && max > 0)
        {
            extra.Append("max-height:").Append(N(max)).Append("pt;object-fit:contain;");
        }

        var width = "width:" + N(Math.Clamp(image.WidthPercent, 5, 100)) + "%;";
        return ImgStyle().Replace(html, m =>
        {
            var tag = m.Value;
            var styleAt = tag.IndexOf(" style=\"", StringComparison.Ordinal);
            if (styleAt >= 0)
            {
                return tag.Insert(styleAt + 8, extra.ToString());
            }

            return tag[..^1] + " style=\"" + width + extra + "\">";
        }, 1);
    }

    private static string ImagePlaceholder(ImageModule image, PdfTheme theme)
    {
        var colors = $"background:{ThemeBuilderColor.ToCss(theme.Colors.Surface, "#F3F4F6")};border:.75pt solid {ThemeBuilderColor.ToCss(theme.Colors.Border, "#D1D5DB")};color:{ThemeBuilderColor.ToCss(theme.Colors.Muted, "#6B7280")}";
        var align = image.Align switch
        {
            BlockAlign.Left => "margin-right:auto",
            BlockAlign.Right => "margin-left:auto",
            _ => "margin-left:auto;margin-right:auto",
        };
        var label = string.IsNullOrWhiteSpace(image.Alt) ? "Image" : image.Alt!;
        var caption = string.IsNullOrWhiteSpace(image.Caption) ? string.Empty : $"<figcaption>{Encode(image.Caption)}</figcaption>";
        return $"<figure class=\"aio-figure cr-figure-ph\"><div class=\"cr-img-ph\" style=\"width:{N(Math.Clamp(image.WidthPercent, 5, 100))}%;{align};border-radius:{N(image.CornerRadiusPt)}pt;{colors}\">" +
               $"<span class=\"cr-img-ph-title\">{Encode(label)}</span><span class=\"cr-img-ph-hint\">Choose an image in the inspector</span></div>{caption}</figure>";
    }

    private static string DividerHtml(DividerModule divider, PdfTheme theme)
    {
        var color = ThemeBuilderColor.ToCss(ThemeBuilderColor.IsValid(divider.Color) ? divider.Color : theme.Colors.Border, "#D1D5DB");
        var style = divider.Style switch
        {
            DividerStyle.Dashed => "dashed",
            DividerStyle.Dotted => "dotted",
            _ => "solid",
        };
        var align = divider.Align switch
        {
            BlockAlign.Left => "margin-right:auto",
            BlockAlign.Right => "margin-left:auto",
            _ => "margin-left:auto;margin-right:auto",
        };
        var spacing = Math.Max(6, theme.Typography.ParagraphSpacingPt);
        return $"<div class=\"cr-divider\" role=\"separator\" style=\"padding:{N(spacing)}pt 0\"><div style=\"height:0;border-top:{N(Math.Max(0.25, divider.ThicknessPt))}pt {style} {color};width:{N(Math.Clamp(divider.WidthPercent, 5, 100))}%;{align}\"></div></div>";
    }

    private static string ButtonHtml(ButtonModule button, PdfTheme theme)
    {
        var color = ThemeBuilderColor.IsValid(button.Color) ? button.Color! : theme.Colors.Accent;
        var css = ThemeBuilderColor.ToCss(color, "#0F6CBD");
        var onColor = ThemeBuilderColor.IsDark(color) ? "#FFFFFF" : "#111827";
        var align = button.Align switch
        {
            BlockAlign.Center => "center",
            BlockAlign.Right => "right",
            _ => "left",
        };
        var radius = N(Math.Max(0, button.CornerRadiusPt));
        var size = N(theme.Typography.BodySizePt);
        var font = ThemeBuilderCssGenerator.FontStack(theme.Typography.BodyFont);
        var look = button.Style switch
        {
            ButtonStyle.Outline => $"background:transparent;color:{css};border:1pt solid {css};border-radius:{radius}pt;padding:5pt 14pt;",
            ButtonStyle.Link => $"background:transparent;color:{css};text-decoration:underline;padding:0;",
            _ => $"background:{css};color:{onColor};border-radius:{radius}pt;padding:6pt 15pt;",
        };
        var text = Encode(string.IsNullOrWhiteSpace(button.Text) ? "Button" : button.Text);
        var url = string.IsNullOrWhiteSpace(button.Url) ? string.Empty : $"<span class=\"cr-button-url\">{Encode(button.Url)}</span>";
        return $"<div class=\"cr-button\" style=\"text-align:{align};margin:0 0 {N(theme.Typography.ParagraphSpacingPt)}pt\">" +
               $"<span class=\"cr-button-face\" style=\"display:inline-block;font-family:{font};font-size:{size}pt;font-weight:600;line-height:1.3;{look}\">{text}</span>{url}</div>";
    }

    private static string ContentsHtml(ContentsModule contents, Publication publication)
    {
        var depth = Math.Clamp(contents.Depth, 1, 6);
        var headings = CreatorFreeFlow.ModulesOf(publication)
            .OfType<HeadingModule>().Where(h => !h.Hidden && h.Level <= depth).ToList();
        var sb = new StringBuilder();
        sb.Append("<nav class=\"tb-toc\" aria-label=\"Contents\">");
        if (!string.IsNullOrWhiteSpace(contents.Title))
        {
            sb.Append("<div class=\"tb-toc-title\">").Append(Encode(contents.Title)).Append("</div>");
        }

        if (headings.Count == 0)
        {
            sb.Append("<p class=\"aio-p cr-empty-text\">Add heading modules and they are listed here.</p></nav>");
            return sb.ToString();
        }

        var min = headings.Min(h => h.Level);
        sb.Append("<ol class=\"tb-toc-list\">");
        foreach (var h in headings)
        {
            var text = Inline.ToPlainText(ModuleBlocks.ToBlocks(h, publication).OfType<HeadingBlock>().FirstOrDefault()?.Inlines ?? []);
            sb.Append("<li class=\"tb-toc-entry").Append(h.Level == min ? " tb-toc-top" : string.Empty)
              .Append("\" style=\"padding-left:").Append(((h.Level - min) * 14).ToString(CultureInfo.InvariantCulture)).Append("pt\">")
              .Append("<span class=\"tb-toc-label\">").Append(Encode(text)).Append("</span>");
            if (contents.ShowPageNumbers)
            {
                sb.Append("<span class=\"tb-toc-dots\" aria-hidden=\"true\"></span><span class=\"tb-toc-page\">#</span>");
            }

            sb.Append("</li>");
        }

        sb.Append("</ol></nav>");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ helpers

    private static string CacheKey(Module module, Publication publication)
    {
        var key = new StringBuilder(JsonSerializer.Serialize(module, Json));
        switch (module)
        {
            case CalloutModule callout:
                key.Append('|').Append(publication.Theme.Callouts.Kind(callout.Kind).Label);
                break;
            case ImageModule image:
                key.Append('|').Append(publication.FindAsset(image.AssetId)?.Data.Length ?? -1);
                break;
            case DividerModule or ButtonModule:
                key.Append('|').Append(publication.Theme.Colors.Accent).Append(publication.Theme.Colors.Border)
                   .Append(publication.Theme.Typography.BodyFont).Append(publication.Theme.Typography.BodySizePt)
                   .Append(publication.Theme.Typography.ParagraphSpacingPt);
                break;
        }

        key.Append('|').Append(publication.Metadata.Language);
        return key.ToString();
    }

    private static Document MetadataDocument(Publication publication)
    {
        var document = new Document();
        var meta = publication.Metadata;
        document.Metadata.Title = string.IsNullOrWhiteSpace(meta.Title) ? publication.Name : meta.Title;
        document.Metadata.Subtitle = meta.Subtitle;
        document.Metadata.Authors.AddRange(meta.Authors.Where(a => !string.IsNullOrWhiteSpace(a)));
        document.Metadata.Date = meta.Date;
        document.Metadata.Language = meta.Language;
        return document;
    }

    private static string Article(string inner) => "<article class=\"aio-doc\">" + inner + "</article>";

    private static string StripArticle(string fragment)
    {
        var start = fragment.IndexOf('>', StringComparison.Ordinal);
        if (!fragment.StartsWith("<article", StringComparison.Ordinal) || start < 0)
        {
            return fragment;
        }

        var inner = fragment[(start + 1)..];
        return inner.EndsWith("</article>", StringComparison.Ordinal) ? inner[..^"</article>".Length] : inner;
    }

    private static void AppendSpacing(StringBuilder sb, string property, Spacing? spacing)
    {
        if (spacing is null || (spacing.TopPt <= 0 && spacing.RightPt <= 0 && spacing.BottomPt <= 0 && spacing.LeftPt <= 0))
        {
            return;
        }

        sb.Append(property).Append(':')
          .Append(N(Math.Max(0, spacing.TopPt))).Append("pt ")
          .Append(N(Math.Max(0, spacing.RightPt))).Append("pt ")
          .Append(N(Math.Max(0, spacing.BottomPt))).Append("pt ")
          .Append(N(Math.Max(0, spacing.LeftPt))).Append("pt;");
    }

    private static string N(double value) => ThemeBuilderCssGenerator.N(value);

    private static string Encode(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);

    [GeneratedRegex("<figure class=\"aio-figure\"")]
    private static partial Regex FigureOpen();

    [GeneratedRegex("<img [^>]*>")]
    private static partial Regex ImgStyle();
}
