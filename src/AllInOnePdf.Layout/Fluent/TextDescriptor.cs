using AllInOnePdf.Layout.Infrastructure;
using AllInOnePdf.Layout.Text;

namespace AllInOnePdf.Layout.Fluent;

/// <summary>Builds a text block (a paragraph): its spans, page numbers, inline elements, alignment and clamping.</summary>
public sealed class TextDescriptor
{
    private readonly TextBlock _block;

    internal TextDescriptor(TextBlock block)
    {
        _block = block;
    }

    /// <summary>The base style of the block's spans (applied over the enclosing default style).</summary>
    public void DefaultTextStyle(Func<TextStyle, TextStyle> style)
    {
        ArgumentNullException.ThrowIfNull(style);
        _block.DefaultStyle = style(_block.DefaultStyle) ?? TextStyle.Inherit;
    }

    /// <summary>Lines start at the left edge (the default).</summary>
    public void AlignLeft() => _block.Alignment = TextAlignment.Left;

    /// <summary>Lines are centred.</summary>
    public void AlignCenter() => _block.Alignment = TextAlignment.Center;

    /// <summary>Lines end at the right edge.</summary>
    public void AlignRight() => _block.Alignment = TextAlignment.Right;

    /// <summary>Every line but the last (and lines ending in a hard break) is stretched to the full width.</summary>
    public void Justify() => _block.Alignment = TextAlignment.Justify;

    /// <summary>Indents the first line, and the first line after every hard break, by <paramref name="points"/>.</summary>
    public void ParagraphFirstLineIndentation(float points) => _block.FirstLineIndent = Math.Max(0, points);

    /// <summary>Shows at most <paramref name="maxLines"/> lines; when text was cut the last line ends with <paramref name="ellipsis"/>.</summary>
    public void ClampLines(int maxLines, string ellipsis = TextBlock.DefaultEllipsis)
    {
        _block.MaxLines = Math.Max(0, maxLines);
        _block.Ellipsis = ellipsis ?? string.Empty;
    }

    /// <summary>Adds text; "\n" inside it is a hard line break.</summary>
    public TextSpanDescriptor Span(string? text) => Add(new TextSpanItem(text ?? string.Empty));

    /// <summary>Adds text that links to an external URL.</summary>
    public TextSpanDescriptor Hyperlink(string? text, string url) => Add(new TextSpanItem(text ?? string.Empty) { Url = url });

    /// <summary>Adds text that links to a section declared with <c>Section(name)</c>.</summary>
    public TextSpanDescriptor SectionLink(string? text, string sectionName) => Add(new TextSpanItem(text ?? string.Empty) { SectionName = sectionName });

    /// <summary>Adds the number of the page on which the section starts ("?" while unknown).</summary>
    public TextSpanDescriptor BeginPageNumberOfSection(string sectionName) => Add(new PageNumberItem(PageNumberKind.SectionBeginPage, sectionName));

    /// <summary>Adds the current page number.</summary>
    public TextSpanDescriptor CurrentPageNumber() => Add(new PageNumberItem(PageNumberKind.CurrentPage));

    /// <summary>Adds the document's page count ("?" in the measuring pass).</summary>
    public TextSpanDescriptor TotalPages() => Add(new PageNumberItem(PageNumberKind.TotalPages));

    /// <summary>Adds an inline element (a small image) whose bottom sits on the baseline; returns its container.</summary>
    public IContainer Element()
    {
        var host = new InlineElementHost();
        _block.Add(new InlineElementItem(host));
        return host;
    }

    private TextSpanDescriptor Add(TextBlockItem item)
    {
        _block.Add(item);
        return new TextSpanDescriptor(item);
    }
}
