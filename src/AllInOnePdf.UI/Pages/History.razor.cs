using System.Globalization;
using AllInOnePdf.Core.Storage;
using AllInOnePdf.Core.Themes;
using AllInOnePdf.UI.Services;
using AllInOnePdf.UI.Services.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;

namespace AllInOnePdf.UI.Pages;

/// <summary>Recent conversions from <see cref="IConversionHistory"/> with search, filters and "convert again".</summary>
public partial class History
{
    private List<HistoryEntry>? _entries;
    private Dictionary<string, string> _themeNames = new(StringComparer.OrdinalIgnoreCase);
    private string? _search;
    private string? _result = ConvertHistoryView.AllResults;
    private string? _target = ConvertHistoryView.AllTargets;
    private HistorySortColumn _sort = HistorySortColumn.When;
    private bool _descending = true;
    private readonly Debouncer _reload = new(TimeSpan.FromMilliseconds(100));

    [Inject] private IConversionHistory HistoryStore { get; set; } = default!;

    [Inject] private IThemeStore Themes { get; set; } = default!;

    [Inject] private IPreferencesService Preferences { get; set; } = default!;

    [Inject] private IHostServices Host { get; set; } = default!;

    [Inject] private ConvertWorkspace Workspace { get; set; } = default!;

    [Inject] private IDialogService Dialogs { get; set; } = default!;

    [Inject] private IToastService Toasts { get; set; } = default!;

    [Inject] private NavigationManager Nav { get; set; } = default!;

    /// <summary>The entries the table shows: filtered and sorted.</summary>
    private IReadOnlyList<HistoryEntry> Shown => ConvertHistoryView.Apply(_entries ?? [], _search, _result, _target, _sort, _descending);

    private string HeaderDescription => Host.SupportsNativeDialogs
        ? "Your recent conversions. Open a result, or convert the same file again with the same options."
        : "Your recent conversions. This web version does not keep uploaded files or their results: to convert a file again, add it on the Convert page.";

    protected override async Task OnInitializedAsync()
    {
        HistoryStore.Changed += OnHistoryChanged;
        await Preferences.LoadAsync();
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var entries = await HistoryStore.GetAsync();
        _entries = entries.OrderByDescending(e => e.Timestamp).ToList();
        try
        {
            var themes = await Themes.GetAllAsync();
            _themeNames = themes.GroupBy(t => t.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            // Theme names are cosmetic here; fall back to ids.
        }
    }

    // A batch of conversions writes several entries in a row: reload once for the burst.
    private void OnHistoryChanged() => _reload.Coalesce(_ => InvokeAsync(async () =>
    {
        await LoadAsync();
        StateHasChanged();
    }));

    private string ThemeName(string? id) =>
        id is null ? "None" : _themeNames.TryGetValue(id, out var name) ? name : id;

    /// <summary>
    /// "Convert again" needs the source on disk, so it only exists in the desktop app (the web host never re-reads
    /// server paths named by history) and only while the file is still there.
    /// </summary>
    private bool CanRepeat(HistoryEntry entry) =>
        Host.SupportsNativeDialogs && !string.IsNullOrEmpty(entry.SourcePath) && File.Exists(entry.SourcePath);

    private static string RepeatUnavailableReason(HistoryEntry entry) =>
        string.IsNullOrEmpty(entry.SourcePath)
            ? "Added by drag and drop, so there is no path to convert it again from"
            : "The source file was moved or deleted";

    private void SortBy(HistorySortColumn column) => (_sort, _descending) = ConvertHistoryView.Toggle(_sort, _descending, column);

    private async Task OpenAsync(string path)
    {
        if (!File.Exists(path))
        {
            Toasts.ShowWarning("That file was moved or deleted.");
            return;
        }

        await Host.OpenWithDefaultAppAsync(path);
    }

    private async Task RevealAsync(string path)
    {
        if (!File.Exists(path))
        {
            Toasts.ShowWarning("That file was moved or deleted.");
            return;
        }

        await Host.RevealInFolderAsync(path);
    }

    private async Task RepeatAsync(HistoryEntry entry)
    {
        await Workspace.EnsureInitializedAsync();
        var item = Workspace.AddFromHistory(entry);
        if (item is null)
        {
            Toasts.ShowWarning(RepeatUnavailableReason(entry) + ".");
            return;
        }

        Nav.NavigateTo("/");
    }

    private async Task RemoveAsync(HistoryEntry entry)
    {
        await HistoryStore.RemoveAsync(entry.Id);
    }

    private async Task ClearAllAsync()
    {
        if (!await Dialogs.ConfirmDestructiveAsync(
                "This removes every entry from the history. Converted files stay where they are.",
                "Clear history",
                "Clear history?"))
        {
            return;
        }

        await HistoryStore.ClearAsync();
        Toasts.ShowSuccess("History cleared");
    }

    public void Dispose()
    {
        HistoryStore.Changed -= OnHistoryChanged;
        _reload.Dispose();
        GC.SuppressFinalize(this);
    }
}
