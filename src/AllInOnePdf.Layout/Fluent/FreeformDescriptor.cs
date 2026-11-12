using AllInOnePdf.Layout.Elements;
using AllInOnePdf.Layout.Infrastructure;

namespace AllInOnePdf.Layout.Fluent;

/// <summary>Places the children of a <c>Freeform</c> canvas.</summary>
public sealed class FreeformDescriptor
{
    internal FreeformDescriptor()
    {
    }

    internal FreeformElement Freeform { get; } = new();

    /// <summary>
    /// Places a child with its top-left at (<paramref name="x"/>, <paramref name="y"/>) points from the canvas's
    /// top-left, measured and drawn at <paramref name="width"/> × <paramref name="height"/> and clipped to that box
    /// and to the canvas. Items paint in ascending <paramref name="z"/>, ties in the order they were added; they may
    /// extend past the canvas's edges (that part is clipped). Content that does not fit its box is drawn as far as it
    /// fits and, when <paramref name="key"/> is given, reported in <see cref="DocumentLayoutInfo.Overflowing"/>.
    /// </summary>
    /// <param name="x">Left edge in points from the canvas's left edge (may be negative).</param>
    /// <param name="y">Top edge in points from the canvas's top edge (may be negative).</param>
    /// <param name="width">Width of the box in points (negative counts as 0).</param>
    /// <param name="height">Height of the box in points (negative counts as 0).</param>
    /// <param name="z">Paint order among the canvas's items (higher paints later, over lower ones).</param>
    /// <param name="key">Reported in <see cref="DocumentLayoutInfo.Overflowing"/> when the content does not fit.</param>
    /// <returns>The container for the item's content.</returns>
    public IContainer Item(float x, float y, float width, float height, int z = 0, string? key = null)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(width) || !float.IsFinite(height))
        {
            throw new ArgumentException("A freeform item needs a finite position and size.");
        }

        var slot = new SlotElement();
        Freeform.Items.Add(new FreeformItem(x, y, width, height, z, Freeform.Items.Count, key, slot));
        return slot;
    }
}
