using System.Globalization;
using AllInOnePdf.Core.Storage;

namespace AllInOnePdf.UI.Services;

/// <summary>Columns of the History table that can be sorted.</summary>
public enum HistorySortColumn
{
    When,
    File,
    Target,
    Result,
    Duration,
    Size,
}

/// <summary>
/// Pure rules of the History page (no state, no I/O): filtering, sorting and the count line, kept apart so they can
/// be tested.
/// </summary>
public static class ConvertHistoryView
{
    /// <summary>Filter values of the result box.</summary>
    public const string AllResults = "all";
    public const string ConvertedOnly = "ok";
    public const string FailedOnly = "failed";

    /// <summary>Filter value of the target box that keeps every target.</summary>
    public const string AllTargets = "all";

    /// <summary>
    /// Entries matching the search text (source or output file name), the result filter and the target filter, in
    /// the requested order. Ties keep the newest first.
    /// </summary>
    public static IReadOnlyList<HistoryEntry> Apply(
        IEnumerable<HistoryEntry> entries, string? search, string? result, string? target, HistorySortColumn sort, bool descending)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var query = entries;
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(e => e.SourceFileName.Contains(term, StringComparison.OrdinalIgnoreCase)
                || e.OutputPaths.Any(p => Path.GetFileName(p).Contains(term, StringComparison.OrdinalIgnoreCase)));
        }

        query = result switch
        {
            ConvertedOnly => query.Where(e => e.Success),
            FailedOnly => query.Where(e => !e.Success),
            _ => query,
        };

        if (!string.IsNullOrEmpty(target) && target != AllTargets)
        {
            query = query.Where(e => string.Equals(e.TargetFormatId, target, StringComparison.OrdinalIgnoreCase));
        }

        var newestFirst = query.OrderByDescending(e => e.Timestamp).ToList();
        if (sort == HistorySortColumn.When)
        {
            if (!descending)
            {
                newestFirst.Reverse();
            }

            return newestFirst;
        }

        // OrderBy is stable, so equal keys keep the newest-first order.
        IOrderedEnumerable<HistoryEntry> sorted = sort switch
        {
            HistorySortColumn.File => Order(newestFirst, e => e.SourceFileName, descending, StringComparer.CurrentCultureIgnoreCase),
            HistorySortColumn.Target => Order(newestFirst, e => ConvertFormatting.FormatName(e.TargetFormatId), descending, StringComparer.CurrentCultureIgnoreCase),
            HistorySortColumn.Result => Order(newestFirst, e => e.Success, descending, Comparer<bool>.Default),
            HistorySortColumn.Duration => Order(newestFirst, e => e.ElapsedSeconds, descending, Comparer<double>.Default),
            HistorySortColumn.Size => Order(newestFirst, e => e.Success ? e.OutputBytes : -1, descending, Comparer<long>.Default),
            _ => newestFirst.OrderBy(_ => 0),
        };
        return sorted.ToList();
    }

    /// <summary>"5 conversions", or "2 of 5 conversions" while a filter hides some.</summary>
    public static string CountText(int shown, int total)
    {
        var noun = total == 1 ? "conversion" : "conversions";
        return shown == total
            ? string.Create(CultureInfo.CurrentCulture, $"{total} {noun}")
            : string.Create(CultureInfo.CurrentCulture, $"{shown} of {total} {noun}");
    }

    /// <summary>The sort a header click gives: the same column flips direction; a new column starts in its natural order.</summary>
    public static (HistorySortColumn Column, bool Descending) Toggle(HistorySortColumn current, bool descending, HistorySortColumn clicked) =>
        clicked == current
            ? (clicked, !descending)
            : (clicked, clicked is HistorySortColumn.When or HistorySortColumn.Duration or HistorySortColumn.Size);

    private static IOrderedEnumerable<HistoryEntry> Order<TKey>(IEnumerable<HistoryEntry> source, Func<HistoryEntry, TKey> key, bool descending, IComparer<TKey> comparer) =>
        descending ? source.OrderByDescending(key, comparer) : source.OrderBy(key, comparer);
}
