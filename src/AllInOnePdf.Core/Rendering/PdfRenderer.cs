using AllInOnePdf.Core.Conversion;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Themes;
using AllInOnePdf.Layout.Fluent;
using AllInOnePdf.Layout.Infrastructure;
using Document = AllInOnePdf.Core.Documents.Document;
using QDocument = AllInOnePdf.Layout.Fluent.Document;
using QMetadata = AllInOnePdf.Layout.Infrastructure.DocumentMetadata;

namespace AllInOnePdf.Core.Rendering;

/// <summary>
/// Pure .NET PDF renderer built on the app's layout engine (<c>AllInOnePdf.Layout</c>). Lays a whole document out as cover, contents, body and footnotes; every
/// themed drawing is done by <see cref="ThemedComposer"/>. No browser, no external processes.
/// </summary>
public sealed class PdfRenderer : IPdfRenderer
{
    private readonly ISyntaxHighlighter? _highlighter;

    /// <summary>Creates a renderer; <paramref name="highlighter"/> colours code blocks when the theme enables it.</summary>
    public PdfRenderer(ISyntaxHighlighter? highlighter = null)
    {
        _highlighter = highlighter;
    }

    /// <inheritdoc />
    public Task<byte[]> RenderAsync(Document document, PdfTheme theme, PdfRenderOptions? options = null, IProgress<ConversionProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new ConversionProgress(ConversionStage.Rendering, 0.05, "Laying out pages"));
            var bytes = Render(document, theme, options ?? new PdfRenderOptions(), cancellationToken);
            progress?.Report(new ConversionProgress(ConversionStage.Rendering, 1, "PDF ready"));
            return bytes;
        }, cancellationToken);

    private byte[] Render(Document doc, PdfTheme t, PdfRenderOptions opt, CancellationToken ct)
    {
        var composer = new ThemedComposer(t, doc, _highlighter, new ThemedComposerOptions
        {
            Draft = opt.Draft,
            CancellationToken = ct,
            FallbackTitle = opt.FallbackTitle,
        });
        composer.RegisterHeadings(doc.Headings);

        var includeCover = opt.IncludeCover ?? t.Cover.Enabled;
        var tocEntries = doc.Headings.Where(h => h.Level <= t.Toc.Depth).ToList();
        var includeToc = (opt.IncludeToc ?? t.Toc.Enabled) && tocEntries.Count > 0;

        var pdf = QDocument.Create(container =>
        {
            if (includeCover)
            {
                container.Page(page =>
                {
                    composer.ConfigurePage(page, isCover: true);

                    // Header/Footer.ShowOnCover: the cover normally stays clean; themes can opt in.
                    if (t.Header.HasContent && t.Header.ShowOnCover)
                    {
                        page.Header().Element(composer.ComposeHeader);
                    }

                    if (t.Footer.HasContent && t.Footer.ShowOnCover)
                    {
                        page.Footer().Element(composer.ComposeFooter);
                    }

                    page.Content().Element(composer.ComposeCover);
                });
            }

            container.Page(page =>
            {
                composer.ConfigurePage(page);

                if (t.Header.HasContent)
                {
                    var header = t.Header.ShowOnFirstPage ? page.Header() : page.Header().SkipOnce();
                    header.Element(composer.ComposeHeader);
                }

                if (t.Footer.HasContent)
                {
                    var footer = t.Footer.ShowOnFirstPage ? page.Footer() : page.Footer().SkipOnce();
                    footer.Element(composer.ComposeFooter);
                }

                page.Content().Column(col =>
                {
                    col.Spacing(0);
                    if (includeToc)
                    {
                        composer.ComposeToc(col.Item(), tocEntries);
                        if (t.Toc.PageBreakAfter)
                        {
                            col.Item().PageBreak();
                        }
                        else
                        {
                            col.Item().Height((float)t.Headings.H1.SpaceBeforePt);
                        }
                    }

                    composer.ComposeBlocks(col, doc.Blocks);

                    if (doc.Footnotes.Count > 0)
                    {
                        composer.ComposeFootnotes(col.Item());
                    }
                });
            });
        });

        if (opt.EmbedMetadata)
        {
            pdf = pdf.WithMetadata(new QMetadata
            {
                Title = composer.Title,
                Author = string.Join(", ", doc.Metadata.Authors),
                Subject = doc.Metadata.Description ?? doc.Metadata.Subtitle ?? string.Empty,
                Keywords = string.Join(", ", doc.Metadata.Keywords),
                Creator = "AllInOne PDF",
                Producer = "AllInOne PDF",
                Language = doc.Metadata.Language ?? "en",
                CreationDate = DateTime.Now,
                ModifiedDate = DateTime.Now,
            });
        }

        return pdf.WithSettings(composer.CreateDocumentSettings(opt.ImageQuality)).GeneratePdf();
    }
}
