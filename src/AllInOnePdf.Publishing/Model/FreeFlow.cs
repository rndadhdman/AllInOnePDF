namespace AllInOnePdf.Publishing.Model;

/// <summary>How a section arranges its modules.</summary>
public enum SectionLayout
{
    /// <summary>Rows and columns in reading order; the content flows and breaks across pages.</summary>
    Structure,

    /// <summary>
    /// A fixed canvas, one page tall by default, where every module is placed by hand at an x, y, width, height and
    /// z level (<see cref="Section.Items"/>). The section's rows are ignored.
    /// </summary>
    FreeFlow,
}

/// <summary>A module placed by hand on the canvas of a free-flow section (<see cref="SectionLayout.FreeFlow"/>).</summary>
public sealed class PlacedModule
{
    /// <summary>Width a module gets when it is dropped on the canvas without a size of its own.</summary>
    public const double DefaultWidthPt = 240;

    /// <summary>Height a module gets when it is dropped on the canvas without a size of its own.</summary>
    public const double DefaultHeightPt = 80;

    /// <summary>Smallest width or height of a placed module.</summary>
    public const double MinSizePt = 8;

    /// <summary>Largest coordinate or size in points the validator accepts.</summary>
    public const double MaxPt = 2000;

    /// <summary>
    /// Left edge in points from the canvas's left edge. The canvas starts inside the section's margin, border and
    /// padding. May be negative; the part outside the canvas is cut off.
    /// </summary>
    public double XPt { get; set; }

    /// <summary>Top edge in points from the canvas's top edge. May be negative.</summary>
    public double YPt { get; set; }

    /// <summary>Width of the box in points. Content that does not fit is cut off.</summary>
    public double WidthPt { get; set; } = DefaultWidthPt;

    /// <summary>Height of the box in points. Content that does not fit is cut off.</summary>
    public double HeightPt { get; set; } = DefaultHeightPt;

    /// <summary>
    /// Paint order among the canvas's items, 0 to <see cref="BoxDesign.MaxZIndex"/>: a higher level paints over lower
    /// ones, equal levels paint in list order.
    /// </summary>
    public int Z { get; set; }

    /// <summary>Resizing keeps the width-to-height ratio (the Creator sets it for images).</summary>
    public bool LockAspect { get; set; }

    /// <summary>
    /// The module itself. Its <see cref="Module.Design"/> applies inside the box; the design's offsets and Z level are
    /// ignored here because the box already says where the module goes.
    /// </summary>
    public Module Module { get; set; } = new TextModule();
}
