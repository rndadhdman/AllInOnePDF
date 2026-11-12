using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Exporters.Docx;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using Document = AllInOnePdf.Core.Documents.Document;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using M = DocumentFormat.OpenXml.Math;
using V = DocumentFormat.OpenXml.Vml;

namespace AllInOnePdf.Core.Importers.Docx;

/// <summary>Marks a <c>w:br w:type="page"</c> inside a paragraph; the block reader splits the paragraph there.</summary>
internal sealed class PageBreakMarker : Inline;

/// <summary>An image from a <c>w:drawing</c> (or VML picture) with its displayed size; the block reader decides inline vs block.</summary>
internal sealed class DrawingInline : Inline
{
    public required string Source { get; init; }
    public string? AltText { get; init; }
    public string? Title { get; init; }
    public double WidthPt { get; init; }
    public double HeightPt { get; init; }
}

/// <summary>What one paragraph's runs produced.</summary>
internal sealed class InlineResult
{
    public List<Inline> Inlines { get; } = [];
    public List<string> Bookmarks { get; } = [];
    public int TextRuns { get; set; }
    public int MonoRuns { get; set; }
    /// <summary>Text of runs styled <c>CalloutLabel</c> (the exporter's callout title line).</summary>
    public string? CalloutLabel { get; set; }
    public bool HasOnlyMath { get; set; }

    public bool AllMonospace => TextRuns > 0 && MonoRuns == TextRuns;
}

/// <summary>
/// Runs → inlines: direct and character-style formatting flags, inline code (code character styles and monospace
/// fonts), line/page breaks, tabs, symbols, hyperlinks (relationship and HYPERLINK fields), footnote/endnote
/// references, images and Office Math (as text). Complex fields are tracked across paragraphs so TOC results are skipped.
/// </summary>
internal sealed partial class DocxInlineReader
{
    private readonly DocxStyleResolver _styles;
    private readonly DocxImportSettings _settings;
    private readonly DocxImageStore _images;
    private readonly Func<bool, int, string?> _registerNote;
    private readonly List<FieldFrame> _fields = [];

    private OpenXmlPart _owner = null!;
    private string? _paragraphStyle;
    private InlineResult _result = null!;

    public DocxInlineReader(DocxStyleResolver styles, DocxImportSettings settings, DocxImageStore images, Func<bool, int, string?> registerNote)
    {
        _styles = styles;
        _settings = settings;
        _images = images;
        _registerNote = registerNote;
    }

    private sealed class FieldFrame
    {
        public StringBuilder Instruction { get; } = new();
        public bool InResult { get; set; }
        public bool Skip { get; set; }
        public LinkInline? Link { get; set; }
    }

    /// <summary>True while the content belongs to the result of a skipped field (a table of contents).</summary>
    public bool Skipping => _fields.Any(f => f.Skip && f.InResult);

    public InlineResult Read(Paragraph paragraph, OpenXmlPart owner, string? paragraphStyle)
    {
        _owner = owner;
        _paragraphStyle = paragraphStyle;
        _result = new InlineResult();

        // Hyperlink fields do not survive a paragraph boundary; their remaining result is plain text.
        foreach (var f in _fields)
        {
            f.Link = null;
        }

        var mathOnly = true;
        var any = false;
        foreach (var child in paragraph.ChildElements)
        {
            if (child is ParagraphProperties)
            {
                continue;
            }

            any = true;
            if (child is not M.OfficeMath and not M.Paragraph and not BookmarkStart and not BookmarkEnd)
            {
                if (child is not Run r || !string.IsNullOrWhiteSpace(r.InnerText) || r.Descendants<Drawing>().Any())
                {
                    mathOnly = false;
                }
            }

            ReadElement(child, InlineStyle.None, false, _result.Inlines);
        }

        _result.HasOnlyMath = any && mathOnly && _result.Inlines.Count > 0 && _result.Inlines.All(i => i is MathInline);
        return _result;
    }

    private List<Inline> Target(List<Inline> fallback)
    {
        for (var i = _fields.Count - 1; i >= 0; i--)
        {
            if (_fields[i].Link is { } link && _fields[i].InResult)
            {
                return link.Inlines;
            }
        }

        return fallback;
    }

    private void ReadElement(OpenXmlElement element, InlineStyle inherited, bool inLink, List<Inline> output)
    {
        switch (element)
        {
            case Run run:
                ReadRun(run, inherited, inLink, output);
                break;
            case Hyperlink hyperlink:
                ReadHyperlink(hyperlink, inherited, output);
                break;
            case SimpleField field:
                ReadSimpleField(field, inherited, inLink, output);
                break;
            case BookmarkStart bookmark when bookmark.Name?.Value is { } name:
                _result.Bookmarks.Add(name);
                break;
            case M.OfficeMath math:
                if (!Skipping)
                {
                    Target(output).Add(new MathInline { Latex = MathText(math) });
                }

                break;
            case M.Paragraph mathPara:
                foreach (var math in mathPara.Elements<M.OfficeMath>())
                {
                    ReadElement(math, inherited, inLink, output);
                }

                break;
            case DeletedRun or MoveFromRun or ParagraphProperties or ProofError or PermStart or PermEnd:
                break;
            case SdtRun sdt:
                if (sdt.SdtContentRun is { } content)
                {
                    foreach (var child in content.ChildElements)
                    {
                        ReadElement(child, inherited, inLink, output);
                    }
                }

                break;
            default:
                // InsertedRun, MoveToRun, SmartTagRun, CustomXmlRun, ... : read through.
                if (element.HasChildren && element is not OpenXmlLeafElement)
                {
                    foreach (var child in element.ChildElements)
                    {
                        ReadElement(child, inherited, inLink, output);
                    }
                }

                break;
        }
    }

    private static string MathText(OpenXmlElement math)
    {
        var sb = new StringBuilder();
        foreach (var t in math.Descendants<M.Text>())
        {
            sb.Append(t.Text);
        }

        return sb.ToString();
    }

    private void ReadHyperlink(Hyperlink hyperlink, InlineStyle inherited, List<Inline> output)
    {
        string? url = null;
        if (hyperlink.Id?.Value is { } relId)
        {
            url = _owner.HyperlinkRelationships.FirstOrDefault(r => r.Id == relId)?.Uri.OriginalString;
        }

        if (hyperlink.Anchor?.Value is { Length: > 0 } anchor)
        {
            url = url is null ? "#" + anchor : url + "#" + anchor;
        }
        else if (hyperlink.DocLocation?.Value is { Length: > 0 } location && url is not null)
        {
            url += "#" + location;
        }

        if (url is null || Skipping)
        {
            foreach (var child in hyperlink.ChildElements)
            {
                ReadElement(child, inherited, false, output);
            }

            return;
        }

        var link = new LinkInline { Url = url, Title = string.IsNullOrWhiteSpace(hyperlink.Tooltip?.Value) ? null : hyperlink.Tooltip!.Value };
        foreach (var child in hyperlink.ChildElements)
        {
            ReadElement(child, inherited, true, link.Inlines);
        }

        if (link.Inlines.Count > 0)
        {
            Target(output).Add(link);
        }
    }

    private void ReadSimpleField(SimpleField field, InlineStyle inherited, bool inLink, List<Inline> output)
    {
        var instruction = field.Instruction?.Value ?? string.Empty;
        var (kind, target) = ParseField(instruction);
        if (kind == "TOC" && _settings.SkipToc)
        {
            return;
        }

        if (kind == "HYPERLINK" && target is not null && !inLink && !Skipping)
        {
            var link = new LinkInline { Url = target };
            foreach (var child in field.ChildElements)
            {
                ReadElement(child, inherited, true, link.Inlines);
            }

            if (link.Inlines.Count > 0)
            {
                Target(output).Add(link);
            }

            return;
        }

        foreach (var child in field.ChildElements)
        {
            ReadElement(child, inherited, inLink, output);
        }
    }

    /// <summary>Field keyword and its target: HYPERLINK "url" (or \l "anchor"), TOC, PAGE ...</summary>
    private static (string Kind, string? Target) ParseField(string instruction)
    {
        var text = instruction.Trim();
        var space = text.IndexOf(' ', StringComparison.Ordinal);
        var kind = (space < 0 ? text : text[..space]).ToUpperInvariant();
        if (kind != "HYPERLINK")
        {
            return (kind, null);
        }

        string? url = null;
        string? anchor = null;
        var tokens = FieldTokens().Matches(text[(space < 0 ? text.Length : space)..]).Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).ToList();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i] == "\\l" && i + 1 < tokens.Count)
            {
                anchor = tokens[++i];
            }
            else if (tokens[i].StartsWith('\\'))
            {
                if (tokens[i] is "\\o" or "\\t" && i + 1 < tokens.Count)
                {
                    i++;
                }
            }
            else
            {
                url ??= tokens[i];
            }
        }

        if (anchor is not null)
        {
            return (kind, (url ?? string.Empty) + "#" + anchor);
        }

        return (kind, url);
    }

    [GeneratedRegex("\"([^\"]*)\"|(\\S+)")]
    private static partial Regex FieldTokens();

    private enum RunKind { Text, Code, Math }

    private void ReadRun(Run run, InlineStyle inherited, bool inLink, List<Inline> output)
    {
        var rPr = run.RunProperties;
        var charStyle = rPr?.RunStyle?.Val?.Value;
        var flags = inherited | _styles.CharacterFlags(charStyle);
        if (rPr is not null)
        {
            flags = DocxStyleResolver.Apply(flags, InlineStyle.Bold, rPr.Bold);
            flags = DocxStyleResolver.Apply(flags, InlineStyle.Italic, rPr.Italic);
            flags = DocxStyleResolver.Apply(flags, InlineStyle.Strikethrough, rPr.Strike);
            flags = DocxStyleResolver.Apply(flags, InlineStyle.Strikethrough, rPr.DoubleStrike);
            flags = DocxStyleResolver.Apply(flags, InlineStyle.SmallCaps, rPr.SmallCaps);
            if (rPr.Underline?.Val is { } u)
            {
                flags = u.Value == UnderlineValues.None ? flags & ~InlineStyle.Underline : flags | InlineStyle.Underline;
            }

            if (rPr.VerticalTextAlignment?.Val is { } v)
            {
                flags &= ~(InlineStyle.Superscript | InlineStyle.Subscript);
                if (v.Value == VerticalPositionValues.Superscript)
                {
                    flags |= InlineStyle.Superscript;
                }
                else if (v.Value == VerticalPositionValues.Subscript)
                {
                    flags |= InlineStyle.Subscript;
                }
            }

            if (rPr.Highlight?.Val is { } h && h.Value != HighlightColorValues.None)
            {
                flags |= InlineStyle.Highlight;
            }
        }

        if (inLink)
        {
            flags &= ~InlineStyle.Underline;
        }

        var isLabel = charStyle is not null && _styles.IsStyle(charStyle, DocxStyleIds.CalloutLabel);
        var font = rPr?.RunFonts?.Ascii?.Value ?? rPr?.RunFonts?.HighAnsi?.Value ?? _styles.CharacterFont(charStyle) ?? _styles.ParagraphFont(_paragraphStyle);
        var kind = RunKind.Text;
        if (charStyle is not null && _styles.IsStyle(charStyle, DocxStyleIds.MathChar))
        {
            kind = RunKind.Math;
        }
        else if (charStyle is not null && _styles.IsCodeCharacterStyle(charStyle))
        {
            kind = RunKind.Code;
        }
        else if (_settings.DetectCode && DocxStyleResolver.IsMonospace(font) && !DocxStyleResolver.IsMonospace(_styles.ParagraphFont(_paragraphStyle)))
        {
            kind = RunKind.Code;
        }

        var text = new StringBuilder();
        void Flush()
        {
            if (text.Length == 0)
            {
                return;
            }

            var s = text.ToString();
            text.Clear();
            if (Skipping)
            {
                return;
            }

            if (s.Trim().Length > 0)
            {
                _result.TextRuns++;
                if (DocxStyleResolver.IsMonospace(font) || kind == RunKind.Code)
                {
                    _result.MonoRuns++;
                }
            }

            if (isLabel)
            {
                _result.CalloutLabel = (_result.CalloutLabel ?? string.Empty) + s;
                return;
            }

            Emit(Target(output), s, flags, kind);
        }

        foreach (var child in run.ChildElements)
        {
            switch (child)
            {
                case Text t:
                    text.Append(t.Text);
                    break;
                case FieldChar fc:
                    Flush();
                    OnFieldChar(fc, output);
                    break;
                case FieldCode code:
                    if (_fields.Count > 0 && !_fields[^1].InResult)
                    {
                        _fields[^1].Instruction.Append(code.Text);
                    }

                    break;
                case TabChar or PositionalTab:
                    text.Append('\t');
                    break;
                case Break br:
                    Flush();
                    if (Skipping)
                    {
                        break;
                    }

                    if (br.Type?.Value == BreakValues.Page)
                    {
                        output.Add(new PageBreakMarker());
                    }
                    else
                    {
                        Target(output).Add(new LineBreakInline());
                    }

                    break;
                case CarriageReturn:
                    Flush();
                    if (!Skipping)
                    {
                        Target(output).Add(new LineBreakInline());
                    }

                    break;
                case NoBreakHyphen:
                    text.Append('-');
                    break;
                case SymbolChar sym:
                    if (Symbol(sym) is { } ch)
                    {
                        text.Append(ch);
                    }

                    break;
                case Drawing drawing:
                    Flush();
                    if (!Skipping && _images.FromDrawing(drawing, _owner) is { } image)
                    {
                        Target(output).Add(image);
                    }

                    break;
                case AlternateContent alt:
                    Flush();
                    ReadAlternateContent(alt, output);
                    break;
                case Picture pict:
                    Flush();
                    if (!Skipping && _images.FromVml(pict, _owner) is { } vml)
                    {
                        Target(output).Add(vml);
                    }

                    break;
                case FootnoteReference fn when fn.Id?.Value is { } fnId:
                    Flush();
                    if (_settings.Footnotes && !Skipping && _registerNote(false, (int)fnId) is { } label)
                    {
                        Target(output).Add(new FootnoteReferenceInline { Label = label });
                    }

                    break;
                case EndnoteReference en when en.Id?.Value is { } enId:
                    Flush();
                    if (_settings.Footnotes && !Skipping && _registerNote(true, (int)enId) is { } elabel)
                    {
                        Target(output).Add(new FootnoteReferenceInline { Label = elabel });
                    }

                    break;
            }
        }

        Flush();
    }

    private void ReadAlternateContent(AlternateContent alt, List<Inline> output)
    {
        if (Skipping)
        {
            return;
        }

        var drawing = alt.Descendants<Drawing>().FirstOrDefault();
        if (drawing is not null && _images.FromDrawing(drawing, _owner) is { } image)
        {
            Target(output).Add(image);
            return;
        }

        var pict = alt.Descendants<Picture>().FirstOrDefault();
        if (pict is not null && _images.FromVml(pict, _owner) is { } vml)
        {
            Target(output).Add(vml);
        }
    }

    private void OnFieldChar(FieldChar fc, List<Inline> output)
    {
        var type = fc.FieldCharType?.Value;
        if (type == FieldCharValues.Begin)
        {
            _fields.Add(new FieldFrame());
        }
        else if (type == FieldCharValues.Separate && _fields.Count > 0)
        {
            var frame = _fields[^1];
            var (kind, target) = ParseField(frame.Instruction.ToString());
            if (kind == "TOC" && _settings.SkipToc)
            {
                frame.Skip = true;
            }
            else if (kind == "HYPERLINK" && target is not null && !Skipping)
            {
                var link = new LinkInline { Url = target };
                Target(output).Add(link);
                frame.Link = link;
            }

            frame.InResult = true;
        }
        else if (type == FieldCharValues.End && _fields.Count > 0)
        {
            var frame = _fields[^1];
            _fields.RemoveAt(_fields.Count - 1);
            if (frame.Link is { Inlines.Count: 0 } empty)
            {
                Target(output).Remove(empty);
            }
        }
    }

    /// <summary>Best-effort mapping of <c>w:sym</c> (Symbol / Wingdings private-use codes) to Unicode.</summary>
    private static char? Symbol(SymbolChar sym)
    {
        if (!int.TryParse(sym.Char?.Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
        {
            return null;
        }

        if (code >= 0xF000)
        {
            code -= 0xF000;
        }

        var font = sym.Font?.Value?.ToLowerInvariant() ?? string.Empty;
        if (font.StartsWith("wingdings", StringComparison.Ordinal))
        {
            return code switch
            {
                0xA8 or 0x6F or 0x71 => '☐',
                0xFE or 0x78 => '☒',
                0xFC => '✓',
                0xFB => '✗',
                0x9F or 0x6C => '•',
                0xA7 => '▪',
                0xD8 => '➢',
                0xE0 or 0xE8 => '→',
                _ => '•',
            };
        }

        if (font == "symbol")
        {
            return code switch
            {
                0xB7 => '•',
                0xAE => '→',
                0xAC => '←',
                0xB4 => '×',
                0xB8 => '÷',
                0xB1 => '±',
                0xA3 => '≤',
                0xB3 => '≥',
                0xB9 => '≠',
                0xA5 => '∞',
                0xD3 or 0xE3 => '©',
                0xD2 or 0xE2 => '®',
                0xD4 or 0xE4 => '™',
                >= 0x61 and <= 0x7A => "αβχδεφγηιϕκλμνοπθρστυϖωξψζ"[code - 0x61],
                >= 0x41 and <= 0x5A => "ΑΒΧΔΕΦΓΗΙϑΚΛΜΝΟΠΘΡΣΤΥςΩΞΨΖ"[code - 0x41],
                _ => code >= 0x20 ? (char)code : null,
            };
        }

        return code >= 0x20 ? (char)code : null;
    }

    /// <summary>Appends text, merging with the previous inline when it has the same formatting.</summary>
    private static void Emit(List<Inline> target, string text, InlineStyle flags, RunKind kind)
    {
        var last = target.Count > 0 ? target[^1] : null;
        switch (kind)
        {
            case RunKind.Code:
                if (flags == InlineStyle.None && last is CodeInline lc)
                {
                    lc.Code += text;
                }
                else if (flags != InlineStyle.None && last is StyledInline { Inlines: [CodeInline sc] } ls && ls.Style == flags)
                {
                    sc.Code += text;
                }
                else
                {
                    target.Add(flags == InlineStyle.None ? new CodeInline(text) : new StyledInline(flags, new CodeInline(text)));
                }

                return;
            case RunKind.Math:
                if (last is MathInline lm)
                {
                    lm.Latex += text;
                }
                else
                {
                    target.Add(new MathInline { Latex = text });
                }

                return;
        }

        if (flags == InlineStyle.None)
        {
            if (last is TextInline lt)
            {
                lt.Text += text;
            }
            else
            {
                target.Add(new TextInline(text));
            }
        }
        else if (last is StyledInline { Inlines: [TextInline st] } styled && styled.Style == flags)
        {
            st.Text += text;
        }
        else
        {
            target.Add(new StyledInline(flags, text));
        }
    }
}

/// <summary>Turns image parts into document resources (once per part) and drawings into <see cref="DrawingInline"/>s.</summary>
internal sealed class DocxImageStore
{
    private readonly Document _document;
    private readonly DocxImportSettings _settings;
    private readonly Action<string> _warn;
    private readonly Dictionary<Uri, string?> _resources = [];
    private int _skippedVector;
    private int _skippedLarge;

    public DocxImageStore(Document document, DocxImportSettings settings, Action<string> warn)
    {
        _document = document;
        _settings = settings;
        _warn = warn;
    }

    public DrawingInline? FromDrawing(Drawing drawing, OpenXmlPart owner)
    {
        if (!_settings.ExtractImages)
        {
            return null;
        }

        var extent = drawing.Inline?.Extent ?? drawing.Anchor?.GetFirstChild<DW.Extent>();
        var docPr = drawing.Descendants<DW.DocProperties>().FirstOrDefault();
        var blip = drawing.Descendants<A.Blip>().FirstOrDefault();
        var relId = blip?.Embed?.Value;
        if (relId is null)
        {
            if (drawing.Descendants<A.GraphicData>().Any(g => g.Uri?.Value?.Contains("chart", StringComparison.OrdinalIgnoreCase) == true
                || g.Uri?.Value?.Contains("diagram", StringComparison.OrdinalIgnoreCase) == true))
            {
                _warn("Charts and SmartArt diagrams are not imported.");
            }

            return null;
        }

        var source = Resource(owner, relId, docPr?.Name?.Value);
        if (source is null)
        {
            return null;
        }

        return new DrawingInline
        {
            Source = source,
            AltText = Clean(docPr?.Description?.Value),
            Title = Clean(docPr?.Title?.Value),
            WidthPt = (extent?.Cx?.Value ?? 0) / 12700.0,
            HeightPt = (extent?.Cy?.Value ?? 0) / 12700.0,
        };
    }

    public DrawingInline? FromVml(Picture picture, OpenXmlPart owner)
    {
        if (!_settings.ExtractImages)
        {
            return null;
        }

        var data = picture.Descendants<V.ImageData>().FirstOrDefault();
        if (data?.RelationshipId?.Value is not { } relId)
        {
            return null;
        }

        var source = Resource(owner, relId, null);
        if (source is null)
        {
            return null;
        }

        var shape = picture.Descendants<V.Shape>().FirstOrDefault();
        var (w, h) = VmlSize(shape?.Style?.Value);
        return new DrawingInline { Source = source, AltText = Clean(shape?.Alternate?.Value ?? data.Title?.Value), WidthPt = w, HeightPt = h };
    }

    private static (double, double) VmlSize(string? style)
    {
        double w = 0, h = 0;
        foreach (var part in (style ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split(':', 2);
            if (kv.Length != 2)
            {
                continue;
            }

            var value = kv[1].Trim();
            var factor = value.EndsWith("in", StringComparison.Ordinal) ? 72 : value.EndsWith("px", StringComparison.Ordinal) ? 0.75 : 1;
            var number = new string(value.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
            if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            {
                continue;
            }

            switch (kv[0].Trim().ToLowerInvariant())
            {
                case "width":
                    w = n * factor;
                    break;
                case "height":
                    h = n * factor;
                    break;
            }
        }

        return (w, h);
    }

    private string? Resource(OpenXmlPart owner, string relId, string? name)
    {
        OpenXmlPart part;
        try
        {
            part = owner.GetPartById(relId);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }

        if (part is not ImagePart image)
        {
            return null;
        }

        if (_resources.TryGetValue(image.Uri, out var known))
        {
            return known;
        }

        string? reference = null;
        var mediaType = image.ContentType.ToLowerInvariant();
        if (mediaType.Contains("emf", StringComparison.Ordinal) || mediaType.Contains("wmf", StringComparison.Ordinal))
        {
            if (_skippedVector++ == 0)
            {
                _warn("EMF/WMF vector pictures are not supported and were left out.");
            }
        }
        else
        {
            using var stream = image.GetStream(FileMode.Open, FileAccess.Read);
            if (stream.CanSeek && stream.Length > _settings.MaxImageBytes)
            {
                if (_skippedLarge++ == 0)
                {
                    _warn($"Images larger than {_settings.MaxImageMb} MB were left out.");
                }
            }
            else
            {
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                if (buffer.Length > _settings.MaxImageBytes)
                {
                    if (_skippedLarge++ == 0)
                    {
                        _warn($"Images larger than {_settings.MaxImageMb} MB were left out.");
                    }
                }
                else
                {
                    var fileName = Path.GetFileName(image.Uri.OriginalString);
                    reference = _document.AddResource(mediaType == "image/jpg" ? "image/jpeg" : mediaType, buffer.ToArray(), string.IsNullOrEmpty(fileName) ? name : fileName).Reference;
                }
            }
        }

        _resources[image.Uri] = reference;
        return reference;
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
