namespace AllInOnePdf.UI.Components.ThemeBuilder;

/// <summary>Model of <see cref="UnsavedChangesDialog"/>.</summary>
public sealed class UnsavedChangesModel
{
    /// <summary>Name of the theme or publication being left.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Label of the save button ("Save and leave", or "Save a copy and leave" for a built-in theme).</summary>
    public string SaveLabel { get; set; } = "Save and leave";
}
