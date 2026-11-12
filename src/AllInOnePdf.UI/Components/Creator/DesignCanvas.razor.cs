using AllInOnePdf.Core.Rendering;
using AllInOnePdf.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace AllInOnePdf.UI.Components.Creator;

/// <summary>
/// The Creator's design canvas. Renders <see cref="CreatorState.Working"/> as HTML styled by the theme snapshot's
/// CSS and reports clicks, toolbar actions and drops back to the state. Each module's HTML is cached by content,
/// so an edit re-renders (and sends over the circuit) only the module that changed.
/// </summary>
public partial class DesignCanvas
{
    /// <summary>Changes whenever the publication or the selection changes (re-render trigger).</summary>
    [Parameter] public int Version { get; set; }

    /// <summary>"fit" or a scale factor such as "0.75".</summary>
    [Parameter] public string Zoom { get; set; } = "fit";

    /// <summary>Raised when an empty column's "Add module" is pressed (the page opens the Insert panel).</summary>
    [Parameter] public EventCallback<string> OnAddModule { get; set; }

    /// <summary>Raised when a toolbar's settings button is pressed (the page opens the inspector).</summary>
    [Parameter] public EventCallback OnSettings { get; set; }

    /// <summary>Raised when a free-flow item is double-clicked (the page opens the inspector's Content tab).</summary>
    [Parameter] public EventCallback<string> OnEditContent { get; set; }

    [Inject] private CreatorState State { get; set; } = default!;
    [Inject] private IDocumentHtmlRenderer Renderer { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;

    private readonly string _scope = "crc-" + Guid.NewGuid().ToString("N")[..10];
    private ElementReference _root;
    private IJSObjectReference? _module;
    private DotNetObjectReference<DesignCanvas>? _self;
    private CreatorCanvasHtml? _html;
    private string _style = string.Empty;
    private string? _themeJson;
    private string? _appliedZoom;
    private double _appliedWidth;
    private bool _disposed;

    private CreatorCanvasHtml Html => _html ??= new CreatorCanvasHtml(Renderer);

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        if (State.Working is not { } p)
        {
            return;
        }

        var json = p.Theme.ToJson();
        if (!string.Equals(json, _themeJson, StringComparison.Ordinal))
        {
            _themeJson = json;
            _style = "<style>" + CreatorCanvasHtml.Css(p.Theme, _scope) + "</style>";
        }
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_disposed)
        {
            return;
        }

        if (firstRender)
        {
            try
            {
                _module = await JS.InvokeAsync<IJSObjectReference>("import", "./_content/AllInOnePdf.UI/Components/Creator/DesignCanvas.razor.js");
                _self = DotNetObjectReference.Create(this);
                await _module.InvokeVoidAsync("initCanvas", _root, _self);
            }
            catch (JSDisconnectedException)
            {
                return;
            }
            catch (JSException)
            {
                // Drag and drop is an enhancement; clicks, the toolbars and the keyboard still work.
                return;
            }
        }

        var width = PageWidthPx();
        if (_module is not null && (_appliedZoom != Zoom || Math.Abs(_appliedWidth - width) > 0.5))
        {
            _appliedZoom = Zoom;
            _appliedWidth = width;
            try
            {
                await _module.InvokeVoidAsync("setZoom", _root, Zoom, width);
            }
            catch (JSDisconnectedException)
            {
            }
        }
    }

    /// <summary>Moves keyboard focus to the canvas element with that id.</summary>
    public async Task FocusAsync(string? id)
    {
        if (_module is null || id is null)
        {
            return;
        }

        try
        {
            await _module.InvokeVoidAsync("focusId", _root, id);
        }
        catch (JSDisconnectedException)
        {
        }
    }

    /// <summary>Scrolls the element with that id into view.</summary>
    public async Task RevealAsync(string? id)
    {
        if (_module is null || id is null)
        {
            return;
        }

        try
        {
            await _module.InvokeVoidAsync("revealId", _root, id);
        }
        catch (JSDisconnectedException)
        {
        }
    }

    /// <summary>Called by the canvas script when something is dropped.</summary>
    [JSInvokable]
    public Task OnCanvasDrop(string kind, string id, string targetId, int index) => InvokeAsync(() => CreatorDrops.Apply(State, kind, id, targetId, index));

    /// <summary>Called by the canvas script when an Insert tile or a module is dropped on a free-flow canvas, at a point in points.</summary>
    [JSInvokable]
    public Task OnCanvasPlace(string kind, string id, string sectionId, double xPt, double yPt) =>
        InvokeAsync(() => CreatorDrops.Place(State, kind, id, sectionId, xPt, yPt));

    /// <summary>Called by the canvas script when a free-flow item has been dragged to a new top-left (one undo step).</summary>
    [JSInvokable]
    public Task OnItemMoved(string id, double xPt, double yPt) => InvokeAsync(() => State.MoveItem(id, xPt, yPt));

    /// <summary>Called by the canvas script when a free-flow item has been resized with a handle (one undo step).</summary>
    [JSInvokable]
    public Task OnItemResized(string id, double xPt, double yPt, double widthPt, double heightPt) =>
        InvokeAsync(() => State.ResizeItem(id, xPt, yPt, widthPt, heightPt));

    /// <summary>The eight resize handles of a selected free-flow item, corners first.</summary>
    private static readonly string[] ResizeHandles = ["nw", "ne", "se", "sw", "n", "e", "s", "w"];

    private async Task EditContentAsync(string id)
    {
        State.Select(CreatorSelectionKind.Module, id);
        await OnEditContent.InvokeAsync(id);
    }

    private void Select(CreatorSelectionKind kind, string id) => State.Select(kind, id);

    private void ClearSelection() => State.ClearSelection();

    private async Task OpenSettingsAsync(CreatorSelectionKind kind, string id)
    {
        State.Select(kind, id);
        await OnSettings.InvokeAsync();
    }

    private static string LabelOf(string what, string? label, int index, int count, bool selected)
    {
        var text = CreatorCanvasHtml.Label(what, index, count);
        if (!string.IsNullOrWhiteSpace(label))
        {
            text += ", " + label.Trim();
        }

        return selected ? text + ", selected" : text;
    }

    private RenderFragment Tools(CreatorSelectionKind kind, string id, string what) => builder =>
    {
        var seq = 0;
        void Button(string cls, string title, Icon icon, Func<Task> onClick)
        {
            builder.OpenElement(seq++, "button");
            builder.AddAttribute(seq++, "type", "button");
            builder.AddAttribute(seq++, "class", "cr-tool " + cls);
            builder.AddAttribute(seq++, "title", title);
            builder.AddAttribute(seq++, "aria-label", title);
            builder.AddAttribute(seq++, "onclick", EventCallback.Factory.Create(this, onClick));
            builder.AddEventStopPropagationAttribute(seq++, "onclick", true);
            builder.OpenComponent<FluentIcon<Icon>>(seq++);
            builder.AddComponentParameter(seq++, nameof(FluentIcon<Icon>.Value), icon);
            builder.AddComponentParameter(seq++, nameof(FluentIcon<Icon>.Color), Color.Custom);
            builder.AddComponentParameter(seq++, nameof(FluentIcon<Icon>.CustomColor), "currentColor");
            builder.CloseComponent();
            builder.CloseElement();
        }

        Button("cr-tool-settings", $"Settings for this {what}", new Icons.Regular.Size16.Settings(), () => OpenSettingsAsync(kind, id));
        Button("cr-tool-dup", $"Duplicate this {what} (Ctrl+D)", new Icons.Regular.Size16.Copy(), () =>
        {
            State.Duplicate(id);
            return Task.CompletedTask;
        });
        Button("cr-tool-del", $"Delete this {what} (Delete)", new Icons.Regular.Size16.Delete(), () =>
        {
            State.Delete(id);
            return Task.CompletedTask;
        });
    };

    /// <summary>A free-flow item's toolbar: settings, the four stacking moves, duplicate and delete.</summary>
    private RenderFragment ItemTools(string id, string what) => builder =>
    {
        var seq = 0;
        void Button(string cls, string title, Icon icon, Func<Task> onClick)
        {
            builder.OpenElement(seq++, "button");
            builder.AddAttribute(seq++, "type", "button");
            builder.AddAttribute(seq++, "class", "cr-tool " + cls);
            builder.AddAttribute(seq++, "title", title);
            builder.AddAttribute(seq++, "aria-label", title);
            builder.AddAttribute(seq++, "onclick", EventCallback.Factory.Create(this, onClick));
            builder.AddEventStopPropagationAttribute(seq++, "onclick", true);
            builder.AddEventStopPropagationAttribute(seq++, "ondblclick", true);
            builder.OpenComponent<FluentIcon<Icon>>(seq++);
            builder.AddComponentParameter(seq++, nameof(FluentIcon<Icon>.Value), icon);
            builder.AddComponentParameter(seq++, nameof(FluentIcon<Icon>.Color), Color.Custom);
            builder.AddComponentParameter(seq++, nameof(FluentIcon<Icon>.CustomColor), "currentColor");
            builder.CloseComponent();
            builder.CloseElement();
        }

        Task Run(Func<string, bool> action)
        {
            action(id);
            return Task.CompletedTask;
        }

        Button("cr-tool-settings", $"Settings for this {what}", new Icons.Regular.Size16.Settings(), () => OpenSettingsAsync(CreatorSelectionKind.Module, id));
        Button("cr-tool-front", "Bring to front (Ctrl+Shift+])", new Icons.Regular.Size16.ChevronDoubleUp(), () => Run(State.BringToFront));
        Button("cr-tool-forward", "Bring forward (Ctrl+])", new Icons.Regular.Size16.ArrowUp(), () => Run(State.BringForward));
        Button("cr-tool-backward", "Send backward (Ctrl+[)", new Icons.Regular.Size16.ArrowDown(), () => Run(State.SendBackward));
        Button("cr-tool-back", "Send to back (Ctrl+Shift+[)", new Icons.Regular.Size16.ChevronDoubleDown(), () => Run(State.SendToBack));
        Button("cr-tool-dup", $"Duplicate this {what} (Ctrl+D)", new Icons.Regular.Size16.Copy(), () =>
        {
            State.Duplicate(id);
            return Task.CompletedTask;
        });
        Button("cr-tool-del", $"Delete this {what} (Delete)", new Icons.Regular.Size16.Delete(), () =>
        {
            State.Delete(id);
            return Task.CompletedTask;
        });
    };

    private double PageWidthPx() => (State.Working?.Theme.Page.GetSizeMm().WidthMm ?? 210) * 96 / 25.4;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_module is not null)
        {
            try
            {
                await _module.InvokeVoidAsync("dispose", _root);
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        _self?.Dispose();
        GC.SuppressFinalize(this);
    }
}
