using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace AllInOnePdf.Core.Exporters.Epub;

/// <summary>Collects package items and writes the OCF zip: <c>mimetype</c>, <c>META-INF/container.xml</c>, <c>OEBPS/*</c>.</summary>
internal sealed class EpubPackage
{
    public const string MimeType = "application/epub+zip";
    public const string OpfPath = "OEBPS/content.opf";
    public const string ContentRoot = "OEBPS/";

    public List<EpubItem> Items { get; } = [];

    /// <summary>Spine in reading order (item ids).</summary>
    public List<string> Spine { get; } = [];

    public string? CoverPageHref { get; set; }
    public string? CoverImageId { get; set; }
    public bool NavInSpine { get; set; }
    public string FirstChapterHref { get; set; } = string.Empty;

    public void Add(EpubItem item)
    {
        if (Items.Any(i => string.Equals(i.Href, item.Href, StringComparison.OrdinalIgnoreCase) || string.Equals(i.Id, item.Id, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"Duplicate EPUB item {item.Id} ({item.Href}).");
        }

        Items.Add(item);
    }

    public string BuildOpf(EpubBookInfo info)
    {
        var sb = new StringBuilder(4096);
        var lang = EpubXml.Escape(info.Language);
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        sb.Append("<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"book-id\" xml:lang=\"").Append(lang).Append("\">\n");
        sb.Append("<metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:opf=\"http://www.idpf.org/2007/opf\">\n");
        sb.Append("<dc:identifier id=\"book-id\">").Append(EpubXml.Escape(info.Identifier)).Append("</dc:identifier>\n");
        sb.Append("<dc:title id=\"title\">").Append(EpubXml.Escape(info.Title)).Append("</dc:title>\n");
        if (!string.IsNullOrWhiteSpace(info.Subtitle))
        {
            // EPUB 3 title refinement: readers (and our importer) tell the main title from the subtitle. EPUB 2
            // readers take the first dc:title, which stays the main title.
            sb.Append("<meta refines=\"#title\" property=\"title-type\">main</meta>\n");
            sb.Append("<dc:title id=\"subtitle\">").Append(EpubXml.Escape(info.Subtitle)).Append("</dc:title>\n");
            sb.Append("<meta refines=\"#subtitle\" property=\"title-type\">subtitle</meta>\n");
        }

        for (var i = 0; i < info.Authors.Count; i++)
        {
            var id = "creator-" + (i + 1).ToString(CultureInfo.InvariantCulture);
            sb.Append("<dc:creator id=\"").Append(id).Append("\">").Append(EpubXml.Escape(info.Authors[i])).Append("</dc:creator>\n");
            sb.Append("<meta refines=\"#").Append(id).Append("\" property=\"role\" scheme=\"marc:relators\">aut</meta>\n");
        }

        sb.Append("<dc:language>").Append(lang).Append("</dc:language>\n");
        sb.Append("<dc:date>").Append((info.Date ?? info.Modified).ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append("</dc:date>\n");
        if (!string.IsNullOrWhiteSpace(info.Description))
        {
            sb.Append("<dc:description>").Append(EpubXml.Escape(info.Description)).Append("</dc:description>\n");
        }

        foreach (var keyword in info.Keywords)
        {
            sb.Append("<dc:subject>").Append(EpubXml.Escape(keyword)).Append("</dc:subject>\n");
        }

        sb.Append("<meta property=\"dcterms:modified\">").Append(FormatModified(info.Modified)).Append("</meta>\n");
        if (CoverImageId is not null)
        {
            sb.Append("<meta name=\"cover\" content=\"").Append(EpubXml.Escape(CoverImageId)).Append("\" />\n");
        }

        sb.Append("<meta name=\"generator\" content=\"AllInOne PDF\" />\n");
        sb.Append("</metadata>\n<manifest>\n");
        foreach (var item in Items)
        {
            sb.Append("<item id=\"").Append(EpubXml.Escape(item.Id)).Append("\" href=\"").Append(EpubXml.Escape(item.Href))
              .Append("\" media-type=\"").Append(EpubXml.Escape(item.MediaType)).Append('"');
            if (!string.IsNullOrEmpty(item.Properties))
            {
                sb.Append(" properties=\"").Append(EpubXml.Escape(item.Properties)).Append('"');
            }

            sb.Append(" />\n");
        }

        sb.Append("</manifest>\n<spine toc=\"ncx\">\n");
        foreach (var idref in Spine)
        {
            sb.Append("<itemref idref=\"").Append(EpubXml.Escape(idref)).Append("\" />\n");
        }

        sb.Append("</spine>\n<guide>\n");
        if (CoverPageHref is not null)
        {
            sb.Append("<reference type=\"cover\" title=\"Cover\" href=\"").Append(EpubXml.Escape(CoverPageHref)).Append("\" />\n");
        }

        if (NavInSpine)
        {
            sb.Append("<reference type=\"toc\" title=\"Contents\" href=\"").Append(EpubNavigation.NavHref).Append("\" />\n");
        }

        sb.Append("<reference type=\"text\" title=\"Start\" href=\"").Append(EpubXml.Escape(FirstChapterHref)).Append("\" />\n");
        sb.Append("</guide>\n</package>\n");
        return sb.ToString();
    }

    public byte[] Write(EpubBookInfo info)
    {
        var opf = BuildOpf(info);
        var stamp = info.Modified.Year < 1980 ? new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero) : info.Modified;
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            // OCF: "mimetype" must be the first entry, stored (no compression, no extra field).
            WriteEntry(zip, "mimetype", Encoding.ASCII.GetBytes(MimeType), CompressionLevel.NoCompression, stamp);
            WriteEntry(zip, "META-INF/container.xml", EpubXml.Utf8.GetBytes(ContainerXml), CompressionLevel.Optimal, stamp);
            WriteEntry(zip, OpfPath, EpubXml.Utf8.GetBytes(opf), CompressionLevel.Optimal, stamp);
            foreach (var item in Items)
            {
                var level = item.MediaType.StartsWith("image/", StringComparison.Ordinal) && item.MediaType != "image/svg+xml"
                    ? CompressionLevel.NoCompression
                    : CompressionLevel.Optimal;
                WriteEntry(zip, ContentRoot + item.Href, item.Data, level, stamp);
            }
        }

        return ms.ToArray();
    }

    /// <summary>W3C date-time in UTC without fractional seconds, as <c>dcterms:modified</c> requires.</summary>
    public static string FormatModified(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static void WriteEntry(ZipArchive zip, string path, byte[] data, CompressionLevel level, DateTimeOffset stamp)
    {
        var entry = zip.CreateEntry(path, level);
        entry.LastWriteTime = stamp;
        using var stream = entry.Open();
        stream.Write(data);
    }

    private const string ContainerXml =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
        "<container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\">\n" +
        "<rootfiles>\n<rootfile full-path=\"" + OpfPath + "\" media-type=\"application/oebps-package+xml\" />\n</rootfiles>\n" +
        "</container>\n";
}
