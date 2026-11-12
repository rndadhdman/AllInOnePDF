using System.Text;
using AllInOnePdf.Core;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.UI.Components.Converting;
using AllInOnePdf.UI.Services;
using AllInOnePdf.UI.Services.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;

namespace AllInOnePdf.UI.Pages;

/// <summary>The Convert workspace (home page). State lives in the scoped <see cref="ConvertWorkspace"/>.</summary>
public partial class Home
{
    // Progress can fire many times a second from background threads; render at most every 50 ms.
    private readonly Debouncer _render = new(TimeSpan.FromMilliseconds(50));
    private readonly HashSet<string> _announcedUnsupported = [];
    private bool _ready;
    private bool _bulkMenuOpen;
    private DateTime _bulkMenuClosedAt;
    private int _revision;

    [Inject] private ConvertWorkspace Workspace { get; set; } = default!;

    [Inject] private IPreferencesService Preferences { get; set; } = default!;

    [Inject] private IDialogService Dialogs { get; set; } = default!;

    [Inject] private IToastService Toasts { get; set; } = default!;

    [Inject] private IHostServices Host { get; set; } = default!;

    private IReadOnlyList<ConvertItem> Items => Workspace.Items;

    private ConvertBatchSummary Summary => Workspace.Summary();

    private int PendingCount => Workspace.PendingItems().Count;

    private int UnsupportedCount => Items.Count(i => !i.IsSupported);

    private IReadOnlyList<IExporter> BulkTargets => Workspace.AllTargets();

    private bool ShowSample => Preferences.Current.ShowWelcomeSample;

    private int OverallPercent => Workspace.OverallPercent();

    /// <summary>Finished rows whose results wait to be downloaded (web host).</summary>
    private int DownloadCount => Workspace.WritesToDisk ? 0 : Items.Count(i => i.State == ConvertItemState.Succeeded && i.Outcome?.Downloads is { Count: > 0 });

    protected override async Task OnInitializedAsync()
    {
        Workspace.Changed += OnWorkspaceChanged;
        Workspace.BatchFinished += OnBatchFinished;
        await Workspace.EnsureInitializedAsync();
        _ready = true;
    }

    private void OnWorkspaceChanged() => _render.Coalesce(_ => InvokeAsync(() =>
    {
        _revision++;
        StateHasChanged();
    }));

    // One toast per batch (the workspace raises BatchFinished once, when the last file of the batch is done).
    private void OnBatchFinished(ConvertBatchFinished finished) => _ = InvokeAsync(() => ShowFinishedToast(finished));

    private void ShowFinishedToast(ConvertBatchFinished finished)
    {
        var summary = finished.Summary;
        var job = finished.LastJob;
        if (summary.Total == 1)
        {
            switch (job.Status)
            {
                case JobStatus.Succeeded:
                    var name = job.Result?.Primary?.FileName ?? finished.LastItem.FileName;
                    Toasts.ShowSuccess(Workspace.WritesToDisk ? $"{name} is saved" : $"{name} is ready to download");
                    break;
                case JobStatus.Failed:
                    Toasts.ShowError($"{finished.LastItem.FileName} could not be converted. {ConvertRules.RowError(job.Result?.Error, finished.LastItem.Size)}");
                    break;
            }

            return;
        }

        if (summary.Failed == 0 && summary.Succeeded > 0)
        {
            Toasts.ShowSuccess($"{summary.Succeeded} files converted");
        }
        else if (summary.Failed > 0)
        {
            Toasts.ShowWarning($"{summary.Succeeded} converted, {summary.Failed} failed. Each failed file shows the reason in the list.");
        }
    }

    private void OnFilesAdded(int count)
    {
        _revision++;
        if (count > 0)
        {
            // Only files added just now: a row already marked Unsupported was announced when it arrived.
            var unsupported = Items.Where(i => !i.IsSupported && _announcedUnsupported.Add(i.Id)).ToList();
            if (unsupported.Count > 0)
            {
                Toasts.ShowWarning(unsupported.Count == 1
                    ? $"{unsupported[0].FileName} cannot be converted: AllInOne PDF cannot read .{unsupported[0].Extension.ToLowerInvariant()} files."
                    : $"{unsupported.Count} files cannot be converted: AllInOne PDF cannot read their type. They are marked Unsupported in the list.");
            }
        }
    }

    private void AddSample()
    {
        Workspace.AddContent("Designing a theme.md", Encoding.UTF8.GetBytes(SampleDocuments.WelcomeMarkdown));
    }

    // "Set target for all" menu. The button toggles it; Escape closes it and gives focus back to the
    // button (app.js); a click on the button just after the menu closed itself (outside click) must not reopen it.
    private void ToggleBulkMenu()
    {
        if (!_bulkMenuOpen && DateTime.UtcNow - _bulkMenuClosedAt < TimeSpan.FromMilliseconds(120))
        {
            return;
        }

        _bulkMenuOpen = !_bulkMenuOpen;
        if (!_bulkMenuOpen)
        {
            _bulkMenuClosedAt = DateTime.UtcNow;
        }
    }

    private void OnBulkMenuOpenChanged(bool open)
    {
        if (_bulkMenuOpen && !open)
        {
            _bulkMenuClosedAt = DateTime.UtcNow;
        }

        _bulkMenuOpen = open;
    }

    private void SetTargetForAll(IExporter target)
    {
        _bulkMenuOpen = false;
        _bulkMenuClosedAt = DateTime.UtcNow;
        var count = Workspace.SetTargetForAll(target.Format.Id);
        var skipped = Items.Count(i => i.IsSupported && !i.IsBusy) - count;
        if (skipped > 0)
        {
            Toasts.ShowInfo($"Target set to {target.Format.DisplayName} for {count} {(count == 1 ? "file" : "files")}. {skipped} cannot be converted to {target.Format.DisplayName} and keep their target.");
        }
    }

    private async Task DownloadAllAsync()
    {
        foreach (var item in Items.Where(i => i.State == ConvertItemState.Succeeded))
        {
            if (item.Outcome?.Downloads is not { Count: > 0 } outputs)
            {
                continue;
            }

            try
            {
                foreach (var file in ConvertDownloads.Prepare(outputs))
                {
                    await Host.SaveFileAsync(file.FileName, file.Content, file.MediaType);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or JSException or TaskCanceledException)
            {
                Toasts.ShowError($"{item.FileName} could not be downloaded. Try again.");
                return;
            }

            // Downloaded: the bytes leave memory (convert again to download again).
            await Workspace.MarkDownloadedAsync(item);
        }
    }

    private Task ConvertAllAsync() => ConvertAsync(Workspace.PendingItems());

    private Task ConvertOneAsync(ConvertItem item) => ConvertAsync([item]);

    private async Task ConvertAsync(IReadOnlyList<ConvertItem> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        // Jobs without an explicit theme use the default theme as it is now.
        await Workspace.RefreshDefaultThemeAsync();

        var overwrite = Workspace.Overwrite == OverwritePolicy.Replace;
        if (Workspace.Overwrite == OverwritePolicy.Ask)
        {
            var conflicts = Workspace.FindConflicts(items);
            if (conflicts.Count > 0)
            {
                var message = conflicts.Count == 1
                    ? $"A converted copy of {conflicts[0].FileName} already exists in the output folder. Replace it, or keep both with a numbered name?"
                    : $"{conflicts.Count} converted files already exist in the output folder. Replace them, or keep both with numbered names?";
                overwrite = await Dialogs.ConfirmDestructiveAsync(message, "Replace", "Files already exist", cancelText: "Keep both");
            }
        }

        var queued = Workspace.EnqueueAll(items, overwrite);
        if (queued == 0)
        {
            Toasts.ShowWarning("Nothing was converted. The files marked with a warning need attention first.");
        }
    }

    private async Task OpenOptionsAsync(ConvertItem item)
    {
        var parameters = new DialogParameters
        {
            Title = "Conversion options",
            Alignment = HorizontalAlignment.Right,
            Width = "440px",
            PrimaryAction = "Done",
            SecondaryAction = null,
            TrapFocus = true,
            Modal = true,
            PreventScroll = true,
        };

        var panel = await Dialogs.ShowPanelAsync<ConvertOptionsDrawer>(new ConvertOptionsContext(item, Workspace), parameters);
        await panel.Result;
        _revision++;
        StateHasChanged();
    }

    public void Dispose()
    {
        Workspace.Changed -= OnWorkspaceChanged;
        Workspace.BatchFinished -= OnBatchFinished;
        _render.Dispose();
        GC.SuppressFinalize(this);
    }
}
