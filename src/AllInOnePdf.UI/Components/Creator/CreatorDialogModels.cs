namespace AllInOnePdf.UI.Components.Creator;

/// <summary>Model of <see cref="ThemePickerDialog"/>: the theme in use and the one picked.</summary>
public sealed class ThemePickerModel
{
    /// <summary>Id of the gallery theme the publication's snapshot came from.</summary>
    public string? CurrentId { get; set; }

    /// <summary>Id of the theme the user picked, or null.</summary>
    public string? SelectedId { get; set; }
}

/// <summary>Model of <see cref="TemplatePickerDialog"/>: the template picked.</summary>
public sealed class TemplatePickerModel
{
    /// <summary>Id of the template the user picked, or null.</summary>
    public string? SelectedId { get; set; }
}

/// <summary>Model of <see cref="LayoutSwitchDialog"/>: the layout chosen and how many sections use the other one.</summary>
public sealed class LayoutSwitchModel
{
    /// <summary>The layout the publication switches to.</summary>
    public AllInOnePdf.Publishing.Model.SectionLayout Layout { get; set; }

    /// <summary>Sections that use the other layout.</summary>
    public int OtherSections { get; set; }
}

/// <summary>Model of <see cref="SaveAsPublicationDialog"/>: the name of the copy.</summary>
public sealed class SaveAsPublicationModel
{
    /// <summary>Name for the new publication.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>True when the open publication was never saved, so there is no original to keep.</summary>
    public bool NeverSaved { get; set; }
}
