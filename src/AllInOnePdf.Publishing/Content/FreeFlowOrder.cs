using AllInOnePdf.Publishing.Model;

namespace AllInOnePdf.Publishing.Content;

/// <summary>
/// Reads a free-flow canvas as rows and columns: items whose vertical ranges overlap form a band (top to bottom),
/// inside a band items whose horizontal ranges overlap form a column (left to right), and a column is read top to
/// bottom. The flattener's reading order and <see cref="SectionLayoutConverter.ToStructure"/> both use it, so an
/// export reads a canvas in the same order a conversion would lay it out.
/// </summary>
internal static class FreeFlowOrder
{
    /// <summary>Overlaps smaller than this (boxes that merely touch) do not join items.</summary>
    private const double TouchPt = 0.5;

    /// <summary>A column of a band: its items top to bottom and its horizontal extent.</summary>
    internal sealed record Column<T>(List<T> Items, double Left, double Right)
    {
        public double Width => Math.Max(PlacedModule.MinSizePt, Right - Left);
    }

    /// <summary>The bands of <paramref name="items"/>, each split into columns.</summary>
    public static List<List<Column<T>>> Bands<T>(IEnumerable<T> items, Func<T, PlacedModule> placed, int maxColumns = int.MaxValue)
    {
        var ordered = items.Select((item, index) => (item, p: placed(item), index))
            .OrderBy(e => Top(e.p)).ThenBy(e => Left(e.p)).ThenBy(e => e.index).ToList();

        var bands = new List<List<Column<T>>>();
        var band = new List<(T item, PlacedModule p, int index)>();
        var bottom = double.NegativeInfinity;
        foreach (var entry in ordered)
        {
            if (band.Count > 0 && Top(entry.p) >= bottom - TouchPt)
            {
                bands.Add(Columns(band, maxColumns));
                band.Clear();
                bottom = double.NegativeInfinity;
            }

            band.Add(entry);
            bottom = Math.Max(bottom, Top(entry.p) + Height(entry.p));
        }

        if (band.Count > 0)
        {
            bands.Add(Columns(band, maxColumns));
        }

        return bands;
    }

    /// <summary><paramref name="items"/> in reading order: bands top to bottom, columns left to right, items top to bottom.</summary>
    public static IEnumerable<T> ReadingOrder<T>(IEnumerable<T> items, Func<T, PlacedModule> placed) =>
        Bands(items, placed).SelectMany(b => b).SelectMany(c => c.Items);

    private static List<Column<T>> Columns<T>(List<(T item, PlacedModule p, int index)> band, int maxColumns)
    {
        var columns = new List<(List<(T item, PlacedModule p, int index)> items, double left, double right)>();
        foreach (var entry in band.OrderBy(e => Left(e.p)).ThenBy(e => Top(e.p)).ThenBy(e => e.index))
        {
            var left = Left(entry.p);
            var right = left + Width(entry.p);
            if (columns.Count > 0 && left < columns[^1].right - TouchPt)
            {
                var last = columns[^1];
                last.items.Add(entry);
                columns[^1] = (last.items, last.left, Math.Max(last.right, right));
            }
            else
            {
                columns.Add(([entry], left, right));
            }
        }

        // Too many columns for a row: merge the neighbours that together are the narrowest until it fits.
        while (columns.Count > Math.Max(1, maxColumns))
        {
            var best = 0;
            for (var i = 1; i < columns.Count - 1; i++)
            {
                if (columns[i + 1].right - columns[i].left < columns[best + 1].right - columns[best].left)
                {
                    best = i;
                }
            }

            var a = columns[best];
            var b = columns[best + 1];
            a.items.AddRange(b.items);
            columns[best] = (a.items, a.left, Math.Max(a.right, b.right));
            columns.RemoveAt(best + 1);
        }

        return columns
            .Select(c => new Column<T>([.. c.items.OrderBy(e => Top(e.p)).ThenBy(e => Left(e.p)).ThenBy(e => e.index).Select(e => e.item)], c.left, c.right))
            .ToList();
    }

    private static double Top(PlacedModule p) => double.IsFinite(p.YPt) ? p.YPt : 0;

    private static double Left(PlacedModule p) => double.IsFinite(p.XPt) ? p.XPt : 0;

    private static double Width(PlacedModule p) => double.IsFinite(p.WidthPt) && p.WidthPt > 0 ? p.WidthPt : PlacedModule.MinSizePt;

    private static double Height(PlacedModule p) => double.IsFinite(p.HeightPt) && p.HeightPt > 0 ? p.HeightPt : PlacedModule.MinSizePt;
}
