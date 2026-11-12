using AllInOnePdf.Core;
using AllInOnePdf.Core.Rendering;
using AllInOnePdf.Core.Themes;

namespace AllInOnePdf.UI.Services;

/// <summary>
/// Page-1 thumbnails for the Themes gallery (singleton, shared by every circuit / window): renders the welcome sample
/// as a draft PDF, rasterises page 0 at low DPI and keeps one data URL per theme id. A newer
/// <see cref="PdfTheme.Modified"/> replaces the entry; the cache is bounded (least recently used entries go first)
/// and thread-safe. At most two renders run at once, on the thread pool, and every render honours cancellation.
/// </summary>
public sealed class ThemeBuilderThumbnailCache : IDisposable
{
    /// <summary>Most thumbnails kept in memory (each is a small JPEG data URL, roughly 10–30 KB).</summary>
    public const int Capacity = 50;

    private const int Dpi = 64;

    private readonly IPdfRenderer _renderer;
    private readonly IPdfRasterizer _rasterizer;
    private readonly SemaphoreSlim _gate = new(2, 2);
    private readonly Lock _lock = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _lru = new();

    private sealed record Entry(string Id, long Stamp, string DataUrl);

    public ThemeBuilderThumbnailCache(IPdfRenderer renderer, IPdfRasterizer rasterizer)
    {
        _renderer = renderer;
        _rasterizer = rasterizer;
    }

    /// <summary>Returns the cached thumbnail when it is current for <paramref name="theme"/>.</summary>
    public bool TryGet(PdfTheme theme, out string? dataUrl)
    {
        var stamp = StampOf(theme);
        lock (_lock)
        {
            if (_entries.TryGetValue(theme.Id, out var node) && node.Value.Stamp == stamp)
            {
                Touch(node);
                dataUrl = node.Value.DataUrl;
                return true;
            }
        }

        dataUrl = null;
        return false;
    }

    /// <summary>Returns the cached thumbnail or renders it off the UI thread. Returns null when rendering fails.</summary>
    public async Task<string?> GetAsync(PdfTheme theme, CancellationToken cancellationToken = default)
    {
        if (TryGet(theme, out var cached))
        {
            return cached;
        }

        // Snapshot the theme now: the caller may keep mutating its instance while we render.
        var snapshot = theme.Clone();
        var id = theme.Id;
        var stamp = StampOf(theme);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (TryGet(theme, out cached))
            {
                return cached;
            }

            var url = await Task.Run(() => RenderAsync(snapshot, cancellationToken), cancellationToken);
            Store(id, stamp, url);
            return url;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate(string themeId)
    {
        lock (_lock)
        {
            if (_entries.Remove(themeId, out var node))
            {
                _lru.Remove(node);
            }
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task<string> RenderAsync(PdfTheme theme, CancellationToken cancellationToken)
    {
        // A fresh document per render: renderers assign heading ids on the model, so never share it across threads.
        var document = await SampleDocuments.CreateWelcomeAsync(cancellationToken);
        var pdf = await _renderer.RenderAsync(document, theme, new PdfRenderOptions { Draft = true, EmbedMetadata = false }, null, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var image = await _rasterizer.RasterizePageAsync(pdf, 0, new RasterizeOptions
        {
            Dpi = Dpi,
            Encoding = ImageEncoding.Jpeg,
            JpegQuality = 82,
            WithAnnotations = false,
        }, cancellationToken);
        return image.ToDataUrl();
    }

    private void Store(string id, long stamp, string url)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(id, out var existing))
            {
                if (existing.Value.Stamp > stamp)
                {
                    // A newer version of this theme was rendered meanwhile; keep it.
                    return;
                }

                _lru.Remove(existing);
            }

            _entries[id] = _lru.AddFirst(new Entry(id, stamp, url));
            while (_entries.Count > Capacity && _lru.Last is { } oldest)
            {
                _lru.RemoveLast();
                _entries.Remove(oldest.Value.Id);
            }
        }
    }

    private void Touch(LinkedListNode<Entry> node)
    {
        if (node != _lru.First)
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
        }
    }

    // Built-ins are re-created on every read (fresh Modified) and never change, so they always share one stamp.
    private static long StampOf(PdfTheme theme) => theme.IsBuiltIn ? 0 : theme.Modified.UtcTicks;
}
