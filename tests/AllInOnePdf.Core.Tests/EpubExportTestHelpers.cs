using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Exporters;
using AllInOnePdf.Core.Highlighting;
using AllInOnePdf.Core.Importers;
using AllInOnePdf.Core.Themes;

namespace AllInOnePdf.Core.Tests;

/// <summary>Exports documents to EPUB and checks the package structure the way a strict reader would.</summary>
internal static partial class EpubExportTestHelpers
{
    public static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
    public static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    public static readonly XNamespace Xhtml = "http://www.w3.org/1999/xhtml";
    public static readonly XNamespace Ops = "http://www.idpf.org/2007/ops";
    public static readonly XNamespace Ncx = "http://www.daisy.org/z3986/2005/ncx/";
    public static readonly XNamespace Container = "urn:oasis:names:tc:opendocument:xmlns:container";

    public static async Task<Document> LoadAsync(string sample)
    {
        if (sample == "welcome")
        {
            return await SampleDocuments.CreateWelcomeAsync();
        }

        var path = SamplePath(sample);
        var importer = new MarkdownImporter();
        await using var fs = File.OpenRead(path);
        return await importer.ImportAsync(new ImportContext { Input = fs, FileName = sample, SourcePath = path, Options = OptionValues.FromDefaults(importer.Options) });
    }

    public static async Task<Document> FromMarkdownAsync(string markdown)
    {
        var importer = new MarkdownImporter();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(markdown));
        return await importer.ImportAsync(new ImportContext { Input = stream, FileName = "test.md", Options = OptionValues.FromDefaults(importer.Options) });
    }

    public static async Task<EpubPackageView> ExportAsync(Document document, PdfTheme? theme = null, Action<OptionValues>? configure = null, EpubExporter? exporter = null)
    {
        exporter ??= new EpubExporter(new SyntaxHighlighter(), null, null);
        var options = OptionValues.FromDefaults(exporter.Options);
        configure?.Invoke(options);
        var result = await exporter.ExportAsync(new ExportContext { Document = document, BaseName = "book", Options = options, Theme = theme ?? BuiltInThemes.Clean() });
        var file = Assert.Single(result.Files);
        Assert.Equal("book.epub", file.FileName);
        Assert.Equal("application/epub+zip", file.MediaType);
        return new EpubPackageView(file.Content, result.Warnings);
    }

    private static string SamplePath(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "samples", name)))
        {
            dir = dir.Parent;
        }

        return dir is null ? throw new FileNotFoundException($"samples/{name} not found above the test output folder.") : Path.Combine(dir.FullName, "samples", name);
    }

    public static XDocument ParseXml(string xml)
    {
        // The XHTML documents start with <!DOCTYPE html>; Ignore skips it without processing any DTD or resolving anything.
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(xml), settings);
        return XDocument.Load(reader);
    }

    /// <summary>Resolves <paramref name="href"/> (relative, optionally with a fragment) against the folder of <paramref name="fromPath"/>.</summary>
    public static (string Path, string? Fragment) Resolve(string fromPath, string href)
    {
        var hash = href.IndexOf('#', StringComparison.Ordinal);
        var file = hash >= 0 ? href[..hash] : href;
        var fragment = hash >= 0 ? Uri.UnescapeDataString(href[(hash + 1)..]) : null;
        if (file.Length == 0)
        {
            return (fromPath, fragment);
        }

        var parts = fromPath.Split('/').SkipLast(1).ToList();
        foreach (var segment in Uri.UnescapeDataString(file).Split('/'))
        {
            if (segment == "..")
            {
                parts.RemoveAt(parts.Count - 1);
            }
            else if (segment != ".")
            {
                parts.Add(segment);
            }
        }

        return (string.Join('/', parts), fragment);
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$")]
    public static partial Regex ModifiedFormat();
}

/// <summary>An exported EPUB opened for inspection.</summary>
internal sealed class EpubPackageView
{
    public EpubPackageView(byte[] bytes, List<string> warnings)
    {
        Bytes = bytes;
        Warnings = warnings;
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            using var stream = entry.Open();
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            Entries.Add(entry.FullName, ms.ToArray());
            EntryOrder.Add(entry.FullName);
            if (entry.FullName == "mimetype")
            {
                MimetypeCompressedLength = entry.CompressedLength;
            }
        }

        Opf = EpubExportTestHelpers.ParseXml(Text("OEBPS/content.opf"));
        foreach (var item in Opf.Root!.Element(EpubExportTestHelpers.Opf + "manifest")!.Elements(EpubExportTestHelpers.Opf + "item"))
        {
            Manifest.Add(new ManifestItem((string)item.Attribute("id")!, "OEBPS/" + (string)item.Attribute("href")!, (string)item.Attribute("media-type")!, (string?)item.Attribute("properties")));
        }

        Spine = Opf.Root.Element(EpubExportTestHelpers.Opf + "spine")!.Elements(EpubExportTestHelpers.Opf + "itemref").Select(e => (string)e.Attribute("idref")!).ToList();
    }

    public sealed record ManifestItem(string Id, string Path, string MediaType, string? Properties);

    public byte[] Bytes { get; }
    public List<string> Warnings { get; }
    public Dictionary<string, byte[]> Entries { get; } = new(StringComparer.Ordinal);
    public List<string> EntryOrder { get; } = [];
    public long MimetypeCompressedLength { get; }
    public XDocument Opf { get; }
    public List<ManifestItem> Manifest { get; } = [];
    public List<string> Spine { get; }

    public string Text(string path) => Encoding.UTF8.GetString(Entries[path]);

    public IEnumerable<string> XhtmlPaths => Entries.Keys.Where(k => k.EndsWith(".xhtml", StringComparison.Ordinal));

    public List<ManifestItem> Chapters => Spine.Select(id => Manifest.Single(m => m.Id == id)).Where(m => m.Path.Contains("/text/chapter-", StringComparison.Ordinal)).ToList();

    public string Css => Text("OEBPS/css/style.css");

    public ManifestItem Item(string id) => Manifest.Single(m => m.Id == id);
}
