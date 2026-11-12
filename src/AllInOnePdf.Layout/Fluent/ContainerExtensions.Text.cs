using AllInOnePdf.Layout.Infrastructure;
using AllInOnePdf.Layout.Text;

namespace AllInOnePdf.Layout.Fluent;

/// <summary>Fluent container methods (text part).</summary>
public static partial class ContainerExtensions
{
    /// <summary>Puts a text block (paragraph) in the container, built by <paramref name="content"/>.</summary>
    public static void Text(this IContainer container, Action<TextDescriptor> content)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(content);
        var block = new TextBlock();
        content(new TextDescriptor(block));
        container.SetChild(block);
    }

    /// <summary>Puts a one-span text block in the container and returns the span for styling.</summary>
    public static TextSpanDescriptor Text(this IContainer container, string? text)
    {
        ArgumentNullException.ThrowIfNull(container);
        var block = new TextBlock();
        var span = new TextDescriptor(block).Span(text);
        container.SetChild(block);
        return span;
    }
}
