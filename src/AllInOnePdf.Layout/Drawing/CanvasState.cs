using AllInOnePdf.Layout.Infrastructure;

namespace AllInOnePdf.Layout.Drawing;

/// <summary>One clip applied to a canvas: the origin (relative to the page) it was applied at and its rectangle.</summary>
/// <param name="Origin">The canvas origin, relative to the page's top-left, when the clip was applied.</param>
/// <param name="Position">The clip rectangle's top-left relative to <paramref name="Origin"/>.</param>
/// <param name="Size">The clip rectangle's size.</param>
/// <param name="CornerRadius">The clip's corner radius.</param>
internal readonly record struct ClipRecord(Position Origin, Position Position, Size Size, float CornerRadius);

/// <summary>
/// A snapshot of a canvas's drawing state (the origin relative to the page and the active clips) that can be
/// re-established later on the same page, so drawing that was deferred (<c>ZIndex</c>) paints exactly where and how it
/// would have painted in place.
/// </summary>
/// <param name="Offset">The origin relative to the page's top-left.</param>
/// <param name="Clips">The active clips, outermost first.</param>
internal sealed record CanvasState(Position Offset, IReadOnlyList<ClipRecord> Clips);

/// <summary>A canvas that can capture its state and re-establish it later (see <see cref="CanvasState"/>).</summary>
internal interface ICanvasStateCapture : IPositionTracking
{
    /// <summary>The current origin and active clips.</summary>
    CanvasState CaptureState();

    /// <summary>
    /// Saves the canvas, then re-applies <paramref name="state"/>'s clips and moves the origin to its offset. Pair with
    /// <see cref="IDrawingCanvas.Restore"/>. Must be called with the origin at the same reference (the page) the state
    /// was captured against.
    /// </summary>
    void ApplyState(CanvasState state);
}

/// <summary>
/// The bookkeeping behind <see cref="ICanvasStateCapture"/>: follows translations, clips and save/restore pairs.
/// Both page canvases (<see cref="MeasuringCanvas"/>, <see cref="SkiaCanvas"/>) keep one.
/// </summary>
internal sealed class CanvasStateTracker
{
    private readonly Stack<(Position Offset, int ClipCount)> _saved = new();
    private readonly List<ClipRecord> _clips = [];

    /// <summary>The current origin relative to the page's top-left.</summary>
    public Position Offset { get; private set; }

    public void Translate(Position vector) => Offset = new Position(Offset.X + vector.X, Offset.Y + vector.Y);

    public void Save() => _saved.Push((Offset, _clips.Count));

    public void Restore()
    {
        if (_saved.Count == 0)
        {
            return;
        }

        var (offset, clipCount) = _saved.Pop();
        Offset = offset;
        if (_clips.Count > clipCount)
        {
            _clips.RemoveRange(clipCount, _clips.Count - clipCount);
        }
    }

    public void Clip(Position position, Size size, float cornerRadius) => _clips.Add(new ClipRecord(Offset, position, size, cornerRadius));

    /// <summary>Back to the page origin with no saved states and no clips.</summary>
    public void Clear()
    {
        Offset = Position.Zero;
        _saved.Clear();
        _clips.Clear();
    }

    public CanvasState Capture() => new(Offset, _clips.ToArray());

    /// <summary>Re-establishes <paramref name="state"/> on <paramref name="canvas"/> (which owns this tracker).</summary>
    public static void Apply(IDrawingCanvas canvas, CanvasStateTracker tracker, CanvasState state)
    {
        canvas.Save();
        foreach (var clip in state.Clips)
        {
            MoveTo(canvas, tracker, clip.Origin);
            canvas.ClipRectangle(clip.Position, clip.Size, clip.CornerRadius);
        }

        MoveTo(canvas, tracker, state.Offset);
    }

    private static void MoveTo(IDrawingCanvas canvas, CanvasStateTracker tracker, Position target)
    {
        var delta = new Position(target.X - tracker.Offset.X, target.Y - tracker.Offset.Y);
        if (delta.X != 0 || delta.Y != 0)
        {
            canvas.Translate(delta);
        }
    }
}
