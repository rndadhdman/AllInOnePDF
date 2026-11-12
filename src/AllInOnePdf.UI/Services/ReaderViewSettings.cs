using System.Globalization;

namespace AllInOnePdf.UI.Services;

/// <summary>Reflow or print-preview pages.</summary>
public enum ReaderMode { Reflow, Pages }

/// <summary>
/// The Reader's typography settings, mirrored from <see cref="UserPreferences"/> (<c>Reader*</c> properties).
/// Immutable so components can compare instances; all values are clamped to comfortable ranges.
/// </summary>
public sealed record ReaderViewSettings
{
    public const double MinFontSize = 14;
    public const double MaxFontSize = 28;
    public const double MinLineHeight = 1.3;
    public const double MaxLineHeight = 2.0;
    public const int MinMeasure = 45;
    public const int MaxMeasure = 90;

    public static IReadOnlyList<string> FontFamilies { get; } = ["Literata", "Inter", "JetBrains Mono", "System"];

    public ReaderTheme Palette { get; init; } = ReaderTheme.Paper;
    public string Font { get; init; } = "Literata";
    public double FontSizePx { get; init; } = 19;
    public double LineHeight { get; init; } = 1.7;
    public int MeasureCh { get; init; } = 68;
    public bool Justify { get; init; }
    public bool ShowOutline { get; init; } = true;

    public static ReaderViewSettings FromPreferences(UserPreferences p) => new ReaderViewSettings
    {
        Palette = p.ReaderTheme,
        Font = p.ReaderFont,
        FontSizePx = p.ReaderFontSizePx,
        LineHeight = p.ReaderLineHeight,
        MeasureCh = p.ReaderMeasureCh,
        Justify = p.ReaderJustify,
        ShowOutline = p.ReaderShowOutline,
    }.Normalized();

    public void ApplyTo(UserPreferences p)
    {
        p.ReaderTheme = Palette;
        p.ReaderFont = Font;
        p.ReaderFontSizePx = FontSizePx;
        p.ReaderLineHeight = LineHeight;
        p.ReaderMeasureCh = MeasureCh;
        p.ReaderJustify = Justify;
        p.ReaderShowOutline = ShowOutline;
    }

    public ReaderViewSettings Normalized() => this with
    {
        Font = FontFamilies.FirstOrDefault(f => string.Equals(f, Font, StringComparison.OrdinalIgnoreCase)) ?? "Literata",
        FontSizePx = Math.Round(Math.Clamp(double.IsFinite(FontSizePx) ? FontSizePx : 19, MinFontSize, MaxFontSize)),
        LineHeight = Math.Round(Math.Clamp(double.IsFinite(LineHeight) ? LineHeight : 1.7, MinLineHeight, MaxLineHeight), 2),
        MeasureCh = Math.Clamp(MeasureCh, MinMeasure, MaxMeasure),
    };

    public ReaderViewSettings WithFontSizeStep(int direction) => (this with { FontSizePx = FontSizePx + direction }).Normalized();

    /// <summary>
    /// <paramref name="current"/> with the typography (font, size, line height, width, justify) of
    /// <paramref name="defaults"/>: what the Text panel's "Reset to defaults" shows. Palette and outline stay.
    /// </summary>
    public static ReaderViewSettings WithTypographyOf(ReaderViewSettings current, ReaderViewSettings defaults)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(defaults);
        return (current with
        {
            Font = defaults.Font,
            FontSizePx = defaults.FontSizePx,
            LineHeight = defaults.LineHeight,
            MeasureCh = defaults.MeasureCh,
            Justify = defaults.Justify,
        }).Normalized();
    }

    public static string PaletteClass(ReaderTheme palette) => palette switch
    {
        ReaderTheme.Sepia => "aio-reader-sepia",
        ReaderTheme.Dusk => "aio-reader-dusk",
        ReaderTheme.Night => "aio-reader-night",
        _ => "aio-reader-paper",
    };

    public static bool IsDarkPalette(ReaderTheme palette) => palette is ReaderTheme.Dusk or ReaderTheme.Night;

    /// <summary>
    /// Palette actually shown: the chosen one, or, while the user has not picked one
    /// (<see cref="UserPreferences.ReaderPaletteChosen"/> is false), Dusk in dark app mode and the stored palette
    /// (Paper by default) otherwise.
    /// </summary>
    public static ReaderTheme EffectivePalette(ReaderTheme palette, bool paletteChosen, bool appDark) =>
        !paletteChosen && appDark ? ReaderTheme.Dusk : palette;

    public static string FontStack(string font) => font switch
    {
        "Inter" => "\"Inter\", system-ui, -apple-system, \"Segoe UI\", sans-serif",
        "JetBrains Mono" => "var(--aio-font-mono)",
        "System" => "system-ui, -apple-system, \"Segoe UI Variable Text\", \"Segoe UI\", Roboto, \"Noto Sans\", sans-serif",
        _ => "var(--aio-font-serif)",
    };

    /// <summary>
    /// CSS custom properties consumed by <c>.aio-doc</c> (app.css) and the reading surface: set inline on the Reader
    /// surface and on the Settings preview so both render document text the same way.
    /// </summary>
    public string ToCssVariables() => string.Create(CultureInfo.InvariantCulture,
        $"--aio-doc-font:{FontStack(Font)};--aio-doc-size:{FontSizePx}px;--aio-doc-leading:{LineHeight};--aio-doc-measure:{MeasureCh}ch;");
}
