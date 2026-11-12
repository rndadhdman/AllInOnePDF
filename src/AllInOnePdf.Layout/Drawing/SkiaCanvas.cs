using AllInOnePdf.Layout.Infrastructure;
using SkiaSharp;

namespace AllInOnePdf.Layout.Drawing;

/// <summary>A canvas that knows where its origin is relative to the page's top-left (for captured positions).</summary>
internal interface IPositionTracking
{
    /// <summary>The current origin relative to the page's top-left corner.</summary>
    Position Offset { get; }
}

/// <summary>
/// The canvas of the measuring pass: draws nothing but follows <see cref="Translate"/>, <see cref="Save"/> and
/// <see cref="Restore"/> so elements can learn their position on the page, and collects the glyphs every run would
/// draw (<see cref="Glyphs"/>) so the drawing pass can embed subset fonts.
/// </summary>
internal sealed class MeasuringCanvas : IDrawingCanvas, ICanvasStateCapture
{
    private readonly CanvasStateTracker _state = new();

    public Position Offset => _state.Offset;

    /// <summary>The glyphs drawn so far, per font.</summary>
    public GlyphUsage Glyphs { get; } = new();

    public SKCanvas? Native => null;

    public void Translate(Position vector) => _state.Translate(vector);

    public void Save() => _state.Save();

    public void Restore() => _state.Restore();

    /// <summary>Back to the page origin with no saved states (start of a page).</summary>
    public void ResetOrigin() => _state.Clear();

    public CanvasState CaptureState() => _state.Capture();

    public void ApplyState(CanvasState state) => CanvasStateTracker.Apply(this, _state, state);

    public void DrawRectangle(Position position, Size size, Color color, float cornerRadius = 0)
    {
    }

    public void DrawRectangleOutline(Position position, Size size, Color color, float thickness, float cornerRadius = 0)
    {
    }

    public void DrawLine(Position from, Position to, float thickness, Color color, float[]? dashPattern = null)
    {
    }

    public void DrawGlyphRun(GlyphRun run, Position baselineOrigin) => Glyphs.Record(run);

    public void DrawImage(SKImage image, Position position, Size size)
    {
    }

    public void ClipRectangle(Position position, Size size, float cornerRadius = 0) => _state.Clip(position, size, cornerRadius);

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

/// <summary>
/// <see cref="IDrawingCanvas"/> over a Skia canvas: the PDF page canvas of the drawing pass, or any
/// <see cref="SKCanvas"/> (a bitmap for previews and tests). Not thread-safe; one instance per document.
/// </summary>
public sealed class SkiaCanvas : IDrawingCanvas, ICanvasStateCapture, IDisposable
{
    private readonly SKPaint _fill = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPaint _stroke = new() { Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPaint _text = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKTextBlobBuilder _blobs = new();
    private readonly CanvasStateTracker _state = new();
    private SKCanvas? _canvas;

    /// <summary>Draws onto <paramref name="canvas"/>, starting at its current origin.</summary>
    /// <param name="canvas">The target; owned by the caller.</param>
    public SkiaCanvas(SKCanvas canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        _canvas = canvas;
    }

    /// <summary>A canvas without a target until <see cref="SetTarget"/> (the PDF writer hands one per page).</summary>
    internal SkiaCanvas()
    {
    }

    /// <inheritdoc />
    public SKCanvas? Native => _canvas;

    Position IPositionTracking.Offset => _state.Offset;

    /// <summary>Subset typefaces to draw glyph runs with (the PDF pass of a document); null draws with the runs' own fonts.</summary>
    internal FontSubsets? Subsets { get; set; }

    /// <summary>Switches to a new target (a new PDF page), or to none: drawing is then discarded.</summary>
    internal void SetTarget(SKCanvas? canvas)
    {
        _canvas = canvas;
        _state.Clear();
    }

    /// <inheritdoc />
    CanvasState ICanvasStateCapture.CaptureState() => _state.Capture();

    /// <inheritdoc />
    void ICanvasStateCapture.ApplyState(CanvasState state) => CanvasStateTracker.Apply(this, _state, state);

    /// <inheritdoc />
    public void Translate(Position vector)
    {
        _state.Translate(vector);
        _canvas?.Translate(vector.X, vector.Y);
    }

    /// <inheritdoc />
    public void Save()
    {
        _state.Save();
        _canvas?.Save();
    }

    /// <inheritdoc />
    public void Restore()
    {
        _state.Restore();
        _canvas?.Restore();
    }

    /// <inheritdoc />
    public void DrawRectangle(Position position, Size size, Color color, float cornerRadius = 0)
    {
        if (_canvas is null || color.Alpha == 0 || size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        _fill.Color = color.ToSKColor();
        var rect = SKRect.Create(position.X, position.Y, size.Width, size.Height);
        if (cornerRadius > 0)
        {
            using var rounded = new SKRoundRect(rect, cornerRadius);
            _canvas.DrawRoundRect(rounded, _fill);
        }
        else
        {
            _canvas.DrawRect(rect, _fill);
        }
    }

    /// <inheritdoc />
    public void DrawRectangleOutline(Position position, Size size, Color color, float thickness, float cornerRadius = 0)
    {
        if (_canvas is null || color.Alpha == 0 || thickness <= 0)
        {
            return;
        }

        _stroke.Color = color.ToSKColor();
        _stroke.StrokeWidth = thickness;
        _stroke.PathEffect = null;
        var rect = SKRect.Create(position.X, position.Y, size.Width, size.Height);
        if (cornerRadius > 0)
        {
            using var rounded = new SKRoundRect(rect, cornerRadius);
            _canvas.DrawRoundRect(rounded, _stroke);
        }
        else
        {
            _canvas.DrawRect(rect, _stroke);
        }
    }

    /// <inheritdoc />
    public void DrawLine(Position from, Position to, float thickness, Color color, float[]? dashPattern = null)
    {
        if (_canvas is null || color.Alpha == 0 || thickness <= 0)
        {
            return;
        }

        _stroke.Color = color.ToSKColor();
        _stroke.StrokeWidth = thickness;
        using var dash = dashPattern is { Length: >= 2 } && dashPattern.Length % 2 == 0 && dashPattern.All(static d => d >= 0) && dashPattern.Any(static d => d > 0)
            ? SKPathEffect.CreateDash(dashPattern, 0)
            : null;
        _stroke.PathEffect = dash;
        _canvas.DrawLine(from.X, from.Y, to.X, to.Y, _stroke);
        _stroke.PathEffect = null;
    }

    /// <inheritdoc />
    public void DrawGlyphRun(GlyphRun run, Position baselineOrigin)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (_canvas is null || run.GlyphIds.Length == 0 || run.Color.Alpha == 0)
        {
            return;
        }

        var count = Math.Min(run.GlyphIds.Length, run.Positions.Length);
        var font = Subsets?.Resolve(run.Font, run.GlyphIds.AsSpan(0, count)) ?? run.Font;

        // With the source text and clusters, Skia's PDF backend maps every glyph back to its characters (ligatures,
        // alternates, fallback faces), as it does for the glyphs of Skia's paragraph.
        if (Text.GlyphRunText.Of(run)?.ToUtf8(count) is { } source)
        {
            var (utf8, clusters) = source;
            if (IsHorizontal(run.Positions, count))
            {
                var xs = new float[count];
                for (var i = 0; i < count; i++)
                {
                    xs[i] = run.Positions[i].X;
                }

                var horizontal = _blobs.AllocateHorizontalTextRun(font, count, 0, utf8.Length);
                horizontal.SetGlyphs(run.GlyphIds.AsSpan(0, count));
                horizontal.SetPositions(xs);
                horizontal.SetText(utf8);
                horizontal.SetClusters(clusters);
            }
            else
            {
                var textBuffer = _blobs.AllocatePositionedTextRun(font, count, utf8.Length);
                textBuffer.SetGlyphs(run.GlyphIds.AsSpan(0, count));
                textBuffer.SetPositions(run.Positions.AsSpan(0, count));
                textBuffer.SetText(utf8);
                textBuffer.SetClusters(clusters);
            }
        }
        else
        {
            var buffer = _blobs.AllocatePositionedRun(font, count);
            buffer.SetGlyphs(run.GlyphIds.AsSpan(0, count));
            buffer.SetPositions(run.Positions.AsSpan(0, count));
        }

        using var blob = _blobs.Build();
        if (blob is null)
        {
            return;
        }

        _text.Color = run.Color.ToSKColor();
        _canvas.DrawText(blob, baselineOrigin.X, baselineOrigin.Y, _text);
    }

    private static bool IsHorizontal(SKPoint[] positions, int count)
    {
        for (var i = 0; i < count; i++)
        {
            if (positions[i].Y != 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public void DrawImage(SKImage image, Position position, Size size)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (_canvas is null || size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        _canvas.DrawImage(image, SKRect.Create(position.X, position.Y, size.Width, size.Height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
    }

    /// <inheritdoc />
    public void ClipRectangle(Position position, Size size, float cornerRadius = 0)
    {
        _state.Clip(position, size, cornerRadius);
        if (_canvas is null)
        {
            return;
        }

        var rect = SKRect.Create(position.X, position.Y, Math.Max(0, size.Width), Math.Max(0, size.Height));
        if (cornerRadius > 0)
        {
            using var rounded = new SKRoundRect(rect, cornerRadius);
            _canvas.ClipRoundRect(rounded, SKClipOperation.Intersect, antialias: true);
        }
        else
        {
            _canvas.ClipRect(rect, SKClipOperation.Intersect, antialias: true);
        }
    }

    /// <inheritdoc />
    public void DrawHyperlink(Position position, Size size, string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (_canvas is null || size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        using var data = _canvas.DrawUrlAnnotation(SKRect.Create(position.X, position.Y, size.Width, size.Height), url);
    }

    /// <inheritdoc />
    public void DrawSectionLink(Position position, Size size, string sectionName)
    {
        ArgumentNullException.ThrowIfNull(sectionName);
        if (_canvas is null || size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        using var data = _canvas.DrawLinkDestinationAnnotation(SKRect.Create(position.X, position.Y, size.Width, size.Height), sectionName);
    }

    /// <inheritdoc />
    public void DrawSectionDestination(string sectionName, Position position)
    {
        ArgumentNullException.ThrowIfNull(sectionName);
        if (_canvas is null)
        {
            return;
        }

        using var data = _canvas.DrawNamedDestinationAnnotation(new SKPoint(position.X, position.Y), sectionName);
    }

    /// <summary>Releases the paints; the target canvas belongs to the caller.</summary>
    public void Dispose()
    {
        _fill.Dispose();
        _stroke.Dispose();
        _text.Dispose();
        _blobs.Dispose();
    }
}
