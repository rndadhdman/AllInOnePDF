using System.Text.RegularExpressions;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Exporters;
using AllInOnePdf.Core.Highlighting;
using AllInOnePdf.Core.Importers;
using AllInOnePdf.Core.Themes;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;

namespace AllInOnePdf.Core.Tests;

/// <summary>Word export/import shortcuts, validation and the normalisation used to compare round trips.</summary>
internal static partial class DocxTestHelpers
{
    public static async Task<byte[]> ExportAsync(Document document, PdfTheme? theme = null, params (string Key, object Value)[] options)
    {
        var exporter = new DocxExporter(new SyntaxHighlighter());
        var values = new OptionValues();
        foreach (var (key, value) in options)
        {
            values.Set(key, value);
        }

        var result = await exporter.ExportAsync(new ExportContext
        {
            Document = document,
            BaseName = "doc",
            Theme = theme,
            Options = values.WithDefaults(exporter.Options),
        });
        Assert.Equal("doc.docx", result.Primary!.FileName);
        return result.Primary.Content;
    }

    public static async Task<Document> ImportAsync(byte[] docx, Action<OptionValues>? configure = null, List<string>? warnings = null)
    {
        var importer = new DocxImporter();
        var options = OptionValues.FromDefaults(importer.Options);
        configure?.Invoke(options);
        using var stream = new MemoryStream(docx);
        var context = new ImportContext { Input = stream, FileName = "test.docx", Options = options };
        var doc = await importer.ImportAsync(context);
        warnings?.AddRange(context.Warnings);
        return doc;
    }

    public static async Task<Document> LoadSampleAsync(string sample) => sample == "welcome"
        ? await SampleDocuments.CreateWelcomeAsync()
        : await MarkdownExportTestHelpers.ImportAsync(await File.ReadAllTextAsync(MarkdownExportTestHelpers.FindSample(sample)));

    /// <summary>Schema and semantic errors reported by the Open XML validator (Office 2019 rules).</summary>
    public static List<string> Validate(byte[] docx)
    {
        using var stream = new MemoryStream(docx);
        using var package = WordprocessingDocument.Open(stream, false);
        var validator = new OpenXmlValidator(FileFormatVersions.Office2019);
        return validator.Validate(package)
            .Select(e => $"{e.ErrorType} {e.Part?.Uri} {e.Path?.XPath}: {e.Description}")
            .ToList();
    }

    /// <summary>
    /// <see cref="MarkdownExportTestHelpers.Dump"/> with the documented round-trip tolerances applied: embedded
    /// resources get new ids, and Word has no notion of loose vs tight lists (every imported list is tight).
    /// </summary>
    public static string Normalized(Document document)
    {
        var dump = MarkdownExportTestHelpers.Dump(document);
        dump = ResourceId().Replace(dump, "res:*");
        return dump.Replace(" loose\n", " tight\n", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"res:[0-9a-zA-Z_-]+")]
    private static partial Regex ResourceId();

    /// <summary>A 4×3 red PNG (valid zlib stream, no decoder needed to read its header).</summary>
    public static byte[] TinyPng(int width = 4, int height = 3)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        void Chunk(string type, byte[] data)
        {
            var len = BitConverter.GetBytes(data.Length);
            Array.Reverse(len);
            ms.Write(len);
            var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
            ms.Write(typeBytes);
            ms.Write(data);
            var crc = Crc32([.. typeBytes, .. data]);
            var crcBytes = BitConverter.GetBytes(crc);
            Array.Reverse(crcBytes);
            ms.Write(crcBytes);
        }

        var ihdr = new byte[13];
        var w = BitConverter.GetBytes(width);
        var h = BitConverter.GetBytes(height);
        Array.Reverse(w);
        Array.Reverse(h);
        w.CopyTo(ihdr, 0);
        h.CopyTo(ihdr, 4);
        ihdr[8] = 8;
        ihdr[9] = 2;
        Chunk("IHDR", ihdr);

        var raw = new List<byte>();
        for (var y = 0; y < height; y++)
        {
            raw.Add(0);
            for (var x = 0; x < width; x++)
            {
                raw.AddRange([255, 0, 0]);
            }
        }

        using var z = new MemoryStream();
        using (var deflate = new System.IO.Compression.ZLibStream(z, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write([.. raw]);
        }

        Chunk("IDAT", z.ToArray());
        Chunk("IEND", []);
        return ms.ToArray();
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }
}
