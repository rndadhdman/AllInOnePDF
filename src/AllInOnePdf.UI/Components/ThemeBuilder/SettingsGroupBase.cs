using AllInOnePdf.Core.Themes;
using AllInOnePdf.UI.Services;
using Microsoft.AspNetCore.Components;

namespace AllInOnePdf.UI.Components.ThemeBuilder;

/// <summary>Base for one settings group in the builder panel. Re-renders when <see cref="Version"/> changes.</summary>
public abstract class SettingsGroupBase : ComponentBase
{
    [Parameter] public int Version { get; set; }

    /// <summary>Sub-tab within the group (heading level "h2", "header"/"footer"), driven by click-to-select.</summary>
    [Parameter] public string? Sub { get; set; }

    [Parameter] public EventCallback<string?> SubChanged { get; set; }

    [Inject] protected ThemeBuilderState State { get; set; } = default!;

    /// <summary>The working theme copy (never null while the builder is open).</summary>
    protected PdfTheme T => State.Working!;

    protected Task SelectSub(string? sub) => SubChanged.InvokeAsync(sub);
}
