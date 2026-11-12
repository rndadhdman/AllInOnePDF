namespace AllInOnePdf.Core.Conversion;

public enum FormatCategory
{
    Document,
    Markup,
    Text,
    Image,
    Data,
    Presentation,
    Spreadsheet,
    Ebook,
    Code,
    Other,
}

/// <summary>Identifies a file format the app can read or write.</summary>
public sealed record FileFormat(
    string Id,
    string DisplayName,
    IReadOnlyList<string> Extensions,
    string MediaType,
    FormatCategory Category,
    string? Description = null)
{
    /// <summary>Primary extension including the dot, e.g. ".pdf".</summary>
    public string PrimaryExtension => Extensions[0];

    public bool Matches(string fileNameOrExtension)
    {
        var ext = Path.GetExtension(fileNameOrExtension);
        if (string.IsNullOrEmpty(ext))
        {
            ext = fileNameOrExtension;
        }

        if (!ext.StartsWith('.'))
        {
            ext = "." + ext;
        }

        return Extensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Well-known formats. Importers/exporters reference these so ids stay consistent across the app.</summary>
public static class KnownFormats
{
    public static readonly FileFormat Pdf = new("pdf", "PDF", [".pdf"], "application/pdf", FormatCategory.Document, "Portable Document Format");
    public static readonly FileFormat Markdown = new("markdown", "Markdown", [".md", ".markdown", ".mdown", ".mkd"], "text/markdown", FormatCategory.Markup, "CommonMark / GitHub-flavoured Markdown");
    public static readonly FileFormat PlainText = new("text", "Plain text", [".txt", ".text", ".log"], "text/plain", FormatCategory.Text, "Unformatted text");
    public static readonly FileFormat Html = new("html", "HTML", [".html", ".htm", ".xhtml"], "text/html", FormatCategory.Markup, "Web page");
    public static readonly FileFormat Docx = new("docx", "Word document", [".docx"], "application/vnd.openxmlformats-officedocument.wordprocessingml.document", FormatCategory.Document, "Microsoft Word (Open XML)");
    public static readonly FileFormat Png = new("png", "PNG image", [".png"], "image/png", FormatCategory.Image, "One image per page");
    public static readonly FileFormat Jpeg = new("jpeg", "JPEG image", [".jpg", ".jpeg"], "image/jpeg", FormatCategory.Image, "One image per page");
    public static readonly FileFormat WebP = new("webp", "WebP image", [".webp"], "image/webp", FormatCategory.Image, "One image per page");
    public static readonly FileFormat Gif = new("gif", "GIF image", [".gif"], "image/gif", FormatCategory.Image);
    public static readonly FileFormat Bmp = new("bmp", "Bitmap image", [".bmp"], "image/bmp", FormatCategory.Image);
    public static readonly FileFormat Image = new("image", "Image", [".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp"], "image/*", FormatCategory.Image, "Pictures placed one per page");
    public static readonly FileFormat Csv = new("csv", "CSV", [".csv"], "text/csv", FormatCategory.Data, "Comma-separated values");
    public static readonly FileFormat Json = new("json", "JSON", [".json"], "application/json", FormatCategory.Data);
    public static readonly FileFormat Epub = new("epub", "EPUB", [".epub"], "application/epub+zip", FormatCategory.Ebook);
    public static readonly FileFormat Rtf = new("rtf", "Rich Text", [".rtf"], "application/rtf", FormatCategory.Document);
    public static readonly FileFormat Xlsx = new("xlsx", "Excel workbook", [".xlsx"], "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", FormatCategory.Spreadsheet);
    public static readonly FileFormat Pptx = new("pptx", "PowerPoint", [".pptx"], "application/vnd.openxmlformats-officedocument.presentationml.presentation", FormatCategory.Presentation);
    public static readonly FileFormat SourceCode = new("code", "Source code", [".cs", ".js", ".mjs", ".ts", ".tsx", ".jsx", ".py", ".java", ".go", ".rs", ".cpp", ".cc", ".c", ".h", ".hpp", ".css", ".scss", ".xml", ".yaml", ".yml", ".toml", ".ini", ".sh", ".ps1", ".sql", ".rb", ".php", ".kt", ".swift", ".diff", ".patch"], "text/plain", FormatCategory.Code, "Rendered with syntax highlighting");

    public static IReadOnlyList<FileFormat> All { get; } =
    [
        Pdf, Markdown, PlainText, Html, Docx, Epub, Png, Jpeg, WebP, Gif, Bmp, Image, Csv, Json, Rtf, Xlsx, Pptx, SourceCode,
    ];

    public static FileFormat? FindByExtension(string fileNameOrExtension) =>
        All.FirstOrDefault(f => f.Matches(fileNameOrExtension));

    public static FileFormat? FindById(string id) =>
        All.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));
}
