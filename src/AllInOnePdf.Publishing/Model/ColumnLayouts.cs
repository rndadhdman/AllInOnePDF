namespace AllInOnePdf.Publishing.Model;

/// <summary>A named column split for the row layout picker.</summary>
/// <param name="Name">Display name such as "⅓ ⅔".</param>
/// <param name="Fractions">Column widths summing to 1.</param>
public sealed record ColumnLayoutPreset(string Name, IReadOnlyList<double> Fractions);

/// <summary>The Divi set of column layouts and the operation that changes a row's layout without losing content.</summary>
public static class ColumnLayouts
{
    private const double Third = 1.0 / 3;
    private const double Sixth = 1.0 / 6;

    /// <summary>1 · ½ ½ · ⅓ ⅔ · ⅔ ⅓ · ⅓ ⅓ ⅓ · ¼ ¼ ¼ ¼ · ¼ ¾ · ¾ ¼ · ¼ ½ ¼ · ⅕ ⅗ ⅕ · ½ ¼ ¼ · ¼ ¼ ½ · ⅙ ×6.</summary>
    public static IReadOnlyList<ColumnLayoutPreset> Presets { get; } =
    [
        new("1", [1]),
        new("½ ½", [0.5, 0.5]),
        new("⅓ ⅔", [Third, 1 - Third]),
        new("⅔ ⅓", [1 - Third, Third]),
        new("⅓ ⅓ ⅓", [Third, Third, Third]),
        new("¼ ¼ ¼ ¼", [0.25, 0.25, 0.25, 0.25]),
        new("¼ ¾", [0.25, 0.75]),
        new("¾ ¼", [0.75, 0.25]),
        new("¼ ½ ¼", [0.25, 0.5, 0.25]),
        new("⅕ ⅗ ⅕", [0.2, 0.6, 0.2]),
        new("½ ¼ ¼", [0.5, 0.25, 0.25]),
        new("¼ ¼ ½", [0.25, 0.25, 0.5]),
        new("⅙ ×6", [Sixth, Sixth, Sixth, Sixth, Sixth, Sixth]),
    ];

    /// <summary>The preset whose fractions match <paramref name="fractions"/> (within 0.001), or null for a custom split.</summary>
    public static ColumnLayoutPreset? Match(IReadOnlyList<double> fractions)
    {
        ArgumentNullException.ThrowIfNull(fractions);
        return Presets.FirstOrDefault(p =>
            p.Fractions.Count == fractions.Count && p.Fractions.Zip(fractions).All(z => Math.Abs(z.First - z.Second) < 0.001));
    }

    /// <summary>
    /// Changes <paramref name="row"/> to the given split. Fractions are copied and scaled to sum 1. New columns are
    /// empty; the modules of removed columns are appended, in order, to the last remaining column.
    /// </summary>
    /// <exception cref="ArgumentException">No fractions, or a fraction that is not a positive number.</exception>
    public static void Apply(Row row, IReadOnlyList<double> fractions)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(fractions);
        if (fractions.Count == 0 || fractions.Any(f => !double.IsFinite(f) || f <= 0))
        {
            throw new ArgumentException("A layout needs at least one positive fraction.", nameof(fractions));
        }

        var sum = fractions.Sum();
        row.Fractions = [.. fractions.Select(f => f / sum)];
        row.Columns ??= [];
        row.Columns.RemoveAll(c => c is null);
        ResizeColumns(row.Columns, fractions.Count);
    }

    /// <summary>Adds empty columns or folds surplus columns into the last kept one.</summary>
    internal static void ResizeColumns(List<Column> columns, int count)
    {
        while (columns.Count < count)
        {
            columns.Add(new Column());
        }

        if (columns.Count > count)
        {
            var target = columns[count - 1];
            target.Modules ??= [];
            foreach (var removed in columns.Skip(count))
            {
                if (removed.Modules is not null)
                {
                    target.Modules.AddRange(removed.Modules);
                }
            }

            columns.RemoveRange(count, columns.Count - count);
        }
    }
}
