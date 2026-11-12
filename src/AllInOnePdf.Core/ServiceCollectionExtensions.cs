using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Exporters;
using AllInOnePdf.Core.Exporters.Images;
using AllInOnePdf.Core.Highlighting;
using AllInOnePdf.Core.Importers;
using AllInOnePdf.Core.Rendering;
using AllInOnePdf.Core.Storage;
using AllInOnePdf.Core.Themes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AllInOnePdf.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the document model services: importers, exporters, PDF rendering, themes, history.
    /// Hosts call this once; add more <see cref="IImporter"/>/<see cref="IExporter"/> registrations to extend the format matrix.
    /// </summary>
    public static IServiceCollection AddAllInOnePdfCore(this IServiceCollection services, string? dataDirectory = null)
    {
        services.TryAddSingleton(_ =>
        {
            var paths = dataDirectory is null ? new AppPaths() : new AppPaths(dataDirectory);
            paths.EnsureCreated();
            return paths;
        });

        services.TryAddSingleton<IThemeStore, FileThemeStore>();
        services.TryAddSingleton<IConversionHistory, FileConversionHistory>();
        services.TryAddSingleton<IDraftStore, FileDraftStore>();
        services.TryAddSingleton<IPdfRasterizer, PdfiumRasterizer>();
        services.TryAddSingleton<ISyntaxHighlighter, SyntaxHighlighter>();
        services.TryAddSingleton<IDocumentHtmlRenderer>(sp => new DocumentHtmlRenderer(sp.GetService<ISyntaxHighlighter>()));
        services.TryAddSingleton<IPdfRenderer>(sp => new PdfRenderer(sp.GetService<ISyntaxHighlighter>()));

        services.AddSingleton<IImporter, MarkdownImporter>();
        services.AddSingleton<IImporter, PlainTextImporter>();
        services.AddSingleton<IImporter, PdfImporter>();
        services.AddSingleton<IImporter, HtmlImporter>();
        services.AddSingleton<IImporter, EpubImporter>();
        services.AddSingleton<IImporter, ImageImporter>();
        services.AddSingleton<IImporter, CsvImporter>();
        services.AddSingleton<IImporter, JsonImporter>();
        services.AddSingleton<IImporter, SourceCodeImporter>();

        services.AddSingleton<IExporter, PdfExporter>();
        services.AddSingleton<IExporter, PlainTextExporter>();
        services.AddSingleton<IExporter, MarkdownExporter>();
        services.AddSingleton<IExporter>(sp => new HtmlExporter(sp.GetService<ISyntaxHighlighter>()));
        services.AddSingleton<IExporter>(sp => new EpubExporter(sp.GetService<ISyntaxHighlighter>(), sp.GetService<IPdfRenderer>(), sp.GetService<IPdfRasterizer>()));
        services.AddSingleton<IExporter>(sp => new PngPagesExporter(sp.GetRequiredService<IPdfRenderer>(), sp.GetRequiredService<IPdfRasterizer>()));
        services.AddSingleton<IExporter>(sp => new JpegPagesExporter(sp.GetRequiredService<IPdfRenderer>(), sp.GetRequiredService<IPdfRasterizer>()));
        services.AddSingleton<IExporter>(sp => new WebpPagesExporter(sp.GetRequiredService<IPdfRenderer>(), sp.GetRequiredService<IPdfRasterizer>()));

        services.AddSingleton<IImporter, DocxImporter>();
        services.AddSingleton<IExporter>(sp => new DocxExporter(sp.GetService<ISyntaxHighlighter>()));
        services.AddSingleton<IImporter, XlsxImporter>();
        services.AddSingleton<IExporter, XlsxExporter>();
        services.AddSingleton<IImporter, PptxImporter>();
        services.AddSingleton<IExporter, PptxExporter>();
        services.AddSingleton<IImporter, RtfImporter>();

        services.TryAddSingleton<FormatRegistry>();
        services.TryAddSingleton<IConversionService, ConversionService>();
        services.TryAddSingleton<ConversionQueue>();
        return services;
    }
}
