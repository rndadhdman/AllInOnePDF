using UglyToad.PdfPig.Actions;
using UglyToad.PdfPig.Annotations;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using UglyToad.PdfPig.Graphics;
using UglyToad.PdfPig.Tokens;

namespace AllInOnePdf.Core.Importers.Pdf;

/// <summary>
/// Turns one PdfPig page into a <see cref="PageContent"/>: words (with letter-level font info), lines in reading
/// order (PdfPig's Docstrum segmentation + unsupervised reading order when the page has several columns), vector
/// graphics the layout analysis cares about (rules, background boxes, accent bars, checkboxes), images and links.
/// </summary>
internal static class PageExtractor
{
    private static readonly NameToken FilterName = NameToken.Create("Filter");

    public static PageContent Extract(Page page, PdfImportSettings settings)
    {
        var content = new PageContent { Number = page.Number, Width = page.Width, Height = page.Height };

        // Space glyphs stay in: the word extractor uses them as separators (narrow spaces in some faces would
        // otherwise glue words together). Whitespace-only words are dropped afterwards.
        var letters = page.Letters.Where(l => l.TextOrientation == TextOrientation.Horizontal && !string.IsNullOrEmpty(l.Value)).ToList();
        content.HasLetters = letters.Any(l => Clean(l.Value).Length > 0);

        if (letters.Count > 0)
        {
            var map = new Dictionary<Word, PdfWord>(ReferenceEqualityComparer.Instance);
            var pigWords = new List<Word>();
            foreach (var word in NearestNeighbourWordExtractor.Instance.GetWords(letters))
            {
                var converted = Convert(word);
                if (converted is not null)
                {
                    map[word] = converted;
                    pigWords.Add(word);
                }
            }

            var lines = LineAssembler.Assemble(map.Values, page.Number);
            if (!settings.SimpleLayout && LooksMultiColumn(lines, page.Width))
            {
                lines = OrderByColumns(pigWords, map, page) ?? lines;
            }

            content.Lines.AddRange(lines);
        }

        ExtractGraphics(page, content);
        ExtractLinks(page, content);
        ExtractImages(page, content, settings);
        MarkDecorations(content);
        AssignContainers(content);
        return content;
    }

    // -------------------------------------------------------------- decorations

    /// <summary>
    /// Flags glyphs drawn with a thin line through their middle as struck through and glyphs on a filled
    /// rectangle the height of their line (whose edges meet the glyphs, unlike a shaded box or table cell) as
    /// highlighted. The rules and rectangles used this way are no longer table or break candidates.
    /// </summary>
    internal static void MarkDecorations(PageContent content)
    {
        var usedRules = new HashSet<int>();
        var usedFills = new HashSet<int>();
        foreach (var line in content.Lines)
        {
            if (line.IsMono)
            {
                continue;
            }

            var glyphs = line.Words.SelectMany(w => w.Glyphs).ToList();
            foreach (var word in line.Words)
            {
                List<GlyphInfo>? changed = null;
                for (var g = 0; g < word.Glyphs.Count; g++)
                {
                    var glyph = word.Glyphs[g];
                    if ((glyph.Flags & FontFlags.Mono) != 0)
                    {
                        continue;
                    }

                    var center = (glyph.X + glyph.Right) / 2;
                    var flags = glyph.Flags;
                    for (var r = 0; r < content.HorizontalRules.Count; r++)
                    {
                        var rule = content.HorizontalRules[r];
                        var y = rule.Y - word.Baseline;
                        if (center >= rule.Left && center <= rule.Right && y >= word.Size * 0.18 && y <= word.Size * 0.45
                            && rule.Thickness <= Math.Max(1.5, word.Size * 0.15) && rule.Right - rule.Left <= line.Right - line.Left + 2)
                        {
                            flags |= FontFlags.Strike;
                            usedRules.Add(r);
                            break;
                        }
                    }

                    for (var f = 0; f < content.Fills.Count; f++)
                    {
                        var fill = content.Fills[f];
                        if (center >= fill.Left && center <= fill.Right && word.Baseline >= fill.Bottom && word.Baseline <= fill.Top
                            && fill.Height >= word.Size * 0.95 && fill.Height <= word.Size * 2.1
                            && glyphs.Any(o => Math.Abs(o.X - fill.Left) <= 1.5) && glyphs.Any(o => Math.Abs(o.Right - fill.Right) <= 1.5))
                        {
                            flags |= FontFlags.Highlight;
                            usedFills.Add(f);
                            break;
                        }
                    }

                    if (flags != glyph.Flags)
                    {
                        changed ??= [.. word.Glyphs];
                        changed[g] = glyph with { Flags = flags };
                    }
                }

                if (changed is not null)
                {
                    word.Glyphs = changed;
                }
            }
        }

        foreach (var r in usedRules.OrderDescending())
        {
            content.HorizontalRules.RemoveAt(r);
        }

        foreach (var fill in usedFills.Select(f => content.Fills[f]).ToList())
        {
            content.Boxes.RemoveAll(b => Same(b, fill));
        }
    }

    private static bool IsPrivateUse(char ch) => ch is >= '\uE000' and <= '\uF8FF';

    private static PdfWord? Convert(Word word)
    {
        var glyphs = new List<GlyphInfo>(word.Letters.Count);
        double left = double.MaxValue, right = double.MinValue, sizeSum = 0, baseline = 0;
        var boldCount = 0;
        var italicCount = 0;
        var monoCount = 0;
        var sb = new System.Text.StringBuilder(word.Letters.Count);
        foreach (var letter in word.Letters)
        {
            var value = Clean(letter.Value);
            if (value.Length == 0)
            {
                continue;
            }

            var size = letter.PointSize > 0 ? letter.PointSize : letter.FontSize > 0 ? letter.FontSize : Math.Max(1, letter.BoundingBox.Height);
            var flags = FontClassifier.Classify(letter);
            var x0 = Math.Min(letter.StartBaseLine.X, letter.EndBaseLine.X);
            var x1 = Math.Max(letter.StartBaseLine.X, letter.EndBaseLine.X);
            if (x1 - x0 < 0.01)
            {
                x0 = letter.BoundingBox.Left;
                x1 = letter.BoundingBox.Right;
            }

            glyphs.Add(new GlyphInfo(value, flags, x0, x1, size));
            sb.Append(value);
            left = Math.Min(left, x0);
            right = Math.Max(right, x1);
            sizeSum += size;
            baseline += letter.StartBaseLine.Y;
            if ((flags & FontFlags.Bold) != 0)
            {
                boldCount++;
            }

            if ((flags & FontFlags.Italic) != 0)
            {
                italicCount++;
            }

            if ((flags & FontFlags.Mono) != 0)
            {
                monoCount++;
            }
        }

        if (glyphs.Count == 0)
        {
            return null;
        }

        var n = glyphs.Count;
        var avgSize = sizeSum / n;
        var baseY = baseline / n;
        var majority = FontFlags.None;
        if (boldCount * 2 > n)
        {
            majority |= FontFlags.Bold;
        }

        if (italicCount * 2 > n)
        {
            majority |= FontFlags.Italic;
        }

        if (monoCount * 2 > n)
        {
            majority |= FontFlags.Mono;
        }

        return new PdfWord
        {
            Text = sb.ToString(),
            Glyphs = glyphs,
            Left = left,
            Right = right,
            Baseline = baseY,
            Bottom = baseY - avgSize * 0.25,
            Top = baseY + avgSize * 0.75,
            Size = avgSize,
            Flags = majority,
        };
    }

    /// <summary>Drops private-use and control characters (unmapped alternate glyphs some PDF writers emit).</summary>
    private static string Clean(string value)
    {
        // Symbol / Wingdings bullets that Word maps into the private-use area.
        if (value.Length == 1)
        {
            switch (value[0])
            {
                case '\uF0B7':
                case '\uF06C':
                    return "\u2022";
                case '\uF0A7':
                case '\uF06E':
                    return "\u25AA";
                case '\uF0D8':
                    return "\u27A2";
                case '\uF0FC':
                    return "\u2713";
                case '\uF0A8':
                case '\uF06F':
                    return "\u2610";
            }
        }

        var clean = true;
        foreach (var ch in value)
        {
            if (IsPrivateUse(ch) || char.IsControl(ch) || char.IsWhiteSpace(ch))
            {
                clean = false;
                break;
            }
        }

        if (clean)
        {
            return value;
        }

        var sb = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (!IsPrivateUse(ch) && !char.IsControl(ch) && !char.IsWhiteSpace(ch))
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }

    // ------------------------------------------------------------------ columns

    /// <summary>Cheap pre-check before running Docstrum: many lines broken by a wide gap in the middle of the page.</summary>
    private static bool LooksMultiColumn(List<PdfLine> lines, double pageWidth)
    {
        if (lines.Count < 8)
        {
            return false;
        }

        var split = 0;
        foreach (var line in lines)
        {
            for (var i = 1; i < line.Segments.Count; i++)
            {
                var gapLeft = line.Segments[i - 1].Right;
                var gapRight = line.Segments[i].Left;
                if (gapRight > pageWidth * 0.3 && gapLeft < pageWidth * 0.7 && line.Segments[i - 1].Words.Count >= 3 && line.Segments[i].Words.Count >= 3)
                {
                    split++;
                    break;
                }
            }
        }

        return split >= 6 && split >= lines.Count * 0.3;
    }

    /// <summary>
    /// Segments the page with Docstrum and orders blocks with the unsupervised reading-order detector. Returns null
    /// when the blocks do not actually form side-by-side columns (then the plain top-to-bottom order is better).
    /// </summary>
    private static List<PdfLine>? OrderByColumns(List<Word> words, Dictionary<Word, PdfWord> map, Page page)
    {
        try
        {
            var blocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);
            if (blocks.Count < 2)
            {
                return null;
            }

            var sideBySide = false;
            for (var i = 0; i < blocks.Count && !sideBySide; i++)
            {
                for (var j = i + 1; j < blocks.Count; j++)
                {
                    var a = blocks[i].BoundingBox;
                    var b = blocks[j].BoundingBox;
                    if (blocks[i].TextLines.Count < 3 || blocks[j].TextLines.Count < 3 || a.Width < page.Width * 0.2 || b.Width < page.Width * 0.2)
                    {
                        continue;
                    }

                    var horizontalGap = Math.Max(a.Left, b.Left) - Math.Min(a.Right, b.Right);
                    var verticalOverlap = Math.Min(a.Top, b.Top) - Math.Max(a.Bottom, b.Bottom);
                    var lineHeight = Math.Max(a.Height / blocks[i].TextLines.Count, b.Height / blocks[j].TextLines.Count);
                    if (horizontalGap > 5 && verticalOverlap > lineHeight * 3)
                    {
                        sideBySide = true;
                        break;
                    }
                }
            }

            if (!sideBySide)
            {
                return null;
            }

            var ordered = UnsupervisedReadingOrderDetector.Instance.Get(blocks).OrderBy(b => b.ReadingOrder);
            var result = new List<PdfLine>();
            var used = new HashSet<PdfWord>(ReferenceEqualityComparer.Instance);
            foreach (var block in ordered)
            {
                var blockWords = block.TextLines.SelectMany(l => l.Words).Where(map.ContainsKey).Select(w => map[w]).Where(used.Add).ToList();
                result.AddRange(LineAssembler.Assemble(blockWords, page.Number));
            }

            var leftovers = map.Values.Where(w => !used.Contains(w)).ToList();
            if (leftovers.Count > 0)
            {
                result.AddRange(LineAssembler.Assemble(leftovers, page.Number));
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return null;
        }
    }

    // ----------------------------------------------------------------- graphics

    private static void ExtractGraphics(Page page, PageContent content)
    {
        var pageArea = page.Width * page.Height;
        var squares = new List<(Box Bounds, bool Solid, bool Outline)>();
        var marks = new List<Box>();
        foreach (var path in page.Paths)
        {
            if (!path.IsFilled && !path.IsStroked)
            {
                continue;
            }

            var rect = path.GetBoundingRectangle();
            if (rect is null)
            {
                continue;
            }

            var r = rect.Value;
            var box = new Box(r.Left, r.Bottom, r.Right, r.Top);
            var w = box.Width;
            var h = box.Height;
            if (box.Area > pageArea * 0.9)
            {
                continue;
            }

            if (path.IsFilled && w >= 2 && h is >= 3 and <= 40 && !IsWhite(path.FillColor))
            {
                content.Fills.Add(box);
            }

            var small = w is >= 4 and <= 16 && h is >= 4 and <= 16 && Math.Abs(w - h) <= 1.5;
            if (small)
            {
                // Checkbox candidates: a solid fill (checked), or an outline drawn as a stroke or as a filled ring.
                var solid = path.IsFilled && path.Count == 1 && !IsWhite(path.FillColor);
                var outline = path.IsStroked || (path.IsFilled && path.Count >= 2);
                squares.Add((box, solid, outline));
                continue;
            }

            if (w is >= 2 and <= 16 && h is >= 2 and <= 16 && !(w <= 5 && h >= 10) && !(h <= 2.5 && w >= 6) && !(w <= 2.5 && h >= 6))
            {
                // Tick marks and other small glyph-like drawings.
                marks.Add(box);
                continue;
            }

            if (h <= 2.5 && w >= 6)
            {
                content.HorizontalRules.Add(new HorizontalRule(box.Left, box.Right, (box.Bottom + box.Top) / 2, Math.Max(h, path.IsStroked ? path.LineWidth : 0)));
                continue;
            }

            if (path.IsFilled && w is >= 1.5 and <= 5 && h >= 10 && !IsWhite(path.FillColor))
            {
                // Accent bar of a quote or callout (2-4 pt wide). It may also be a thick table rule.
                content.Bars.Add(box);
                content.VerticalRules.Add(new VerticalRule((box.Left + box.Right) / 2, box.Bottom, box.Top));
                continue;
            }

            if (w <= 2.5 && h >= 6)
            {
                content.VerticalRules.Add(new VerticalRule((box.Left + box.Right) / 2, box.Bottom, box.Top));
                continue;
            }

            if (path.IsFilled && w <= 5 && h >= 10)
            {
                content.Bars.Add(box);
                continue;
            }

            if (path.IsFilled && w >= 20 && h >= 8 && !IsWhite(path.FillColor))
            {
                if (!content.Boxes.Any(b => Same(b, box)))
                {
                    content.Boxes.Add(box);
                }
            }

            if (path.IsStroked && w > 2.5 && h > 2.5)
            {
                // A stroked rectangle (table cell, frame) contributes its four edges.
                content.HorizontalRules.Add(new HorizontalRule(box.Left, box.Right, box.Top, path.LineWidth));
                content.HorizontalRules.Add(new HorizontalRule(box.Left, box.Right, box.Bottom, path.LineWidth));
                content.VerticalRules.Add(new VerticalRule(box.Left, box.Bottom, box.Top));
                content.VerticalRules.Add(new VerticalRule(box.Right, box.Bottom, box.Top));
            }
        }

        foreach (var sq in squares)
        {
            if (content.Checkboxes.Any(c => Same(c.Bounds, sq.Bounds)))
            {
                continue;
            }

            var group = squares.Where(o => Same(o.Bounds, sq.Bounds)).ToList();
            var ticked = marks.Any(m => sq.Bounds.Contains((m.Left + m.Right) / 2, (m.Bottom + m.Top) / 2));
            content.Checkboxes.Add(new CheckboxMark { Bounds = sq.Bounds, Checked = ticked || group.Any(o => o.Solid) });
        }
    }

    private static bool Same(Box a, Box b) =>
        Math.Abs(a.Left - b.Left) < 1 && Math.Abs(a.Right - b.Right) < 1 && Math.Abs(a.Top - b.Top) < 1 && Math.Abs(a.Bottom - b.Bottom) < 1;

    private static bool IsWhite(UglyToad.PdfPig.Graphics.Colors.IColor? color)
    {
        if (color is null)
        {
            return false;
        }

        try
        {
            var (r, g, b) = color.ToRGBValues();
            return r > 0.97 && g > 0.97 && b > 0.97;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    // -------------------------------------------------------------------- links

    private static void ExtractLinks(Page page, PageContent content)
    {
        IEnumerable<Annotation> annotations;
        try
        {
            annotations = page.GetAnnotations().ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return;
        }

        var uriLinks = new List<(Box Area, string Url)>();
        foreach (var a in annotations)
        {
            if (a.Type != AnnotationType.Link)
            {
                continue;
            }

            var r = a.Rectangle;
            var area = new Box(Math.Min(r.Left, r.Right), Math.Min(r.Bottom, r.Top), Math.Max(r.Left, r.Right), Math.Max(r.Bottom, r.Top));
            switch (a.Action)
            {
                case UriAction { Uri: { Length: > 0 } uri } when IsSafeUrl(uri):
                    uriLinks.Add((area, uri.Trim()));
                    break;
                case GoToAction:
                    content.InternalLinks.Add(area);
                    break;
            }
        }

        if (uriLinks.Count == 0)
        {
            return;
        }

        foreach (var line in content.Lines)
        {
            foreach (var word in line.Words)
            {
                foreach (var (area, url) in uriLinks)
                {
                    if (area.Contains(word.CenterX, word.Baseline + word.Size * 0.3, 1.5))
                    {
                        word.LinkUrl = url;
                        break;
                    }
                }
            }
        }
    }

    private static bool IsSafeUrl(string url)
    {
        var u = url.Trim();
        return u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || u.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
            || u.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase)
            || u.StartsWith("tel:", StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------- images

    private static void ExtractImages(Page page, PageContent content, PdfImportSettings settings)
    {
        IEnumerable<IPdfImage> images;
        try
        {
            images = page.GetImages().ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return;
        }

        var pageArea = page.Width * page.Height;
        foreach (var image in images)
        {
            content.HasImages = true;
            if (!settings.ExtractImages)
            {
                continue;
            }

            var r = image.BoundingBox;
            var bounds = new Box(Math.Min(r.Left, r.Right), Math.Min(r.Bottom, r.Top), Math.Max(r.Left, r.Right), Math.Max(r.Bottom, r.Top));
            if (image.WidthInSamples < settings.MinImageSizePx || image.HeightInSamples < settings.MinImageSizePx)
            {
                continue;
            }

            if (bounds.Area > pageArea * 0.9)
            {
                continue;
            }

            var encoded = Encode(image);
            if (encoded is null)
            {
                continue;
            }

            content.Images.Add(new PageImage
            {
                Bounds = bounds,
                Data = encoded.Value.Data,
                MediaType = encoded.Value.MediaType,
                PixelWidth = image.WidthInSamples,
                PixelHeight = image.HeightInSamples,
            });
        }
    }

    private static (byte[] Data, string MediaType)? Encode(IPdfImage image)
    {
        try
        {
            var filters = GetFilters(image);
            if (filters.Contains("DCTDecode") || filters.Contains("DCT"))
            {
                return (image.RawMemory.ToArray(), "image/jpeg");
            }

            if (image.TryGetPng(out var png) && png is { Length: > 0 })
            {
                return (png, "image/png");
            }

            if (filters.Contains("JPXDecode"))
            {
                return (image.RawMemory.ToArray(), "image/jp2");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            // Unsupported colour space or filter: skip the image rather than failing the import.
        }

        return null;
    }

    private static HashSet<string> GetFilters(IPdfImage image)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (image.ImageDictionary is null || !image.ImageDictionary.TryGet(FilterName, out var token))
        {
            return result;
        }

        switch (token)
        {
            case NameToken name:
                result.Add(name.Data);
                break;
            case ArrayToken array:
                foreach (var item in array.Data.OfType<NameToken>())
                {
                    result.Add(item.Data);
                }

                break;
        }

        return result;
    }

    // --------------------------------------------------------------- containers

    private static void AssignContainers(PageContent content)
    {
        foreach (var line in content.Lines)
        {
            int? best = null;
            var bestArea = double.MaxValue;
            for (var i = 0; i < content.Boxes.Count; i++)
            {
                var box = content.Boxes[i];
                if (box.Height < (line.Top - line.Bottom) * 1.15)
                {
                    continue;
                }

                if (box.ContainsLine(line) && box.Area < bestArea)
                {
                    best = i;
                    bestArea = box.Area;
                }
            }

            line.BoxId = best;

            var centerY = (line.Top + line.Bottom) / 2;
            for (var i = 0; i < content.Bars.Count; i++)
            {
                var bar = content.Bars[i];
                if (line.Left >= bar.Right - 1 && line.Left <= bar.Right + 30 && centerY >= bar.Bottom - 1 && centerY <= bar.Top + 1)
                {
                    line.BarId = i;
                    break;
                }
            }
        }
    }
}
