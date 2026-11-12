using AllInOnePdf.Layout.Elements;
using AllInOnePdf.Layout.Infrastructure;

namespace AllInOnePdf.Layout.Fluent;

/// <summary>Builds stacked layers. Exactly one layer must be the primary layer, which decides size and pagination.</summary>
public sealed class LayersDescriptor
{
    internal LayersDescriptor()
    {
    }

    internal LayersElement Layers { get; } = new();

    /// <summary>A background or overlay layer drawn in the primary layer's size, in declaration order.</summary>
    public IContainer Layer() => Add(isPrimary: false);

    /// <summary>The layer that decides the size and pagination of the whole stack.</summary>
    public IContainer PrimaryLayer() => Add(isPrimary: true);

    internal void Validate()
    {
        var primaries = Layers.Layers.Count(static l => l.IsPrimary);
        if (primaries != 1)
        {
            throw new InvalidOperationException($"Layers need exactly one PrimaryLayer(); found {primaries}.");
        }
    }

    private SlotElement Add(bool isPrimary)
    {
        var slot = new SlotElement();
        Layers.Layers.Add((slot, isPrimary));
        return slot;
    }
}
