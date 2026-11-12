using System.Xml;
using System.Xml.Linq;
using AllInOnePdf.Core.Importers.Html;
using AngleSharp.Dom;

namespace AllInOnePdf.Core.Importers.Epub;

internal sealed record EpubManifestItem(string Id, string Path, string MediaType, string Properties)
{
    public bool IsXhtml => MediaType is "application/xhtml+xml" or "text/html" or "application/xml" || Path.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase) || Path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || Path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase);

    public bool HasProperty(string property) => Properties.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(property, StringComparer.Ordinal);
}

/// <summary>The parts of an EPUB package (OPF) the importer needs: metadata, manifest, spine, navigation titles and the cover.</summary>
internal sealed partial class EpubPackage
{
    private const string DcNamespace = "http://purl.org/dc/elements/1.1/";

    /// <summary>Encryption algorithms that only obfuscate embedded fonts (not DRM); such books open normally.</summary>
    private static readonly HashSet<string> FontObfuscation = new(StringComparer.Ordinal)
    {
        "http://www.idpf.org/2008/embedding",
        "http://ns.adobe.com/pdf/enc#RC",
    };

    public string OpfPath { get; private init; } = string.Empty;
    public string? Title { get; private set; }
    public string? Subtitle { get; private set; }
    public List<string> Creators { get; } = [];
    public string? Language { get; private set; }
    public string? Description { get; private set; }
    public string? Date { get; private set; }
    public List<string> Subjects { get; } = [];
    public Dictionary<string, EpubManifestItem> Manifest { get; } = new(StringComparer.Ordinal);
    public List<EpubManifestItem> Spine { get; } = [];

    /// <summary>Navigation titles keyed by archive path (first entry for each file wins).</summary>
    public Dictionary<string, string> NavTitles { get; } = new(StringComparer.Ordinal);

    public string? NavPath { get; private set; }
    public string? CoverImagePath { get; private set; }
    public string? CoverPagePath { get; private set; }

    public static void EnsureNotEncrypted(EpubArchive archive)
    {
        if (archive.Contains("META-INF/rights.xml"))
        {
            throw DrmError();
        }

        var encryption = archive.ReadText("META-INF/encryption.xml", 8L * 1024 * 1024);
        if (encryption is null)
        {
            return;
        }

        try
        {
            var doc = LoadXml(encryption);
            var algorithms = doc.Descendants().Where(e => e.Name.LocalName == "EncryptionMethod").Select(e => (string?)e.Attribute("Algorithm") ?? string.Empty).ToList();
            if (algorithms.Count == 0 || algorithms.Any(a => !FontObfuscation.Contains(a)))
            {
                throw DrmError();
            }
        }
        catch (XmlException)
        {
            throw DrmError();
        }
    }

    private static NotSupportedException DrmError() =>
        new("This EPUB is DRM-protected (its content is encrypted). Only DRM-free EPUB files can be converted.");

    public static EpubPackage Load(EpubArchive archive)
    {
        var container = archive.ReadText("META-INF/container.xml", 1024 * 1024);
        string? opfPath = null;
        if (container is not null)
        {
            var doc = LoadXml(container);
            opfPath = doc.Descendants().Where(e => e.Name.LocalName == "rootfile")
                .Select(e => ((string?)e.Attribute("full-path"), (string?)e.Attribute("media-type")))
                .Where(r => !string.IsNullOrWhiteSpace(r.Item1))
                .OrderBy(r => string.Equals(r.Item2, "application/oebps-package+xml", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .Select(r => r.Item1!.Trim())
                .FirstOrDefault();
        }

        opfPath ??= archive.EntryNames.FirstOrDefault(n => n.EndsWith(".opf", StringComparison.OrdinalIgnoreCase));
        if (opfPath is null || !EpubArchive.IsSafeEntryName(opfPath))
        {
            throw new InvalidDataException("This file is not a valid EPUB: META-INF/container.xml does not point to a package document.");
        }

        var opfText = archive.ReadText(opfPath, 16L * 1024 * 1024)
            ?? throw new InvalidDataException($"This EPUB is incomplete: the package document '{opfPath}' is missing.");
        var package = new EpubPackage { OpfPath = opfPath };
        package.Parse(LoadXml(opfText));
        package.LoadNavigation(archive);
        return package;
    }

    internal static XDocument LoadXml(string xml)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            MaxCharactersFromEntities = 1024,
            MaxCharactersInDocument = 64L * 1024 * 1024,
            IgnoreComments = true,
        };
        using var reader = XmlReader.Create(new StringReader(xml.TrimStart('﻿')), settings);
        return XDocument.Load(reader);
    }

    private void Parse(XDocument opf)
    {
        var metadata = opf.Descendants().FirstOrDefault(e => e.Name.LocalName == "metadata");
        if (metadata is not null)
        {
            IEnumerable<XElement> Dc(string name) => metadata.Descendants().Where(e => e.Name.LocalName == name && e.Name.NamespaceName == DcNamespace);
            string? Text(XElement e) => string.IsNullOrWhiteSpace(e.Value) ? null : HtmlDocumentConverter.CollapseText(e.Value).Trim();

            // EPUB 3 refines titles with <meta refines="#id" property="title-type">subtitle</meta>.
            var refinements = metadata.Elements().Where(e => e.Name.LocalName == "meta" && (string?)e.Attribute("property") == "title-type")
                .GroupBy(e => ((string?)e.Attribute("refines") ?? string.Empty).TrimStart('#'), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Value.Trim(), StringComparer.Ordinal);
            foreach (var title in Dc("title"))
            {
                var text = Text(title);
                if (text is null)
                {
                    continue;
                }

                var type = refinements.GetValueOrDefault((string?)title.Attribute("id") ?? string.Empty);
                if (string.Equals(type, "subtitle", StringComparison.OrdinalIgnoreCase))
                {
                    Subtitle ??= text;
                }
                else
                {
                    Title ??= text;
                }
            }

            Creators.AddRange(Dc("creator").Select(Text).OfType<string>().Distinct(StringComparer.Ordinal));
            Language = Dc("language").Select(Text).FirstOrDefault(t => t is not null);
            Description = Dc("description").Select(e => Text(e) is { } t ? StripTags(t) : null).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
            Date = Dc("date").Select(Text).FirstOrDefault(t => t is not null);
            Subjects.AddRange(Dc("subject").Select(Text).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase));

            var coverId = metadata.Elements().Where(e => e.Name.LocalName == "meta" && (string?)e.Attribute("name") == "cover").Select(e => (string?)e.Attribute("content")).FirstOrDefault();
            if (coverId is not null)
            {
                CoverImageId = coverId;
            }
        }

        foreach (var item in opf.Descendants().Where(e => e.Name.LocalName == "item"))
        {
            var id = (string?)item.Attribute("id");
            var href = (string?)item.Attribute("href");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(href))
            {
                continue;
            }

            var path = EpubArchive.ResolvePath(OpfPath, href);
            if (path is null)
            {
                continue;
            }

            Manifest.TryAdd(id, new EpubManifestItem(id, path, ((string?)item.Attribute("media-type") ?? string.Empty).Trim().ToLowerInvariant(), (string?)item.Attribute("properties") ?? string.Empty));
        }

        var spine = opf.Descendants().FirstOrDefault(e => e.Name.LocalName == "spine");
        if (spine is not null)
        {
            foreach (var itemref in spine.Elements().Where(e => e.Name.LocalName == "itemref"))
            {
                var idref = (string?)itemref.Attribute("idref");
                if (idref is not null && Manifest.TryGetValue(idref, out var item) && item.IsXhtml)
                {
                    Spine.Add(item);
                }
            }

            var tocId = (string?)spine.Attribute("toc");
            if (tocId is not null && Manifest.TryGetValue(tocId, out var ncx))
            {
                NcxPath = ncx.Path;
            }
        }

        NcxPath ??= Manifest.Values.FirstOrDefault(i => i.MediaType == "application/x-dtbncx+xml")?.Path;
        NavPath = Manifest.Values.FirstOrDefault(i => i.HasProperty("nav"))?.Path;

        CoverImagePath = Manifest.Values.FirstOrDefault(i => i.HasProperty("cover-image"))?.Path
            ?? (CoverImageId is not null && Manifest.TryGetValue(CoverImageId, out var coverItem) ? coverItem.Path : null);

        var guideCover = opf.Descendants().Where(e => e.Name.LocalName == "reference" && string.Equals((string?)e.Attribute("type"), "cover", StringComparison.OrdinalIgnoreCase))
            .Select(e => (string?)e.Attribute("href")).FirstOrDefault(h => h is not null);
        CoverPagePath = guideCover is null ? null : EpubArchive.ResolvePath(OpfPath, guideCover);
        if (CoverPagePath is null && Spine.Count > 0)
        {
            var first = Spine[0];
            if (first.Id.Contains("cover", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(first.Path).Contains("cover", StringComparison.OrdinalIgnoreCase) || first.HasProperty("calibre:title-page"))
            {
                CoverPagePath = first.Path;
            }
        }
    }

    private string? CoverImageId { get; set; }

    private string? NcxPath { get; set; }

    private void LoadNavigation(EpubArchive archive)
    {
        if (NavPath is not null && archive.ReadText(NavPath, 8L * 1024 * 1024) is { } navText)
        {
            var dom = HtmlDocumentConverter.CreateParser().ParseDocument(HtmlDocumentConverter.FixSelfClosingTags(navText));
            var navs = dom.QuerySelectorAll("nav").ToList();
            var toc = navs.FirstOrDefault(n => (n.GetAttribute("epub:type") ?? string.Empty).Contains("toc", StringComparison.Ordinal)) ?? navs.FirstOrDefault();
            foreach (var a in toc?.QuerySelectorAll("a[href]") ?? Enumerable.Empty<IElement>())
            {
                AddNavTitle(NavPath, a.GetAttribute("href")!, a.TextContent);
            }
        }

        if (NavTitles.Count == 0 && NcxPath is not null && archive.ReadText(NcxPath, 8L * 1024 * 1024) is { } ncxText)
        {
            try
            {
                var ncx = LoadXml(ncxText);
                foreach (var point in ncx.Descendants().Where(e => e.Name.LocalName == "navPoint"))
                {
                    var label = point.Elements().FirstOrDefault(e => e.Name.LocalName == "navLabel")?.Value;
                    var src = (string?)point.Elements().FirstOrDefault(e => e.Name.LocalName == "content")?.Attribute("src");
                    if (label is not null && src is not null)
                    {
                        AddNavTitle(NcxPath, src, label);
                    }
                }
            }
            catch (XmlException)
            {
                // A broken NCX only costs us chapter titles.
            }
        }
    }

    private void AddNavTitle(string fromPath, string href, string text)
    {
        var path = EpubArchive.ResolvePath(fromPath, href);
        var title = HtmlDocumentConverter.CollapseText(text).Trim();
        if (path is not null && title.Length > 0)
        {
            NavTitles.TryAdd(path, title);
        }
    }

    private static string StripTags(string text) => HtmlDocumentConverter.CollapseText(TagPattern().Replace(text, " ")).Trim();

    [System.Text.RegularExpressions.GeneratedRegex("<[^>]+>")]
    private static partial System.Text.RegularExpressions.Regex TagPattern();
}
