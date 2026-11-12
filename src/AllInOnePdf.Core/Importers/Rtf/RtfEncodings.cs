using System.Text;

namespace AllInOnePdf.Core.Importers.Rtf;

/// <summary>RTF character sets and code pages → .NET encodings (the code-page provider ships with .NET).</summary>
internal static class RtfEncodings
{
    public const int Western = 1252;

    private static readonly Dictionary<int, Encoding?> Cache = [];
    private static readonly Lock CacheLock = new();

    static RtfEncodings()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>Code page for an <c>\fcharsetN</c> value; null for "default"/unknown sets (the caller falls back).</summary>
    public static int? CodePageForCharset(int charset) => charset switch
    {
        0 => 1252,
        2 => 1252,
        77 => 10000,
        128 => 932,
        129 => 949,
        130 => 1361,
        134 => 936,
        136 => 950,
        161 => 1253,
        162 => 1254,
        163 => 1258,
        177 => 1255,
        178 => 1256,
        186 => 1257,
        204 => 1251,
        222 => 874,
        238 => 1250,
        254 => 437,
        _ => null,
    };

    /// <summary>True for East Asian character sets (their fixed-pitch fonts are ordinary text fonts, not code fonts).</summary>
    public static bool IsEastAsian(int? charset) => charset is 128 or 129 or 130 or 134 or 136;

    /// <summary>The encoding for a code page, or null when .NET does not know it.</summary>
    public static Encoding? Get(int codePage)
    {
        lock (CacheLock)
        {
            if (Cache.TryGetValue(codePage, out var cached))
            {
                return cached;
            }

            Encoding? encoding;
            try
            {
                encoding = Encoding.GetEncoding(codePage, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                encoding = null;
            }

            Cache[codePage] = encoding;
            return encoding;
        }
    }

    /// <summary>Windows-1252, or Latin-1 if the provider is somehow unavailable.</summary>
    public static Encoding WesternEncoding => Get(Western) ?? Encoding.Latin1;

    /// <summary>
    /// Maps characters decoded as Windows-1252 from a symbol font (Symbol, Wingdings) to the Unicode glyphs they
    /// display, for the handful used as list bullets and check marks.
    /// </summary>
    public static string MapSymbolFont(string text, bool wingdings)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            sb.Append(ch switch
            {
                '·' => '•',
                '§' => '▪',
                'Ø' => '➢',
                'ü' when wingdings => '✓',
                'q' when wingdings => '❑',
                'v' when wingdings => '❖',
                'n' when wingdings => '■',
                'l' when wingdings => '●',
                'o' when wingdings => '□',
                _ => ch,
            });
        }

        return sb.ToString();
    }
}
