using System.Text.RegularExpressions;
using AllInOnePdf.Core.Documents;
using AllInOnePdf.Core.Themes;
using AllInOnePdf.Publishing.Model;

namespace AllInOnePdf.Publishing.Validation;

/// <summary>
/// Repairs publications that come from outside the app (imported packages, hand-edited JSON, older versions): fills
/// missing objects, drops null entries, regenerates missing or duplicate ids, clamps every number to a range the
/// renderer can lay out and normalises the theme snapshot. Never throws; running it twice changes nothing.
/// </summary>
public static partial class PublicationValidator
{
    /// <summary>Smallest column fraction.</summary>
    public const double MinFraction = 0.05;

    /// <summary>Most columns in a row (<c>1 / MinFraction</c>).</summary>
    public const int MaxColumns = 20;

    /// <summary>Smallest height of a free-flow canvas (<see cref="Section.CanvasHeightPt"/>).</summary>
    public const double MinCanvasHeightPt = 40;

    private const double FractionTolerance = 1e-9;

    /// <summary>Normalises <paramref name="publication"/> in place and returns it.</summary>
    public static Publication Normalize(Publication publication)
    {
        ArgumentNullException.ThrowIfNull(publication);
        var p = publication;

        p.Id = IsSafeId(p.Id) ? p.Id : Publication.NewId();
        p.Name = string.IsNullOrWhiteSpace(p.Name) ? "Untitled publication" : p.Name.Trim();
        p.Description = NullIfBlank(p.Description);
        p.SchemaVersion = Math.Max(1, p.SchemaVersion);
        if (p.Created == default)
        {
            p.Created = p.Modified == default ? DateTimeOffset.UtcNow : p.Modified;
        }

        if (p.Modified == default)
        {
            p.Modified = p.Created;
        }

        p.Tags = CleanStrings(p.Tags);
        p.ThemeId = NullIfBlank(p.ThemeId);
        p.DefaultLayout = Defined(p.DefaultLayout, SectionLayout.Structure);

        p.Metadata ??= new PublicationMetadata();
        var m = p.Metadata;
        m.Title = NullIfBlank(m.Title);
        m.Subtitle = NullIfBlank(m.Subtitle);
        m.Description = NullIfBlank(m.Description);
        m.Language = string.IsNullOrWhiteSpace(m.Language) ? "en" : m.Language.Trim();
        m.Authors = CleanStrings(m.Authors);
        m.Keywords = CleanStrings(m.Keywords);

        p.Theme ??= BuiltInThemes.Clean();
        ThemeValidator.Normalize(p.Theme);

        var assetIds = NormalizeAssets(p);
        var ids = new HashSet<string>(StringComparer.Ordinal);

        p.Sections ??= [];
        p.Sections.RemoveAll(s => s is null);
        foreach (var section in p.Sections)
        {
            section.Id = UniqueId(section.Id, ids);
            section.Label = NullIfBlank(section.Label);
            section.Design = NormalizeBox(section.Design ??= new BoxDesign());
            section.Layout = Defined(section.Layout, SectionLayout.Structure);
            section.CanvasHeightPt = section.CanvasHeightPt is { } canvas && double.IsFinite(canvas) && canvas > 0
                ? Math.Clamp(canvas, MinCanvasHeightPt, PlacedModule.MaxPt)
                : null;
            section.Rows ??= [];
            section.Rows.RemoveAll(r => r is null);
            foreach (var row in section.Rows)
            {
                NormalizeRow(row, ids, assetIds);
            }

            section.Items ??= [];
            section.Items.RemoveAll(i => i is null || i.Module is null);
            foreach (var item in section.Items)
            {
                NormalizePlacedModule(item, ids, assetIds);
            }
        }

        return p;
    }

    private static void NormalizePlacedModule(PlacedModule item, HashSet<string> ids, Dictionary<string, string> assetIds)
    {
        item.XPt = Clamp(item.XPt, -PlacedModule.MaxPt, PlacedModule.MaxPt, 0);
        item.YPt = Clamp(item.YPt, -PlacedModule.MaxPt, PlacedModule.MaxPt, 0);
        item.WidthPt = Clamp(item.WidthPt, PlacedModule.MinSizePt, PlacedModule.MaxPt, PlacedModule.DefaultWidthPt);
        item.HeightPt = Clamp(item.HeightPt, PlacedModule.MinSizePt, PlacedModule.MaxPt, PlacedModule.DefaultHeightPt);
        item.Z = Math.Clamp(item.Z, 0, BoxDesign.MaxZIndex);
        NormalizeModule(item.Module, ids, assetIds);
    }

    private static void NormalizeRow(Row row, HashSet<string> ids, Dictionary<string, string> assetIds)
    {
        row.Id = UniqueId(row.Id, ids);
        row.Label = NullIfBlank(row.Label);
        row.GutterPt = Clamp(row.GutterPt, 0, 100, 16);
        row.Align = Defined(row.Align, VerticalAlign.Top);
        row.Design = NormalizeBox(row.Design ??= new BoxDesign());

        row.Columns ??= [];
        row.Columns.RemoveAll(c => c is null);
        row.Fractions ??= [];
        var count = row.Columns.Count > 0 ? row.Columns.Count : Math.Max(1, row.Fractions.Count);
        count = Math.Min(count, MaxColumns);
        ColumnLayouts.ResizeColumns(row.Columns, count);
        row.Fractions = NormalizeFractions(row.Fractions, count);

        foreach (var column in row.Columns)
        {
            column.Id = UniqueId(column.Id, ids);
            column.Design = NormalizeBox(column.Design ??= new BoxDesign());
            column.Modules ??= [];
            column.Modules.RemoveAll(mod => mod is null);
            foreach (var module in column.Modules)
            {
                NormalizeModule(module, ids, assetIds);
            }
        }
    }

    /// <summary>
    /// Pads or truncates to <paramref name="count"/>, replaces invalid entries with the mean, scales to sum 1 and lifts
    /// fractions under <see cref="MinFraction"/> (shrinking the others). Left untouched when already valid, so it is
    /// idempotent despite floating-point rounding.
    /// </summary>
    internal static List<double> NormalizeFractions(List<double> fractions, int count)
    {
        var valid = fractions.Where(f => double.IsFinite(f) && f > 0).ToList();
        var mean = valid.Count > 0 ? valid.Average() : 1;
        var values = new double[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = i < fractions.Count && double.IsFinite(fractions[i]) && fractions[i] > 0 ? fractions[i] : mean;
        }

        if (values.Length == fractions.Count
            && Math.Abs(values.Sum() - 1) < FractionTolerance
            && values.All(v => v >= MinFraction - FractionTolerance)
            && values.Zip(fractions).All(z => z.First == z.Second))
        {
            return fractions;
        }

        var sum = values.Sum();
        for (var i = 0; i < count; i++)
        {
            values[i] /= sum;
        }

        // Water-fill: pin the small ones at the minimum and share what is left among the rest in proportion.
        var pinned = new bool[count];
        while (true)
        {
            var changed = false;
            for (var i = 0; i < count; i++)
            {
                if (!pinned[i] && values[i] < MinFraction)
                {
                    pinned[i] = true;
                    changed = true;
                }
            }

            if (!changed)
            {
                break;
            }

            var free = 1 - pinned.Count(x => x) * MinFraction;
            var freeSum = Enumerable.Range(0, count).Where(i => !pinned[i]).Sum(i => values[i]);
            for (var i = 0; i < count; i++)
            {
                values[i] = pinned[i] ? MinFraction : freeSum > 0 ? values[i] / freeSum * free : 0;
            }
        }

        return [.. values];
    }

    private static void NormalizeModule(Module module, HashSet<string> ids, Dictionary<string, string> assetIds)
    {
        module.Id = UniqueId(module.Id, ids);
        module.Label = NullIfBlank(module.Label);
        var d = module.Design ??= new ModuleDesign();
        NormalizeBox(d);
        d.Alignment = d.Alignment is { } a && Enum.IsDefined(a) ? a : null;
        d.TextColor = OptionalColor(d.TextColor);
        d.FontScale = Clamp(d.FontScale, 0.5, 3, 1);

        switch (module)
        {
            case TextModule t:
                t.Markdown ??= "";
                break;
            case HeadingModule h:
                h.Text ??= "";
                h.Level = Math.Clamp(h.Level, 1, 6);
                break;
            case ImageModule i:
                i.AssetId = i.AssetId is not null && assetIds.TryGetValue(i.AssetId, out var mapped) ? mapped : null;
                i.Alt = NullIfBlank(i.Alt);
                i.Caption = NullIfBlank(i.Caption);
                i.WidthPercent = Clamp(i.WidthPercent, 5, 100, 100);
                i.Align = Defined(i.Align, BlockAlign.Center);
                i.CornerRadiusPt = Clamp(i.CornerRadiusPt, 0, 100, 0);
                i.MaxHeightPt = i.MaxHeightPt is { } max && double.IsFinite(max) && max > 0 ? Math.Min(max, 2000) : null;
                break;
            case DividerModule dv:
                dv.Style = Defined(dv.Style, DividerStyle.Solid);
                dv.Color = OptionalColor(dv.Color);
                dv.ThicknessPt = Clamp(dv.ThicknessPt, 0.25, 20, 0.75);
                dv.WidthPercent = Clamp(dv.WidthPercent, 5, 100, 100);
                dv.Align = Defined(dv.Align, BlockAlign.Center);
                break;
            case SpacerModule s:
                s.HeightPt = Clamp(s.HeightPt, 0, 600, 24);
                break;
            case QuoteModule q:
                q.Markdown ??= "";
                q.Attribution = NullIfBlank(q.Attribution);
                break;
            case CalloutModule c:
                c.Kind = Defined(c.Kind, CalloutKind.Note);
                c.Title = NullIfBlank(c.Title);
                c.Markdown ??= "";
                break;
            case CodeModule code:
                code.Code ??= "";
                code.Language = NullIfBlank(code.Language);
                code.Caption = NullIfBlank(code.Caption);
                break;
            case TableModule table:
                NormalizeTable(table);
                break;
            case ButtonModule b:
                b.Text ??= "";
                b.Url = NullIfBlank(b.Url);
                b.Style = Defined(b.Style, ButtonStyle.Filled);
                b.Align = Defined(b.Align, BlockAlign.Left);
                b.Color = OptionalColor(b.Color);
                b.CornerRadiusPt = Clamp(b.CornerRadiusPt, 0, 100, 4);
                break;
            case ContentsModule toc:
                toc.Title ??= "Contents";
                toc.Depth = Math.Clamp(toc.Depth, 1, 6);
                break;
        }
    }

    private static void NormalizeTable(TableModule table)
    {
        table.Caption = NullIfBlank(table.Caption);
        table.Cells ??= [];
        table.Cells.RemoveAll(r => r is null);
        var columns = table.Cells.Count == 0 ? 0 : table.Cells.Max(r => r.Count);
        foreach (var row in table.Cells)
        {
            for (var i = 0; i < row.Count; i++)
            {
                row[i] ??= "";
            }

            while (row.Count < columns)
            {
                row.Add("");
            }
        }

        table.Alignments ??= [];
        for (var i = 0; i < table.Alignments.Count; i++)
        {
            table.Alignments[i] = Defined(table.Alignments[i], ColumnAlignment.Default);
        }

        if (table.Alignments.Count > columns)
        {
            table.Alignments.RemoveRange(columns, table.Alignments.Count - columns);
        }

        while (table.Alignments.Count < columns)
        {
            table.Alignments.Add(ColumnAlignment.Default);
        }
    }

    /// <summary>Drops null and duplicate assets, repairs ids and returns old id → current id for the image references.</summary>
    private static Dictionary<string, string> NormalizeAssets(Publication p)
    {
        p.Assets ??= [];
        p.Assets.RemoveAll(a => a is null);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var keep = new List<PublicationAsset>(p.Assets.Count);
        foreach (var asset in p.Assets)
        {
            var original = asset.Id ?? "";
            if (map.ContainsKey(original))
            {
                continue; // a second asset with the same id: references are ambiguous, the first one wins
            }

            var id = original.Trim().ToLowerInvariant();
            if (!IsAssetId(id) || !seen.Add(id))
            {
                id = Publication.NewId();
                seen.Add(id);
            }

            asset.Id = id;
            map[original] = id;
            map[id] = id;
            asset.Data ??= [];
            asset.MediaType = string.IsNullOrWhiteSpace(asset.MediaType) ? GuessMediaType(asset.FileName) : asset.MediaType.Trim().ToLowerInvariant();
            asset.FileName = string.IsNullOrWhiteSpace(asset.FileName) ? "image" : Path.GetFileName(asset.FileName.Trim());
            if (asset.FileName.Length == 0)
            {
                asset.FileName = "image";
            }

            asset.PixelWidth = asset.PixelWidth is > 0 ? asset.PixelWidth : null;
            asset.PixelHeight = asset.PixelHeight is > 0 ? asset.PixelHeight : null;
            keep.Add(asset);
        }

        p.Assets = keep;
        return map;
    }

    private static string GuessMediaType(string? fileName) => Path.GetExtension(fileName ?? "").ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        ".bmp" => "image/bmp",
        _ => "application/octet-stream",
    };

    private static BoxDesign NormalizeBox(BoxDesign box)
    {
        box.Margin = NormalizeSpacing(box.Margin);
        box.Padding = NormalizeSpacing(box.Padding);
        box.Background = OptionalColor(box.Background);
        box.BorderColor = OptionalColor(box.BorderColor);
        box.BorderWidthPt = Clamp(box.BorderWidthPt, 0, 20, 0);
        box.CornerRadiusPt = Clamp(box.CornerRadiusPt, 0, 100, 0);
        box.OffsetXPt = Clamp(box.OffsetXPt, -BoxDesign.MaxOffsetPt, BoxDesign.MaxOffsetPt, 0);
        box.OffsetYPt = Clamp(box.OffsetYPt, -BoxDesign.MaxOffsetPt, BoxDesign.MaxOffsetPt, 0);
        box.ZIndex = Math.Clamp(box.ZIndex, 0, BoxDesign.MaxZIndex);
        return box;
    }

    private static Spacing NormalizeSpacing(Spacing? spacing)
    {
        spacing ??= new Spacing();
        spacing.TopPt = Clamp(spacing.TopPt, 0, 200, 0);
        spacing.RightPt = Clamp(spacing.RightPt, 0, 200, 0);
        spacing.BottomPt = Clamp(spacing.BottomPt, 0, 200, 0);
        spacing.LeftPt = Clamp(spacing.LeftPt, 0, 200, 0);
        return spacing;
    }

    private static string UniqueId(string? id, HashSet<string> used)
    {
        var value = string.IsNullOrWhiteSpace(id) ? Publication.NewId() : id.Trim();
        while (!used.Add(value))
        {
            value = Publication.NewId();
        }

        return value;
    }

    private static List<string> CleanStrings(List<string>? values)
    {
        if (values is null)
        {
            return [];
        }

        values.RemoveAll(string.IsNullOrWhiteSpace);
        for (var i = 0; i < values.Count; i++)
        {
            values[i] = values[i].Trim();
        }

        return values;
    }

    private static TEnum Defined<TEnum>(TEnum value, TEnum fallback)
        where TEnum : struct, Enum => Enum.IsDefined(value) ? value : fallback;

    private static double Clamp(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? OptionalColor(string? value) => ThemeValidator.IsHexColor(value) ? value!.Trim() : null;

    /// <summary>True for a 32-character lowercase hex asset id (the only form allowed as a package entry name).</summary>
    public static bool IsAssetId(string? id) => id is not null && AssetIdPattern().IsMatch(id);

    /// <summary>True for an id that is safe as a file name: 1 to 64 letters, digits, '-' or '_'.</summary>
    public static bool IsSafeId(string? id) => id is not null && SafeIdPattern().IsMatch(id);

    [GeneratedRegex("^[a-f0-9]{32}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex AssetIdPattern();

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex SafeIdPattern();
}
