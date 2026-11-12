using AllInOnePdf.Core.Themes;
using AllInOnePdf.Publishing.Model;
using AllInOnePdf.Publishing.Rendering;

namespace AllInOnePdf.UI.Services;

/// <summary>
/// Geometry of a free-flow section's canvas (<see cref="SectionLayout.FreeFlow"/>) as the design canvas draws it and
/// the PDF lays it out, plus the per-type default sizes of modules dropped on a canvas and the paint order of items.
/// </summary>
/// <remarks>
/// The canvas sits inside the section's margin, border and padding. Its width is the page's content width (page
/// width minus the left and right margins) minus those insets. A full-page canvas (<see cref="Section.CanvasHeightPt"/>
/// null) is as tall as the body: the page height minus the top and bottom margins and the header and footer bands
/// (<see cref="BodyHeightPt"/>, the same formula as <c>ThemedComposer.BodyHeightPt</c>), minus the section's vertical
/// insets. A custom height is used as it is, capped to that full-page height.
/// </remarks>
public static class CreatorFreeFlow
{
    /// <summary>Points per millimetre.</summary>
    public const double PtPerMm = 72 / 25.4;

    /// <summary>Offset of a duplicated item (Ctrl+D) from the original, in points, both ways.</summary>
    public const double DuplicateOffsetPt = 12;

    /// <summary>Page width minus the left and right margins, in points.</summary>
    public static double ContentWidthPt(PdfTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        var (w, _) = theme.Page.GetSizeMm();
        return (w - theme.Page.MarginLeftMm - theme.Page.MarginRightMm) * PtPerMm;
    }

    /// <summary>
    /// Page height minus the top and bottom margins and the header and footer bands, at least 72 pt. A band is
    /// <c>size × 1.25 + spacing (+ 6 with a rule)</c> when the header or footer has content, else 0.
    /// </summary>
    public static double BodyHeightPt(PdfTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        var (_, h) = theme.Page.GetSizeMm();
        var content = (h - theme.Page.MarginTopMm - theme.Page.MarginBottomMm) * PtPerMm;
        static double Band(HeaderFooterSettings hf) => hf.HasContent ? (hf.SizePt * 1.25) + hf.SpacingPt + (hf.Rule ? 6 : 0) : 0;
        return Math.Max(72, content - Band(theme.Header) - Band(theme.Footer));
    }

    /// <summary>Width of the section's canvas in points (its content box), at least <see cref="PlacedModule.MinSizePt"/>.</summary>
    public static double CanvasWidthPt(PdfTheme theme, Section section)
    {
        ArgumentNullException.ThrowIfNull(section);
        var (left, _, right, _) = Insets(section.Design);
        return Math.Max(PlacedModule.MinSizePt, ContentWidthPt(theme) - left - right);
    }

    /// <summary>Height of a full-page canvas for the section: the body height minus the section's vertical insets.</summary>
    public static double FullPageHeightPt(PdfTheme theme, Section section)
    {
        ArgumentNullException.ThrowIfNull(section);
        var (_, top, _, bottom) = Insets(section.Design);
        return Math.Max(40, BodyHeightPt(theme) - top - bottom);
    }

    /// <summary>Height of the section's canvas in points: the custom height capped to a full page, or a full page.</summary>
    public static double CanvasHeightPt(PdfTheme theme, Section section)
    {
        ArgumentNullException.ThrowIfNull(section);
        var full = FullPageHeightPt(theme, section);
        return section.CanvasHeightPt is { } custom && double.IsFinite(custom) && custom > 0 ? Math.Min(Math.Max(40, custom), full) : full;
    }

    /// <summary>The smallest box holding every box (all on one page), on the first box's page.</summary>
    public static PublicationBox Union(IEnumerable<PublicationBox> boxes)
    {
        ArgumentNullException.ThrowIfNull(boxes);
        var list = boxes.ToList();
        if (list.Count == 0)
        {
            return default;
        }

        var left = list.Min(b => b.XPt);
        var top = list.Min(b => b.YPt);
        var right = list.Max(b => b.XPt + b.WidthPt);
        var bottom = list.Max(b => b.YPt + b.HeightPt);
        return new PublicationBox(list[0].Page, left, top, right - left, bottom - top);
    }

    /// <summary>Left, top, right and bottom distance from a box's outer edge to its content: margin + border + padding.</summary>
    public static (double Left, double Top, double Right, double Bottom) Insets(BoxDesign? design)
    {
        if (design is null)
        {
            return (0, 0, 0, 0);
        }

        static double S(double v) => double.IsFinite(v) && v > 0 ? v : 0;
        var border = S(design.BorderWidthPt);
        var m = design.Margin ?? new Spacing();
        var p = design.Padding ?? new Spacing();
        return (S(m.LeftPt) + border + S(p.LeftPt), S(m.TopPt) + border + S(p.TopPt), S(m.RightPt) + border + S(p.RightPt), S(m.BottomPt) + border + S(p.BottomPt));
    }

    /// <summary>
    /// Size a module of that type gets when it is dropped on a canvas: text 240×80, heading 300×48, image 200×150,
    /// divider 240×12, spacer 100×24, quote 260×90, callout 260×100, code 300×120, table 320×120, button 140×36,
    /// contents 260×200; anything else <see cref="PlacedModule.DefaultWidthPt"/>×<see cref="PlacedModule.DefaultHeightPt"/>.
    /// </summary>
    public static (double Width, double Height) DefaultSize(string? type) => type switch
    {
        "text" => (240, 80),
        "heading" => (300, 48),
        "image" => (200, 150),
        "divider" => (240, 12),
        "spacer" => (100, 24),
        "quote" => (260, 90),
        "callout" => (260, 100),
        "code" => (300, 120),
        "table" => (320, 120),
        "button" => (140, 36),
        "contents" => (260, 200),
        _ => (PlacedModule.DefaultWidthPt, PlacedModule.DefaultHeightPt),
    };

    /// <summary>The default size for a module instance.</summary>
    public static (double Width, double Height) DefaultSize(Module module) => DefaultSize(CreatorModuleCatalog.TypeOf(module));

    /// <summary>True when the module can go on a free-flow canvas (a page break cannot).</summary>
    public static bool Allowed(Module module) => module is not PageBreakModule;

    /// <summary>True when modules of that type can go on a free-flow canvas.</summary>
    public static bool Allowed(string? type) => !string.Equals(type, "pagebreak", StringComparison.OrdinalIgnoreCase);

    /// <summary>The section's items in paint order: ascending <see cref="PlacedModule.Z"/>, ties in list order (back to front).</summary>
    public static List<PlacedModule> PaintOrder(Section section)
    {
        ArgumentNullException.ThrowIfNull(section);
        return section.Items.Select((item, index) => (item, index)).OrderBy(t => t.item.Z).ThenBy(t => t.index).Select(t => t.item).ToList();
    }

    /// <summary>The section's items front to back (highest Z first), the order the structure tree lists them in.</summary>
    public static List<PlacedModule> FrontToBack(Section section)
    {
        var order = PaintOrder(section);
        order.Reverse();
        return order;
    }

    /// <summary>Every module of a section in reading order: rows → columns → modules, then the canvas items top to bottom.</summary>
    public static IEnumerable<Module> ModulesOf(Section section)
    {
        ArgumentNullException.ThrowIfNull(section);
        foreach (var module in section.Rows.SelectMany(r => r.Columns).SelectMany(c => c.Modules))
        {
            yield return module;
        }

        foreach (var item in section.Items.OrderBy(i => i.YPt).ThenBy(i => i.XPt))
        {
            if (item.Module is not null)
            {
                yield return item.Module;
            }
        }
    }

    /// <summary>Every module of the publication (structure and free flow) in reading order.</summary>
    public static IEnumerable<Module> ModulesOf(Publication publication)
    {
        ArgumentNullException.ThrowIfNull(publication);
        return publication.Sections.SelectMany(ModulesOf);
    }
}
