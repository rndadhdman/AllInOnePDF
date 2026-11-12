namespace AllInOnePdf.Publishing.Model;

/// <summary>A band of the publication holding rows. Sections can start or end with a page break.</summary>
public sealed class Section
{
    /// <summary>Unique id within the publication.</summary>
    public string Id { get; set; } = Publication.NewId();

    /// <summary>Optional name shown in the structure tree.</summary>
    public string? Label { get; set; }

    /// <summary>Box around the section (margin, padding, background, border).</summary>
    public BoxDesign Design { get; set; } = new();

    /// <summary>Start the section on a new page.</summary>
    public bool PageBreakBefore { get; set; }

    /// <summary>Start the next content on a new page.</summary>
    public bool PageBreakAfter { get; set; }

    /// <summary>Structure (rows and columns) or Free flow (a fixed canvas with modules placed by hand).</summary>
    public SectionLayout Layout { get; set; } = SectionLayout.Structure;

    /// <summary>
    /// Height of a free-flow canvas in points; null means one full page (the body height left after the page margins,
    /// header and footer). Ignored for <see cref="SectionLayout.Structure"/>; the renderer never lets it exceed a page.
    /// </summary>
    public double? CanvasHeightPt { get; set; }

    /// <summary>Rows in reading order. Ignored for <see cref="SectionLayout.FreeFlow"/>.</summary>
    public List<Row> Rows { get; set; } = [];

    /// <summary>Modules placed on a free-flow canvas. Ignored for <see cref="SectionLayout.Structure"/>.</summary>
    public List<PlacedModule> Items { get; set; } = [];
}

/// <summary>A horizontal split of a section into columns sized by <see cref="Fractions"/>.</summary>
public sealed class Row
{
    /// <summary>Unique id within the publication.</summary>
    public string Id { get; set; } = Publication.NewId();

    /// <summary>Optional name shown in the structure tree.</summary>
    public string? Label { get; set; }

    /// <summary>Relative column widths, positive and summing to 1. <c>Columns.Count == Fractions.Count</c>.</summary>
    public List<double> Fractions { get; set; } = [1];

    /// <summary>Space between columns in points.</summary>
    public double GutterPt { get; set; } = 16;

    /// <summary>Vertical alignment of the columns within the row.</summary>
    public VerticalAlign Align { get; set; } = VerticalAlign.Top;

    /// <summary>Keep the row on one page when it fits (the renderer decides by estimated height).</summary>
    public bool KeepTogether { get; set; } = true;

    /// <summary>Box around the row.</summary>
    public BoxDesign Design { get; set; } = new();

    /// <summary>One column per fraction.</summary>
    public List<Column> Columns { get; set; } = [new()];
}

/// <summary>A vertical stack of modules inside a row.</summary>
public sealed class Column
{
    /// <summary>Unique id within the publication.</summary>
    public string Id { get; set; } = Publication.NewId();

    /// <summary>Box around the column.</summary>
    public BoxDesign Design { get; set; } = new();

    /// <summary>Modules in reading order.</summary>
    public List<Module> Modules { get; set; } = [];
}

/// <summary>Vertical alignment of a row's columns.</summary>
public enum VerticalAlign
{
    /// <summary>Columns start at the top of the row.</summary>
    Top,

    /// <summary>Columns are centred vertically.</summary>
    Middle,

    /// <summary>Columns end at the bottom of the row.</summary>
    Bottom,
}

/// <summary>Horizontal placement of a block narrower than its column.</summary>
public enum BlockAlign
{
    /// <summary>Against the left edge.</summary>
    Left,

    /// <summary>Centred.</summary>
    Center,

    /// <summary>Against the right edge.</summary>
    Right,
}
