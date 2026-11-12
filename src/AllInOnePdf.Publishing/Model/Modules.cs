using System.Text.Json.Serialization;
using AllInOnePdf.Core.Documents;

namespace AllInOnePdf.Publishing.Model;

/// <summary>
/// A content element inside a column. Serialised polymorphically with a <c>type</c> discriminator
/// (<c>text</c>, <c>heading</c>, <c>image</c> …).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextModule), "text")]
[JsonDerivedType(typeof(HeadingModule), "heading")]
[JsonDerivedType(typeof(ImageModule), "image")]
[JsonDerivedType(typeof(DividerModule), "divider")]
[JsonDerivedType(typeof(SpacerModule), "spacer")]
[JsonDerivedType(typeof(QuoteModule), "quote")]
[JsonDerivedType(typeof(CalloutModule), "callout")]
[JsonDerivedType(typeof(CodeModule), "code")]
[JsonDerivedType(typeof(TableModule), "table")]
[JsonDerivedType(typeof(ButtonModule), "button")]
[JsonDerivedType(typeof(ContentsModule), "contents")]
[JsonDerivedType(typeof(PageBreakModule), "pagebreak")]
public abstract class Module
{
    /// <summary>Unique id within the publication.</summary>
    public string Id { get; set; } = Publication.NewId();

    /// <summary>Optional name shown in the structure tree.</summary>
    public string? Label { get; set; }

    /// <summary>Box and text styling.</summary>
    public ModuleDesign Design { get; set; } = new();

    /// <summary>Hidden modules stay in the document but are not rendered or exported.</summary>
    public bool Hidden { get; set; }
}

/// <summary>Rich text written in Markdown (CommonMark + GitHub extensions, like the Markdown importer).</summary>
public sealed class TextModule : Module
{
    /// <summary>The Markdown source.</summary>
    public string Markdown { get; set; } = "";
}

/// <summary>A heading; the text allows inline Markdown (<c>**bold**</c>, links).</summary>
public sealed class HeadingModule : Module
{
    /// <summary>Heading text (inline Markdown).</summary>
    public string Text { get; set; } = "Heading";

    /// <summary>1 (largest) to 6.</summary>
    public int Level { get; set; } = 1;
}

/// <summary>An image from <see cref="Publication.Assets"/>; without an asset it is a placeholder.</summary>
public sealed class ImageModule : Module
{
    /// <summary>Id of the asset, or null for a placeholder.</summary>
    public string? AssetId { get; set; }

    /// <summary>Alternative text for accessibility.</summary>
    public string? Alt { get; set; }

    /// <summary>Caption shown below the image.</summary>
    public string? Caption { get; set; }

    /// <summary>Width as a percentage of the column (5 to 100).</summary>
    public double WidthPercent { get; set; } = 100;

    /// <summary>Placement when narrower than the column.</summary>
    public BlockAlign Align { get; set; } = BlockAlign.Center;

    /// <summary>Corner radius in points.</summary>
    public double CornerRadiusPt { get; set; }

    /// <summary>Maximum height in points, or null for no limit.</summary>
    public double? MaxHeightPt { get; set; }
}

/// <summary>A horizontal rule.</summary>
public sealed class DividerModule : Module
{
    /// <summary>Line style.</summary>
    public DividerStyle Style { get; set; } = DividerStyle.Solid;

    /// <summary>Line colour (hex), or null for the theme border colour.</summary>
    public string? Color { get; set; }

    /// <summary>Line thickness in points.</summary>
    public double ThicknessPt { get; set; } = 0.75;

    /// <summary>Width as a percentage of the column (5 to 100).</summary>
    public double WidthPercent { get; set; } = 100;

    /// <summary>Placement when narrower than the column.</summary>
    public BlockAlign Align { get; set; } = BlockAlign.Center;
}

/// <summary>Empty vertical space.</summary>
public sealed class SpacerModule : Module
{
    /// <summary>Height in points (0 to 600).</summary>
    public double HeightPt { get; set; } = 24;
}

/// <summary>A block quote in Markdown with an optional attribution.</summary>
public sealed class QuoteModule : Module
{
    /// <summary>The quoted Markdown.</summary>
    public string Markdown { get; set; } = "";

    /// <summary>Who said it.</summary>
    public string? Attribution { get; set; }
}

/// <summary>A note, tip, important, warning or caution box.</summary>
public sealed class CalloutModule : Module
{
    /// <summary>Kind; picks the theme's colours, label and icon.</summary>
    public CalloutKind Kind { get; set; } = CalloutKind.Note;

    /// <summary>Title replacing the kind's label, or null.</summary>
    public string? Title { get; set; }

    /// <summary>Body Markdown.</summary>
    public string Markdown { get; set; } = "";
}

/// <summary>Source code, highlighted by the theme.</summary>
public sealed class CodeModule : Module
{
    /// <summary>The code.</summary>
    public string Code { get; set; } = "";

    /// <summary>Language id for highlighting, or null.</summary>
    public string? Language { get; set; }

    /// <summary>Caption or file name shown above the code.</summary>
    public string? Caption { get; set; }
}

/// <summary>A table; cell text is inline Markdown.</summary>
public sealed class TableModule : Module
{
    /// <summary>Treat the first row as a header.</summary>
    public bool HeaderRow { get; set; } = true;

    /// <summary>Rows of cells (inline Markdown). Rows are padded to the widest row by the validator.</summary>
    public List<List<string>> Cells { get; set; } = [];

    /// <summary>Per-column alignment, one entry per column.</summary>
    public List<ColumnAlignment> Alignments { get; set; } = [];

    /// <summary>Caption shown with the table.</summary>
    public string? Caption { get; set; }
}

/// <summary>A call-to-action button (a link styled as a button in the PDF).</summary>
public sealed class ButtonModule : Module
{
    /// <summary>Button label.</summary>
    public string Text { get; set; } = "Learn more";

    /// <summary>Link target, or null for a button without a link.</summary>
    public string? Url { get; set; }

    /// <summary>Filled, outlined or a plain link.</summary>
    public ButtonStyle Style { get; set; } = ButtonStyle.Filled;

    /// <summary>Placement in the column.</summary>
    public BlockAlign Align { get; set; } = BlockAlign.Left;

    /// <summary>Button colour (hex), or null for the theme accent.</summary>
    public string? Color { get; set; }

    /// <summary>Corner radius in points.</summary>
    public double CornerRadiusPt { get; set; } = 4;
}

/// <summary>A table of contents built from the publication's headings.</summary>
public sealed class ContentsModule : Module
{
    /// <summary>Title above the entries.</summary>
    public string Title { get; set; } = "Contents";

    /// <summary>Deepest heading level listed (1 to 6).</summary>
    public int Depth { get; set; } = 2;

    /// <summary>Show page numbers with dot leaders.</summary>
    public bool ShowPageNumbers { get; set; } = true;
}

/// <summary>Forces the following content onto a new page.</summary>
public sealed class PageBreakModule : Module;

/// <summary>Line style of a <see cref="DividerModule"/>.</summary>
public enum DividerStyle
{
    /// <summary>A continuous line.</summary>
    Solid,

    /// <summary>Dashes.</summary>
    Dashed,

    /// <summary>Dots.</summary>
    Dotted,
}

/// <summary>Look of a <see cref="ButtonModule"/>.</summary>
public enum ButtonStyle
{
    /// <summary>Solid background, light text.</summary>
    Filled,

    /// <summary>Border only, coloured text.</summary>
    Outline,

    /// <summary>Text link without a box.</summary>
    Link,
}
