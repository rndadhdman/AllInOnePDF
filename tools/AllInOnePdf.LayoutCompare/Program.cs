// Fidelity corpus for the PDF layout engine.
//
//   layoutcompare reference <dir>            render the corpus with the current engine and store page PNGs + sizes
//   layoutcompare compare <refDir> <outDir>  render again and compare with the reference: writes <outDir>/<page>.png
//                                            (current), <page>.diff.png (red = differing pixels), summary.txt and
//                                            summary.json; exit code 1 when any page exceeds the threshold
//   options: --dpi 72 --threshold 0.5 (percent of differing pixels allowed per page) --only <substring>
//
// The corpus: samples/sample-document.md and the welcome sample with every built-in theme, every Creator template
// with placeholders, and a few stress documents (long tables, code, footnotes, nested lists, images).
using System.Globalization;
using System.Text;
using System.Text.Json;
using AllInOnePdf.Core;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Highlighting;
using AllInOnePdf.Core.Importers;
using AllInOnePdf.Core.Rendering;
using AllInOnePdf.Core.Themes;
using AllInOnePdf.Publishing.Rendering;
using AllInOnePdf.Publishing.Templates;
using SkiaSharp;

var mode = args.Length > 0 ? args[0] : "help";
var positional = args.Skip(1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
int dpi = 72;
double threshold = 0.5;
string? only = null;
for (var i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--dpi" when i + 1 < args.Length:
            dpi = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--threshold" when i + 1 < args.Length:
            threshold = double.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--only" when i + 1 < args.Length:
            only = args[++i];
            break;
    }
}

if (mode is not ("reference" or "compare") || positional.Count < (mode == "compare" ? 2 : 1))
{
    Console.WriteLine("usage: layoutcompare reference <dir> | compare <refDir> <outDir> [--dpi 72] [--threshold 0.5] [--only name]");
    return 2;
}

var repo = FindRepoRoot();
var highlighter = new SyntaxHighlighter();
var pdfRenderer = new PdfRenderer(highlighter);
var pubRenderer = new PublicationRenderer(highlighter);
var raster = new PdfiumRasterizer();
var corpus = await Corpus.BuildAsync(repo);
if (only is not null)
{
    corpus = corpus.Where(c => c.Name.Contains(only, StringComparison.OrdinalIgnoreCase)).ToList();
}

if (mode == "reference")
{
    var dir = Path.GetFullPath(positional[0]);
    Directory.CreateDirectory(dir);
    var pages = 0;
    foreach (var item in corpus)
    {
        var pdf = await item.RenderAsync(pdfRenderer, pubRenderer);
        // The PDF itself is kept too, so the text layer (what a reader extracts) can be compared, not only the pixels.
        await File.WriteAllBytesAsync(Path.Combine(dir, item.Name + ".pdf"), pdf);
        var count = raster.GetPageCount(pdf);
        for (var p = 0; p < count; p++)
        {
            var image = await raster.RasterizePageAsync(pdf, p, new RasterizeOptions { Dpi = dpi, WithAnnotations = false });
            await File.WriteAllBytesAsync(Path.Combine(dir, $"{item.Name}-p{p + 1:D3}.png"), image.Data);
            pages++;
        }

        Console.WriteLine($"{item.Name}: {count} pages");
    }

    Console.WriteLine($"{corpus.Count} documents, {pages} pages written to {dir}");
    return 0;
}
else
{
    var refDir = Path.GetFullPath(positional[0]);
    var outDir = Path.GetFullPath(positional[1]);
    Directory.CreateDirectory(outDir);
    var rows = new List<PageResult>();
    foreach (var item in corpus)
    {
        byte[] pdf;
        try
        {
            pdf = await item.RenderAsync(pdfRenderer, pubRenderer);
        }
        catch (Exception ex)
        {
            rows.Add(new PageResult(item.Name, 0, 0, 0, 100, 0, 0, "render failed: " + ex.GetType().Name + ": " + ex.Message));
            Console.WriteLine($"{item.Name}: RENDER FAILED {ex.Message}");
            continue;
        }

        await File.WriteAllBytesAsync(Path.Combine(outDir, item.Name + ".pdf"), pdf);
        var count = raster.GetPageCount(pdf);
        var refCount = Directory.EnumerateFiles(refDir, item.Name + "-p*.png").Count();
        for (var p = 0; p < Math.Max(count, refCount); p++)
        {
            var name = $"{item.Name}-p{p + 1:D3}";
            var refPath = Path.Combine(refDir, name + ".png");
            if (p >= count || !File.Exists(refPath))
            {
                rows.Add(new PageResult(item.Name, p + 1, count, refCount, 100, 0, 0, p >= count ? "missing in current render" : "missing in reference"));
                continue;
            }

            var image = await raster.RasterizePageAsync(pdf, p, new RasterizeOptions { Dpi = dpi, WithAnnotations = false });
            await File.WriteAllBytesAsync(Path.Combine(outDir, name + ".png"), image.Data);
            using var current = SKBitmap.Decode(image.Data);
            using var reference = SKBitmap.Decode(refPath);
            var (percent, differing, total) = Compare(current, reference, Path.Combine(outDir, name + ".diff.png"));
            rows.Add(new PageResult(item.Name, p + 1, count, refCount, percent, differing, total, null));
        }

        var worst = rows.Where(r => r.Document == item.Name).Max(r => r.DifferentPercent);
        Console.WriteLine($"{item.Name}: {count} pages (reference {refCount}), worst page {worst:0.00}%");
    }

    var summary = new StringBuilder();
    summary.AppendLine(CultureInfo.InvariantCulture, $"Corpus: {corpus.Count} documents, {rows.Count} pages, dpi {dpi}, threshold {threshold}%");
    var failing = rows.Where(r => r.DifferentPercent > threshold || r.Note is not null).OrderByDescending(r => r.DifferentPercent).ToList();
    summary.AppendLine(CultureInfo.InvariantCulture, $"Pages over threshold or missing: {failing.Count}");
    summary.AppendLine(CultureInfo.InvariantCulture, $"Mean differing pixels: {rows.Where(r => r.Note is null).Select(r => r.DifferentPercent).DefaultIfEmpty(0).Average():0.000}%");
    summary.AppendLine();
    foreach (var r in rows.OrderByDescending(r => r.DifferentPercent).Take(60))
    {
        summary.AppendLine(CultureInfo.InvariantCulture, $"{r.DifferentPercent,8:0.00}%  {r.Document}-p{r.Page:D3}  pages {r.PageCount}/{r.ReferencePageCount}{(r.Note is null ? string.Empty : "  " + r.Note)}");
    }

    await File.WriteAllTextAsync(Path.Combine(outDir, "summary.txt"), summary.ToString());
    await File.WriteAllTextAsync(Path.Combine(outDir, "summary.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(summary.ToString());
    return failing.Count == 0 ? 0 : 1;
}

static (double Percent, int Differing, int Total) Compare(SKBitmap current, SKBitmap reference, string diffPath)
{
    var width = Math.Max(current.Width, reference.Width);
    var height = Math.Max(current.Height, reference.Height);
    using var diff = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
    var differing = 0;
    var total = width * height;
    for (var y = 0; y < height; y++)
    {
        for (var x = 0; x < width; x++)
        {
            var a = x < current.Width && y < current.Height ? current.GetPixel(x, y) : SKColors.Magenta;
            var b = x < reference.Width && y < reference.Height ? reference.GetPixel(x, y) : SKColors.Magenta;
            // Antialiasing noise is ignored: a pixel counts when any channel differs by more than 40/255.
            var same = Math.Abs(a.Red - b.Red) <= 40 && Math.Abs(a.Green - b.Green) <= 40 && Math.Abs(a.Blue - b.Blue) <= 40;
            if (same)
            {
                var grey = (byte)(255 - ((255 - b.Red) / 4));
                diff.SetPixel(x, y, new SKColor(grey, grey, grey));
            }
            else
            {
                differing++;
                diff.SetPixel(x, y, SKColors.Red);
            }
        }
    }

    using var image = SKImage.FromBitmap(diff);
    using var data = image.Encode(SKEncodedImageFormat.Png, 90);
    using var file = File.Create(diffPath);
    data.SaveTo(file);
    return (100.0 * differing / total, differing, total);
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AllInOnePdf.slnx")))
    {
        dir = dir.Parent;
    }

    return dir?.FullName ?? throw new InvalidOperationException("Run from inside the repository.");
}

sealed record PageResult(string Document, int Page, int PageCount, int ReferencePageCount, double DifferentPercent, int Differing, int Total, string? Note);

sealed class CorpusItem(string name, Func<IPdfRenderer, IPublicationRenderer, Task<byte[]>> render)
{
    public string Name { get; } = name;

    public Task<byte[]> RenderAsync(IPdfRenderer pdf, IPublicationRenderer pub) => render(pdf, pub);
}

static class Corpus
{
    public static async Task<List<CorpusItem>> BuildAsync(string repo)
    {
        var items = new List<CorpusItem>();
        var sampleMarkdown = await File.ReadAllTextAsync(Path.Combine(repo, "samples", "sample-document.md"));
        var stress = StressDocuments();

        foreach (var theme in BuiltInThemes.All())
        {
            var t = theme;
            items.Add(new CorpusItem($"sample-{t.Id}", async (pdf, _) => await pdf.RenderAsync(await ImportAsync(sampleMarkdown, Path.Combine(repo, "samples", "sample-document.md")), t)));
            items.Add(new CorpusItem($"welcome-{t.Id}", async (pdf, _) => await pdf.RenderAsync(await SampleDocuments.CreateWelcomeAsync(), t)));
        }

        // Stress documents with a handful of contrasting themes only.
        foreach (var themeId in new[] { "clean", "manuscript", "technical", "night", "academic", "handout" })
        {
            var t = BuiltInThemes.Find(themeId)!;
            foreach (var (name, markdown) in stress)
            {
                items.Add(new CorpusItem($"{name}-{t.Id}", async (pdf, _) => await pdf.RenderAsync(await ImportAsync(markdown, null), t)));
            }
        }

        foreach (var template in BuiltInTemplates.All())
        {
            var tpl = template;
            items.Add(new CorpusItem($"template-{tpl.Id}", (_, pub) => pub.RenderAsync(tpl.Create(), new PublicationRenderOptions { ShowPlaceholders = true })));
        }

        return items;
    }

    private static async Task<Document> ImportAsync(string markdown, string? sourcePath)
    {
        var importer = new MarkdownImporter();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(markdown));
        return await importer.ImportAsync(new ImportContext { Input = stream, FileName = "corpus.md", SourcePath = sourcePath, Options = OptionValues.FromDefaults(importer.Options) });
    }

    private static List<(string Name, string Markdown)> StressDocuments()
    {
        var sb = new StringBuilder();

        // Long table that must paginate with a repeated header, spans and alignments.
        sb.AppendLine("# Long table").AppendLine().AppendLine("| Item | Quantity | Unit price | Notes |").AppendLine("|:-----|---------:|-----------:|:------|");
        for (var i = 1; i <= 120; i++)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| Item number {i} with a longer name | {i * 3} | {i * 1.25:0.00} | Row {i} of the table, which wraps when the cell is narrow |");
        }

        var table = sb.ToString();

        sb.Clear();
        sb.AppendLine("# Code").AppendLine().AppendLine("Short and long lines, wrapped and clipped, with and without highlighting.").AppendLine().AppendLine("```csharp");
        for (var i = 0; i < 80; i++)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"public static async Task<IReadOnlyList<string>> LoadAsync{i}(string path, CancellationToken ct = default) => await File.ReadAllLinesAsync(path, ct); // a long trailing comment number {i} that goes on and on");
        }

        sb.AppendLine("```").AppendLine().AppendLine("Inline `code` and a `much longer inline code span that may need to wrap across lines` inside a paragraph.");
        var code = sb.ToString();

        sb.Clear();
        sb.AppendLine("# Lists and quotes").AppendLine();
        for (var i = 1; i <= 6; i++)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{i}. Ordered item {i} with **bold**, *italic*, ~~strike~~ and a [link](https://example.com/{i}).");
            sb.AppendLine("   - Nested bullet one with enough words to wrap onto a second line when the column is narrow enough.");
            sb.AppendLine("   - Nested bullet two");
            sb.AppendLine("     1. Third level ordered");
            sb.AppendLine("   - [x] Done task");
            sb.AppendLine("   - [ ] Open task");
        }

        sb.AppendLine().AppendLine("> A quote with a long paragraph that wraps across several lines so the border and the background can be checked against the reference rendering of the same text.").AppendLine(">").AppendLine("> — Attribution");
        sb.AppendLine().AppendLine("> [!NOTE]").AppendLine("> A note callout.").AppendLine().AppendLine("> [!WARNING]").AppendLine("> A warning callout with a second paragraph.").AppendLine(">").AppendLine("> Second paragraph.");
        sb.AppendLine().AppendLine("Footnote reference[^1] and another[^2].").AppendLine().AppendLine("[^1]: The first footnote.").AppendLine("[^2]: The second footnote, longer, wrapping onto a second line when the page is narrow.");
        var lists = sb.ToString();

        sb.Clear();
        sb.AppendLine("# Typography").AppendLine();
        for (var i = 0; i < 14; i++)
        {
            sb.AppendLine("Justified paragraphs with hyphenation exercise the line breaker: extraordinarily long words such as antidisestablishmentarianism and floccinaucinihilipilification appear next to short ones, and punctuation — dashes, “quotes”, ellipses… — sits at line ends. The quick brown fox jumps over the lazy dog; sphinx of black quartz, judge my vow.").AppendLine();
        }

        sb.AppendLine("## Heading that is long enough to wrap onto a second line when the page margins are wide and the font is large").AppendLine().AppendLine("Text after it. Superscript H^2^O is not Markdown here, but footnotes[^3] are.").AppendLine().AppendLine("[^3]: Note.").AppendLine().AppendLine("---").AppendLine().AppendLine("Après la pluie, le beau temps. Straße, Übergröße, ñandú, crème brûlée, Ærø. Ελληνικά, Кириллица.");
        var typography = sb.ToString();

        return [("stress-table", table), ("stress-code", code), ("stress-lists", lists), ("stress-typography", typography)];
    }
}
