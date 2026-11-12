using System.Runtime.CompilerServices;
using AllInOnePdf.Layout.Infrastructure;

namespace AllInOnePdf.Layout.Elements;

/// <summary>
/// Per-document scratch state the layout elements share, attached to the document's <see cref="PageContext"/> (so two
/// documents laid out on two threads never see each other's state): the text style stack used while the tree is
/// initialised, and the soft-reset flag the page loop raises while it re-arms headers and footers for the next page.
/// </summary>
internal sealed class DocumentState
{
    private static readonly ConditionalWeakTable<PageContext, DocumentState> States = new();

    /// <summary>The resolved styles of the <see cref="TextStyleScope"/> elements enclosing the node being initialised.</summary>
    public Stack<TextStyle> TextStyles { get; } = new();

    /// <summary>
    /// True while the page loop resets a header or footer between pages: elements whose state must survive a page
    /// (<see cref="SkipOnceElement"/>) keep it; a hard reset between passes clears everything.
    /// </summary>
    public bool IsSoftReset { get; set; }

    /// <summary>Drawing deferred by <see cref="ZIndexElement"/> until the page loop paints the upper layers of the page.</summary>
    public DeferredLayers Layers { get; } = new();

    public static DocumentState Of(PageContext context) => States.GetValue(context, static _ => new DocumentState());
}

/// <summary>Shared numeric helpers.</summary>
internal static class LayoutMath
{
    /// <summary>Tolerance for "fits" comparisons (QuestPDF's <c>Size.Epsilon</c>).</summary>
    public const float Epsilon = 0.001f;
}

/// <summary>A plain single-child container: the slots behind <c>Item()</c>, <c>Header()</c>, <c>Layer()</c> and friends.</summary>
internal sealed class SlotElement : ContainerElement;
