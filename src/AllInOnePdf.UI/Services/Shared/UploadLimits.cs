namespace AllInOnePdf.UI.Services.Shared;

/// <summary>Size limits for everything the user can hand the app (uploads, drops, imports, images).</summary>
public static class UploadLimits
{
    private const long MB = 1024L * 1024;

    /// <summary>Largest single file accepted by the Convert page.</summary>
    public const long MaxUploadBytes = 256 * MB;

    /// <summary>Largest document the Reader opens from an upload.</summary>
    public const long MaxDocumentBytes = 100 * MB;

    /// <summary>Largest theme JSON accepted by an import.</summary>
    public const long MaxThemeJsonBytes = 2 * MB;

    /// <summary>Largest single image inlined into a rendered document.</summary>
    public const long MaxImageBytes = 20 * MB;

    /// <summary>Total of all local images inlined into one rendered document.</summary>
    public const long MaxTotalImagesBytes = 64 * MB;

    /// <summary>Total size of all uploaded sources one Convert workspace (one browser tab or window) may hold.</summary>
    public const long MaxWorkspaceUploadBytes = 1024 * MB;

    /// <summary>Uploaded sources kept in memory per workspace; beyond this every new upload is spooled to disk.</summary>
    public const long MaxWorkspaceMemoryBytes = 256 * MB;

    /// <summary>Uploads larger than this are spooled to a temporary file instead of being held in memory.</summary>
    public const long SpoolThresholdBytes = 32 * MB;

    /// <summary>"256 MB" style label for a limit.</summary>
    public static string Describe(long bytes) => $"{bytes / MB} MB";
}
