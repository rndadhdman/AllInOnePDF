using System.Collections.Concurrent;
using AllInOnePdf.Core.Rendering;
using AllInOnePdf.Publishing.Model;
using AllInOnePdf.Publishing.Rendering;
using AllInOnePdf.Publishing.Storage;
using AllInOnePdf.Publishing.Templates;
using Microsoft.Extensions.DependencyInjection;

namespace AllInOnePdf.UI.Services;

/// <summary>Thrown when no <see cref="IPublicationRenderer"/> is registered (the renderer ships separately).</summary>
public sealed class CreatorRendererMissingException : InvalidOperationException
{
    /// <summary>Creates the exception with the standard message.</summary>
    public CreatorRendererMissingException()
        : base("The PDF renderer is not available in this build.")
    {
    }

    /// <summary>Creates the exception with a custom message.</summary>
    public CreatorRendererMissingException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a custom message and cause.</summary>
    public CreatorRendererMissingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Page-1 thumbnails for the Creator gallery (singleton, shared by every circuit / window) and the renderer access
/// the Creator uses for PDFs. Template thumbnails are rendered once with placeholders; saved publications use the
/// thumbnail stored in their package. Every render runs on the thread pool, at most two at once, and honours
/// cancellation. When the publication renderer is not registered every method degrades to "no thumbnail".
/// </summary>
public sealed class CreatorThumbnailCache : IDisposable
{
    /// <summary>Resolution of the thumbnail saved into packages.</summary>
    public const int PackageThumbnailDpi = 48;

    private const int TemplateDpi = 56;

    private readonly IServiceProvider _services;
    private readonly IPdfRasterizer _rasterizer;
    private readonly IPublicationStore _store;
    private readonly SemaphoreSlim _gate = new(2, 2);
    private readonly ConcurrentDictionary<string, string> _templates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (long Stamp, string? Url)> _publications = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (long Stamp, string? Url)> _drafts = new(StringComparer.Ordinal);

    /// <summary>Creates the cache.</summary>
    public CreatorThumbnailCache(IServiceProvider services, IPdfRasterizer rasterizer, IPublicationStore store)
    {
        _services = services;
        _rasterizer = rasterizer;
        _store = store;
    }

    /// <summary>True when an <see cref="IPublicationRenderer"/> is registered.</summary>
    public bool RendererAvailable => _services.GetService<IPublicationRenderer>() is not null;

    /// <summary>Renders a publication to PDF bytes off the UI thread.</summary>
    /// <exception cref="CreatorRendererMissingException">No renderer is registered.</exception>
    public async Task<byte[]> RenderPdfAsync(Publication publication, PublicationRenderOptions? options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publication);
        var renderer = _services.GetService<IPublicationRenderer>() ?? throw new CreatorRendererMissingException();
        return await Task.Run(() => renderer.RenderAsync(publication, options, cancellationToken), cancellationToken);
    }

    /// <summary>Renders a publication off the UI thread and reports where the PDF put every section, row, column and module.</summary>
    /// <exception cref="CreatorRendererMissingException">No renderer is registered.</exception>
    public async Task<PublicationRenderResult> RenderWithPagesAsync(Publication publication, PublicationRenderOptions? options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publication);
        var renderer = _services.GetService<IPublicationRenderer>() ?? throw new CreatorRendererMissingException();
        return await Task.Run(() => renderer.RenderWithPagesAsync(publication, options, cancellationToken), cancellationToken);
    }

    /// <summary>Renders page 1 as a PNG for the package, or null when rendering is not possible.</summary>
    public async Task<byte[]?> TryRenderThumbnailPngAsync(Publication publication, CancellationToken cancellationToken = default)
    {
        try
        {
            var pdf = await RenderPdfAsync(publication, new PublicationRenderOptions { Draft = true, ShowPlaceholders = true, EmbedMetadata = false }, cancellationToken);
            var image = await _rasterizer.RasterizePageAsync(pdf, 0, new RasterizeOptions
            {
                Dpi = PackageThumbnailDpi,
                Encoding = ImageEncoding.Png,
                WithAnnotations = false,
            }, cancellationToken);
            return image.Data;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // A missing or failing renderer must never block saving; the gallery shows a placeholder instead.
            return null;
        }
    }

    /// <summary>The cached thumbnail of a template, if rendered already.</summary>
    public bool TryGetTemplate(string templateId, out string? dataUrl)
    {
        var found = _templates.TryGetValue(templateId, out var url);
        dataUrl = url;
        return found;
    }

    /// <summary>The template's page 1 with placeholders as a data URL, or null when it cannot be rendered.</summary>
    public async Task<string?> GetTemplateAsync(PublicationTemplate template, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (_templates.TryGetValue(template.Id, out var cached))
        {
            return cached;
        }

        if (!RendererAvailable)
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_templates.TryGetValue(template.Id, out cached))
            {
                return cached;
            }

            var pdf = await RenderPdfAsync(template.Create(), new PublicationRenderOptions { Draft = true, ShowPlaceholders = true, EmbedMetadata = false }, cancellationToken);
            var image = await _rasterizer.RasterizePageAsync(pdf, 0, new RasterizeOptions
            {
                Dpi = TemplateDpi,
                Encoding = ImageEncoding.Jpeg,
                JpegQuality = 82,
                WithAnnotations = false,
            }, cancellationToken);
            var url = image.ToDataUrl();
            _templates[template.Id] = url;
            return url;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The cached thumbnail of a saved publication when it is current for <paramref name="summary"/>.</summary>
    public bool TryGetPublication(PublicationSummary summary, out string? dataUrl)
    {
        ArgumentNullException.ThrowIfNull(summary);
        if (_publications.TryGetValue(summary.Id, out var entry) && entry.Stamp == summary.Modified.UtcTicks)
        {
            dataUrl = entry.Url;
            return true;
        }

        dataUrl = null;
        return false;
    }

    /// <summary>The thumbnail stored in the publication's package as a data URL, or null when it has none.</summary>
    public async Task<string?> GetPublicationAsync(PublicationSummary summary, CancellationToken cancellationToken = default)
    {
        if (TryGetPublication(summary, out var cached))
        {
            return cached;
        }

        string? url = null;
        try
        {
            var png = await _store.GetThumbnailAsync(summary.Id, cancellationToken);
            if (png is { Length: > 0 })
            {
                url = "data:image/png;base64," + Convert.ToBase64String(png);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            url = null;
        }

        _publications[summary.Id] = (summary.Modified.UtcTicks, url);
        return url;
    }

    /// <summary>The cached thumbnail of a draft when it is current for <paramref name="written"/>.</summary>
    public bool TryGetDraft(string key, DateTimeOffset written, out string? dataUrl)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (_drafts.TryGetValue(key, out var entry) && entry.Stamp == written.UtcTicks)
        {
            dataUrl = entry.Url;
            return true;
        }

        dataUrl = null;
        return false;
    }

    /// <summary>
    /// Page 1 of a draft's publication (with placeholders) as a data URL, cached per draft key and write time; null
    /// when it cannot be rendered. <paramref name="read"/> loads the publication and is only called on a cache miss.
    /// </summary>
    public async Task<string?> GetDraftAsync(string key, DateTimeOffset written, Func<CancellationToken, Task<Publication?>> read, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (TryGetDraft(key, written, out var cached))
        {
            return cached;
        }

        string? url = null;
        if (RendererAvailable && await read(cancellationToken) is { } publication)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                var pdf = await RenderPdfAsync(publication, new PublicationRenderOptions { Draft = true, ShowPlaceholders = true, EmbedMetadata = false }, cancellationToken);
                var image = await _rasterizer.RasterizePageAsync(pdf, 0, new RasterizeOptions
                {
                    Dpi = TemplateDpi,
                    Encoding = ImageEncoding.Jpeg,
                    JpegQuality = 82,
                    WithAnnotations = false,
                }, cancellationToken);
                url = image.ToDataUrl();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                url = null;
            }
            finally
            {
                _gate.Release();
            }
        }

        _drafts[key] = (written.UtcTicks, url);
        return url;
    }

    /// <summary>Forgets the cached thumbnail of a publication (after a save or delete).</summary>
    public void InvalidatePublication(string id) => _publications.TryRemove(id, out _);

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();
}
