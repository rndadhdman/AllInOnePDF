using AllInOnePdf.Layout.Infrastructure;

namespace AllInOnePdf.Layout.Text;

/// <summary>Horizontal alignment of a text block's lines.</summary>
internal enum TextAlignment
{
    Left,
    Center,
    Right,

    /// <summary>Stretch every line to the full width except the last one and lines ending in a hard break.</summary>
    Justify,
}

/// <summary>One entry of a text block: a span, a page number, or an inline element. <see cref="Style"/> is relative (unset values inherit).</summary>
internal abstract class TextBlockItem
{
    public TextStyle Style { get; set; } = TextStyle.Inherit;
}

/// <summary>Literal text, optionally a link. "\n" inside the text is a hard line break.</summary>
internal sealed class TextSpanItem(string text) : TextBlockItem
{
    public string Text { get; } = text;

    /// <summary>External link target, or null.</summary>
    public string? Url { get; init; }

    /// <summary>Internal link target (a section name), or null.</summary>
    public string? SectionName { get; init; }
}

/// <summary>Which number a <see cref="PageNumberItem"/> shows.</summary>
internal enum PageNumberKind
{
    CurrentPage,
    TotalPages,
    SectionBeginPage,
}

/// <summary>Digits that change per page; re-shaped every time the block is laid out on a page.</summary>
internal sealed class PageNumberItem(PageNumberKind kind, string? sectionName = null) : TextBlockItem
{
    public PageNumberKind Kind { get; } = kind;

    public string? SectionName { get; } = sectionName;

    /// <summary>The text for the page being laid out ("?" for numbers the first pass does not know yet).</summary>
    public string GetText(PageContext? context)
    {
        if (context is null)
        {
            return "?";
        }

        return Kind switch
        {
            PageNumberKind.CurrentPage => context.CurrentPage.ToString(System.Globalization.CultureInfo.InvariantCulture),
            PageNumberKind.TotalPages => context.IsFirstPass || context.PageCount <= 0 ? "?" : context.PageCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => SectionName is not null && context.GetSectionPage(SectionName) is { } page ? page.ToString(System.Globalization.CultureInfo.InvariantCulture) : "?",
        };
    }
}

/// <summary>An element laid out inline: its bottom sits on the baseline.</summary>
internal sealed class InlineElementItem(InlineElementHost host) : TextBlockItem
{
    public InlineElementHost Host { get; } = host;
}

/// <summary>The container <c>TextDescriptor.Element()</c> returns; transparent.</summary>
internal sealed class InlineElementHost : ContainerElement;
