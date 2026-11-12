namespace AllInOnePdf.Core.Conversion;

/// <summary>A resolved source → target pair.</summary>
public sealed record ConversionPath(IImporter Importer, IExporter Exporter)
{
    public bool IsAvailable => Importer.Status != ConverterStatus.Planned && Exporter.Status != ConverterStatus.Planned;
}

/// <summary>
/// Knows every importer and exporter. Because all converters go through the neutral document model,
/// any importer can feed any exporter, so the format matrix is importers × exporters.
/// </summary>
public sealed class FormatRegistry
{
    private readonly List<IImporter> _importers;
    private readonly List<IExporter> _exporters;

    public FormatRegistry(IEnumerable<IImporter> importers, IEnumerable<IExporter> exporters)
    {
        _importers = importers.ToList();
        _exporters = exporters.ToList();
    }

    public IReadOnlyList<IImporter> Importers => _importers;

    public IReadOnlyList<IExporter> Exporters => _exporters;

    /// <summary>All file extensions the app can open, for file-picker filters.</summary>
    public IReadOnlyList<string> ImportableExtensions =>
        _importers.Where(i => i.Status != ConverterStatus.Planned)
            .SelectMany(i => i.Format.Extensions)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(e => e, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public IImporter? FindImporter(string fileName) =>
        _importers.FirstOrDefault(i => i.Status != ConverterStatus.Planned && i.CanImport(fileName))
        ?? _importers.FirstOrDefault(i => i.CanImport(fileName));

    public IImporter? FindImporterByFormat(string formatId) =>
        _importers.FirstOrDefault(i => string.Equals(i.Format.Id, formatId, StringComparison.OrdinalIgnoreCase));

    public IExporter? FindExporter(string formatId) =>
        _exporters.FirstOrDefault(e => string.Equals(e.Format.Id, formatId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Targets offered for a given source file (every exporter except the source's own format).</summary>
    public IReadOnlyList<IExporter> GetTargetsFor(string fileName)
    {
        var importer = FindImporter(fileName);
        if (importer is null)
        {
            return [];
        }

        return _exporters
            .Where(e => !string.Equals(e.Format.Id, importer.Format.Id, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Status)
            .ThenBy(e => e.Format.Id == KnownFormats.Pdf.Id ? 0 : 1)
            .ThenBy(e => e.Format.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public ConversionPath? Resolve(string sourceFileName, string targetFormatId)
    {
        var importer = FindImporter(sourceFileName);
        var exporter = FindExporter(targetFormatId);
        return importer is null || exporter is null ? null : new ConversionPath(importer, exporter);
    }

    public bool CanOpen(string fileName) => FindImporter(fileName) is { Status: not ConverterStatus.Planned };
}
