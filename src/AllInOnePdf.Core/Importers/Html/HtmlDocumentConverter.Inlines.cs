using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AllInOnePdf.Core.Documents;
using AngleSharp.Dom;
using Document = AllInOnePdf.Core.Documents.Document;

namespace AllInOnePdf.Core.Importers.Html;

internal sealed partial class HtmlDocumentConverter
{
    private readonly Dictionary<ImageInline, (double? WidthPercent, int? PixelWidth, int? PixelHeight)> _imageSizes = new(ReferenceEqualityComparer.Instance);

    private void ConvertInlineChildren(INode parent, List<Inline> target)
    {
        if (++_depth > MaxDepth)
        {
            AppendText(target, parent.TextContent);
            _depth--;
            return;
        }

        foreach (var node in parent.ChildNodes.ToArray())
        {
            switch (node)
            {
                case IText text:
                    AppendText(target, text.Data);
                    break;
                case IElement el when !IsDropped(el):
                    if (RawElements.Contains(el.LocalName))
                    {
                        if (_settings.KeepRawHtml)
                        {
                            target.Add(new HtmlInline { Html = el.OuterHtml });
                        }
                        else if (el.LocalName == "svg" && SvgImage(el) is { } svgImage)
                        {
                            target.Add(svgImage);
                        }
                        else if (el.LocalName is not ("input" or "button" or "select" or "textarea"))
                        {
                            _rawDropped++;
                        }
                    }
                    else if (_settings.KeepRawHtml && !KnownInline.Contains(el.LocalName) && !BlockElements.Contains(el.LocalName))
                    {
                        target.Add(new HtmlInline { Html = el.OuterHtml });
                    }
                    else
                    {
                        ConvertInline(el, target);
                    }

                    break;
            }
        }

        _depth--;
    }

    private void AppendText(List<Inline> target, string data)
    {
        if (data.Length == 0)
        {
            return;
        }

        if (!_settings.PreserveLineBreaks)
        {
            target.Add(new TextInline(CollapseText(data)));
            return;
        }

        var lines = data.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                target.Add(new LineBreakInline());
            }

            if (lines[i].Length > 0)
            {
                target.Add(new TextInline(CollapseText(lines[i])));
            }
        }
    }

    private void ConvertInline(IElement el, List<Inline> target)
    {
        switch (el.LocalName)
        {
            case "br":
                target.Add(new LineBreakInline());
                return;
            case "wbr":
                return;
            case "strong" or "b":
                Styled(el, InlineStyle.Bold, target);
                return;
            case "em" or "i" or "cite" or "dfn" or "var":
                Styled(el, InlineStyle.Italic, target);
                return;
            case "u" or "ins":
                Styled(el, InlineStyle.Underline, target);
                return;
            case "s" or "del" or "strike":
                Styled(el, InlineStyle.Strikethrough, target);
                return;
            case "mark":
                Styled(el, InlineStyle.Highlight, target);
                return;
            case "sup":
                if (FootnoteRefFrom(el) is { } fnref)
                {
                    target.Add(fnref);
                    return;
                }

                Styled(el, InlineStyle.Superscript, target);
                return;
            case "sub":
                Styled(el, InlineStyle.Subscript, target);
                return;
            case "code" or "kbd" or "samp" or "tt":
                if (el.ClassList.Contains("aio-math-inline") || el.ClassList.Contains("math"))
                {
                    target.Add(new MathInline { Latex = el.TextContent.Trim() });
                }
                else
                {
                    var code = el.TextContent.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ');
                    if (code.Length > 0)
                    {
                        target.Add(new CodeInline(code));
                    }
                }

                return;
            case "a":
                ConvertLink(el, target);
                return;
            case "img" or "image":
                if (ImageFrom(el) is { } image)
                {
                    target.Add(image);
                }

                return;
            case "picture":
                if (el.QuerySelector("img") is { } inner && ImageFrom(inner) is { } pic)
                {
                    target.Add(pic);
                }

                return;
            case "math":
                target.Add(new MathInline { Latex = MathText(el) });
                return;
            case "q":
                target.Add(new TextInline("\u201C"));
                ConvertInlineChildren(el, target);
                target.Add(new TextInline("\u201D"));
                return;
            case "rp" or "rt":
                return;
            case "span":
                if (el.ClassList.Contains("katex") || (el.ClassList.Contains("math") && el.ClassList.Contains("inline")))
                {
                    target.Add(new MathInline { Latex = MathText(el) });
                    return;
                }

                break;
        }

        if (IsSingleFootnote(el) || IsFootnoteContainer(el))
        {
            ReadFootnotes(el);
            return;
        }

        var style = StyleFromCss(el);
        if (style != InlineStyle.None)
        {
            Styled(el, style, target);
            return;
        }

        // Unknown inline or a block element nested inside inline content: keep the text, drop the wrapper.
        ConvertInlineChildren(el, target);
    }

    private void Styled(IElement el, InlineStyle style, List<Inline> target)
    {
        style |= StyleFromCss(el);
        var styled = new StyledInline { Style = style };
        ConvertInlineChildren(el, styled.Inlines);
        if (styled.Inlines.Count > 0)
        {
            target.Add(styled);
        }
    }

    /// <summary>Inline CSS that changes meaning (Google Docs and word processors export bold/italic as spans).</summary>
    private static InlineStyle StyleFromCss(IElement el)
    {
        var css = el.GetAttribute("style");
        var style = InlineStyle.None;
        if (el.ClassList.Contains("aio-smallcaps"))
        {
            style |= InlineStyle.SmallCaps;
        }

        if (string.IsNullOrEmpty(css))
        {
            return style;
        }

        foreach (Match m in CssDeclaration().Matches(css))
        {
            var prop = m.Groups[1].Value.ToLowerInvariant();
            var value = m.Groups[2].Value.Trim().ToLowerInvariant();
            switch (prop)
            {
                case "font-weight" when value is "bold" or "bolder" || (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var w) && w >= 600):
                    style |= InlineStyle.Bold;
                    break;
                case "font-style" when value.StartsWith("italic", StringComparison.Ordinal) || value.StartsWith("oblique", StringComparison.Ordinal):
                    style |= InlineStyle.Italic;
                    break;
                case "text-decoration" or "text-decoration-line":
                    if (value.Contains("underline", StringComparison.Ordinal))
                    {
                        style |= InlineStyle.Underline;
                    }

                    if (value.Contains("line-through", StringComparison.Ordinal))
                    {
                        style |= InlineStyle.Strikethrough;
                    }

                    break;
                case "vertical-align" when value == "super":
                    style |= InlineStyle.Superscript;
                    break;
                case "vertical-align" when value == "sub":
                    style |= InlineStyle.Subscript;
                    break;
                case "font-variant" or "font-variant-caps" when value.Contains("small-caps", StringComparison.Ordinal):
                    style |= InlineStyle.SmallCaps;
                    break;
            }
        }

        return style;
    }

    // ------------------------------------------------------------------ links

    private void ConvertLink(IElement el, List<Inline> target)
    {
        if (FootnoteRefFrom(el) is { } fnref)
        {
            target.Add(fnref);
            return;
        }

        var href = el.GetAttribute("href")?.Trim();
        if (string.IsNullOrEmpty(href) || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) || href.StartsWith("vbscript:", StringComparison.OrdinalIgnoreCase))
        {
            ConvertInlineChildren(el, target);
            return;
        }

        if (_rewriteLink is not null)
        {
            href = _rewriteLink(href);
        }

        var link = new LinkInline { Url = href, Title = NullIfEmpty(el.GetAttribute("title")) };
        ConvertInlineChildren(el, link.Inlines);
        if (InlineWhitespace.HasContent(link.Inlines))
        {
            // Empty anchors (permalink icons, "back to top" targets) carry no content and are dropped.
            target.Add(link);
        }
    }

    // ------------------------------------------------------------------ images

    private ImageInline? ImageFrom(IElement el)
    {
        var raw = el.GetAttribute("src") ?? el.GetAttribute("href") ?? el.GetAttribute("xlink:href") ?? el.GetAttribute("http://www.w3.org/1999/xlink", "href");
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = el.GetAttribute("data-src") ?? el.GetAttribute("data-original") ?? FirstSrcSet(el.GetAttribute("srcset"));
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var image = new ImageInline
        {
            Source = ResolveImageSource(raw.Trim()),
            AltText = el.GetAttribute("alt"),
            Title = NullIfEmpty(el.GetAttribute("title")),
        };

        double? widthPercent = null;
        int? pixelWidth = null;
        int? pixelHeight = null;
        var width = el.GetAttribute("width")?.Trim();
        var style = el.GetAttribute("style");
        if (style is not null && CssWidth().Match(style) is { Success: true } m)
        {
            width = m.Groups[1].Value;
        }

        if (width is not null)
        {
            if (width.EndsWith('%') && double.TryParse(width[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pct) && pct > 0)
            {
                widthPercent = Math.Clamp(pct, 1, 100);
            }
            else if (int.TryParse(width.Replace("px", string.Empty, StringComparison.OrdinalIgnoreCase), NumberStyles.Integer, CultureInfo.InvariantCulture, out var px) && px > 0)
            {
                pixelWidth = px;
            }
        }

        if (int.TryParse(el.GetAttribute("height")?.Replace("px", string.Empty, StringComparison.OrdinalIgnoreCase), NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) && h > 0)
        {
            pixelHeight = h;
        }

        if (widthPercent is not null || pixelWidth is not null || pixelHeight is not null)
        {
            _imageSizes[image] = (widthPercent, pixelWidth, pixelHeight);
        }

        return image;
    }

    private ImageBlock ImageBlockFrom(ImageInline img)
    {
        var block = new ImageBlock { Source = img.Source, AltText = img.AltText, Title = img.Title };
        if (_imageSizes.TryGetValue(img, out var size))
        {
            block.WidthPercent = size.WidthPercent;
            block.PixelWidth = size.PixelWidth;
            block.PixelHeight = size.PixelHeight;
        }

        return block;
    }

    /// <summary>EPUB covers often wrap the picture in <c>&lt;svg&gt;&lt;image xlink:href&gt;</c>.</summary>
    private ImageInline? SvgImage(IElement svg)
    {
        var images = svg.QuerySelectorAll("image").ToList();
        return images.Count == 1 ? ImageFrom(images[0]) : null;
    }

    private static string? FirstSrcSet(string? srcset)
    {
        if (string.IsNullOrWhiteSpace(srcset))
        {
            return null;
        }

        var first = srcset.Split(',')[0].Trim();
        var space = first.IndexOf(' ', StringComparison.Ordinal);
        return space > 0 ? first[..space] : first;
    }

    private string ResolveImageSource(string src)
    {
        if (_imageCache.TryGetValue(src, out var cached))
        {
            return cached;
        }

        var resolved = ResolveImageSourceCore(src);
        _imageCache[src] = resolved;
        return resolved;
    }

    private string ResolveImageSourceCore(string src)
    {
        if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return EmbedDataUrl(src) ?? src;
        }

        if (src.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || src.StartsWith("https:", StringComparison.OrdinalIgnoreCase) || src.StartsWith("//", StringComparison.Ordinal))
        {
            _remoteImages.Add(src);
            return src;
        }

        if (src.StartsWith(Document.ResourceScheme, StringComparison.Ordinal) || HasNonFileScheme(src) || !_settings.EmbedImages || _resolver is null)
        {
            return src;
        }

        ImageLookup lookup;
        try
        {
            lookup = _resolver(src, _settings.MaxImageBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidDataException)
        {
            lookup = ImageLookup.NotFound;
        }

        switch (lookup.Status)
        {
            case ImageLookupStatus.Found when lookup.Data is not null && lookup.MediaType is not null:
                {
                    var key = lookup.Key ?? src;
                    if (_imageCache.TryGetValue("\0key:" + key, out var existing))
                    {
                        return existing;
                    }

                    var resource = _doc.AddResource(lookup.MediaType, lookup.Data, lookup.FileName);
                    _imageCache["\0key:" + key] = resource.Reference;
                    return resource.Reference;
                }

            case ImageLookupStatus.TooLarge:
                _oversizedImages.Add(src);
                return src;
            default:
                _missingImages.Add(src);
                return src;
        }
    }

    private string? EmbedDataUrl(string src)
    {
        var comma = src.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0)
        {
            return null;
        }

        var header = src[5..comma];
        var payload = src[(comma + 1)..];
        var parts = header.Split(';', StringSplitOptions.TrimEntries);
        var mediaType = parts[0].ToLowerInvariant();
        if (!mediaType.StartsWith("image/", StringComparison.Ordinal))
        {
            return null;
        }

        byte[] data;
        try
        {
            data = parts.Any(p => string.Equals(p, "base64", StringComparison.OrdinalIgnoreCase))
                ? System.Convert.FromBase64String(Uri.UnescapeDataString(payload).Trim())
                : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
        }
        catch (FormatException)
        {
            return null;
        }

        if (data.Length == 0)
        {
            return null;
        }

        if (data.LongLength > _settings.MaxImageBytes)
        {
            _oversizedImages.Add("data:" + mediaType);
            return null;
        }

        var sniffed = ImageMediaTypes.Detect(data, null) ?? mediaType;
        return _doc.AddResource(sniffed, data).Reference;
    }

    private static bool HasNonFileScheme(string src)
    {
        var colon = src.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 1)
        {
            // No scheme, or a Windows drive letter ("C:\...").
            return false;
        }

        var scheme = src[..colon];
        return !string.Equals(scheme, "file", StringComparison.OrdinalIgnoreCase) && scheme.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.');
    }

    // ------------------------------------------------------------------ math

    private static string MathText(IElement el)
    {
        var annotation = el.QuerySelectorAll("annotation").FirstOrDefault(a => (a.GetAttribute("encoding") ?? string.Empty).Contains("tex", StringComparison.OrdinalIgnoreCase));
        var text = (annotation?.TextContent ?? el.TextContent).Trim();
        if (text.StartsWith("\\(", StringComparison.Ordinal) && text.EndsWith("\\)", StringComparison.Ordinal) && text.Length >= 4)
        {
            text = text[2..^2].Trim();
        }
        else if (text.StartsWith("\\[", StringComparison.Ordinal) && text.EndsWith("\\]", StringComparison.Ordinal) && text.Length >= 4)
        {
            text = text[2..^2].Trim();
        }

        return text;
    }

    [GeneratedRegex(@"([a-zA-Z-]+)\s*:\s*([^;]+)")]
    private static partial Regex CssDeclaration();

    [GeneratedRegex(@"(?:^|;)\s*width\s*:\s*([0-9.]+(?:%|px)?)", RegexOptions.IgnoreCase)]
    private static partial Regex CssWidth();
}
