using SkiaSharp;

namespace AllInOnePdf.Layout.Infrastructure;

/// <summary>
/// A run of positioned glyphs in one font: the unit the text engine hands to the canvas. Positions are relative to
/// the run origin on the baseline (x advances, y offsets for super/subscript), in points.
/// </summary>
/// <param name="Font">The font (typeface, size, no fake styles) to draw with.</param>
/// <param name="GlyphIds">Glyph indices into the font.</param>
/// <param name="Positions">One position per glyph, relative to the run origin.</param>
/// <param name="Width">Total advance of the run in points.</param>
/// <param name="Color">Fill colour.</param>
public sealed record GlyphRun(SKFont Font, ushort[] GlyphIds, SKPoint[] Positions, float Width, Color Color);

/// <summary>
/// Everything an element can draw. Coordinates are relative to the current origin; parents move the origin with
/// <see cref="Translate"/> before drawing a child and move it back afterwards. The first layout pass uses a canvas
/// that records nothing; the second pass uses the Skia PDF canvas.
/// </summary>
public interface IDrawingCanvas
{
    /// <summary>Moves the origin.</summary>
    void Translate(Position vector);

    /// <summary>Fills a rectangle; <paramref name="cornerRadius"/> rounds the corners.</summary>
    void DrawRectangle(Position position, Size size, Color color, float cornerRadius = 0);

    /// <summary>Strokes a rectangle outline centred on its edges.</summary>
    void DrawRectangleOutline(Position position, Size size, Color color, float thickness, float cornerRadius = 0);

    /// <summary>Draws a straight line; <paramref name="dashPattern"/> alternates on/off lengths in points.</summary>
    void DrawLine(Position from, Position to, float thickness, Color color, float[]? dashPattern = null);

    /// <summary>Draws shaped text with the run origin at <paramref name="baselineOrigin"/>.</summary>
    void DrawGlyphRun(GlyphRun run, Position baselineOrigin);

    /// <summary>Draws a decoded image scaled into the rectangle.</summary>
    void DrawImage(SKImage image, Position position, Size size);

    /// <summary>Clips everything drawn until <see cref="Restore"/> to a (rounded) rectangle; pair with <see cref="Save"/>.</summary>
    void ClipRectangle(Position position, Size size, float cornerRadius = 0);

    void Save();

    void Restore();

    /// <summary>Marks a rectangle as a link to an external URL.</summary>
    void DrawHyperlink(Position position, Size size, string url);

    /// <summary>Marks a rectangle as a link to a named destination inside the document (see <see cref="DrawSectionDestination"/>).</summary>
    void DrawSectionLink(Position position, Size size, string sectionName);

    /// <summary>Declares a named destination at a point on the current page.</summary>
    void DrawSectionDestination(string sectionName, Position position);

    /// <summary>The underlying Skia canvas for drawing the engine cannot express (SVG paths). Null on the measuring pass.</summary>
    SKCanvas? Native { get; }
}

/// <summary>Records nothing: the canvas of the measuring pass.</summary>
public sealed class FreeCanvas : IDrawingCanvas
{
    public static readonly FreeCanvas Instance = new();

    public SKCanvas? Native => null;

    public void Translate(Position vector)
    {
    }

    public void DrawRectangle(Position position, Size size, Color color, float cornerRadius = 0)
    {
    }

    public void DrawRectangleOutline(Position position, Size size, Color color, float thickness, float cornerRadius = 0)
    {
    }

    public void DrawLine(Position from, Position to, float thickness, Color color, float[]? dashPattern = null)
    {
    }

    public void DrawGlyphRun(GlyphRun run, Position baselineOrigin)
    {
    }

    public void DrawImage(SKImage image, Position position, Size size)
    {
    }

    public void ClipRectangle(Position position, Size size, float cornerRadius = 0)
    {
    }

    public void Save()
    {
    }

    public void Restore()
    {
    }

    public void DrawHyperlink(Position position, Size size, string url)
    {
    }

    public void DrawSectionLink(Position position, Size size, string sectionName)
    {
    }

    public void DrawSectionDestination(string sectionName, Position position)
    {
    }
}
