namespace AllInOnePdf.Layout.Infrastructure;

/// <summary>What an element answered when asked how much of the offered space it needs.</summary>
public enum SpacePlanType
{
    /// <summary>Nothing to draw (an empty container, or content that was fully drawn on earlier pages).</summary>
    Empty,

    /// <summary>The element does not fit at all in the offered space and must start on the next page.</summary>
    Wrap,

    /// <summary>Part of the element fits; the rest continues on the next page.</summary>
    PartialRender,

    /// <summary>The whole (remaining) element fits.</summary>
    FullRender,
}

/// <summary>
/// The answer to <see cref="Element.Measure"/>: the plan type and, for partial and full renders, the size the element
/// will occupy when <see cref="Element.Draw"/> is called with the same available space.
/// </summary>
public readonly record struct SpacePlan(SpacePlanType Type, float Width, float Height)
{
    public static SpacePlan Empty() => new(SpacePlanType.Empty, 0, 0);

    public static SpacePlan Wrap() => new(SpacePlanType.Wrap, 0, 0);

    public static SpacePlan PartialRender(float width, float height) => new(SpacePlanType.PartialRender, width, height);

    public static SpacePlan PartialRender(Size size) => PartialRender(size.Width, size.Height);

    public static SpacePlan FullRender(float width, float height) => new(SpacePlanType.FullRender, width, height);

    public static SpacePlan FullRender(Size size) => FullRender(size.Width, size.Height);

    public Size Size => new(Width, Height);

    public bool IsEmpty => Type == SpacePlanType.Empty;

    public bool IsWrap => Type == SpacePlanType.Wrap;

    public bool IsPartial => Type == SpacePlanType.PartialRender;

    public bool IsFull => Type == SpacePlanType.FullRender;

    /// <summary>True for partial and full renders: something will be drawn.</summary>
    public bool HasContent => Type is SpacePlanType.PartialRender or SpacePlanType.FullRender;
}
