using AllInOnePdf.Core.Fonts;
using SkiaSharp;

namespace AllInOnePdf.UI.Services;

/// <summary>Font families for the theme builder's font picker: bundled (portable) first, then installed system fonts.</summary>
public static class ThemeBuilderFonts
{
    private static readonly Lazy<IReadOnlyList<string>> SystemFamilies = new(LoadSystemFamilies, LazyThreadSafetyMode.ExecutionAndPublication);

    public static IReadOnlyList<BundledFontFamily> Bundled => BundledFonts.Families;

    /// <summary>Installed font families on the machine running the app (the server, on the web host). Bundled names excluded.</summary>
    public static IReadOnlyList<string> System => SystemFamilies.Value;

    public static bool IsPortable(string? family) => string.IsNullOrWhiteSpace(family) || BundledFonts.IsBundled(family);

    private static List<string> LoadSystemFamilies()
    {
        try
        {
            return SKFontManager.Default.FontFamilies
                .Where(f => !string.IsNullOrWhiteSpace(f) && !f.StartsWith('.') && !BundledFonts.IsBundled(f))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception)
        {
            // Font enumeration can fail on minimal Linux installs without fontconfig; bundled fonts still work.
            return [];
        }
    }
}
