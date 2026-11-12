namespace AllInOnePdf.UI.Components.Shell;

/// <summary>Content of a <see cref="MarkupDialog"/>.</summary>
/// <param name="Html">Trusted, already encoded HTML to show.</param>
/// <param name="Label">Accessible name of the scrollable content.</param>
/// <param name="Intro">Optional plain-text sentence shown above the content.</param>
public sealed record MarkupDialogContent(string Html, string Label, string? Intro = null);
