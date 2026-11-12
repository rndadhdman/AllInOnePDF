using System.Globalization;
using System.Text;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Highlighting;
using AllInOnePdf.Core.Rendering;
using AllInOnePdf.Core.Themes;
using static AllInOnePdf.Core.Exporters.Html.CssValues;

namespace AllInOnePdf.Core.Exporters.Html;

/// <summary>
/// Turns a <see cref="PdfTheme"/> into the single stylesheet of an exported HTML page. It styles the <c>aio-*</c>
/// markup of <see cref="DocumentHtmlRenderer"/> plus the page frame, cover and contents list, and maps the theme's
/// geometry to <c>@page</c>/<c>@media print</c>. Values follow the theme builder preview (and so the PDF renderer)
/// as closely as CSS allows. The theme alone decides colours; <c>prefers-color-scheme</c> is not used.
/// </summary>
internal static class ThemeStylesheet
{
    public static string Generate(PdfTheme t, string? fontFaces = null, bool minify = false)
    {
        var css = new StringBuilder(12288);
        var nl = minify ? string.Empty : "\n";

        void Rule(string selector, string declarations) => css.Append(selector).Append('{').Append(declarations).Append('}').Append(nl);

        if (!string.IsNullOrEmpty(fontFaces))
        {
            css.Append(fontFaces);
        }

        const string d = ".aio-doc";
        var (wMm, hMm) = t.Page.GetSizeMm();
        var contentWidthMm = Math.Max(40, wMm - t.Page.MarginLeftMm - t.Page.MarginRightMm);
        var contentHeightPt = MmToPt(hMm - t.Page.MarginTopMm - t.Page.MarginBottomMm);
        var pageBg = Color(t.Page.Background, "#FFFFFF");
        var darkPage = IsDark(t.Page.Background);
        var text = Color(t.Colors.Text, "#1F2937");
        var muted = Color(t.Colors.Muted, "#6B7280");
        var accent = Color(t.Colors.Accent, "#0F6CBD");
        var surface = Color(t.Colors.Surface, "#F3F4F6");
        var border = Color(t.Colors.Border, "#D1D5DB");
        var body = t.Typography.BodySizePt;
        var sp = t.Typography.ParagraphSpacingPt;
        var bodyFont = FontStack(t.Typography.BodyFont);
        var headingFont = FontStack(t.Headings.Font ?? t.Typography.BodyFont);
        var mono = FontStack(t.Code.Font);
        var headingColor = Color(t.Headings.Color, "#111827");
        var align = CssAlign(t.Typography.Alignment);
        var hyphens = t.Typography.Hyphenate ? "auto" : "manual";

        // ---- page frame
        Rule(":root", $"color-scheme:{(darkPage ? "dark" : "light")};");
        Rule("*,*::before,*::after", "box-sizing:border-box;");
        Rule("html", $"background:{pageBg};-webkit-text-size-adjust:100%;text-size-adjust:100%;");
        Rule("body", $"margin:0;background:{pageBg};color:{text};font-family:{bodyFont};font-size:{N(body)}pt;line-height:{N(t.Typography.LineHeight)};" +
            $"letter-spacing:{N(t.Typography.LetterSpacingEm)}em;font-kerning:normal;overflow-wrap:break-word;");
        Rule(".aio-page", $"max-width:{N(contentWidthMm)}mm;margin:0 auto;padding:{N(Math.Min(t.Page.MarginTopMm, 25))}mm 16px {N(Math.Min(t.Page.MarginBottomMm, 25))}mm;");

        // ---- document baseline
        Rule(d, $"counter-reset:aioh1 aioh2 aioh3 aioh4 aioh5 aioh6;");
        Rule(d + " strong", "font-weight:700;");
        Rule(d + " .aio-smallcaps", "font-variant:small-caps;");
        Rule(d + " mark", $"background:{(darkPage ? "#5C4B00" : "#FFF3A0")};color:inherit;padding:0 .1em;");
        Rule(d + " sup," + d + " sub", "line-height:0;");

        // ---- paragraphs
        Rule(d + " .aio-p", $"margin:0 0 {N(sp)}pt;text-align:{align};-webkit-hyphens:{hyphens};hyphens:{hyphens};");
        if (t.Typography.FirstLineIndentPt > 0)
        {
            Rule(d + " > .aio-p + .aio-p", $"text-indent:{N(t.Typography.FirstLineIndentPt)}pt;");
        }

        Rule(d + " :is(li,blockquote,aside,td,th,.aio-footnotes) .aio-p", $"margin-bottom:{N(Math.Min(sp, 4))}pt;text-indent:0;");
        Rule(d + " :is(li,blockquote,aside,td,th) > .aio-p:last-child", "margin-bottom:0;");
        Rule(d + " .aio-rawhtml", $"font-style:italic;color:{muted};");

        // ---- headings
        Rule(d + " .aio-h", $"font-family:{headingFont};line-height:{N(t.Headings.LineHeight)};color:{headingColor};padding:0;");
        for (var level = 1; level <= 6; level++)
        {
            var h = t.Headings.Level(level);
            var lv = level.ToString(CultureInfo.InvariantCulture);
            var decl = new StringBuilder();
            decl.Append(CultureInfo.InvariantCulture, $"font-size:{N(h.SizePt)}pt;font-weight:{Weight(h.Weight)};font-style:{(h.Italic ? "italic" : "normal")};");
            decl.Append(CultureInfo.InvariantCulture, $"text-transform:{(h.Uppercase ? "uppercase" : "none")};letter-spacing:{N(h.LetterSpacingEm)}em;");
            decl.Append(CultureInfo.InvariantCulture, $"margin:{N(h.SpaceBeforePt)}pt 0 {N(h.SpaceAfterPt)}pt;text-align:{CssAlign(h.Alignment, headings: true)};");
            if (!string.IsNullOrWhiteSpace(h.Color))
            {
                decl.Append(CultureInfo.InvariantCulture, $"color:{Color(h.Color, t.Headings.Color)};");
            }

            if (h.Underline)
            {
                decl.Append(CultureInfo.InvariantCulture, $"border-bottom:{N(Math.Max(0.25, h.RuleWidthPt))}pt solid {Color(h.RuleColor, t.Colors.Accent)};padding-bottom:3pt;");
            }

            decl.Append("counter-increment:aioh").Append(lv).Append(';');
            if (level < 6)
            {
                decl.Append("counter-reset:").Append(string.Join(' ', Enumerable.Range(level + 1, 6 - level).Select(l => "aioh" + l.ToString(CultureInfo.InvariantCulture)))).Append(';');
            }

            Rule(d + " .aio-h" + lv, decl.ToString());

            if (t.Headings.NumberHeadings)
            {
                var counters = string.Join(" \".\" ", Enumerable.Range(1, level).Select(l => $"counter(aioh{l.ToString(CultureInfo.InvariantCulture)})"));
                Rule(d + " .aio-h" + lv + "::before", $"content:{counters} \"\\00a0\\00a0\";color:{accent};");
            }
        }

        Rule(d + " > .aio-h:first-child", "margin-top:0;");

        // ---- links
        var linkColor = Color(t.Links.Color, t.Colors.Accent);
        Rule(d + " a.aio-link", $"color:{linkColor};text-decoration:{(t.Links.Underline ? "underline" : "none")};font-weight:{Weight(t.Links.Weight)};");
        Rule(d + " a.aio-link:hover", "text-decoration:underline;");
        Rule(d + " .aio-fnref a", $"color:{accent};text-decoration:none;");

        // ---- inline code and math
        var c = t.Code;
        var codeBgHex = c.Background ?? t.Colors.Surface;
        var codeBg = Color(c.Background, t.Colors.Surface);
        var codeFg = Color(c.TextColor, t.Colors.Text);
        var inlineBg = Color(c.InlineBackground ?? c.Background, t.Colors.Surface);
        var inlineFg = c.InlineTextColor is not null || c.TextColor is not null ? Color(c.InlineTextColor ?? c.TextColor, t.Colors.Text) : "inherit";
        Rule(d + " .aio-code", $"font-family:{mono};font-size:{N(c.InlineScale * 100)}%;background:{inlineBg};color:{inlineFg};padding:.05em .25em;border-radius:3px;letter-spacing:0;");
        Rule(d + " .aio-math-inline", $"font-family:{mono};font-size:95%;");
        Rule(d + " .aio-math", $"margin:0 0 {N(sp)}pt;text-align:center;font-family:{mono};font-size:{N(c.SizePt)}pt;white-space:pre-wrap;");
        Rule(d + " .aio-math code", "font:inherit;");

        // ---- code blocks
        Rule(d + " .aio-codeblock", $"margin:0 0 {N(sp)}pt;");
        var codeBorder = c.BorderWidthPt > 0 ? $"{N(c.BorderWidthPt)}pt solid {Color(c.BorderColor, t.Colors.Border)}" : "none";
        Rule(d + " .aio-codeblock pre", $"margin:0;background:{codeBg};color:{codeFg};border:{codeBorder};border-radius:{N(c.CornerRadiusPt)}pt;padding:{N(c.PaddingPt)}pt;" +
            $"font-family:{mono};font-size:{N(c.SizePt)}pt;line-height:{N(c.LineHeight)};letter-spacing:0;tab-size:4;font-variant-ligatures:none;" +
            (c.WrapLongLines ? "white-space:pre-wrap;overflow-wrap:anywhere;" : "white-space:pre;overflow-x:auto;"));
        Rule(d + " .aio-codeblock pre code", "font:inherit;color:inherit;background:none;padding:0;");
        var labelDecl = $"display:block;font-family:{mono};font-size:{N(c.SizePt * 0.9)}pt;color:{muted};margin:0 0 2pt;line-height:1.3;";
        Rule(d + " .aio-codeblock figcaption", c.ShowLanguageLabel ? labelDecl : "display:none;");
        if (c.ShowLanguageLabel)
        {
            Rule(d + " .aio-codeblock[data-lang]:not(:has(figcaption))::before", "content:attr(data-lang);" + labelDecl);
        }

        AppendHighlightPalette(Rule, t, IsDark(codeBgHex));

        // ---- quotes
        var q = t.Quotes;
        var quoteBorder = q.BorderWidthPt > 0 ? $"{N(q.BorderWidthPt)}pt solid {Color(q.BorderColor, t.Colors.Accent)}" : "none";
        Rule(d + " .aio-quote", $"margin:0 0 {N(sp)}pt;border-left:{quoteBorder};background:{(string.IsNullOrWhiteSpace(q.Background) ? "transparent" : Color(q.Background, "#F3F4F6"))};" +
            $"padding:{N(q.PaddingPt * 0.5)}pt {N(q.PaddingPt * 0.5)}pt {N(q.PaddingPt * 0.5)}pt {N(q.PaddingPt)}pt;font-style:{(q.Italic ? "italic" : "normal")};font-size:{N(q.FontScale * 100)}%;color:{Color(q.TextColor, t.Colors.Text)};");
        Rule(d + " .aio-quote .aio-attribution", $"display:block;margin-top:4pt;font-style:normal;font-size:90%;color:{muted};");
        Rule(d + " .aio-quote .aio-attribution::before", "content:\"\\2014\\00a0\";");

        // ---- callouts
        var co = t.Callouts;
        Rule(d + " .aio-callout", $"display:block;margin:0 0 {N(sp)}pt;border-radius:{N(co.CornerRadiusPt)}pt;padding:{N(co.PaddingPt)}pt;");
        foreach (var kind in Enum.GetValues<CalloutKind>())
        {
            var style = co.Kind(kind);
            var kindAccent = Color(style.Accent, t.Colors.Accent);
            var kindBg = Color(style.Background ?? co.Background, t.Colors.Surface);
            var bar = co.BorderWidthPt > 0 ? $"border-left:{N(co.BorderWidthPt)}pt solid {kindAccent};" : "border-left:0;";
            var cls = d + " .aio-callout-" + kind.ToString().ToLowerInvariant();
            Rule(cls, $"background:{kindBg};{bar}--aio-callout:{kindAccent};");
        }

        Rule(d + " .aio-callout-title", co.ShowLabel
            ? $"font-weight:600;font-size:95%;line-height:1.2;color:var(--aio-callout);margin:0;padding:0 0 4pt;"
            : "display:none;");
        if (co.ShowIcon && co.ShowLabel)
        {
            Rule(d + " .aio-callout-title::before", $"content:\"\";display:inline-block;width:.8em;height:.8em;border-radius:50%;background:var(--aio-callout);margin-right:6pt;vertical-align:-.05em;");
        }

        // ---- lists
        var l = t.Lists;
        var marker = Color(l.MarkerColor, t.Colors.Accent);
        Rule(d + " .aio-list", $"margin:0 0 {N(sp)}pt;padding:0 0 0 {N(l.IndentPt)}pt;");
        Rule(d + " .aio-list .aio-list", $"margin:{N(l.ItemSpacingPt)}pt 0 0;");
        Rule(d + " .aio-list > li", "margin:0;position:relative;");
        Rule(d + " .aio-list > li + li", $"margin-top:{N(l.ItemSpacingPt)}pt;");
        Rule(d + " ul.aio-list", "list-style:none;");
        Rule(d + " ul.aio-list > li::before", $"content:\"{Bullet(l.Bullet)}\";position:absolute;left:-{N(l.IndentPt)}pt;width:{N(l.IndentPt)}pt;color:{marker};font-style:normal;font-weight:400;");
        Rule(d + " ol.aio-list", $"list-style-type:{NumberStyleCss(l.Numbering)};");
        Rule(d + " ol.aio-list > li::marker", $"color:{marker};");
        if (l.Numbering == NumberStyle.DecimalParen)
        {
            Rule(d + " ol.aio-list > li::marker", "content:counter(list-item) \")\\00a0\";");
        }

        if (l.TaskCheckboxes)
        {
            Rule(d + " .aio-list > li.aio-task::before", "content:none;");
            Rule(d + " .aio-task > input", $"appearance:none;-webkit-appearance:none;position:absolute;left:-{N(l.IndentPt)}pt;top:.3em;width:.85em;height:.85em;margin:0;border:.9pt solid {marker};border-radius:2px;background:transparent;");
            Rule(d + " .aio-task > input:checked", $"background:{marker};box-shadow:inset 0 0 0 1.5pt {pageBg};");
            Rule(d + " .aio-task-done > .aio-p", $"color:{muted};");
        }
        else
        {
            Rule(d + " .aio-task > input", "display:none;");
        }

        Rule(d + " .aio-task > .aio-p", "display:inline;");

        // ---- tables
        var tb = t.Tables;
        var tBorder = Color(tb.BorderColor, t.Colors.Border);
        var bw = tb.BorderWidthPt;
        string? headerBg = tb.HeaderBackground is not null ? Color(tb.HeaderBackground, "#F3F4F6")
            : tb.Style is TableStyle.Grid or TableStyle.Striped ? surface : null;
        Rule(d + " .aio-tablewrap", $"margin:0 0 {N(sp)}pt;overflow-x:auto;");
        Rule(d + " .aio-table", $"width:100%;border-collapse:collapse;font-size:{N(tb.FontScale * 100)}%;line-height:1.35;");
        Rule(d + " .aio-table caption", tb.ShowCaptions
            ? $"caption-side:top;text-align:left;font-style:italic;font-size:90%;color:{muted};padding:0 0 4pt;"
            : "display:none;");
        Rule(d + " .aio-table :is(th,td)", $"padding:{N(tb.CellPaddingPt)}pt;vertical-align:top;text-align:left;");
        Rule(d + " .aio-table th", $"font-weight:{Weight(tb.HeaderWeight)};color:{Color(tb.HeaderTextColor, t.Colors.Text)};{(headerBg is null ? string.Empty : "background:" + headerBg + ";")}");
        switch (tb.Style)
        {
            case TableStyle.Grid:
                Rule(d + " .aio-table :is(th,td)", $"border:{N(bw)}pt solid {tBorder};");
                break;
            case TableStyle.Lines:
                Rule(d + " .aio-table th", $"border-bottom:{N(bw * 1.6)}pt solid {tBorder};");
                Rule(d + " .aio-table td", $"border-bottom:{N(bw)}pt solid {tBorder};");
                break;
            case TableStyle.Minimal:
                Rule(d + " .aio-table th", $"border-bottom:{N(bw * 1.6)}pt solid {tBorder};");
                break;
            case TableStyle.Striped:
                Rule(d + " .aio-table th", $"border-bottom:{N(bw * 1.6)}pt solid {tBorder};");
                Rule(d + " .aio-table tbody tr:nth-child(even) td", $"background:{Color(tb.StripeColor, t.Colors.Surface)};");
                break;
        }

        // Cell alignment from the renderer's inline style must win over the default left alignment above.
        Rule(d + " .aio-table :is(th,td)[style*=\"text-align:center\"]", "text-align:center;");
        Rule(d + " .aio-table :is(th,td)[style*=\"text-align:right\"]", "text-align:right;");

        // ---- images
        var im = t.Images;
        var imgAlign = im.Align switch { ImageAlign.Left => "left", ImageAlign.Right => "right", _ => "center" };
        Rule(d + " .aio-figure", $"margin:0 0 {N(im.SpacingPt)}pt;text-align:{imgAlign};");
        Rule(d + " .aio-figure img", $"max-width:{N(Math.Clamp(im.MaxWidthPercent, 5, 100))}%;max-height:{N(contentHeightPt * 0.9)}pt;height:auto;vertical-align:top;" +
            $"border-radius:{N(im.CornerRadiusPt)}pt;border:{(im.Border ? ".75pt solid " + border : "0")};");
        Rule(d + " .aio-figure figcaption", im.ShowCaptions
            ? $"display:block;font-size:{N(im.CaptionSizePt)}pt;font-style:italic;color:{muted};margin:0;padding-top:4pt;text-align:{imgAlign};"
            : "display:none;");
        Rule(d + " .aio-image-missing", $"display:inline-block;width:{N(Math.Clamp(im.MaxWidthPercent, 5, 100))}%;background:{surface};padding:12pt;border-radius:{N(im.CornerRadiusPt)}pt;font-style:italic;color:{muted};text-align:center;");
        Rule(d + " .aio-inline-img", "height:1.2em;width:auto;vertical-align:middle;");

        // ---- rules, breaks, footnotes
        Rule(d + " .aio-hr", $"border:0;border-top:.75pt solid {border};height:0;margin:{N(Math.Max(6, sp))}pt 0;");
        Rule(d + " .aio-pagebreak", $"height:0;margin:{N(Math.Max(12, sp * 2))}pt 0;border-top:1px dashed {border};");
        Rule(d + " .aio-footnotes", $"margin-top:18pt;padding-top:6pt;border-top:.5pt solid {border};font-size:85%;");
        Rule(d + " .aio-footnotes ol", "margin:0;padding-left:18pt;");
        Rule(d + " .aio-footnotes li::marker", $"color:{muted};");
        Rule(d + " .aio-footnotes li:target", $"background:{surface};");

        // ---- cover
        var cv = t.Cover;
        var titleColor = Color(cv.TitleColor, t.Headings.Color);
        var coverBg = cv.Background is null ? "transparent" : Color(cv.Background, t.Page.Background);
        Rule(".aio-cover", $"display:flex;flex-direction:column;justify-content:center;min-height:min(70vh,{N(hMm * 0.6)}mm);margin:0 0 24pt;padding:24pt 0;background:{coverBg};");
        Rule(".aio-cover-centered", "text-align:center;align-items:center;");
        Rule(".aio-cover-minimal", "justify-content:flex-start;");
        Rule(".aio-cover-bold", "justify-content:flex-end;");
        Rule(".aio-cover-split", $"border-left:18pt solid {accent};padding-left:28pt;");
        Rule(".aio-cover-banner .aio-cover-band", $"background:{accent};padding:40pt 28pt;margin:0 -16px;");
        Rule(".aio-cover-accent", $"width:64pt;height:5pt;background:{accent};margin-bottom:14pt;");
        Rule(".aio-cover-title", $"margin:0;font-family:{headingFont};font-size:{N(cv.TitleSizePt)}pt;font-weight:700;color:{titleColor};line-height:1.1;");
        Rule(".aio-cover-subtitle", $"margin:10pt 0 0;font-family:{headingFont};font-size:{N(cv.TitleSizePt * 0.45)}pt;color:{muted};line-height:1.25;");
        Rule(".aio-cover-meta", $"margin:28pt 0 0;font-size:{N(body * 1.05)}pt;color:{muted};");
        Rule(".aio-cover-note", $"margin:8pt 0 0;font-size:{N(body * 0.9)}pt;font-style:italic;color:{muted};");
        Rule(".aio-cover-banner .aio-cover-title", "color:#FFFFFF;");
        Rule(".aio-cover-banner .aio-cover-subtitle", "color:#E6F0FA;");

        // ---- table of contents
        var toc = t.Toc;
        var h1 = t.Headings.H1;
        Rule(".aio-toc", "margin:0 0 24pt;");
        Rule(".aio-toc-title", $"margin:0 0 {N(h1.SpaceAfterPt)}pt;font-family:{headingFont};font-size:{N(h1.SizePt * 0.8)}pt;font-weight:{Weight(h1.Weight)};color:{headingColor};");
        Rule(".aio-toc-list", "list-style:none;margin:0;padding:0;");
        Rule(".aio-toc-entry", $"padding:2.5pt 0;font-size:{N(body * 0.94)}pt;");
        Rule(".aio-toc-entry a", $"color:{text};text-decoration:none;");
        Rule(".aio-toc-entry a:hover", $"color:{linkColor};text-decoration:underline;");
        Rule(".aio-toc-top", $"font-size:{N(body)}pt;font-weight:600;");
        if (toc.IndentLevels)
        {
            for (var level = 2; level <= 6; level++)
            {
                Rule(".aio-toc-l" + level.ToString(CultureInfo.InvariantCulture), $"padding-left:{N((level - 1) * 14)}pt;");
            }
        }

        // ---- print
        css.Append(CultureInfo.InvariantCulture, $"@page{{size:{N(wMm)}mm {N(hMm)}mm;margin:{N(t.Page.MarginTopMm)}mm {N(t.Page.MarginRightMm)}mm {N(t.Page.MarginBottomMm)}mm {N(t.Page.MarginLeftMm)}mm;}}").Append(nl);
        css.Append("@media print{").Append(nl);
        Rule("html,body", $"background:{pageBg};-webkit-print-color-adjust:exact;print-color-adjust:exact;");
        Rule(".aio-page", "max-width:none;margin:0;padding:0;");
        if (t.Headings.KeepWithNext)
        {
            Rule(d + " .aio-h", "break-after:avoid;page-break-after:avoid;");
        }

        if (t.Typography.OrphanControl)
        {
            Rule(d + " .aio-p", "orphans:2;widows:2;");
        }

        Rule(d + " :is(.aio-codeblock,.aio-figure,.aio-callout,.aio-quote) ,"+ d + " .aio-table tr", "break-inside:avoid;page-break-inside:avoid;");
        if (tb.RepeatHeaderOnNewPage)
        {
            Rule(d + " .aio-table thead", "display:table-header-group;");
        }

        Rule(d + " .aio-pagebreak", "border:0;margin:0;break-after:page;page-break-after:always;");
        if (t.Headings.PageBreakBeforeH1)
        {
            Rule(d + " > .aio-h1:not(:first-child)", "break-before:page;page-break-before:always;");
        }

        Rule(".aio-cover", $"min-height:{N(hMm - t.Page.MarginTopMm - t.Page.MarginBottomMm - 2)}mm;margin:0;break-after:page;page-break-after:always;");
        Rule(".aio-cover-banner .aio-cover-band", "margin:0;");
        if (toc.PageBreakAfter)
        {
            Rule(".aio-toc", "break-after:page;page-break-after:always;");
        }

        if (t.Links.ShowUrl)
        {
            Rule(d + " a.aio-link:is([href^=\"http:\"],[href^=\"https:\"],[href^=\"mailto:\"])::after", $"content:\" (\" attr(href) \")\";font-size:85%;color:{muted};font-weight:400;");
        }

        Rule(d + " .aio-codeblock pre", "white-space:pre-wrap;overflow-wrap:anywhere;");
        css.Append('}').Append(nl);
        return css.ToString();
    }

    /// <summary>Colours for the highlighter's <c>hl-*</c> classes from the theme palette (Auto follows the code background).</summary>
    private static void AppendHighlightPalette(Action<string, string> rule, PdfTheme t, bool darkCodeBackground)
    {
        if (!t.Code.SyntaxHighlighting)
        {
            return;
        }

        var palette = SyntaxHighlighter.ResolvePalette(t.Code.Palette, darkCodeBackground);
        foreach (var kind in Enum.GetValues<SyntaxTokenKind>())
        {
            var cls = SyntaxTokenKinds.CssClass(kind);
            if (cls is null)
            {
                continue;
            }

            var decl = new StringBuilder();
            if (SyntaxHighlighter.ColorFor(kind, palette, darkCodeBackground) is { } color)
            {
                decl.Append("color:").Append(Color(color, "#000000")).Append(';');
            }

            var bold = palette == SyntaxPalette.Mono && kind is SyntaxTokenKind.Keyword or SyntaxTokenKind.Inserted;
            var italic = kind == SyntaxTokenKind.Comment || (palette == SyntaxPalette.Mono && kind == SyntaxTokenKind.Deleted);
            if (bold)
            {
                decl.Append("font-weight:700;");
            }

            if (italic)
            {
                decl.Append("font-style:italic;");
            }

            if (decl.Length > 0)
            {
                rule(".aio-doc .hl-" + cls, decl.ToString());
            }
        }
    }

    private static double MmToPt(double mm) => mm * 72.0 / 25.4;

    /// <summary>CSS <c>text-align</c> keyword for a theme alignment; headings never justify.</summary>
    internal static string CssAlign(TextAlign align, bool headings = false) => align switch
    {
        TextAlign.Justify => headings ? "left" : "justify",
        TextAlign.Center => "center",
        TextAlign.Right => "right",
        _ => "left",
    };

    private static string Bullet(BulletStyle style) => style switch
    {
        BulletStyle.Circle => "\\25E6",
        BulletStyle.Square => "\\25AA",
        BulletStyle.Dash => "\\2013",
        BulletStyle.Arrow => "\\203A",
        _ => "\\2022",
    };

    private static string NumberStyleCss(NumberStyle style) => style switch
    {
        NumberStyle.LowerAlpha => "lower-alpha",
        NumberStyle.UpperAlpha => "upper-alpha",
        NumberStyle.LowerRoman => "lower-roman",
        NumberStyle.UpperRoman => "upper-roman",
        _ => "decimal",
    };
}
