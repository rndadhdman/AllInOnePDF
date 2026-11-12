using System.Text;
using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Exporters;
using AllInOnePdf.Core.Highlighting;
using AllInOnePdf.Core.Importers;
using AllInOnePdf.Core.Themes;

namespace AllInOnePdf.Core.Tests;

/// <summary>Shortcuts for HTML import/export tests.</summary>
internal static class HtmlTestHelpers
{
    public static async Task<(Document Document, List<string> Warnings)> ImportHtmlAsync(string html, string? sourcePath = null, string fileName = "page.html", params (string Key, object Value)[] options)
    {
        var importer = new HtmlImporter();
        var values = new OptionValues();
        foreach (var (key, value) in options)
        {
            values.Set(key, value);
        }

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(html));
        var context = new ImportContext
        {
            Input = stream,
            FileName = fileName,
            SourcePath = sourcePath,
            Options = values.WithDefaults(importer.Options),
        };
        var document = await importer.ImportAsync(context);
        return (document, context.Warnings);
    }

    public static async Task<ExportResult> ExportHtmlAsync(Document document, PdfTheme? theme = null, string baseName = "doc", params (string Key, object Value)[] options)
    {
        var exporter = new HtmlExporter(new SyntaxHighlighter());
        var values = new OptionValues();
        foreach (var (key, value) in options)
        {
            values.Set(key, value);
        }

        return await exporter.ExportAsync(new ExportContext
        {
            Document = document,
            BaseName = baseName,
            Options = values.WithDefaults(exporter.Options),
            Theme = theme,
        });
    }

    public static async Task<string> ToHtmlAsync(Document document, PdfTheme? theme = null, params (string Key, object Value)[] options)
    {
        var result = await ExportHtmlAsync(document, theme, "doc", options);
        return Encoding.UTF8.GetString(result.Primary!.Content);
    }

    /// <summary>A 1x1 transparent PNG.</summary>
    public static byte[] TinyPng { get; } = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
}
