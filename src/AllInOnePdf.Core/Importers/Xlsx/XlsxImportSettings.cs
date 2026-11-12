using AllInOnePdf.Core.Conversion;

namespace AllInOnePdf.Core.Importers.Xlsx;

internal enum XlsxHeaderMode { Auto, Yes, No }

internal enum XlsxSheetHeadings { Auto, Always, Never }

/// <summary>Typed view of the <see cref="XlsxImporter"/> options.</summary>
internal sealed record XlsxImportSettings(
    string? Sheets,
    bool IncludeHidden,
    XlsxSheetHeadings SheetHeadings,
    bool PageBreaks,
    XlsxHeaderMode HeaderRow,
    bool FormulasAsValues,
    bool TrimEmpty,
    int MaxRows)
{
    public static XlsxImportSettings From(OptionValues values) => new(
        values.GetString(XlsxImporter.OptSheets),
        values.GetBool(XlsxImporter.OptIncludeHidden, false),
        values.GetString(XlsxImporter.OptSheetHeadings)?.Trim().ToLowerInvariant() switch
        {
            "always" or "yes" or "true" => XlsxSheetHeadings.Always,
            "never" or "no" or "false" => XlsxSheetHeadings.Never,
            _ => XlsxSheetHeadings.Auto,
        },
        values.GetBool(XlsxImporter.OptPageBreaks, true),
        values.GetString(XlsxImporter.OptHeaderRow)?.Trim().ToLowerInvariant() switch
        {
            "yes" or "true" => XlsxHeaderMode.Yes,
            "no" or "false" => XlsxHeaderMode.No,
            _ => XlsxHeaderMode.Auto,
        },
        values.GetBool(XlsxImporter.OptFormulasAsValues, true),
        values.GetBool(XlsxImporter.OptTrimEmpty, true),
        Math.Clamp(values.GetInt(XlsxImporter.OptMaxRows, 5000), 1, 1_000_000));
}
