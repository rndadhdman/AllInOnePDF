using AllInOnePdf.Layout.Infrastructure;
using SkiaSharp;

namespace AllInOnePdf.Layout.Drawing;

/// <summary>Where the page loop draws its pages: the PDF writer, or bitmaps in tests.</summary>
internal interface IPageSink
{
    /// <summary>Starts a page of the given size in points and returns its canvas.</summary>
    SKCanvas BeginPage(float width, float height);

    /// <summary>Finishes the page started last.</summary>
    void EndPage();

    /// <summary>Finishes the output (called before the fonts the pages drew with are released).</summary>
    void Close();
}

/// <summary>Writes pages into a PDF through Skia's PDF backend (<see cref="SKDocument"/>).</summary>
internal sealed class PdfDocumentWriter : IPageSink, IDisposable
{
    private readonly SKDocument _document;
    private bool _pageOpen;
    private bool _closed;

    public PdfDocumentWriter(Stream output, DocumentMetadata metadata, DocumentSettings settings)
    {
        var pdf = new SKDocumentPdfMetadata
        {
            Title = metadata.Title ?? string.Empty,
            Author = metadata.Author ?? string.Empty,
            Subject = metadata.Subject ?? string.Empty,
            Keywords = metadata.Keywords ?? string.Empty,
            Creator = metadata.Creator ?? string.Empty,
            Producer = metadata.Producer ?? string.Empty,
            Creation = metadata.CreationDate,
            Modified = metadata.ModifiedDate,
            EncodingQuality = Quality(settings.ImageCompressionQuality),
            RasterDpi = Math.Clamp(settings.ImageRasterDpi, 36, 2400),
        };

        _document = SKDocument.CreatePdf(output, pdf)
            ?? throw new InvalidOperationException("Skia could not create a PDF document.");
    }

    /// <summary>The JPEG quality Skia re-encodes raster images with.</summary>
    public static int Quality(ImageCompressionQuality quality) => quality switch
    {
        ImageCompressionQuality.Best => 100,
        ImageCompressionQuality.VeryHigh => 95,
        ImageCompressionQuality.High => 90,
        ImageCompressionQuality.Medium => 80,
        ImageCompressionQuality.Low => 65,
        ImageCompressionQuality.VeryLow => 40,
        _ => 90,
    };

    public SKCanvas BeginPage(float width, float height)
    {
        _pageOpen = true;
        return _document.BeginPage(width, height);
    }

    public void EndPage()
    {
        if (_pageOpen)
        {
            _document.EndPage();
            _pageOpen = false;
        }
    }

    /// <summary>Finishes the file (the stream is complete afterwards).</summary>
    public void Close()
    {
        EndPage();
        if (!_closed)
        {
            _document.Close();
            _closed = true;
        }
    }

    public void Dispose()
    {
        if (!_closed)
        {
            _document.Abort();
            _closed = true;
        }

        _document.Dispose();
    }
}
