using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Themes;

namespace AllInOnePdf.Core.Conversion;

/// <summary>Availability of a converter. The UI shows unavailable converters greyed out with the reason.</summary>
public enum ConverterStatus
{
    Ready,
    /// <summary>Works, but output fidelity is limited; the UI shows a "preview" badge.</summary>
    Preview,
    /// <summary>Declared so it appears in the format matrix, but not implemented yet.</summary>
    Planned,
}

/// <summary>Reads a file into the neutral <see cref="Document"/> model.</summary>
public interface IImporter
{
    FileFormat Format { get; }

    ConverterStatus Status => ConverterStatus.Ready;

    /// <summary>Shown to the user when <see cref="Status"/> is not <see cref="ConverterStatus.Ready"/>.</summary>
    string? StatusMessage => null;

    IReadOnlyList<OptionDefinition> Options { get; }

    /// <summary>Quick check used to pick an importer for a file; defaults to an extension match.</summary>
    bool CanImport(string fileName) => Format.Matches(fileName);

    Task<Document> ImportAsync(ImportContext context, CancellationToken cancellationToken = default);
}

/// <summary>Writes the neutral <see cref="Document"/> model to a file format.</summary>
public interface IExporter
{
    FileFormat Format { get; }

    ConverterStatus Status => ConverterStatus.Ready;

    string? StatusMessage => null;

    IReadOnlyList<OptionDefinition> Options { get; }

    /// <summary>True when the exporter honours a <see cref="PdfTheme"/> (the UI then shows the theme picker).</summary>
    bool SupportsTheme => false;

    Task<ExportResult> ExportAsync(ExportContext context, CancellationToken cancellationToken = default);
}

public sealed class ImportContext
{
    public required Stream Input { get; init; }

    /// <summary>File name including extension (no directory).</summary>
    public required string FileName { get; init; }

    /// <summary>Absolute path when the input came from disk; lets importers resolve relative images.</summary>
    public string? SourcePath { get; init; }

    public OptionValues Options { get; init; } = OptionValues.Empty;

    public IProgress<ConversionProgress>? Progress { get; init; }

    /// <summary>Non-fatal notes for the user ("3 images skipped"); surfaced in <see cref="ConversionResult.Warnings"/>.</summary>
    public List<string> Warnings { get; } = [];

    public string? SourceDirectory => SourcePath is null ? null : Path.GetDirectoryName(SourcePath);

    public void Report(double fraction, string? message = null) =>
        Progress?.Report(new ConversionProgress(ConversionStage.Importing, fraction, message));
}

public sealed class ExportContext
{
    public required Document Document { get; init; }

    /// <summary>Suggested base name for outputs, without extension.</summary>
    public required string BaseName { get; init; }

    public OptionValues Options { get; init; } = OptionValues.Empty;

    /// <summary>Theme for themed exporters (PDF). Null means "use the default theme".</summary>
    public PdfTheme? Theme { get; init; }

    public IProgress<ConversionProgress>? Progress { get; init; }

    public void Report(double fraction, string? message = null) =>
        Progress?.Report(new ConversionProgress(ConversionStage.Exporting, fraction, message));
}

/// <summary>One produced file. Exporters may return several (e.g. Markdown plus extracted images).</summary>
public sealed record OutputFile(string FileName, byte[] Content, string MediaType, bool IsPrimary = true)
{
    public long Length => Content.LongLength;
}

public sealed class ExportResult
{
    public List<OutputFile> Files { get; } = [];

    /// <summary>Non-fatal notes for the user ("3 images could not be extracted").</summary>
    public List<string> Warnings { get; } = [];

    public OutputFile? Primary => Files.FirstOrDefault(f => f.IsPrimary) ?? Files.FirstOrDefault();

    public static ExportResult Single(string fileName, byte[] content, string mediaType)
    {
        var result = new ExportResult();
        result.Files.Add(new OutputFile(fileName, content, mediaType));
        return result;
    }
}

public enum ConversionStage
{
    Queued,
    Importing,
    Rendering,
    Exporting,
    Writing,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>Progress report; <paramref name="Fraction"/> is 0..1 within the stage.</summary>
public readonly record struct ConversionProgress(ConversionStage Stage, double Fraction, string? Message = null)
{
    /// <summary>Overall 0..1 estimate across stages (import 0-0.4, render/export 0.4-0.9, write 0.9-1).</summary>
    public double Overall => Stage switch
    {
        ConversionStage.Queued => 0,
        ConversionStage.Importing => 0.4 * Math.Clamp(Fraction, 0, 1),
        ConversionStage.Rendering => 0.4 + 0.5 * Math.Clamp(Fraction, 0, 1),
        ConversionStage.Exporting => 0.4 + 0.5 * Math.Clamp(Fraction, 0, 1),
        ConversionStage.Writing => 0.9 + 0.1 * Math.Clamp(Fraction, 0, 1),
        _ => 1,
    };
}
