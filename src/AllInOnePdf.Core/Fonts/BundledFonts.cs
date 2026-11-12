using System.Reflection;
using AllInOnePdf.Layout.Drawing;

namespace AllInOnePdf.Core.Fonts;

public enum FontClass { Sans, Serif, Mono }

public sealed record BundledFontFamily(string Name, FontClass Class, string Description, string License);

/// <summary>
/// Fonts embedded in the assembly (SIL Open Font License) so PDF output is identical on every OS.
/// Static instances were generated from the Google Fonts variable files: Regular, Medium, SemiBold, Bold + italics.
/// </summary>
public static class BundledFonts
{
    public const string Sans = "Inter";
    public const string Serif = "Literata";
    public const string Mono = "JetBrains Mono";

    public static IReadOnlyList<BundledFontFamily> Families { get; } =
    [
        new(Sans, FontClass.Sans, "Clean, highly legible UI sans-serif. Great default for screen and print.", "SIL OFL 1.1"),
        new(Serif, FontClass.Serif, "Contemporary serif designed for comfortable long-form reading on screens.", "SIL OFL 1.1"),
        new(Mono, FontClass.Mono, "Developer monospace with clear glyph shapes for code blocks.", "SIL OFL 1.1"),
    ];

    private static readonly Lazy<bool> Registration = new(RegisterCore, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Registers all embedded fonts with the layout engine's <c>FontManager</c> once. Safe to call repeatedly.</summary>
    public static void EnsureRegistered() => _ = Registration.Value;

    public static bool IsBundled(string? family) =>
        family is not null && Families.Any(f => string.Equals(f.Name, family, StringComparison.OrdinalIgnoreCase));

    /// <summary>Resource names of all embedded TTF files.</summary>
    public static IEnumerable<string> ResourceNames =>
        typeof(BundledFonts).Assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase));

    public static Stream OpenResource(string resourceName) =>
        typeof(BundledFonts).Assembly.GetManifestResourceStream(resourceName)
        ?? throw new FileNotFoundException($"Embedded font '{resourceName}' not found.");

    /// <summary>Returns the embedded font files for a family as (fileName, bytes), e.g. for the web reader or export.</summary>
    public static IEnumerable<(string FileName, byte[] Data)> GetFamilyFiles(string family)
    {
        var compact = family.Replace(" ", "", StringComparison.Ordinal);
        foreach (var name in ResourceNames)
        {
            var fileName = name.Split('.').TakeLast(2).Aggregate((a, b) => a + "." + b);
            if (!fileName.StartsWith(compact + "-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var stream = OpenResource(name);
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            yield return (fileName, ms.ToArray());
        }
    }

    private static bool RegisterCore()
    {
        foreach (var name in ResourceNames)
        {
            using var stream = OpenResource(name);
            FontManager.RegisterFontFromStream(stream);
        }

        return true;
    }
}
