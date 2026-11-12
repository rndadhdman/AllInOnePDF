using System.Text;
using System.Text.Json;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Themes;
using AllInOnePdf.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;

namespace AllInOnePdf.UI.Pages;

/// <summary>Themes gallery (<c>/themes</c>): live page-1 thumbnails, filter and search, and the per-theme actions.</summary>
public partial class Themes
{
    /// <summary>Largest theme file accepted for import (same limit as <see cref="IThemeStore.ImportAsync"/>).</summary>
    private const int MaxImportBytes = 2 * 1024 * 1024;

    [Inject] private IThemeStore Store { get; set; } = default!;
    [Inject] private ThemeBuilderThumbnailCache Thumbnails { get; set; } = default!;
    [Inject] private IHostServices Host { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private IDialogService Dialogs { get; set; } = default!;
    [Inject] private IToastService Toasts { get; set; } = default!;

    private readonly Dictionary<string, string?> _thumbs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failed = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private IReadOnlyList<PdfTheme>? _themes;
    private string _defaultId = BuiltInThemes.DefaultId;
    private string? _query;
    private string? _tag;
    private bool _importing;
    private bool _interactive;

    private IEnumerable<string> AllTags => (_themes ?? [])
        .SelectMany(t => t.Tags)
        .Select(t => t.Trim().ToLowerInvariant())
        .Where(t => t.Length > 0)
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal);

    private IEnumerable<PdfTheme> Filtered => (_themes ?? [])
        .Where(t => _tag is null || t.Tags.Any(x => string.Equals(x, _tag, StringComparison.OrdinalIgnoreCase)))
        .Where(t => Matches(t, _query));

    /// <summary>
    /// Gallery search: every word of <paramref name="query"/> must appear in the theme's name or description, or
    /// start one of its tags. A theme with a dark page also answers to "dark" (and a light one to "light") when its
    /// tags do not say so, so your own themes are found by look too.
    /// </summary>
    public static bool Matches(PdfTheme theme, string? query)
    {
        ArgumentNullException.ThrowIfNull(theme);
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        var tags = theme.Tags.Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
        if (!tags.Any(t => t.Equals("dark", StringComparison.OrdinalIgnoreCase) || t.Equals("light", StringComparison.OrdinalIgnoreCase)))
        {
            tags.Add(ThemeBuilderColor.IsDark(theme.Page?.Background) ? "dark" : "light");
        }

        foreach (var word in query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var hit = theme.Name.Contains(word, StringComparison.OrdinalIgnoreCase)
                      || (theme.Description?.Contains(word, StringComparison.OrdinalIgnoreCase) ?? false)
                      || tags.Any(t => t.StartsWith(word, StringComparison.OrdinalIgnoreCase));
            if (!hit)
            {
                return false;
            }
        }

        return true;
    }

    protected override async Task OnInitializedAsync()
    {
        Store.Changed += OnStoreChanged;
        await LoadAsync();
    }

    protected override void OnAfterRender(bool firstRender)
    {
        if (firstRender)
        {
            // Thumbnails render only once interactive (never during prerender) and off the UI thread.
            _interactive = true;
            _ = LoadThumbnailsAsync();
        }
    }

    private async Task LoadAsync()
    {
        _themes = await Store.GetAllAsync(_cts.Token);
        _defaultId = await Store.GetDefaultThemeIdAsync(_cts.Token);
        foreach (var theme in _themes)
        {
            if (Thumbnails.TryGet(theme, out var url))
            {
                _thumbs[theme.Id] = url;
            }
            else
            {
                _thumbs.Remove(theme.Id);
            }
        }
    }

    private async Task LoadThumbnailsAsync()
    {
        var token = _cts.Token;
        var pending = (_themes ?? []).Where(t => !_thumbs.ContainsKey(t.Id)).ToList();
        if (pending.Count == 0)
        {
            return;
        }

        var tasks = pending.Select(async theme =>
        {
            var url = await Thumbnails.GetAsync(theme, token);
            return (theme.Id, url);
        }).ToList();

        try
        {
            await foreach (var finished in Task.WhenEach(tasks).WithCancellation(token))
            {
                var (id, url) = await finished;
                if (url is null)
                {
                    _failed.Add(id);
                }
                else
                {
                    _failed.Remove(id);
                    _thumbs[id] = url;
                }

                await InvokeAsync(StateHasChanged);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnStoreChanged() => _ = InvokeAsync(async () =>
    {
        if (_cts.IsCancellationRequested)
        {
            return;
        }

        await LoadAsync();
        StateHasChanged();
        if (_interactive)
        {
            await LoadThumbnailsAsync();
        }
    });

    private void ClearFilters()
    {
        _query = null;
        _tag = null;
    }

    // ---------------------------------------------------------------- actions

    private void NewTheme() => Nav.NavigateTo("/themes/" + ThemeBuilder.NewThemeId);

    private void Open(PdfTheme theme) => Nav.NavigateTo("/themes/" + Uri.EscapeDataString(theme.Id));

    private async Task UseAsDefaultAsync(PdfTheme theme)
    {
        try
        {
            await Store.SetDefaultThemeIdAsync(theme.Id, _cts.Token);
            _defaultId = theme.Id;
            Toasts.ShowSuccess($"\"{theme.Name}\" is now the default theme for PDF output.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Toasts.ShowError($"The default theme could not be changed: {ex.Message}");
        }
    }

    private async Task EditAsync(PdfTheme theme)
    {
        if (!theme.IsBuiltIn)
        {
            Open(theme);
            return;
        }

        var copy = await DuplicateCoreAsync(theme);
        if (copy is not null)
        {
            Open(copy);
        }
    }

    private async Task DuplicateAsync(PdfTheme theme)
    {
        var copy = await DuplicateCoreAsync(theme);
        if (copy is not null)
        {
            Toasts.ShowSuccess($"Created \"{copy.Name}\".");
        }
    }

    private async Task<PdfTheme?> DuplicateCoreAsync(PdfTheme theme)
    {
        try
        {
            return await Store.DuplicateAsync(theme.Id, UniqueName(theme.Name + " copy"), _cts.Token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or KeyNotFoundException)
        {
            Toasts.ShowError($"The theme could not be duplicated: {ex.Message}");
            return null;
        }
    }

    private string UniqueName(string name)
    {
        var names = new HashSet<string>((_themes ?? []).Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(name))
        {
            return name;
        }

        for (var i = 2; i < 100; i++)
        {
            var candidate = $"{name} {i}";
            if (!names.Contains(candidate))
            {
                return candidate;
            }
        }

        return name;
    }

    private async Task ExportAsync(PdfTheme theme)
    {
        try
        {
            var export = theme.Clone();
            export.IsBuiltIn = false;
            var bytes = Encoding.UTF8.GetBytes(export.ToJson());
            await Host.SaveFileAsync(ConversionService.SanitizeBaseName(theme.Name) + ".json", bytes, "application/json");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JSException)
        {
            Toasts.ShowError($"The theme could not be exported: {ex.Message}");
        }
    }

    private async Task DeleteAsync(PdfTheme theme)
    {
        if (theme.IsBuiltIn)
        {
            return;
        }

        var dialog = await Dialogs.ShowConfirmationAsync(
            $"Delete \"{theme.Name}\"? This cannot be undone. Export it first if you may want it back.",
            "Delete",
            "Cancel",
            "Delete theme");
        var result = await dialog.Result;
        if (result.Cancelled)
        {
            return;
        }

        try
        {
            if (await Store.DeleteAsync(theme.Id, _cts.Token))
            {
                // The store itself falls back to the built-in default when the deleted theme was the default.
                Thumbnails.Invalidate(theme.Id);

                Toasts.ShowSuccess($"Deleted \"{theme.Name}\".");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Toasts.ShowError($"The theme could not be deleted: {ex.Message}");
        }
    }

    // ----------------------------------------------------------------- import

    private async Task OnImportCompletedAsync(IEnumerable<FluentInputFileEventArgs> files)
    {
        _importing = true;
        var imported = new List<string>();
        var repaired = 0;
        try
        {
            foreach (var file in files)
            {
                // One bad file never stops the others: every failure is reported per file and the loop continues.
                try
                {
                    if (file.Size > MaxImportBytes || (file.LocalFile is { Exists: true } local && local.Length > MaxImportBytes))
                    {
                        Toasts.ShowError($"{file.Name} was not imported: it is larger than 2 MB, the limit for a theme file. Choose a theme file exported from AllInOne PDF.");
                        continue;
                    }

                    if (file.ErrorMessage is { Length: > 0 } error)
                    {
                        Toasts.ShowError($"{file.Name} could not be read: {error}");
                        continue;
                    }

                    if (file.LocalFile is null)
                    {
                        Toasts.ShowError($"{file.Name} could not be read. Try importing it again.");
                        continue;
                    }

                    var json = await File.ReadAllTextAsync(file.LocalFile.FullName, _cts.Token);
                    switch (ThemeValidator.Classify(json))
                    {
                        case ThemeJsonKind.Malformed:
                            Toasts.ShowError($"{file.Name} was not imported: it is not valid JSON. Choose a theme file exported from AllInOne PDF.");
                            continue;
                        case ThemeJsonKind.NotATheme:
                            Toasts.ShowError(NotAThemeMessage(file.Name));
                            continue;
                    }

                    var repairs = ThemeValidator.DescribeRepairs(json);
                    var theme = await Store.ImportAsync(json, _cts.Token);
                    imported.Add(theme.Name);
                    if (repairs.Count > 0)
                    {
                        repaired++;
                        Toasts.ShowWarning(RepairsMessage(theme.Name, repairs));
                    }
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    return;
                }
                catch (JsonException)
                {
                    // Valid JSON (Classify said so) whose values have the wrong types for a theme.
                    Toasts.ShowError(NotAThemeMessage(file.Name));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Toasts.ShowError($"{file.Name} could not be imported: {ex.Message}");
                }
                catch (Exception)
                {
                    Toasts.ShowError($"{file.Name} was not imported: it is not a theme file this app can read. Choose a theme file exported from AllInOne PDF.");
                }
                finally
                {
                    if (file.LocalFile is not null)
                    {
                        TryDelete(file.LocalFile);
                    }
                }
            }
        }
        finally
        {
            _importing = false;
        }

        if (imported.Count == 1 && repaired == 0)
        {
            Toasts.ShowSuccess($"Imported \"{imported[0]}\".");
        }
        else if (imported.Count > 1)
        {
            Toasts.ShowSuccess($"Imported {imported.Count} themes.");
        }
    }

    private static string NotAThemeMessage(string fileName) =>
        $"{fileName} was not imported: it is JSON, but not a theme. Choose a theme file exported from AllInOne PDF.";

    /// <summary>The import toast for a theme whose values were out of range: what changed, the first few in full.</summary>
    public static string RepairsMessage(string themeName, IReadOnlyList<string> repairs)
    {
        ArgumentNullException.ThrowIfNull(repairs);
        const int shown = 3;
        var sb = new StringBuilder();
        sb.Append("Imported \"").Append(themeName).Append("\". ")
          .Append(repairs.Count == 1 ? "One value was changed so the app can use it: " : $"{repairs.Count} values were changed so the app can use them: ")
          .AppendJoin(" ", repairs.Take(shown));
        if (repairs.Count > shown)
        {
            sb.Append(' ').Append(repairs.Count - shown).Append(" more not listed.");
        }

        return sb.ToString();
    }

    private void OnTooManyFiles(int count) => Toasts.ShowWarning($"Import up to 10 theme files at a time ({count} were chosen).");

    private static void TryDelete(FileInfo file)
    {
        try
        {
            file.Delete();
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        Store.Changed -= OnStoreChanged;
        _cts.Cancel();
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}
