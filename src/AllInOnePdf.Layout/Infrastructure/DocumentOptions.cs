namespace AllInOnePdf.Layout.Infrastructure;

/// <summary>PDF information dictionary values.</summary>
public sealed class DocumentMetadata
{
    public string? Title { get; set; }
    public string? Author { get; set; }
    public string? Subject { get; set; }
    public string? Keywords { get; set; }
    public string? Creator { get; set; } = "AllInOne PDF";
    public string? Producer { get; set; } = "AllInOne PDF layout engine";
    public string? Language { get; set; }
    public DateTime CreationDate { get; set; } = DateTime.Now;
    public DateTime ModifiedDate { get; set; } = DateTime.Now;
}

/// <summary>JPEG quality used when raster images are re-encoded into the PDF.</summary>
public enum ImageCompressionQuality
{
    Best,
    VeryHigh,
    High,
    Medium,
    Low,
    VeryLow,
}

/// <summary>Reading direction. Only left-to-right is implemented.</summary>
public enum ContentDirection
{
    LeftToRight,
    RightToLeft,
}

/// <summary>Output settings.</summary>
public sealed class DocumentSettings
{
    public ImageCompressionQuality ImageCompressionQuality { get; set; } = ImageCompressionQuality.High;

    /// <summary>Resolution at which vector content that must be rasterised (SVG with effects) is sampled.</summary>
    public int ImageRasterDpi { get; set; } = 220;

    public ContentDirection ContentDirection { get; set; } = ContentDirection.LeftToRight;

    /// <summary>Flate-compress page streams (always on for shipped files; off makes debugging easier).</summary>
    public bool CompressDocument { get; set; } = true;
}

/// <summary>Thrown when content cannot be laid out: an element that may not break is taller than a page, or layout makes no progress.</summary>
public sealed class DocumentLayoutException : Exception
{
    public DocumentLayoutException()
    {
    }

    public DocumentLayoutException(string message)
        : base(message)
    {
    }

    public DocumentLayoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Thrown when a compose callback (the fluent lambdas) throws; the original exception is the inner one.</summary>
public sealed class DocumentComposeException : Exception
{
    public DocumentComposeException()
    {
    }

    public DocumentComposeException(string message)
        : base(message)
    {
    }

    public DocumentComposeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
