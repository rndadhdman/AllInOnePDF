using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Publishing.Serialization;
using AllInOnePdf.Publishing.Storage;
using AllInOnePdf.Publishing.Templates;
using AllInOnePdf.UI.Components.Creator;
using AllInOnePdf.UI.Services;
using AllInOnePdf.UI.Services.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;

namespace AllInOnePdf.UI.Pages;

/// <summary>The Creator gallery (<c>/create</c>): templates to start from, saved publications and package import.</summary>
public partial class Creator
{
    [Inject] private IPublicationStore Store { get; set; } = default!;
    [Inject] private CreatorThumbnailCache Thumbnails { get; set; } = default!;
    [Inject] private IHostServices Host { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private IDialogService Dialogs { get; set; } = default!;
    [Inject] private IToastService Toasts { get; set; } = default!;
    [Inject] private CreatorDrafts Drafts { get; set; } = default!;
    [Inject] private CreatorState State { get; set; } = default!;

    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, string?> _draftThumbs = new(StringComparer.Ordinal);
    private CreatorDraftMatch? _drafts;
    private readonly Dictionary<string, string?> _thumbs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _templateThumbs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _templateFailed = new(StringComparer.Ordinal);
    private IReadOnlyList<PublicationSummary>? _items;
    private bool _importing;
    private bool _interactive;

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        Store.Changed += OnStoreChanged;
        State.PruneOldDrafts();
        foreach (var t in BuiltInTemplates.All())
        {
            if (Thumbnails.TryGetTemplate(t.Id, out var url))
            {
                _templateThumbs[t.Id] = url;
            }
        }

        await LoadAsync();
    }

    /// <inheritdoc />
    protected override void OnAfterRender(bool firstRender)
    {
        if (firstRender)
        {
            // Thumbnails load only once interactive (never during prerender) and off the UI thread.
            _interactive = true;
            _ = LoadThumbnailsAsync();
            _ = LoadDraftThumbnailsAsync();
            _ = LoadTemplateThumbnailsAsync();
        }
    }

    private async Task LoadAsync()
    {
        try
        {
            _items = await Store.ListAsync(_cts.Token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _items = [];
            Toasts.ShowError($"Your publications could not be listed: {ex.Message}");
        }

        foreach (var item in _items)
        {
            if (Thumbnails.TryGetPublication(item, out var url))
            {
                _thumbs[item.Id] = url;
            }
            else
            {
                _thumbs.Remove(item.Id);
            }
        }

        await LoadDraftsAsync();
    }

    private async Task LoadDraftsAsync()
    {
        try
        {
            _drafts = CreatorDrafts.Match(await Drafts.ListAsync(_cts.Token), _items ?? []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Drafts are a convenience: the gallery works without them.
            _drafts = null;
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        foreach (var draft in _drafts.Unsaved)
        {
            if (Thumbnails.TryGetDraft(draft.Key, draft.Written, out var url))
            {
                _draftThumbs[draft.Key] = url;
            }
            else
            {
                _draftThumbs.Remove(draft.Key);
            }
        }
    }

    private async Task LoadDraftThumbnailsAsync()
    {
        try
        {
            foreach (var draft in (_drafts?.Unsaved ?? []).Where(d => !_draftThumbs.ContainsKey(d.Key)).ToList())
            {
                _draftThumbs[draft.Key] = await Thumbnails.GetDraftAsync(draft.Key, draft.Written, ct => Drafts.ReadPublicationAsync(draft, ct), _cts.Token);
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static string Age(CreatorDraft draft) => DraftAutosaver.DescribeAge(draft.Written, DateTimeOffset.UtcNow);

    private async Task LoadThumbnailsAsync()
    {
        try
        {
            foreach (var item in (_items ?? []).Where(i => !_thumbs.ContainsKey(i.Id)).ToList())
            {
                _thumbs[item.Id] = await Thumbnails.GetPublicationAsync(item, _cts.Token);
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task LoadTemplateThumbnailsAsync()
    {
        try
        {
            foreach (var template in BuiltInTemplates.All().Where(t => !_templateThumbs.ContainsKey(t.Id)).ToList())
            {
                var url = await Thumbnails.GetTemplateAsync(template, _cts.Token);
                _templateThumbs[template.Id] = url;
                if (url is null)
                {
                    _templateFailed.Add(template.Id);
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
            await LoadDraftThumbnailsAsync();
        }
    });

    private static string Ratio(PublicationSummary item)
    {
        var page = new AllInOnePdf.Core.Themes.PageSettings { Size = item.PageSize, Orientation = item.Orientation };
        var (w, h) = page.GetSizeMm();
        return $"aspect-ratio:{w.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} / {h.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}";
    }

    // ---------------------------------------------------------------- actions

    private void StartFrom(string templateId) => Nav.NavigateTo("/create/" + CreatorBuilder.NewPrefix + Uri.EscapeDataString(templateId));

    private void Open(string id) => Nav.NavigateTo("/create/" + Uri.EscapeDataString(id));

    /// <summary>Opens the builder for the draft's route and has it restore the draft without asking.</summary>
    private void Continue(CreatorDraft draft)
    {
        State.RequestDraftRestore(draft.RouteId);
        if (draft.TemplateId is { } templateId)
        {
            StartFrom(templateId);
        }
        else
        {
            Open(draft.RouteId);
        }
    }

    private async Task DiscardAsync(CreatorDraft draft, string? name = null)
    {
        name ??= draft.Name;
        var dialog = await Dialogs.ShowConfirmationAsync(
            $"Discard the unsaved changes to \"{name}\"? They cannot be recovered afterwards.",
            "Discard",
            "Cancel",
            "Discard unsaved changes");
        var result = await dialog.Result;
        if (result.Cancelled)
        {
            return;
        }

        try
        {
            await Drafts.DiscardAsync(draft, _cts.Token);
            _draftThumbs.Remove(draft.Key);
            await LoadDraftsAsync();
            Toasts.ShowSuccess($"Discarded the unsaved changes to \"{name}\".");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Toasts.ShowError($"The unsaved changes could not be discarded: {ex.Message}");
        }
    }

    private async Task NewPublicationAsync()
    {
        var model = new TemplatePickerModel();
        var dialog = await Dialogs.ShowDialogAsync<TemplatePickerDialog>(model, new DialogParameters
        {
            Title = "New publication",
            PrimaryAction = "Create",
            SecondaryAction = "Cancel",
            Width = "820px",
            TrapFocus = true,
            Modal = true,
        });
        var result = await dialog.Result;
        if (!result.Cancelled && !string.IsNullOrEmpty(model.SelectedId))
        {
            StartFrom(model.SelectedId);
        }
    }

    private async Task DuplicateAsync(PublicationSummary item)
    {
        try
        {
            var copy = await Store.DuplicateAsync(item.Id, UniqueName(item.Name + " copy"), _cts.Token);
            Toasts.ShowSuccess($"Created \"{copy.Name}\".");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or KeyNotFoundException or InvalidDataException)
        {
            Toasts.ShowError($"The publication could not be duplicated: {ex.Message}");
        }
    }

    private string UniqueName(string name)
    {
        var names = new HashSet<string>((_items ?? []).Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
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

    private async Task ExportPackageAsync(PublicationSummary item)
    {
        try
        {
            var publication = await Store.GetAsync(item.Id, _cts.Token);
            if (publication is null)
            {
                Toasts.ShowError($"\"{item.Name}\" could not be read. It may have been deleted; refresh the gallery.");
                return;
            }

            var thumbnail = await Store.GetThumbnailAsync(item.Id, _cts.Token);
            var bytes = PublicationPackage.Write(publication, thumbnail);
            await Host.SaveFileAsync(ConversionService.SanitizeBaseName(item.Name) + PublicationPackage.Extension, bytes, PublicationPackage.MediaType);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JSException or InvalidDataException)
        {
            Toasts.ShowError($"The package could not be exported: {ex.Message}");
        }
    }

    private async Task DeleteAsync(PublicationSummary item)
    {
        var dialog = await Dialogs.ShowConfirmationAsync(
            $"Delete \"{item.Name}\"? This cannot be undone. Export the package first if you may want it back.",
            "Delete",
            "Cancel",
            "Delete publication");
        var result = await dialog.Result;
        if (result.Cancelled)
        {
            return;
        }

        try
        {
            if (await Store.DeleteAsync(item.Id, _cts.Token))
            {
                Thumbnails.InvalidatePublication(item.Id);
                await Drafts.DiscardForPublicationAsync(item.Id, _cts.Token);
                Toasts.ShowSuccess($"Deleted \"{item.Name}\".");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Toasts.ShowError($"The publication could not be deleted: {ex.Message}");
        }
    }

    // ----------------------------------------------------------------- import

    private async Task OnImportCompletedAsync(IEnumerable<FluentInputFileEventArgs> files)
    {
        _importing = true;
        var imported = new List<string>();
        try
        {
            foreach (var file in files)
            {
                // One bad file never stops the others.
                try
                {
                    if (file.Size > PublicationPackage.MaxPackageBytes)
                    {
                        Toasts.ShowError($"{file.Name} is larger than 200 MB, so it cannot be imported.");
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

                    await using var stream = file.LocalFile.OpenRead();
                    var publication = await Store.ImportAsync(stream, _cts.Token);
                    imported.Add(publication.Name);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    return;
                }
                catch (InvalidDataException)
                {
                    Toasts.ShowError($"{file.Name} is not a publication package this app can read. Export packages from AllInOne PDF (.aiopub) and try again.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Toasts.ShowError($"{file.Name} could not be imported: {ex.Message}");
                }
                catch (Exception)
                {
                    Toasts.ShowError($"{file.Name} is not a publication package this app can read. Export packages from AllInOne PDF (.aiopub) and try again.");
                }
                finally
                {
                    try
                    {
                        file.LocalFile?.Delete();
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
            }
        }
        finally
        {
            _importing = false;
        }

        if (imported.Count == 1)
        {
            Toasts.ShowSuccess($"Imported \"{imported[0]}\".");
        }
        else if (imported.Count > 1)
        {
            Toasts.ShowSuccess($"Imported {imported.Count} publications.");
        }
    }

    private void OnTooManyFiles(int count) => Toasts.ShowWarning($"Import up to 10 packages at a time ({count} were chosen).");

    /// <inheritdoc />
    public void Dispose()
    {
        Store.Changed -= OnStoreChanged;
        _cts.Cancel();
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}
