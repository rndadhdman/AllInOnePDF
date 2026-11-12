using System.Globalization;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Themes;

namespace AllInOnePdf.UI.Services;

/// <summary>Inline validation messages for the theme builder. Null means "fine".</summary>
public static class ThemeBuilderValidation
{
    public static string? TextContrast(PdfTheme t)
    {
        var ratio = ThemeBuilderColor.Contrast(t.Colors.Text, t.Page.Background);
        return ratio is { } r && r < 4.5
            ? $"Body text has a contrast of {r.ToString("0.0", CultureInfo.InvariantCulture)}:1 against the page background. Aim for at least 4.5:1."
            : null;
    }

    public static string? HeadingContrast(PdfTheme t, string? color)
    {
        var ratio = ThemeBuilderColor.Contrast(color ?? t.Headings.Color, t.Page.Background);
        return ratio is { } r && r < 3
            ? $"Headings have a contrast of {r.ToString("0.0", CultureInfo.InvariantCulture)}:1 against the page background. Aim for at least 3:1."
            : null;
    }

    /// <summary>
    /// Checks one callout kind: body text against its background (4.5:1) and the accent (bar, icon, label) against
    /// the same background (3:1), mirroring how the renderer resolves both colours.
    /// </summary>
    public static string? CalloutContrast(PdfTheme t, CalloutKind kind)
    {
        var style = t.Callouts.Kind(kind);
        var background = style.Background ?? t.Callouts.Background ?? t.Colors.Surface;
        var accent = style.Accent ?? t.Colors.Accent;
        var text = ThemeBuilderColor.Contrast(t.Colors.Text, background);
        var label = ThemeBuilderColor.Contrast(accent, background);
        if (text is not { } tr || label is not { } lr)
        {
            return null;
        }

        var accentShown = t.Callouts.ShowLabel || t.Callouts.ShowIcon;
        if (tr >= 4.5 && (lr >= 3 || !accentShown))
        {
            return null;
        }

        var worst = tr < 4.5 ? $"the text has a contrast of {Ratio(tr)}:1" : $"the accent has a contrast of {Ratio(lr)}:1";
        return $"{kind} callouts: {worst} against the callout background. Aim for {(tr < 4.5 ? "4.5:1" : "3:1")} or more.";
    }

    private static string Ratio(double r) => r.ToString("0.0", CultureInfo.InvariantCulture);

    public static string? Margins(PdfTheme t)
    {
        var (w, h) = t.Page.GetSizeMm();
        var cw = w - t.Page.MarginLeftMm - t.Page.MarginRightMm;
        var ch = h - t.Page.MarginTopMm - t.Page.MarginBottomMm;
        if (cw <= 0 || ch <= 0)
        {
            return "The margins add up to more than the page width or height, so there is no room for text.";
        }

        if (cw < w * 0.3 || ch < h * 0.3)
        {
            return "The margins leave less than a third of the page width or height for text.";
        }

        return null;
    }

    /// <summary>
    /// Checks a header or footer template: a <c>{</c> without its <c>}</c>, or a token the renderer does not know,
    /// is printed as typed, so both get a message naming the first problem.
    /// </summary>
    public static string? Template(string? template)
    {
        if (string.IsNullOrEmpty(template))
        {
            return null;
        }

        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] != '{')
            {
                continue;
            }

            var end = template.IndexOfAny(['{', '}'], i + 1);
            if (end < 0 || template[end] == '{')
            {
                var stop = template.IndexOfAny([' ', '{'], i + 1);
                var fragment = stop < 0 ? template[i..] : template[i..stop];
                return $"\"{fragment}\" is not closed: add }} to make it a token, or it prints as typed.";
            }

            var token = template[i..(end + 1)];
            if (!ThemeBuilderPreview.Tokens.Any(t => string.Equals(t.Token, token, StringComparison.OrdinalIgnoreCase)))
            {
                return $"{token} is not a token, so it prints as typed. Use one of {string.Join(", ", ThemeBuilderPreview.Tokens.Select(t => t.Token))}.";
            }

            i = end;
        }

        return null;
    }

    /// <summary>All current warnings, for the top bar summary.</summary>
    public static IReadOnlyList<string> All(PdfTheme t)
    {
        var list = new List<string>();
        if (TextContrast(t) is { } a)
        {
            list.Add(a);
        }

        if (Margins(t) is { } b)
        {
            list.Add(b);
        }

        foreach (var (hf, name) in new[] { (t.Header, "Header"), (t.Footer, "Footer") })
        {
            if (!hf.Enabled)
            {
                continue;
            }

            foreach (var part in new[] { hf.Left, hf.Center, hf.Right })
            {
                if (Template(part) is { } message)
                {
                    list.Add($"{name}: {message}");
                    break;
                }
            }
        }

        foreach (var kind in Enum.GetValues<CalloutKind>())
        {
            if (CalloutContrast(t, kind) is { } c)
            {
                list.Add(c);
            }
        }

        if (!ThemeBuilderFonts.IsPortable(t.Typography.BodyFont) || !ThemeBuilderFonts.IsPortable(t.Headings.Font) || !ThemeBuilderFonts.IsPortable(t.Code.Font))
        {
            list.Add("A system font is used. On a computer without it, PDFs use Inter instead.");
        }

        return list;
    }
}
