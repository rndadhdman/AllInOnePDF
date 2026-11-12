using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Fonts;
using AllInOnePdf.Core.Highlighting;
using AllInOnePdf.Core.Rendering;
using AllInOnePdf.Core.Themes;
using ThemeFontWeight = AllInOnePdf.Core.Themes.FontWeight;

namespace AllInOnePdf.UI.Services;

/// <summary>
/// Turns a <see cref="PdfTheme"/> into a CSS block for the theme builder's fast preview.
/// Every rule is scoped under one wrapper class so several previews can coexist and the app's reader baseline
/// (<c>.aio-doc</c> rules in app.css) is overridden. Values mirror <c>PdfRenderer</c> as closely as CSS allows.
/// All theme values are validated before they reach CSS (themes are imported from untrusted JSON).
/// </summary>
public static partial class ThemeBuilderCssGenerator
{
    public static string Generate(PdfTheme t, string scopeClass)
    {
        var s = "." + SafeIdent(scopeClass);
        var d = s + " .aio-doc";
        var css = new StringBuilder(8192);

        var (wMm, hMm) = t.Page.GetSizeMm();
        var pageBg = Color(t.Page.Background, "#FFFFFF");
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
        var contentHeightPt = MmToPt(hMm - t.Page.MarginTopMm - t.Page.MarginBottomMm);

        void Rule(string selector, string declarations) => css.Append(selector).Append('{').Append(declarations).Append("}\n");

        // ---- page sheets
        Rule(s + " .tb-sheet", $"position:relative;box-sizing:border-box;width:{N(wMm)}mm;min-height:{N(hMm)}mm;padding:{N(t.Page.MarginTopMm)}mm {N(t.Page.MarginRightMm)}mm {N(t.Page.MarginBottomMm)}mm {N(t.Page.MarginLeftMm)}mm;" +
            $"background:{pageBg};color:{text};font-family:{bodyFont};font-size:{N(body)}pt;line-height:{N(t.Typography.LineHeight)};letter-spacing:{N(t.Typography.LetterSpacingEm)}em;" +
            "display:flex;flex-direction:column;text-align:left;overflow-wrap:anywhere;font-kerning:normal;");
        Rule(s + " .tb-body", "flex:1 0 auto;display:flex;flex-direction:column;min-width:0;");
        Rule(s + " .tb-cover-sheet", $"height:{N(hMm)}mm;overflow:hidden;background:{Color(t.Cover.Background, t.Page.Background)};");

        // ---- document baseline reset (beats app.css .aio-doc rules by specificity)
        Rule(d, "max-width:none;margin:0;padding:0;font:inherit;line-height:inherit;color:inherit;letter-spacing:inherit;display:flex;flex-direction:column;min-width:0;counter-reset:tbh1 tbh2 tbh3 tbh4 tbh5 tbh6;");
        Rule(d + " > *", "min-width:0;max-width:100%;");
        Rule(d + " strong", "font-weight:700;");
        Rule(d + " .aio-smallcaps", "text-transform:uppercase;");
        Rule(d + " mark", $"background:{(ThemeBuilderColor.IsDark(t.Page.Background) ? "#5C4B00" : "#FFF3A0")};color:inherit;padding:0;");

        // ---- paragraphs
        var align = CssAlign(t.Typography.Alignment);

        // Explicit text rules for every text container. Fluent's reboot.css styles `p` (font, size, line height)
        // directly, which beats inheritance from the sheet; these selectors (0,2,1 and up) win over that `p { … }`.
        string Sel(params string[] parts) => string.Join(',', parts.Select(p => d + " " + p));
        Rule(Sel("p", ".aio-p", "li", "blockquote", "td", "th", "figcaption"),
            $"font-family:{bodyFont};font-size:{N(body)}pt;line-height:{N(t.Typography.LineHeight)};letter-spacing:{N(t.Typography.LetterSpacingEm)}em;" +
            $"color:{text};text-align:{align};text-indent:0;");

        // Containers that scale or recolour their text (quotes, callouts, footnotes, tables) pass it down to nested text.
        Rule(Sel(":is(.aio-quote,.aio-callout,.aio-footnotes) p", ":is(.aio-quote,.aio-callout,.aio-footnotes) .aio-p", ":is(.aio-quote,.aio-callout,.aio-footnotes) li"),
            "font-size:inherit;line-height:inherit;color:inherit;");
        Rule(Sel(".aio-table td", ".aio-table th"), "font-size:inherit;line-height:inherit;");

        var hyphens = t.Typography.Hyphenate ? "auto" : "manual";
        Rule(d + " .aio-p", $"margin:0 0 {N(sp)}pt;text-align:{align};text-indent:0;-webkit-hyphens:{hyphens};hyphens:{hyphens};");
        Rule(Sel("> p", "> .aio-p"), $"text-indent:{N(t.Typography.FirstLineIndentPt)}pt;");
        Rule(d + " :is(li,blockquote,aside,td,th,.aio-footnotes) .aio-p", $"margin-bottom:{N(Math.Min(sp, 4))}pt;text-indent:0;");
        Rule(d + " .aio-rawhtml", $"font-style:italic;color:{muted};");

        // ---- headings
        Rule(d + " .aio-h", $"font-family:{headingFont};line-height:{N(t.Headings.LineHeight)};color:{Color(t.Headings.Color, "#111827")};margin:0;padding:0;border:0;letter-spacing:0;text-transform:none;font-style:normal;");
        for (var level = 1; level <= 6; level++)
        {
            var h = t.Headings.Level(level);
            var decl = new StringBuilder();
            decl.Append(CultureInfo.InvariantCulture, $"font-size:{N(h.SizePt)}pt;font-weight:{Weight(h.Weight)};font-style:{(h.Italic ? "italic" : "normal")};");
            decl.Append(CultureInfo.InvariantCulture, $"text-transform:{(h.Uppercase ? "uppercase" : "none")};letter-spacing:{N(h.LetterSpacingEm)}em;");
            decl.Append(CultureInfo.InvariantCulture, $"margin-top:{N(h.SpaceBeforePt)}pt;margin-bottom:{N(h.SpaceAfterPt)}pt;text-align:{CssAlign(h.Alignment, headings: true)};");
            if (!string.IsNullOrWhiteSpace(h.Color))
            {
                decl.Append(CultureInfo.InvariantCulture, $"color:{Color(h.Color, t.Headings.Color)};");
            }

            if (h.Underline)
            {
                decl.Append(CultureInfo.InvariantCulture, $"border-bottom:{N(Math.Max(0.25, h.RuleWidthPt))}pt solid {Color(h.RuleColor, t.Colors.Accent)};padding-bottom:3pt;");
            }

            decl.Append(CultureInfo.InvariantCulture, $"counter-increment:tbh{level};");
            if (level < 6)
            {
                decl.Append("counter-reset:").Append(string.Join(' ', Enumerable.Range(level + 1, 6 - level).Select(l => "tbh" + l.ToString(CultureInfo.InvariantCulture)))).Append(';');
            }

            Rule(d + " .aio-h" + level.ToString(CultureInfo.InvariantCulture), decl.ToString());

            if (t.Headings.NumberHeadings)
            {
                var counters = string.Join(" \".\" ", Enumerable.Range(1, level).Select(l => $"counter(tbh{l.ToString(CultureInfo.InvariantCulture)})"));
                Rule(d + " .aio-h" + level.ToString(CultureInfo.InvariantCulture) + "::before", $"content:{counters} \"\\00a0\\00a0\";color:{accent};");
            }
        }

        // ---- links
        var linkColor = Color(t.Links.Color, t.Colors.Accent);
        Rule(d + " a.aio-link", $"color:{linkColor};text-decoration:{(t.Links.Underline ? "underline" : "none")};text-decoration-thickness:auto;text-underline-offset:auto;font-weight:{Weight(t.Links.Weight)};cursor:default;");
        if (t.Links.ShowUrl)
        {
            Rule(d + " a.aio-link:is([href^=\"http:\"],[href^=\"https:\"],[href^=\"mailto:\"])::after", $"content:\" (\" attr(href) \")\";font-size:{N(body * 0.85)}pt;color:{muted};font-weight:400;");
        }

        Rule(d + " .aio-fnref a", $"color:{accent};text-decoration:none;");

        // ---- inline code and math
        var c = t.Code;
        var codeBg = Color(c.Background, t.Colors.Surface);
        var codeFg = Color(c.TextColor, t.Colors.Text);
        var inlineBg = Color(c.InlineBackground ?? c.Background, t.Colors.Surface);
        var inlineFg = c.InlineTextColor is not null || c.TextColor is not null ? Color(c.InlineTextColor ?? c.TextColor, t.Colors.Text) : "inherit";
        Rule(d + " .aio-code", $"font-family:{mono};font-size:{N(body * c.InlineScale)}pt;background:{inlineBg};color:{inlineFg};padding:0 .12em;border-radius:0;letter-spacing:0;");
        Rule(d + " .aio-math-inline", $"font-family:{mono};font-size:{N(body * 0.95)}pt;background:none;padding:0;");
        Rule(d + " .aio-math", $"margin:0 0 {N(sp)}pt;padding:0;background:none;text-align:center;font-family:{mono};font-size:{N(c.SizePt)}pt;white-space:pre-wrap;");
        Rule(d + " .aio-math code", "font:inherit;background:none;padding:0;");

        // ---- code blocks
        Rule(d + " .aio-codeblock", $"margin:0 0 {N(sp)}pt;");
        var codeBorder = c.BorderWidthPt > 0 ? $"{N(c.BorderWidthPt)}pt solid {Color(c.BorderColor, t.Colors.Border)}" : "none";
        Rule(d + " .aio-codeblock pre", $"margin:0;background:{codeBg};color:{codeFg};border:{codeBorder};border-radius:{N(c.CornerRadiusPt)}pt;padding:{N(c.PaddingPt)}pt;" +
            $"font-family:{mono};font-size:{N(c.SizePt)}pt;line-height:{N(c.LineHeight)};letter-spacing:0;tab-size:4;font-variant-ligatures:none;" +
            (c.WrapLongLines ? "white-space:pre-wrap;overflow-wrap:anywhere;overflow:hidden;" : "white-space:pre;overflow-wrap:normal;overflow:hidden;text-overflow:ellipsis;"));
        Rule(d + " .aio-codeblock pre code", "font:inherit;color:inherit;background:none;padding:0;border-radius:0;");
        var labelDecl = $"display:block;font-family:{mono};font-size:{N(c.SizePt * 0.9)}pt;color:{muted};margin:0 0 2pt;opacity:1;line-height:1.3;";
        Rule(d + " .aio-codeblock figcaption", c.ShowLanguageLabel ? labelDecl : "display:none;");
        if (c.ShowLanguageLabel)
        {
            Rule(d + " .aio-codeblock[data-lang]:not(:has(figcaption))::before", "content:attr(data-lang);" + labelDecl);
        }

        if (c.ShowLineNumbers)
        {
            Rule(d + " .aio-codeblock pre code", "counter-reset:tbln;");
            Rule(d + " .tb-ln", $"display:block;padding-left:calc(2.2ch + 8pt);text-indent:0;{(c.WrapLongLines ? string.Empty : "overflow:hidden;text-overflow:ellipsis;")}");
            Rule(d + " .tb-ln::before", $"counter-increment:tbln;content:counter(tbln);display:inline-block;width:2.2ch;margin-left:calc(-2.2ch - 8pt);margin-right:8pt;text-align:left;color:{muted};");
        }

        AppendHighlightPalette(Rule, d, t, ThemeBuilderColor.IsDark(c.Background ?? t.Colors.Surface));

        // ---- quotes
        var q = t.Quotes;
        var quoteBorder = q.BorderWidthPt > 0 ? $"{N(q.BorderWidthPt)}pt solid {Color(q.BorderColor, t.Colors.Accent)}" : "none";
        Rule(d + " .aio-quote", $"margin:0 0 {N(sp)}pt;border:0;border-left:{quoteBorder};border-radius:0;background:{(string.IsNullOrWhiteSpace(q.Background) ? "transparent" : Color(q.Background, "#F3F4F6"))};" +
            $"padding:{N(q.PaddingPt * 0.5)}pt {N(q.PaddingPt * 0.5)}pt {N(q.PaddingPt * 0.5)}pt {N(q.PaddingPt)}pt;font-style:{(q.Italic ? "italic" : "normal")};font-size:{N(body * q.FontScale)}pt;color:{Color(q.TextColor, t.Colors.Text)};");
        Rule(d + " .aio-quote .aio-attribution", $"font-style:normal;font-size:{N(body * 0.9)}pt;color:{muted};opacity:1;");
        Rule(d + " .aio-quote .aio-attribution::before", "content:\"\\2014\\00a0\";");
        // A last line starting with an em dash or "--" (split out by ThemeBuilderPreview.PrepareDocument).
        Rule(d + " .aio-quote .aio-p.tb-attribution", $"margin:2pt 0 0;text-align:right;text-indent:0;font-style:normal;font-size:{N(body * 0.9)}pt;color:{muted};");

        // ---- callouts (mirrors PdfRenderer.ComposeCallout)
        var co = t.Callouts;
        var labelSize = body * 0.95;
        var iconSize = labelSize * 1.15;
        Rule(d + " .aio-callout", $"margin:0 0 {N(sp)}pt;border:0;border-radius:{N(co.CornerRadiusPt)}pt;padding:{N(co.PaddingPt)}pt;overflow:hidden;");
        foreach (var kind in Enum.GetValues<CalloutKind>())
        {
            var style = co.Kind(kind);
            var kindAccent = Color(style.Accent, t.Colors.Accent);
            var kindBg = Color(style.Background ?? co.Background, t.Colors.Surface);
            var bar = co.BorderWidthPt > 0 ? $"border-left:{N(co.BorderWidthPt)}pt solid {kindAccent};" : "border-left:0;";
            var cls = d + " .aio-callout-" + kind.ToString().ToLowerInvariant();
            Rule(cls, $"background:{kindBg};{bar}--tb-callout:{kindAccent};");
            if (co.ShowIcon)
            {
                Rule(cls + " > .aio-callout-title::before", $"background-image:url(\"{CalloutIconUri(kind, ThemeBuilderColor.ToRgb6(style.Accent ?? t.Colors.Accent, "#0F6CBD"))}\");");
            }
        }

        Rule(d + " .aio-callout-title", co.ShowLabel || co.ShowIcon
            ? $"display:flex;align-items:center;gap:6pt;font-family:inherit;font-weight:600;font-size:{N(co.ShowLabel ? labelSize : 0)}pt;line-height:1.2;text-transform:none;letter-spacing:normal;color:var(--tb-callout);margin:0;padding:0 0 4pt;"
            : "display:none;");
        if (co.ShowIcon)
        {
            Rule(d + " .aio-callout-title::before", $"content:\"\";flex:0 0 auto;width:{N(iconSize)}pt;height:{N(iconSize)}pt;background-position:center;background-size:contain;background-repeat:no-repeat;");
        }

        // ---- lists
        var l = t.Lists;
        var marker = Color(l.MarkerColor, t.Colors.Accent);
        Rule(d + " .aio-list", $"margin:0 0 {N(sp)}pt;padding:0 0 0 {N(l.IndentPt)}pt;");
        Rule(d + " .aio-list .aio-list", "margin-bottom:2pt;");
        Rule(d + " .aio-list > li", "margin:0;position:relative;opacity:1;text-decoration:none;");
        Rule(d + " .aio-list > li + li", $"margin-top:{N(l.ItemSpacingPt)}pt;");
        Rule(d + " ul.aio-list", "list-style:none;");
        Rule(d + " ul.aio-list > li::before", $"content:\"{Bullet(l.Bullet)}\";position:absolute;left:-{N(l.IndentPt)}pt;color:{marker};font-style:normal;font-weight:400;");
        Rule(d + " ol.aio-list", $"list-style-type:{NumberStyleCss(l.Numbering)};");
        Rule(d + " ol.aio-list > li::marker", $"color:{marker};");
        if (l.Numbering == NumberStyle.DecimalParen)
        {
            Rule(d + " ol.aio-list > li::marker", "content:counter(list-item) \")\\00a0\";");
        }

        Rule(d + " .aio-tasklist", $"padding-left:{N(l.IndentPt)}pt;");
        if (l.TaskCheckboxes)
        {
            Rule(d + " .aio-list > li.aio-task::before", "content:none;");
            Rule(d + " .aio-task > input", $"appearance:none;-webkit-appearance:none;position:absolute;left:-{N(l.IndentPt)}pt;top:{N(body * 0.22)}pt;width:{N(body * 0.85)}pt;height:{N(body * 0.85)}pt;margin:0;border:.9pt solid {marker};border-radius:0;background:transparent;");
            Rule(d + " .aio-task > input:checked", $"background:{marker};");
        }
        else
        {
            Rule(d + " .aio-task > input", "display:none;");
        }

        // ---- tables
        var tb = t.Tables;
        var tBorder = Color(tb.BorderColor, t.Colors.Border);
        var bw = tb.BorderWidthPt;
        string? headerBg = tb.HeaderBackground is not null ? Color(tb.HeaderBackground, "#F3F4F6")
            : tb.Style is TableStyle.Grid or TableStyle.Striped ? surface : null;
        Rule(d + " .aio-tablewrap", $"margin:0 0 {N(sp)}pt;overflow:visible;");
        Rule(d + " .aio-table", $"width:100%;border-collapse:collapse;table-layout:fixed;font-family:inherit;font-size:{N(body * tb.FontScale)}pt;line-height:1.3;");
        Rule(d + " .aio-table caption", tb.ShowCaptions
            ? $"caption-side:top;text-align:left;font-style:italic;font-size:{N(body * 0.9)}pt;color:{muted};padding:0 0 4pt;"
            : "display:none;");
        Rule(d + " .aio-table :is(th,td)", $"padding:{N(tb.CellPaddingPt)}pt;border:0;vertical-align:top;text-align:left;background:transparent;");
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

        // ---- images
        var im = t.Images;
        var imgAlign = im.Align switch { ImageAlign.Left => "left", ImageAlign.Right => "right", _ => "center" };
        Rule(d + " .aio-figure", $"margin:0 0 {N(im.SpacingPt)}pt;text-align:{imgAlign};");
        Rule(d + " .aio-figure img", $"width:{N(Math.Clamp(im.MaxWidthPercent, 5, 100))}%;max-width:100%;max-height:{N(contentHeightPt * 0.9)}pt;height:auto;object-fit:contain;vertical-align:top;" +
            $"border-radius:{N(im.CornerRadiusPt)}pt;border:{(im.Border ? ".75pt solid " + border : "0")};");
        Rule(d + " .aio-figure figcaption", im.ShowCaptions
            ? $"display:block;font-family:inherit;font-size:{N(im.CaptionSizePt)}pt;font-style:italic;color:{muted};opacity:1;margin:0;padding-top:4pt;text-align:{imgAlign};"
            : "display:none;");
        Rule(d + " .aio-image-missing", $"display:inline-block;box-sizing:border-box;width:{N(Math.Clamp(im.MaxWidthPercent, 5, 100))}%;background:{surface};padding:12pt;border:0;border-radius:{N(im.CornerRadiusPt)}pt;font-family:inherit;font-size:inherit;font-style:italic;color:{muted};opacity:1;text-align:center;");
        Rule(d + " .aio-inline-img", $"height:{N(body * 1.2)}pt;width:auto;vertical-align:middle;");

        // ---- rules, breaks, footnotes
        Rule(d + " .aio-hr", $"border:0;border-top:.75pt solid {border};height:0;width:100%;margin:{N(Math.Max(6, sp))}pt 0;");
        Rule(d + " .aio-footnotes", $"margin-top:18pt;padding-top:6pt;border-top:.5pt solid {border};font-size:{N(body * 0.85)}pt;");
        Rule(d + " .aio-footnotes ol", "margin:0;padding-left:18pt;");
        Rule(d + " .aio-footnotes li::marker", $"color:{muted};font-size:.95em;");

        // ---- header and footer strips
        foreach (var (cls, hf, isHeader) in new[] { ("tb-header", t.Header, true), ("tb-footer", t.Footer, false) })
        {
            var hfColor = Color(hf.Color, t.Colors.Muted);
            Rule(s + " ." + cls + "-slot", isHeader ? $"padding-bottom:{N(hf.SpacingPt)}pt;" : $"margin-top:auto;padding-top:{N(hf.SpacingPt)}pt;");
            var rule = hf.Rule ? (isHeader ? $"border-bottom:.5pt solid {border};padding-bottom:5pt;" : $"border-top:.5pt solid {border};padding-top:5pt;") : string.Empty;
            Rule(s + " ." + cls, $"display:grid;grid-template-columns:1fr 1fr 1fr;gap:4pt;font-size:{N(hf.SizePt)}pt;line-height:1.2;color:{hfColor};{rule}");
        }

        Rule(s + " .tb-hf-l", "text-align:left;");
        Rule(s + " .tb-hf-c", "text-align:center;");
        Rule(s + " .tb-hf-r", "text-align:right;");

        // ---- table of contents
        var h1 = t.Headings.H1;
        Rule(s + " .tb-toc", "display:block;");
        Rule(s + " .tb-toc-title", $"font-family:{headingFont};font-size:{N(h1.SizePt * 0.8)}pt;font-weight:{Weight(h1.Weight)};color:{Color(t.Headings.Color, "#111827")};padding-bottom:{N(h1.SpaceAfterPt)}pt;");
        Rule(s + " .tb-toc-list", "list-style:none;margin:0;padding:0;");
        Rule(s + " .tb-toc-entry", $"display:flex;align-items:baseline;gap:6pt;padding:2.5pt 0;font-size:{N(body * 0.94)}pt;color:{text};");
        Rule(s + " .tb-toc-entry.tb-toc-top", $"font-size:{N(body)}pt;font-weight:600;");
        Rule(s + " .tb-toc-label", "flex:1 1 auto;min-width:0;");
        Rule(s + " .tb-toc-entry:has(.tb-toc-dots) .tb-toc-label", "flex:0 1 auto;");
        Rule(s + " .tb-toc-dots", $"flex:1 1 auto;min-width:12pt;align-self:baseline;height:0;border-bottom:.9pt dotted {muted};");
        Rule(s + " .tb-toc-page", $"flex:0 0 34pt;text-align:right;color:{muted};font-weight:400;");
        Rule(s + " .tb-toc-spacer", $"height:{N(h1.SpaceBeforePt)}pt;");

        // ---- cover
        var cv = t.Cover;
        var titleColor = Color(cv.TitleColor, t.Headings.Color);
        Rule(s + " .tb-cover", "flex:1 1 auto;display:flex;flex-direction:column;min-height:0;");
        Rule(s + " .tb-cover-centered", "justify-content:center;text-align:center;");
        Rule(s + " .tb-cover-minimal", "justify-content:flex-start;padding-top:40pt;");
        Rule(s + " .tb-cover-bold", "justify-content:flex-end;padding-bottom:30pt;");
        Rule(s + " .tb-cover-split", "flex-direction:row;");
        Rule(s + " .tb-cover-split .tb-cover-bar", $"flex:0 0 18pt;background:{accent};");
        Rule(s + " .tb-cover-split .tb-cover-block", "flex:1 1 auto;padding-left:28pt;display:flex;flex-direction:column;justify-content:center;");
        Rule(s + " .tb-cover-accent", $"width:64pt;height:5pt;background:{accent};margin-bottom:14pt;");
        Rule(s + " .tb-cover-centered .tb-cover-accent", "margin-left:auto;margin-right:auto;");
        Rule(s + " .tb-cover-title", $"font-family:{headingFont};font-size:{N(cv.TitleSizePt)}pt;font-weight:700;color:{titleColor};line-height:1.1;letter-spacing:0;");
        Rule(s + " .tb-cover-subtitle", $"font-family:{headingFont};font-size:{N(cv.TitleSizePt * 0.45)}pt;color:{muted};line-height:1.25;padding-top:10pt;");
        Rule(s + " .tb-cover-meta", $"font-size:{N(body * 1.05)}pt;color:{muted};padding-top:28pt;");
        Rule(s + " .tb-cover-note", $"font-size:{N(body * 0.9)}pt;font-style:italic;color:{muted};padding-top:8pt;");
        Rule(s + " .tb-cover-banner .tb-cover-band", $"background:{accent};padding:40pt 28pt;");
        Rule(s + " .tb-cover-banner .tb-cover-title", "color:#FFFFFF;");
        Rule(s + " .tb-cover-banner .tb-cover-subtitle", "color:#E6F0FA;");
        Rule(s + " .tb-cover-banner .tb-cover-after", "padding-top:30pt;");
        Rule(s + " .tb-cover-banner .tb-cover-meta", $"font-size:{N(body)}pt;padding-top:0;");
        Rule(s + " .tb-cover-banner .tb-cover-note", $"font-size:{N(body)}pt;padding-top:6pt;");

        // ---- page break markers (fast preview splits sheets at explicit breaks)
        Rule(s + " .tb-break-note", $"font-size:8pt;color:{muted};text-align:center;opacity:.8;margin-bottom:6pt;");

        return css.ToString();
    }

    /// <summary>Quoted CSS font stack with the same fallback the PDF renderer uses (Inter).</summary>
    public static string FontStack(string? family)
    {
        var name = SafeFontName(family);
        if (string.IsNullOrEmpty(name))
        {
            name = BundledFonts.Sans;
        }

        var generic = BundledFonts.Families.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))?.Class switch
        {
            FontClass.Serif => "serif",
            FontClass.Mono => "monospace",
            _ => "sans-serif",
        };
        return string.Equals(name, BundledFonts.Sans, StringComparison.OrdinalIgnoreCase)
            ? $"\"{name}\",{generic}"
            : $"\"{name}\",\"{BundledFonts.Sans}\",{generic}";
    }

    /// <summary>Strips anything that could break out of a quoted CSS string.</summary>
    public static string SafeFontName(string? family) =>
        family is null ? string.Empty : FontNameStrip().Replace(family, string.Empty).Trim();

    public static string Weight(ThemeFontWeight w) => w switch
    {
        ThemeFontWeight.Medium => "500",
        ThemeFontWeight.SemiBold => "600",
        ThemeFontWeight.Bold => "700",
        _ => "400",
    };

    public static string N(double value) => Math.Round(double.IsFinite(value) ? value : 0, 3).ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>CSS <c>text-align</c> keyword for a theme alignment; headings never justify.</summary>
    private static string CssAlign(TextAlign align, bool headings = false) => align switch
    {
        TextAlign.Justify => headings ? "left" : "justify",
        TextAlign.Center => "center",
        TextAlign.Right => "right",
        _ => "left",
    };

    /// <summary>
    /// Colours for the highlighter's <c>hl-*</c> spans (the HTML renderer emits classes, not colours) from the theme's
    /// palette, mirroring <c>PdfRenderer</c>: Auto follows the code background. With highlighting off, the
    /// reader's default colours in app.css are neutralised so the preview shows plain code like the PDF.
    /// </summary>
    private static void AppendHighlightPalette(Action<string, string> rule, string d, PdfTheme t, bool darkCodeBackground)
    {
        if (!t.Code.SyntaxHighlighting)
        {
            rule(d + " .hl", "color:inherit;background:none;font-weight:inherit;font-style:inherit;");
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

            var color = SyntaxHighlighter.ColorFor(kind, palette, darkCodeBackground);
            var bold = palette == SyntaxPalette.Mono && kind is SyntaxTokenKind.Keyword or SyntaxTokenKind.Inserted;
            var italic = kind == SyntaxTokenKind.Comment || (palette == SyntaxPalette.Mono && kind == SyntaxTokenKind.Deleted);
            rule(d + " .hl-" + cls,
                $"color:{(color is null ? "inherit" : Color(color, "#000000"))};background:none;font-weight:{(bold ? "700" : "inherit")};font-style:{(italic ? "italic" : "inherit")};");
        }
    }

    public static double MmToPt(double mm) => mm * 72.0 / 25.4;

    private static string Color(string? hex, string? fallback) => ThemeBuilderColor.ToCss(hex, ThemeBuilderColor.IsValid(fallback) ? fallback! : "#000000");

    /// <summary>
    /// The renderer's callout icons (<c>PdfRenderer.CalloutIcon</c>) as an SVG data URI. <paramref name="accent"/>
    /// is a normalised #RRGGBB value, so nothing user-controlled reaches the CSS unescaped.
    /// </summary>
    private static string CalloutIconUri(CalloutKind kind, string accent)
    {
        var a = ThemeBuilderColor.IsValid(accent) ? accent : "#0F6CBD";
        var f = ThemeBuilderColor.IsDark(a) ? "#FFFFFF" : "#111111";
        var body = kind switch
        {
            CalloutKind.Tip => $"<circle cx='8' cy='8' r='8' fill='{a}'/><path d='M4.4 8.4 L6.9 10.9 L11.6 5.5' stroke='{f}' stroke-width='1.8' fill='none' stroke-linecap='round' stroke-linejoin='round'/>",
            CalloutKind.Important => $"<circle cx='8' cy='8' r='8' fill='{a}'/><rect x='7' y='3.4' width='2' height='6.2' rx='1' fill='{f}'/><circle cx='8' cy='12.2' r='1.2' fill='{f}'/>",
            CalloutKind.Warning => $"<path d='M8 0.8 L15.6 14.8 L0.4 14.8 Z' fill='{a}'/><rect x='7.1' y='5.4' width='1.8' height='5' rx='0.9' fill='{f}'/><circle cx='8' cy='12.6' r='1.05' fill='{f}'/>",
            CalloutKind.Caution => $"<polygon points='4.7,0.3 11.3,0.3 15.7,4.7 15.7,11.3 11.3,15.7 4.7,15.7 0.3,11.3 0.3,4.7' fill='{a}'/><path d='M5.5 5.5 L10.5 10.5 M10.5 5.5 L5.5 10.5' stroke='{f}' stroke-width='1.8' stroke-linecap='round'/>",
            _ => $"<circle cx='8' cy='8' r='8' fill='{a}'/><rect x='7' y='6.8' width='2' height='5.8' rx='1' fill='{f}'/><circle cx='8' cy='4.4' r='1.2' fill='{f}'/>",
        };
        var svg = $"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 16 16'>{body}</svg>";
        return "data:image/svg+xml," + svg.Replace("#", "%23", StringComparison.Ordinal).Replace("<", "%3C", StringComparison.Ordinal).Replace(">", "%3E", StringComparison.Ordinal);
    }

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

    private static string SafeIdent(string ident) => IdentStrip().Replace(ident, string.Empty);

    [GeneratedRegex(@"[^\p{L}\p{N} _\-\.]")]
    private static partial Regex FontNameStrip();

    [GeneratedRegex(@"[^a-zA-Z0-9_\-]")]
    private static partial Regex IdentStrip();
}
