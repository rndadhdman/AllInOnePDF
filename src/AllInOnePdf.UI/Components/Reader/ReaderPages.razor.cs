using System.Globalization;
using System.Text.Json;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Rendering;
using AllInOnePdf.Core.Themes;
using AllInOnePdf.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace AllInOnePdf.UI.Components.Reader;

public partial class ReaderPages
{
    private const string ModulePath = "./_content/AllInOnePdf.UI/Components/Reader/ReaderPages.razor.js";
    private const double CssPxPerPt = 96d / 72d;
    private static readonly double[] ZoomSteps = [0.25, 0.33, 0.5, 0.67, 0.75, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5, 3, 4];

    private enum LoadStatus { Rendering, Ready, Failed }

    [Parameter, EditorRequired] public Document Document { get; set; } = default!;

    [Inject] private IPdfRenderer Renderer { get; set; } = default!;
    [Inject] private IPdfRasterizer Rasterizer { get; set; } = default!;
    [Inject] private IThemeStore Themes { get; set; } = default!;
    [Inject] private ReaderSession Session { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private ILogger<ReaderPages> Logger { get; set; } = default!;

    private readonly SemaphoreSlim _rasterGate = new(1, 1);
    private ElementReference _scroller;
    private ElementReference _progress;
    private IJSObjectReference? _module;
    private IJSObjectReference? _handle;
    private DotNetObjectReference<ReaderPages>? _self;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _rasterCts;
    private Document? _loadedFor;
    private LoadStatus _status = LoadStatus.Rendering;
    private string? _error;
    private bool _fitWidth = true;
    private double _scale = 1;
    private double _viewportWidth;
    private double _devicePixelRatio = 1;
    private int[] _visible = [];
    private int _current;
    private bool _refreshPending;
    private double? _keepFraction;
    private bool _disposed;

    private ReaderPageCache Cache => Session.Pages;

    private int PageCount => _status == LoadStatus.Ready ? Cache.PageSizes.Count : 0;

    private bool IsActualSize => !_fitWidth && Math.Abs(_scale - 1) < 0.001;

    private string ZoomLabel => string.Create(CultureInfo.InvariantCulture, $"{Math.Round(EffectiveScale * 100):0}%");

    private double EffectiveScale
    {
        get
        {
            if (!_fitWidth || _viewportWidth <= 0 || Cache.PageSizes.Count == 0)
            {
                return _scale;
            }

            var widest = Cache.PageSizes.Max(s => s.WidthPt) * CssPxPerPt;
            return Math.Clamp(_viewportWidth / widest, ZoomSteps[0], ZoomSteps[^1]);
        }
    }

    /// <summary>Rasterisation DPI for the current zoom and screen density, in 24-dpi buckets so the cache gets hits.</summary>
    private int CurrentDpi
    {
        get
        {
            var raw = 96 * EffectiveScale * Math.Clamp(_devicePixelRatio, 1, 3);
            var bucket = (int)Math.Ceiling(raw / 24d) * 24;
            return Math.Clamp(bucket, 72, 288);
        }
    }

    private string PageStyle((double WidthPt, double HeightPt) size)
    {
        var width = size.WidthPt * CssPxPerPt * EffectiveScale;
        return string.Create(CultureInfo.InvariantCulture,
            $"width:{width:0.##}px;aspect-ratio:{size.WidthPt:0.###} / {size.HeightPt:0.###};");
    }

    protected override void OnParametersSet()
    {
        if (!ReferenceEquals(_loadedFor, Document))
        {
            _loadedFor = Document;
            _ = LoadAsync(Document);
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (firstRender)
            {
                _self = DotNetObjectReference.Create(this);
                _module = await JS.InvokeAsync<IJSObjectReference>("import", ModulePath);
                _handle = await _module.InvokeAsync<IJSObjectReference>("create", _scroller, _progress, _self);
            }

            if (_handle is not null && _refreshPending)
            {
                _refreshPending = false;
                var keep = _keepFraction ?? -1;
                _keepFraction = null;
                await _handle.InvokeVoidAsync("refresh", keep);
            }
        }
        catch (JSDisconnectedException)
        {
        }
        catch (TaskCanceledException)
        {
        }
    }

    private async Task LoadAsync(Document document)
    {
        CancelAndReplace(ref _loadCts, out var ct);
        CancelAndReplace(ref _rasterCts, out _);
        _status = LoadStatus.Rendering;
        _error = null;
        _visible = [];
        _current = 0;
        await InvokeAsync(StateHasChanged);

        try
        {
            var theme = await Themes.GetDefaultAsync(ct);
            var themeKey = await Task.Run(() => ThemeKey(theme), ct);
            if (!Cache.Matches(document, themeKey))
            {
                var options = new PdfRenderOptions { FallbackTitle = document.ResolveTitle() };
                var pdf = await Renderer.RenderAsync(document, theme, options, null, ct);
                var sizes = await Task.Run(() =>
                {
                    var count = Rasterizer.GetPageCount(pdf);
                    var list = new List<(double WidthPt, double HeightPt)>(count);
                    for (var i = 0; i < count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        list.Add(Rasterizer.GetPageSize(pdf, i));
                    }

                    return list;
                }, ct);
                ct.ThrowIfCancellationRequested();
                Cache.Set(document, themeKey, theme.Name, pdf, sizes);
            }

            _status = LoadStatus.Ready;
            _refreshPending = true;
            _keepFraction = 0;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Print preview failed");
            _status = LoadStatus.Failed;
            _error = $"Couldn't make the print preview: {ex.Message.TrimEnd('.', ' ')}. Try again, or read the file in Reflow.";
        }

        if (!_disposed)
        {
            await InvokeAsync(StateHasChanged);
        }
    }

    private static string ThemeKey(PdfTheme theme)
    {
        var json = JsonSerializer.Serialize(theme, CoreJson.Options);
        return theme.Id + ":" + json.Length.ToString(CultureInfo.InvariantCulture) + ":" + StringComparer.Ordinal.GetHashCode(json).ToString("x8", CultureInfo.InvariantCulture);
    }

    private Task RetryAsync()
    {
        Cache.Clear();
        return LoadAsync(Document);
    }

    private static void CancelAndReplace(ref CancellationTokenSource? cts, out CancellationToken token)
    {
        cts?.Cancel();
        cts?.Dispose();
        cts = new CancellationTokenSource();
        token = cts.Token;
    }

    private void ScheduleRaster()
    {
        if (_status != LoadStatus.Ready || _disposed)
        {
            return;
        }

        CancelAndReplace(ref _rasterCts, out var ct);
        _ = RasterLoopAsync(ct);
    }

    private async Task RasterLoopAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(50, ct);
            var pdf = Cache.Pdf;
            if (pdf is null)
            {
                return;
            }

            var dpi = CurrentDpi;
            var current = _current;
            var wanted = _visible.Where(i => i >= 0 && i < Cache.PageSizes.Count)
                .OrderBy(i => Math.Abs(i - current))
                .ToList();
            foreach (var index in wanted)
            {
                ct.ThrowIfCancellationRequested();
                if (Cache.Get(index) is { } existing && existing.Dpi >= dpi)
                {
                    continue;
                }

                await _rasterGate.WaitAsync(ct);
                try
                {
                    var options = new RasterizeOptions { Dpi = dpi, Encoding = ImageEncoding.Jpeg, JpegQuality = 90 };
                    var image = await Rasterizer.RasterizePageAsync(pdf, index, options, ct);
                    ct.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(Cache.Pdf, pdf))
                    {
                        return;
                    }

                    Cache.Put(image, dpi, _visible);
                }
                finally
                {
                    _rasterGate.Release();
                }

                await InvokeAsync(StateHasChanged);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Rasterising a preview page failed");
        }
    }

    [JSInvokable]
    public Task OnVisiblePages(int[] pages)
    {
        _visible = pages ?? [];
        ScheduleRaster();
        return Task.CompletedTask;
    }

    [JSInvokable]
    public async Task OnCurrentPage(int page)
    {
        if (page == _current)
        {
            return;
        }

        _current = page;
        await InvokeAsync(StateHasChanged);
    }

    [JSInvokable]
    public async Task OnViewport(double width, double devicePixelRatio)
    {
        var dpiBefore = CurrentDpi;
        _viewportWidth = Math.Max(0, width);
        _devicePixelRatio = devicePixelRatio > 0 ? devicePixelRatio : 1;
        if (_fitWidth)
        {
            await InvokeAsync(StateHasChanged);
        }

        if (CurrentDpi != dpiBefore)
        {
            ScheduleRaster();
        }
    }

    public Task ZoomInAsync() => StepZoomAsync(+1);

    public Task ZoomOutAsync() => StepZoomAsync(-1);

    private Task StepZoomAsync(int direction)
    {
        var current = EffectiveScale;
        var next = direction > 0
            ? ZoomSteps.FirstOrDefault(z => z > current + 0.001, ZoomSteps[^1])
            : ZoomSteps.LastOrDefault(z => z < current - 0.001, ZoomSteps[0]);
        return SetScaleAsync(next);
    }

    private Task FitWidthAsync() => ApplyZoomAsync(fitWidth: true, _scale);

    private Task SetScaleAsync(double scale) => ApplyZoomAsync(fitWidth: false, scale);

    private async Task ApplyZoomAsync(bool fitWidth, double scale)
    {
        if (_status != LoadStatus.Ready)
        {
            return;
        }

        try
        {
            _keepFraction = _handle is null ? null : await _handle.InvokeAsync<double>("getFraction");
        }
        catch (JSDisconnectedException)
        {
            _keepFraction = null;
        }

        _fitWidth = fitWidth;
        _scale = Math.Clamp(scale, ZoomSteps[0], ZoomSteps[^1]);
        _refreshPending = true;
        StateHasChanged();
        ScheduleRaster();
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _loadCts?.Cancel();
        _rasterCts?.Cancel();
        try
        {
            if (_handle is not null)
            {
                await _handle.InvokeVoidAsync("dispose");
                await _handle.DisposeAsync();
            }

            if (_module is not null)
            {
                await _module.DisposeAsync();
            }
        }
        catch (JSDisconnectedException)
        {
        }
        catch (JSException)
        {
        }

        _self?.Dispose();
        _loadCts?.Dispose();
        _rasterCts?.Dispose();
        GC.SuppressFinalize(this);
    }
}
