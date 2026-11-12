using System.Globalization;
using System.Text;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Rendering;
using AllInOnePdf.Core.Themes;

namespace AllInOnePdf.Core.Exporters.Epub;

/// <summary>The title page (<c>text/cover.xhtml</c>) and the optional rendered cover image.</summary>
internal static class EpubCover
{
    public const string PageHref = "text/cover.xhtml";
    public const string ImageHref = "images/cover.jpg";
    public const int ImageDpi = 150;

    public static string BuildPage(EpubBookInfo info, PdfTheme theme, string language)
    {
        var cv = theme.Cover;
        var title = string.IsNullOrWhiteSpace(cv.TitleOverride) ? info.Title : cv.TitleOverride.Trim();
        var sb = new StringBuilder(1024);
        sb.Append("<section class=\"aio-cover aio-cover-").Append(cv.Style.ToString().ToLowerInvariant()).Append("\" epub:type=\"titlepage\">\n");
        if (cv.AccentBar)
        {
            sb.Append("<div class=\"aio-cover-accent\"></div>\n");
        }

        if (cv.ShowTitle)
        {
            sb.Append("<h1 class=\"aio-cover-title\">").Append(EpubXml.Escape(title)).Append("</h1>\n");
        }

        if (cv.ShowSubtitle && !string.IsNullOrWhiteSpace(info.Subtitle))
        {
            sb.Append("<p class=\"aio-cover-subtitle\">").Append(EpubXml.Escape(info.Subtitle)).Append("</p>\n");
        }

        if (cv.ShowAuthor && info.Authors.Count > 0)
        {
            sb.Append("<p class=\"aio-cover-author\">").Append(EpubXml.Escape(string.Join(", ", info.Authors))).Append("</p>\n");
        }

        if (cv.ShowDate && info.Date is { } date)
        {
            sb.Append("<p class=\"aio-cover-date\">").Append(EpubXml.Escape(date.ToString("d MMMM yyyy", CultureInfo.InvariantCulture))).Append("</p>\n");
        }

        if (!string.IsNullOrWhiteSpace(cv.Note))
        {
            sb.Append("<p class=\"aio-cover-note\">").Append(EpubXml.Escape(cv.Note)).Append("</p>\n");
        }

        sb.Append("</section>");
        return EpubXml.XhtmlDocument(language, title, "../css/style.css", sb.ToString(), "epub:type=\"cover\"");
    }

    /// <summary>
    /// Renders only the PDF cover page (a one-block document carrying the metadata) and rasterises it to JPEG.
    /// Returns null, with a warning, when rendering fails.
    /// </summary>
    public static async Task<byte[]?> RenderImageAsync(EpubBookInfo info, PdfTheme theme, IPdfRenderer renderer, IPdfRasterizer rasterizer, List<string> warnings, CancellationToken cancellationToken)
    {
        try
        {
            var doc = new Document
            {
                Metadata =
                {
                    Title = info.Title,
                    Subtitle = info.Subtitle,
                    Date = info.Date,
                    Language = info.Language,
                },
            };
            doc.Metadata.Authors.AddRange(info.Authors);
            doc.Blocks.Add(new ParagraphBlock(info.Title));

            var coverTheme = theme.Clone();
            coverTheme.Cover.Enabled = true;
            coverTheme.Toc.Enabled = false;
            var pdf = await renderer.RenderAsync(doc, coverTheme, new PdfRenderOptions
            {
                IncludeCover = true,
                IncludeToc = false,
                EmbedMetadata = false,
                FallbackTitle = info.Title,
            }, null, cancellationToken).ConfigureAwait(false);

            var image = await rasterizer.RasterizePageAsync(pdf, 0, new RasterizeOptions
            {
                Dpi = ImageDpi,
                Encoding = ImageEncoding.Jpeg,
                JpegQuality = 88,
                WithAnnotations = false,
            }, cancellationToken).ConfigureAwait(false);
            return image.Data;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            warnings.Add("The cover image could not be rendered (" + ex.Message + "); the book has a text title page only.");
            return null;
        }
    }
}

/// <summary>Book-level metadata after options and fallbacks are applied.</summary>
internal sealed class EpubBookInfo
{
    public required string Identifier { get; init; }
    public required string Title { get; init; }
    public string? Subtitle { get; init; }
    public List<string> Authors { get; init; } = [];
    public DateTimeOffset? Date { get; init; }
    public string? Description { get; init; }
    public List<string> Keywords { get; init; } = [];
    public required string Language { get; init; }
    public required DateTimeOffset Modified { get; init; }
}
