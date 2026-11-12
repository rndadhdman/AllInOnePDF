using System.Collections.Concurrent;
using UglyToad.PdfPig.Content;

namespace AllInOnePdf.Core.Importers.Pdf;

/// <summary>
/// Classifies fonts as bold / italic / monospace from the font descriptor and from name hints
/// ("Inter-SemiBold", "Courier", "JetBrainsMono-Italic" ...). Results are cached per font name.
/// </summary>
internal static class FontClassifier
{
    private static readonly ConcurrentDictionary<string, FontFlags> NameCache = new(StringComparer.Ordinal);

    private static readonly string[] BoldHints = ["bold", "black", "heavy", "semibold", "demi", "extrabold", "ultrabold"];
    private static readonly string[] ItalicHints = ["italic", "oblique", "slanted"];
    private static readonly string[] MonoHints =
    [
        "mono", "courier", "consolas", "menlo", "monaco", "inconsolata", "code", "fixed", "typewriter",
        "lucidaconsole", "lucida console", "cmtt", "sfmono", "andale", "terminal", "ocr-a", "letter gothic",
    ];

    public static FontFlags Classify(Letter letter)
    {
        var flags = FromName(letter.FontName);
        var details = letter.FontDetails;
        if (details is not null)
        {
            if (details.IsBold || details.Weight >= 600)
            {
                flags |= FontFlags.Bold;
            }

            if (details.IsItalic)
            {
                flags |= FontFlags.Italic;
            }
        }

        return flags;
    }

    /// <summary>Flags implied by a font name alone (subset prefix such as "ABCDEF+" is ignored).</summary>
    public static FontFlags FromName(string? fontName)
    {
        if (string.IsNullOrEmpty(fontName))
        {
            return FontFlags.None;
        }

        return NameCache.GetOrAdd(fontName, static name =>
        {
            var plus = name.IndexOf('+', StringComparison.Ordinal);
            var core = (plus is >= 0 and <= 7 ? name[(plus + 1)..] : name).ToLowerInvariant();
            var flags = FontFlags.None;
            if (BoldHints.Any(h => core.Contains(h, StringComparison.Ordinal)))
            {
                flags |= FontFlags.Bold;
            }

            if (ItalicHints.Any(h => core.Contains(h, StringComparison.Ordinal)))
            {
                flags |= FontFlags.Italic;
            }

            if (MonoHints.Any(h => core.Contains(h, StringComparison.Ordinal)))
            {
                flags |= FontFlags.Mono;
            }

            return flags;
        });
    }
}
