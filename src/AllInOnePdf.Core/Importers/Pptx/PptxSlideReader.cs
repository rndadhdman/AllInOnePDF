using System.Globalization;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Exporters.Docx;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace AllInOnePdf.Core.Importers.Pptx;

/// <summary>
/// Reads one slide: shapes in reading order (title first, then by position), placeholders, text boxes, tables,
/// pictures and speaker notes. Charts, SmartArt and embedded objects are counted and reported once.
/// </summary>
internal sealed class PptxSlideReader
{
    private const string TableUri = "http://schemas.openxmlformats.org/drawingml/2006/table";
    private const string ChartUri = "http://schemas.openxmlformats.org/drawingml/2006/chart";
    private const string DiagramUri = "http://schemas.openxmlformats.org/drawingml/2006/diagram";
    private const string OleUri = "http://schemas.openxmlformats.org/presentationml/2006/ole";

    private static readonly HashSet<string> RasterTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/jpg", "image/pjpeg", "image/gif", "image/bmp", "image/x-bmp", "image/tiff", "image/webp",
    };

    private readonly Document _document;
    private readonly PptxImportSettings _settings;
    private readonly Action<string> _warn;
    private readonly long _slideWidth;
    private readonly Dictionary<Uri, string?> _resources = [];
    private readonly List<(HeadingBlock Heading, int Step)> _subheadings = [];

    private int _skippedLarge;
    private int _skippedVector;
    private int _charts;
    private int _diagrams;
    private int _embedded;
    private int _media;

    public PptxSlideReader(Document document, PptxImportSettings settings, long slideWidth, Action<string> warn)
    {
        _document = document;
        _settings = settings;
        _slideWidth = slideWidth > 0 ? slideWidth : 12192000;
        _warn = warn;
    }

    private enum ShapeRole { None, Title, Subtitle, Body, Skip }

    private sealed record Entry(OpenXmlElement Element, ShapeRole Role, long X, long Y, bool HasPosition, int Order, Map Map);

    /// <summary>Maps child coordinates of (nested) groups to slide coordinates.</summary>
    private readonly record struct Map(double Sx, double Ox, double Sy, double Oy)
    {
        public static readonly Map Identity = new(1, 0, 1, 0);

        public long X(long x) => (long)Math.Round(x * Sx + Ox);

        public long Y(long y) => (long)Math.Round(y * Sy + Oy);

        /// <summary>this ∘ local, where local maps the group's child space to its parent space.</summary>
        public Map Then(double sx, double ox, double sy, double oy) => new(Sx * sx, Sx * ox + Ox, Sy * sy, Sy * oy + Oy);
    }

    /// <summary>Reads the slide's blocks and the title text (null when the slide has no title placeholder text).</summary>
    public (List<Block> Blocks, string? Title) ReadSlide(SlidePart part, int number)
    {
        var blocks = new List<Block>();
        var entries = new List<Entry>();
        if (part.Slide?.CommonSlideData?.ShapeTree is { } tree)
        {
            Collect(part, tree, Map.Identity, entries);
        }

        var titles = entries.Where(e => e.Role == ShapeRole.Title).ToList();
        var rest = entries.Where(e => e.Role != ShapeRole.Title).ToList();
        IEnumerable<Entry> ordered = _settings.ReadingOrderByPosition
            ? rest.Where(e => e.HasPosition).OrderBy(e => e.Y).ThenBy(e => e.X).ThenBy(e => e.Order).Concat(rest.Where(e => !e.HasPosition))
            : rest;

        var title = string.Join(' ', titles
            .Select(t => ((P.Shape)t.Element).TextBody)
            .Where(b => b is not null)
            .Select(b => PptxTextReader.PlainText(b!.Elements<A.Paragraph>()))
            .Where(s => s.Length > 0));

        if (title.Length > 0)
        {
            blocks.Add(new HeadingBlock(_settings.HeadingLevel, title));
        }
        else if (_settings.NumberUntitledSlides)
        {
            blocks.Add(new HeadingBlock(_settings.HeadingLevel, "Slide " + number.ToString(CultureInfo.InvariantCulture)));
        }

        var text = new PptxTextReader(part, _settings.DetectCode);
        foreach (var entry in ordered)
        {
            ReadEntry(part, entry, text, blocks);
        }

        if (_settings.SpeakerNotes)
        {
            ReadNotes(part, blocks);
        }

        foreach (var block in Block.EnumerateRecursive(blocks))
        {
            block.SourcePage = number;
        }

        return (blocks, title.Length > 0 ? title : null);
    }

    /// <summary>Adds one warning per kind of skipped content.</summary>
    public void Finish()
    {
        RankSubheadings();

        var parts = new List<string>();
        if (_charts > 0)
        {
            parts.Add(Count(_charts, "chart", "charts"));
        }

        if (_diagrams > 0)
        {
            parts.Add(Count(_diagrams, "SmartArt graphic", "SmartArt graphics"));
        }

        if (_embedded > 0)
        {
            parts.Add(Count(_embedded, "embedded object", "embedded objects"));
        }

        if (_media > 0)
        {
            parts.Add(Count(_media, "video or audio clip", "video or audio clips"));
        }

        if (parts.Count > 0)
        {
            var list = parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];
            var verb = _charts + _diagrams + _embedded + _media == 1 ? "was" : "were";
            _warn($"Charts, SmartArt, media and embedded objects cannot be converted; {list} {verb} left out.");
        }

        if (_skippedLarge > 0)
        {
            var verb = _skippedLarge == 1 ? "was" : "were";
            _warn(string.Create(CultureInfo.InvariantCulture, $"{Count(_skippedLarge, "picture", "pictures")} larger than {_settings.MaxImageMb} MB {verb} left out."));
        }
    }

    private static string Count(int n, string one, string many) =>
        n.ToString(CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many);

    private void Collect(SlidePart part, OpenXmlCompositeElement container, Map map, List<Entry> entries)
    {
        foreach (var child in container.ChildElements)
        {
            switch (child)
            {
                case P.Shape sp:
                {
                    var ph = sp.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.PlaceholderShape;
                    var xfrm = sp.ShapeProperties?.Transform2D ?? (ph is null ? null : InheritedTransform(part, ph));
                    Add(entries, sp, RoleOf(ph), xfrm?.Offset, map);
                    break;
                }

                case P.Picture pic:
                    Add(entries, pic, ShapeRole.None, pic.ShapeProperties?.Transform2D?.Offset, map);
                    break;
                case P.GraphicFrame frame:
                    Add(entries, frame, ShapeRole.None, frame.Transform?.Offset, map);
                    break;
                case P.GroupShape group:
                {
                    var tg = group.GroupShapeProperties?.TransformGroup;
                    var inner = map;
                    if (tg?.Offset is { } off && tg.Extents is { } ext && tg.ChildOffset is { } chOff && tg.ChildExtents is { } chExt)
                    {
                        var sx = chExt.Cx?.Value is > 0 ? (double)(ext.Cx?.Value ?? 0) / chExt.Cx.Value : 1;
                        var sy = chExt.Cy?.Value is > 0 ? (double)(ext.Cy?.Value ?? 0) / chExt.Cy.Value : 1;
                        inner = map.Then(sx, (off.X?.Value ?? 0) - (chOff.X?.Value ?? 0) * sx, sy, (off.Y?.Value ?? 0) - (chOff.Y?.Value ?? 0) * sy);
                    }

                    Collect(part, group, inner, entries);
                    break;
                }

                case AlternateContent ac:
                    if (ac.GetFirstChild<AlternateContentFallback>() is { } fallback)
                    {
                        Collect(part, fallback, map, entries);
                    }

                    break;
            }
        }
    }

    private static void Add(List<Entry> entries, OpenXmlElement element, ShapeRole role, A.Offset? offset, Map map)
    {
        if (role == ShapeRole.Skip)
        {
            return;
        }

        var hasPosition = offset?.X is not null && offset.Y is not null;
        entries.Add(new Entry(element, role, hasPosition ? map.X(offset!.X!.Value) : 0, hasPosition ? map.Y(offset!.Y!.Value) : 0, hasPosition, entries.Count, map));
    }

    private static ShapeRole RoleOf(P.PlaceholderShape? ph)
    {
        if (ph is null)
        {
            return ShapeRole.None;
        }

        if (ph.Type is not { HasValue: true } type)
        {
            return ShapeRole.Body;
        }

        var t = type.Value;
        if (t == P.PlaceholderValues.Title || t == P.PlaceholderValues.CenteredTitle)
        {
            return ShapeRole.Title;
        }

        if (t == P.PlaceholderValues.SubTitle)
        {
            return ShapeRole.Subtitle;
        }

        if (t == P.PlaceholderValues.DateAndTime || t == P.PlaceholderValues.Footer || t == P.PlaceholderValues.SlideNumber || t == P.PlaceholderValues.Header || t == P.PlaceholderValues.SlideImage)
        {
            return ShapeRole.Skip;
        }

        return ShapeRole.Body;
    }

    /// <summary>A placeholder without its own transform sits where its layout (or master) placeholder sits.</summary>
    private static A.Transform2D? InheritedTransform(SlidePart part, P.PlaceholderShape ph)
    {
        try
        {
            var layout = part.SlideLayoutPart;
            if (FindPlaceholder(layout?.SlideLayout?.CommonSlideData?.ShapeTree, ph, byIndex: true) is { } fromLayout)
            {
                return fromLayout;
            }

            return FindPlaceholder(layout?.SlideMasterPart?.SlideMaster?.CommonSlideData?.ShapeTree, ph, byIndex: false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Xml.XmlException or OpenXmlPackageException)
        {
            return null;
        }
    }

    private static A.Transform2D? FindPlaceholder(P.ShapeTree? tree, P.PlaceholderShape ph, bool byIndex)
    {
        if (tree is null)
        {
            return null;
        }

        var shapes = tree.Descendants<P.Shape>()
            .Select(s => (Shape: s, Ph: s.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.PlaceholderShape))
            .Where(x => x.Ph is not null && x.Shape.ShapeProperties?.Transform2D is not null)
            .ToList();

        if (byIndex && ph.Index?.Value is { } idx and > 0)
        {
            var match = shapes.FirstOrDefault(x => x.Ph!.Index?.Value == idx);
            if (match.Shape is not null)
            {
                return match.Shape.ShapeProperties!.Transform2D;
            }
        }

        var kind = TypeKey(ph);
        var byType = shapes.FirstOrDefault(x => TypeKey(x.Ph!) == kind);
        return byType.Shape?.ShapeProperties?.Transform2D;
    }

    private static string TypeKey(P.PlaceholderShape ph)
    {
        if (ph.Type is not { HasValue: true } type)
        {
            return "body";
        }

        var t = type.Value;
        if (t == P.PlaceholderValues.Title || t == P.PlaceholderValues.CenteredTitle)
        {
            return "title";
        }

        if (t == P.PlaceholderValues.Body || t == P.PlaceholderValues.Object)
        {
            return "body";
        }

        return ((IEnumValue)t).Value;
    }

    private void ReadEntry(SlidePart part, Entry entry, PptxTextReader text, List<Block> blocks)
    {
        switch (entry.Element)
        {
            case P.Shape sp:
                ReadShape(sp, entry.Role, text, blocks);
                break;
            case P.Picture pic:
                ReadPicture(part, pic, entry.Map, blocks);
                break;
            case P.GraphicFrame frame:
                ReadFrame(frame, text, blocks);
                break;
        }
    }

    private void ReadShape(P.Shape sp, ShapeRole role, PptxTextReader text, List<Block> blocks)
    {
        var body = sp.TextBody;
        if (body is null)
        {
            return;
        }

        var paragraphs = body.Elements<A.Paragraph>().ToList();
        if (_settings.DetectCode && role != ShapeRole.Subtitle && PptxTextReader.IsAllMonospace(paragraphs))
        {
            var code = PptxTextReader.CodeText(paragraphs);
            if (code.Trim().Length > 0)
            {
                blocks.Add(new CodeBlock { Code = code });
            }

            return;
        }

        if (role == ShapeRole.Subtitle)
        {
            foreach (var p in paragraphs)
            {
                var para = text.Read(p, bulletsByDefault: false);
                if (para.IsEmpty)
                {
                    continue;
                }

                var block = new ParagraphBlock { Alignment = para.Alignment };
                block.Inlines.Add(new StyledInline(InlineStyle.Italic, [.. para.Inlines]));
                blocks.Add(block);
            }

            return;
        }

        blocks.AddRange(text.ToBlocks(paragraphs, bulletsByDefault: role == ShapeRole.Body, (heading, step) => _subheadings.Add((heading, step))));
    }

    /// <summary>
    /// Gives the in-slide headings their levels below the slide titles: the largest size step above the body text
    /// is the next level, the next step the one after (our exporter writes deeper headings one point smaller).
    /// </summary>
    private void RankSubheadings()
    {
        var steps = _subheadings.Select(s => s.Step).Distinct().OrderDescending().ToList();
        foreach (var (heading, step) in _subheadings)
        {
            heading.Level = Math.Min(6, _settings.HeadingLevel + 1 + steps.IndexOf(step));
        }
    }

    private void ReadPicture(SlidePart part, P.Picture pic, Map map, List<Block> blocks)
    {
        var nvPr = pic.NonVisualPictureProperties?.ApplicationNonVisualDrawingProperties;
        if (nvPr?.GetFirstChild<A.VideoFromFile>() is not null || nvPr?.GetFirstChild<A.AudioFromFile>() is not null
            || nvPr?.Descendants().Any(e => e.LocalName is "media") == true)
        {
            _media++;
            return;
        }

        if (!_settings.ExtractImages)
        {
            return;
        }

        var relId = pic.BlipFill?.Blip?.Embed?.Value;
        if (string.IsNullOrEmpty(relId) || Resource(part, relId) is not { } resource)
        {
            return;
        }

        var props = pic.NonVisualPictureProperties?.NonVisualDrawingProperties;
        var alt = Clean(props?.Description?.Value) ?? Clean(props?.Title?.Value) ?? Clean(props?.Name?.Value);
        var image = new ImageBlock { Source = resource.Reference, AltText = alt };
        if (pic.ShapeProperties?.Transform2D?.Extents?.Cx?.Value is { } cx and > 0)
        {
            image.WidthPercent = Math.Round(Math.Clamp(cx * map.Sx / _slideWidth * 100, 1, 100), 1);
        }

        if (ImageDimensions.Read(resource.Data) is { } size && size.Width > 0 && size.Height > 0)
        {
            image.PixelWidth = size.Width;
            image.PixelHeight = size.Height;
        }

        blocks.Add(image);
    }

    private void ReadFrame(P.GraphicFrame frame, PptxTextReader text, List<Block> blocks)
    {
        var data = frame.Graphic?.GraphicData;
        var uri = data?.Uri?.Value ?? string.Empty;
        if (data?.GetFirstChild<A.Table>() is { } table)
        {
            blocks.Add(ReadTable(table, text));
        }
        else if (string.Equals(uri, TableUri, StringComparison.Ordinal))
        {
            // A table frame without a table: nothing to read.
        }
        else if (uri.StartsWith(ChartUri, StringComparison.Ordinal))
        {
            _charts++;
        }
        else if (uri.StartsWith(DiagramUri, StringComparison.Ordinal))
        {
            _diagrams++;
        }
        else if (uri.StartsWith(OleUri, StringComparison.Ordinal))
        {
            _embedded++;
        }
        else
        {
            _embedded++;
        }
    }

    private static TableBlock ReadTable(A.Table tbl, PptxTextReader text)
    {
        var table = new TableBlock();
        var firstRowFlag = tbl.TableProperties?.FirstRow?.Value == true;
        var firstRowBold = false;
        var rowIndex = 0;
        foreach (var tr in tbl.Elements<A.TableRow>())
        {
            var row = new TableRow();
            var cellsWithText = 0;
            var boldCells = 0;
            foreach (var tc in tr.Elements<A.TableCell>())
            {
                if (tc.HorizontalMerge?.Value == true || tc.VerticalMerge?.Value == true)
                {
                    continue;
                }

                var cell = new TableCell
                {
                    ColumnSpan = Math.Max(1, tc.GridSpan?.Value ?? 1),
                    RowSpan = Math.Max(1, tc.RowSpan?.Value ?? 1),
                };
                var paragraphs = tc.TextBody?.Elements<A.Paragraph>().ToList() ?? [];
                cell.Blocks.AddRange(text.ToBlocks(paragraphs, bulletsByDefault: false));
                if (cell.Blocks.Count > 0)
                {
                    cellsWithText++;
                    if (PptxTextReader.IsAllBold(paragraphs))
                    {
                        boldCells++;
                    }
                }

                row.Cells.Add(cell);
            }

            if (rowIndex == 0)
            {
                firstRowBold = cellsWithText > 0 && boldCells == cellsWithText;
            }

            table.Rows.Add(row);
            rowIndex++;
        }

        if (table.Rows.Count > 0 && (firstRowFlag || firstRowBold))
        {
            var header = table.Rows[0];
            header.IsHeader = true;
            foreach (var cell in header.Cells)
            {
                foreach (var p in cell.Blocks.OfType<InlineContainerBlock>())
                {
                    var stripped = PptxTextReader.StripStyle(p.Inlines, InlineStyle.Bold);
                    p.Inlines.Clear();
                    p.Inlines.AddRange(stripped);
                }
            }
        }

        return table;
    }

    private void ReadNotes(SlidePart part, List<Block> blocks)
    {
        NotesSlidePart? notesPart;
        try
        {
            notesPart = part.NotesSlidePart;
        }
        catch (InvalidOperationException)
        {
            return;
        }

        var tree = notesPart?.NotesSlide?.CommonSlideData?.ShapeTree;
        if (notesPart is null || tree is null)
        {
            return;
        }

        var reader = new PptxTextReader(notesPart, _settings.DetectCode);
        var noteBlocks = new List<Block>();
        foreach (var sp in tree.Descendants<P.Shape>())
        {
            var ph = sp.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.PlaceholderShape;
            if (ph is not null && RoleOf(ph) != ShapeRole.Body)
            {
                continue;
            }

            if (sp.TextBody is { } body)
            {
                noteBlocks.AddRange(reader.ToBlocks(body.Elements<A.Paragraph>(), bulletsByDefault: false));
            }
        }

        if (noteBlocks.Count > 0)
        {
            var callout = new CalloutBlock { Kind = CalloutKind.Note, Title = "Speaker notes" };
            callout.Blocks.AddRange(noteBlocks);
            blocks.Add(callout);
        }
    }

    private DocumentResource? Resource(SlidePart owner, string relId)
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
            return known is null ? null : _document.FindResource(known);
        }

        DocumentResource? resource = null;
        var mediaType = image.ContentType.ToLowerInvariant();
        if (!RasterTypes.Contains(mediaType))
        {
            if (_skippedVector++ == 0)
            {
                _warn("EMF, WMF and SVG pictures are not supported and were left out.");
            }
        }
        else
        {
            using var stream = image.GetStream(FileMode.Open, FileAccess.Read);
            if (stream.CanSeek && stream.Length > _settings.MaxImageBytes)
            {
                _skippedLarge++;
            }
            else
            {
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                if (buffer.Length > _settings.MaxImageBytes)
                {
                    _skippedLarge++;
                }
                else
                {
                    var fileName = Path.GetFileName(image.Uri.OriginalString);
                    var normalized = mediaType switch
                    {
                        "image/jpg" or "image/pjpeg" => "image/jpeg",
                        "image/x-bmp" => "image/bmp",
                        _ => mediaType,
                    };
                    resource = _document.AddResource(normalized, buffer.ToArray(), string.IsNullOrEmpty(fileName) ? null : fileName);
                }
            }
        }

        _resources[image.Uri] = resource?.Reference;
        return resource;
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
