using System.Diagnostics;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Themes;
using Microsoft.Extensions.Logging;

namespace AllInOnePdf.Core.Conversion;

/// <summary>Everything needed to run one conversion.</summary>
public sealed class ConversionRequest
{
    /// <summary>Absolute path of the source file. Either this or <see cref="SourceContent"/> must be set.</summary>
    public string? SourcePath { get; init; }

    /// <summary>In-memory source (browser uploads). Requires <see cref="SourceFileName"/>.</summary>
    public byte[]? SourceContent { get; init; }

    public string? SourceFileName { get; init; }

    public required string TargetFormatId { get; init; }

    public OptionValues ImportOptions { get; init; } = OptionValues.Empty;

    public OptionValues ExportOptions { get; init; } = OptionValues.Empty;

    /// <summary>Theme id for themed exporters; null = default theme.</summary>
    public string? ThemeId { get; init; }

    /// <summary>Inline theme (takes precedence over <see cref="ThemeId"/>); used by the theme builder's live preview.</summary>
    public PdfTheme? Theme { get; init; }

    /// <summary>When set, outputs are written here (file names are sanitised). When null, outputs stay in memory.</summary>
    public string? OutputDirectory { get; init; }

    /// <summary>Optional override of the output base name (without extension).</summary>
    public string? OutputBaseName { get; init; }

    /// <summary>Overwrite existing files instead of appending " (2)".</summary>
    public bool Overwrite { get; init; }

    public string ResolvedFileName => SourceFileName ?? Path.GetFileName(SourcePath ?? "untitled");
}

public sealed class ConversionResult
{
    public required bool Success { get; init; }
    public required TimeSpan Elapsed { get; init; }
    public Document? Document { get; init; }
    public IReadOnlyList<OutputFile> Outputs { get; init; } = [];
    /// <summary>Absolute paths of files written to disk (same order as <see cref="Outputs"/>) when an output directory was given.</summary>
    public IReadOnlyList<string> WrittenPaths { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public string? Error { get; init; }
    public Exception? Exception { get; init; }

    public string? PrimaryPath => WrittenPaths.Count > 0 ? WrittenPaths[0] : null;

    /// <summary>The main output file (first primary), when the conversion succeeded.</summary>
    public OutputFile? Primary => Outputs.FirstOrDefault(o => o.IsPrimary) ?? (Outputs.Count > 0 ? Outputs[0] : null);
}

public interface IConversionService
{
    Task<ConversionResult> ConvertAsync(ConversionRequest request, IProgress<ConversionProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Import only (used by the Reader to open a file without converting it).</summary>
    Task<Document> ImportAsync(string sourcePath, OptionValues? options = null, IProgress<ConversionProgress>? progress = null, CancellationToken cancellationToken = default);

    Task<Document> ImportAsync(Stream content, string fileName, OptionValues? options = null, IProgress<ConversionProgress>? progress = null, CancellationToken cancellationToken = default);
}

public sealed class ConversionService : IConversionService
{
    private readonly FormatRegistry _registry;
    private readonly IThemeStore _themes;
    private readonly ILogger<ConversionService> _logger;

    public ConversionService(FormatRegistry registry, IThemeStore themes, ILogger<ConversionService> logger)
    {
        _registry = registry;
        _themes = themes;
        _logger = logger;
    }

    public async Task<Document> ImportAsync(string sourcePath, OptionValues? options = null, IProgress<ConversionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(sourcePath);
        await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        return await ImportCoreAsync(stream, Path.GetFileName(fullPath), fullPath, options, progress, cancellationToken);
    }

    public Task<Document> ImportAsync(Stream content, string fileName, OptionValues? options = null, IProgress<ConversionProgress>? progress = null, CancellationToken cancellationToken = default) =>
        ImportCoreAsync(content, fileName, null, options, progress, cancellationToken);

    private async Task<Document> ImportCoreAsync(Stream stream, string fileName, string? sourcePath, OptionValues? options, IProgress<ConversionProgress>? progress, CancellationToken ct)
    {
        var importer = _registry.FindImporter(fileName)
            ?? throw new NotSupportedException($"No importer can read '{fileName}'.");
        if (importer.Status == ConverterStatus.Planned)
        {
            throw new NotSupportedException(importer.StatusMessage ?? $"Reading {importer.Format.DisplayName} files is not available yet.");
        }

        var context = new ImportContext
        {
            Input = stream,
            FileName = fileName,
            SourcePath = sourcePath,
            Options = (options ?? OptionValues.Empty).WithDefaults(importer.Options),
            Progress = progress,
        };

        var document = await importer.ImportAsync(context, ct);
        document.Metadata.SourcePath ??= sourcePath;
        document.Metadata.SourceFormat ??= importer.Format.Id;
        document.Metadata.Title ??= Path.GetFileNameWithoutExtension(fileName);
        document.ImportWarnings.AddRange(context.Warnings.Where(w => !string.IsNullOrWhiteSpace(w)));
        return document;
    }

    public async Task<ConversionResult> ConvertAsync(ConversionRequest request, IProgress<ConversionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        Document? document = null;
        try
        {
            var fileName = request.ResolvedFileName;
            var path = _registry.Resolve(fileName, request.TargetFormatId)
                ?? throw new NotSupportedException($"Cannot convert '{fileName}' to {request.TargetFormatId}.");

            if (path.Exporter.Status == ConverterStatus.Planned)
            {
                throw new NotSupportedException(path.Exporter.StatusMessage ?? $"Writing {path.Exporter.Format.DisplayName} is not available yet.");
            }

            progress?.Report(new ConversionProgress(ConversionStage.Importing, 0, "Reading " + fileName));

            if (request.SourceContent is not null)
            {
                using var ms = new MemoryStream(request.SourceContent, writable: false);
                document = await ImportCoreAsync(ms, fileName, null, request.ImportOptions, progress, cancellationToken);
            }
            else if (!string.IsNullOrEmpty(request.SourcePath))
            {
                document = await ImportAsync(request.SourcePath, request.ImportOptions, progress, cancellationToken);
            }
            else
            {
                throw new ArgumentException("Either SourcePath or SourceContent must be provided.", nameof(request));
            }

            cancellationToken.ThrowIfCancellationRequested();

            PdfTheme? theme = null;
            if (path.Exporter.SupportsTheme)
            {
                theme = request.Theme ?? await _themes.GetAsync(request.ThemeId, cancellationToken) ?? await _themes.GetDefaultAsync(cancellationToken);
            }

            progress?.Report(new ConversionProgress(ConversionStage.Exporting, 0, "Writing " + path.Exporter.Format.DisplayName));

            var baseName = SanitizeBaseName(request.OutputBaseName ?? Path.GetFileNameWithoutExtension(fileName));
            var exportContext = new ExportContext
            {
                Document = document,
                BaseName = baseName,
                Options = request.ExportOptions.WithDefaults(path.Exporter.Options),
                Theme = theme,
                Progress = progress,
            };

            var export = await path.Exporter.ExportAsync(exportContext, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var written = new List<string>();
            if (!string.IsNullOrEmpty(request.OutputDirectory))
            {
                progress?.Report(new ConversionProgress(ConversionStage.Writing, 0, "Saving"));
                var outDir = Path.GetFullPath(request.OutputDirectory);
                Directory.CreateDirectory(outDir);
                var i = 0;
                foreach (var file in export.Files)
                {
                    var target = ResolveOutputPath(outDir, file.FileName, request.Overwrite);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await File.WriteAllBytesAsync(target, file.Content, cancellationToken);
                    written.Add(target);
                    progress?.Report(new ConversionProgress(ConversionStage.Writing, (double)++i / export.Files.Count));
                }
            }

            progress?.Report(new ConversionProgress(ConversionStage.Completed, 1, "Done"));
            return new ConversionResult
            {
                Success = true,
                Elapsed = stopwatch.Elapsed,
                Document = document,
                Outputs = export.Files,
                WrittenPaths = written,
                Warnings = [.. document.ImportWarnings, .. export.Warnings],
            };
        }
        catch (OperationCanceledException)
        {
            progress?.Report(new ConversionProgress(ConversionStage.Cancelled, 1, "Cancelled"));
            return new ConversionResult { Success = false, Elapsed = stopwatch.Elapsed, Document = document, Error = "Cancelled" };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Conversion of {File} to {Target} failed", request.ResolvedFileName, request.TargetFormatId);
            progress?.Report(new ConversionProgress(ConversionStage.Failed, 1, ex.Message));
            return new ConversionResult { Success = false, Elapsed = stopwatch.Elapsed, Document = document, Error = ex.Message, Exception = ex };
        }
    }

    /// <summary>Removes path separators and invalid characters so an output name can never escape the output directory.</summary>
    public static string SanitizeBaseName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Trim().Select(c => invalid.Contains(c) || c == '/' || c == '\\' ? '_' : c).ToArray();
        var cleaned = new string(chars).Trim('.', ' ');
        return string.IsNullOrEmpty(cleaned) ? "document" : cleaned;
    }

    /// <summary>
    /// Builds a safe absolute path inside <paramref name="directory"/>, de-duplicating with " (n)" unless overwriting.
    /// Relative sub-folders in <paramref name="fileName"/> (e.g. "notes_files/image-001.png") are kept, with every
    /// segment sanitised; absolute paths and ".." segments are reduced to the file name.
    /// </summary>
    public static string ResolveOutputPath(string directory, string fileName, bool overwrite)
    {
        var normalized = fileName.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var hasUnsafeSegment = Path.IsPathRooted(fileName) || segments.Any(s => s is "." or "..");
        var folders = hasUnsafeSegment || segments.Length < 2
            ? []
            : segments.Take(segments.Length - 1).Select(SanitizeBaseName).ToArray();

        var safeName = Path.GetFileName(normalized);
        var ext = Path.GetExtension(safeName);
        var stem = SanitizeBaseName(Path.GetFileNameWithoutExtension(safeName));
        var targetDir = folders.Length == 0 ? directory : Path.Combine([directory, .. folders]);
        var candidate = Path.Combine(targetDir, stem + ext);

        if (!overwrite)
        {
            var n = 2;
            while (File.Exists(candidate))
            {
                candidate = Path.Combine(targetDir, $"{stem} ({n++}){ext}");
            }
        }

        var full = Path.GetFullPath(candidate);
        var dirFull = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(dirFull, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Output path escaped the output directory.");
        }

        return full;
    }
}
