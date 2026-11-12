namespace AllInOnePdf.UI.Components.ThemeBuilder;

/// <summary>What the user clicked in the fast preview: the settings group to open, an optional sub-tab and a label.</summary>
public sealed record PreviewSelection(string Group, string? Sub, string Label);
