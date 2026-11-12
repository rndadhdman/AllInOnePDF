namespace AllInOnePdf.Layout.Infrastructure;

/// <summary>A width and height in points.</summary>
public readonly record struct Size(float Width, float Height)
{
    public static readonly Size Zero = new(0, 0);

    /// <summary>The largest size the engine ever offers (used by unconstrained measurements).</summary>
    public const float Infinity = 14400;

    public static readonly Size Max = new(Infinity, Infinity);
}

/// <summary>A point relative to the current canvas origin, in points, y growing downwards.</summary>
public readonly record struct Position(float X, float Y)
{
    public static readonly Position Zero = new(0, 0);

    public Position Reverse() => new(-X, -Y);
}

/// <summary>Point conversions for the page and margin API.</summary>
public enum Unit
{
    Point,
    Millimetre,
    Centimetre,
    Inch,
}

public static class UnitExtensions
{
    /// <summary>Converts a length in <paramref name="unit"/> to points (1/72 inch).</summary>
    public static float ToPoints(this float value, Unit unit) => unit switch
    {
        Unit.Millimetre => value * 72f / 25.4f,
        Unit.Centimetre => value * 72f / 2.54f,
        Unit.Inch => value * 72f,
        _ => value,
    };
}
