using AllInOnePdf.Core.Fonts;
using AllInOnePdf.Core.Rendering;
using AllInOnePdf.Publishing.Model;
using AllInOnePdf.Layout.Infrastructure;

namespace AllInOnePdf.Publishing.Rendering;

/// <summary>
/// Lays a publication out as one layout engine document: sections, rows and columns become boxes and rows, modules are drawn
/// through Core's <see cref="ThemedComposer"/> so text looks exactly like the app's other PDFs. Pure .NET, in process.
/// </summary>
public sealed class PublicationRenderer : IPublicationRenderer
{
    private readonly ISyntaxHighlighter? _highlighter;

    /// <summary>Creates a renderer; <paramref name="highlighter"/> colours code modules when the theme enables it.</summary>
    public PublicationRenderer(ISyntaxHighlighter? highlighter = null)
    {
        _highlighter = highlighter;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Runs on the thread pool. <paramref name="ct"/> is checked between sections (and by the composer between blocks);
    /// a cancelled render throws <see cref="OperationCanceledException"/>.
    /// </remarks>
    public async Task<byte[]> RenderAsync(Publication publication, PublicationRenderOptions? options = null, CancellationToken ct = default) =>
        (await RenderWithPagesAsync(publication, options, ct)).Pdf;

    /// <inheritdoc />
    public Task<PublicationRenderResult> RenderWithPagesAsync(Publication publication, PublicationRenderOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(publication);
        var opt = options ?? new PublicationRenderOptions();
        return Task.Run(() => Render(publication, opt, ct), ct);
    }

    private PublicationRenderResult Render(Publication publication, PublicationRenderOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        BundledFonts.EnsureRegistered();
        try
        {
            return Generate(publication, options, keepRowsTogether: true, ct);
        }
        catch (DocumentLayoutException) when (!ct.IsCancellationRequested)
        {
            // A kept-together row the estimate got wrong (taller than a page) cannot be placed: lay out again and let
            // every row flow. A second failure is a real problem and propagates.
            return Generate(publication, options, keepRowsTogether: false, ct);
        }
    }

    private PublicationRenderResult Generate(Publication publication, PublicationRenderOptions options, bool keepRowsTogether, CancellationToken ct)
    {
        try
        {
            var result = new PublicationLayout(publication, options, _highlighter, keepRowsTogether, ct).Generate();
            ct.ThrowIfCancellationRequested();
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not DocumentLayoutException && FindCancellation(ex) is { } cancelled)
        {
            // The layout engine may wrap exceptions thrown while composing; surface cancellation as itself.
            throw cancelled;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ct.IsCancellationRequested)
        {
            // A cancelled token can also surface as a layout or drawing error part-way through; the caller asked
            // for cancellation, so that is what it gets.
            throw new OperationCanceledException("The render was cancelled.", ex, ct);
        }
    }

    private static OperationCanceledException? FindCancellation(Exception ex)
    {
        for (var e = ex.InnerException; e is not null; e = e.InnerException)
        {
            if (e is OperationCanceledException oce)
            {
                return oce;
            }
        }

        return null;
    }
}
