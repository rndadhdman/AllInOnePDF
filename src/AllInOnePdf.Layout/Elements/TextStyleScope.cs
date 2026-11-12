using AllInOnePdf.Layout.Infrastructure;

namespace AllInOnePdf.Layout.Elements;

/// <summary>
/// A container that pushes a default text style for its subtree (<c>DefaultTextStyle</c> on a container or a page).
/// Its <see cref="Style"/> is partial: unset properties come from the nearest enclosing scope, up to
/// <see cref="TextStyle.Default"/>. Text elements read the style that applies to them with
/// <see cref="GetInherited"/> from their <see cref="Element.Initialize"/>.
/// </summary>
public sealed class TextStyleScope : ContainerElement
{
    /// <summary>Creates a scope that applies <paramref name="style"/> over the enclosing style.</summary>
    public TextStyleScope(TextStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        Style = style;
    }

    /// <summary>The style as declared (unset properties inherit).</summary>
    public TextStyle Style { get; }

    /// <summary>The declared style resolved against the enclosing scopes; valid after <see cref="Initialize"/>.</summary>
    public TextStyle EffectiveStyle { get; private set; } = TextStyle.Default;

    /// <inheritdoc />
    public override void Initialize(PageContext pageContext, IDrawingCanvas canvas)
    {
        ArgumentNullException.ThrowIfNull(pageContext);
        EffectiveStyle = Style.Apply(GetInherited(pageContext));
        InitializeWithin(pageContext, EffectiveStyle, () => base.Initialize(pageContext, canvas));
    }

    /// <summary>
    /// The fully resolved style of the nearest enclosing <see cref="TextStyleScope"/> of the element being initialised,
    /// or <see cref="TextStyle.Default"/> outside any scope. Only meaningful while <see cref="Element.Initialize"/>
    /// runs (call it there and keep the result): the scopes are a stack that exists during initialisation only.
    /// </summary>
    /// <param name="pageContext">The context passed to <see cref="Element.Initialize"/>.</param>
    public static TextStyle GetInherited(PageContext pageContext)
    {
        ArgumentNullException.ThrowIfNull(pageContext);
        var stack = DocumentState.Of(pageContext).TextStyles;
        return stack.Count > 0 ? stack.Peek() : TextStyle.Default;
    }

    /// <summary>
    /// Shorthand for <c>style.Apply(GetInherited(pageContext))</c>: a text element's own partial style resolved against
    /// the enclosing default.
    /// </summary>
    /// <param name="style">The element's own (partial) style.</param>
    /// <param name="pageContext">The context passed to <see cref="Element.Initialize"/>.</param>
    public static TextStyle Resolve(TextStyle style, PageContext pageContext)
    {
        ArgumentNullException.ThrowIfNull(style);
        return style.Apply(GetInherited(pageContext));
    }

    /// <summary>Runs <paramref name="initialize"/> with <paramref name="inherited"/> as the enclosing style (used for content created during layout).</summary>
    internal static void InitializeWithin(PageContext pageContext, TextStyle inherited, Action initialize)
    {
        var stack = DocumentState.Of(pageContext).TextStyles;
        stack.Push(inherited);
        try
        {
            initialize();
        }
        finally
        {
            stack.Pop();
        }
    }
}
