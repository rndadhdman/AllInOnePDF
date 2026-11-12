using AllInOnePdf.Core.Documents;

namespace AllInOnePdf.Core.Importers.Pdf;

internal sealed class DetectedRow
{
    public bool IsHeader { get; set; }

    /// <summary>Words per cell in reading order (line by line).</summary>
    public List<List<PdfWord>> Cells { get; } = [];
}

internal sealed class DetectedTable
{
    public List<PdfLine> Lines { get; } = [];
    public List<DetectedRow> Rows { get; } = [];
    public List<ColumnAlignment> Alignments { get; } = [];
    public double Top { get; set; }
}

/// <summary>
/// Best-effort table recognition, deliberately conservative (a wrong table is worse than a right paragraph):
/// <list type="bullet">
/// <item>Ruled tables: at least three horizontal rules with the same extent and text between them; columns come
/// from vertical rules, from cell-border joints in the rules, or from gaps no row crosses.</item>
/// <item>Unruled tables: three or more consecutive lines that all split into the same number (≥ 2) of wide-gap
/// segments whose left, right or centre edges line up column by column.</item>
/// </list>
/// </summary>
internal static class TableDetector
{
    private sealed class RuleRow
    {
        public double Y { get; init; }
        public double Left { get; set; }
        public double Right { get; set; }
        public double Thickness { get; set; }
        public List<(double Left, double Right)> Segments { get; } = [];
    }

    public static List<DetectedTable> Detect(PageContent page, IReadOnlyList<PdfLine> lines, FontStatistics stats)
    {
        var tables = new List<DetectedTable>();
        var claimed = new HashSet<PdfLine>(ReferenceEqualityComparer.Instance);

        foreach (var table in DetectRuled(page, lines, stats))
        {
            if (table.Lines.Any(claimed.Contains))
            {
                continue;
            }

            tables.Add(table);
            claimed.UnionWith(table.Lines);
        }

        tables.AddRange(DetectAligned(lines.Where(l => !claimed.Contains(l)).ToList(), stats));
        return tables;
    }

    // ------------------------------------------------------------------ ruled

    private static List<RuleRow> MergeRules(IEnumerable<HorizontalRule> rules)
    {
        var rows = new List<RuleRow>();
        foreach (var rule in rules.OrderByDescending(r => r.Y).ThenBy(r => r.Left))
        {
            var row = rows.LastOrDefault(r => Math.Abs(r.Y - rule.Y) <= 1.0);
            if (row is null)
            {
                row = new RuleRow { Y = rule.Y, Left = rule.Left, Right = rule.Right, Thickness = rule.Thickness };
                rows.Add(row);
            }

            row.Left = Math.Min(row.Left, rule.Left);
            row.Right = Math.Max(row.Right, rule.Right);
            row.Thickness = Math.Max(row.Thickness, rule.Thickness);
            row.Segments.Add((rule.Left, rule.Right));
        }

        foreach (var row in rows)
        {
            row.Segments.Sort((a, b) => a.Left.CompareTo(b.Left));
        }

        return rows;
    }

    private static IEnumerable<DetectedTable> DetectRuled(PageContent page, IReadOnlyList<PdfLine> lines, FontStatistics stats)
    {
        if (page.HorizontalRules.Count < 3)
        {
            yield break;
        }

        // Running-header and footer rules never belong to a table; a table's own cell borders (drawn per cell, so
        // several segments on one line) still count there, or the last row on a full page would lose its bottom rule.
        var zone = page.Height * HeaderFooterFilter.ZoneFraction;
        var rows = MergeRules(page.HorizontalRules).Where(r => (r.Y > zone && r.Y < page.Height - zone) || r.Segments.Count >= 2).ToList();
        var maxBand = stats.BodySize * 10;
        var i = 0;
        while (i < rows.Count)
        {
            var group = new List<RuleRow> { rows[i] };
            var j = i + 1;
            while (j < rows.Count)
            {
                var prev = group[^1];
                var row = rows[j];
                if (Math.Abs(row.Left - prev.Left) > 4 || Math.Abs(row.Right - prev.Right) > 4)
                {
                    break;
                }

                var height = prev.Y - row.Y;
                if (height < 2)
                {
                    // Double rule or a duplicated edge.
                    j++;
                    continue;
                }

                if (height > maxBand || !lines.Any(l => l.Baseline < prev.Y && l.Baseline > row.Y && l.Left >= prev.Left - 2 && l.Right <= prev.Right + 2))
                {
                    break;
                }

                group.Add(row);
                j++;
            }

            if (group.Count >= 2)
            {
                var table = BuildRuled(page, lines, group, stats);
                if (table is not null)
                {
                    yield return table;
                    i = j;
                    continue;
                }
            }

            i++;
        }
    }

    private static DetectedTable? BuildRuled(PageContent page, IReadOnlyList<PdfLine> lines, List<RuleRow> rules, FontStatistics stats)
    {
        var left = rules.Min(r => r.Left);
        var right = rules.Max(r => r.Right);
        var bands = new List<(double Top, double Bottom, bool HeaderAbove)>();
        for (var k = 0; k + 1 < rules.Count; k++)
        {
            bands.Add((rules[k].Y, rules[k + 1].Y, false));
        }

        var bandHeights = bands.Select(b => b.Top - b.Bottom).Order().ToList();
        var medianBand = bandHeights[bandHeights.Count / 2];

        bool InRegion(PdfLine l) => l.Left >= left - 2 && l.Right <= right + 2 && !l.IsMono;

        var boundaries = ColumnBoundaries(page, rules, left, right, bands, lines, stats);
        if (boundaries.Count == 0)
        {
            return null;
        }

        // Header row above the first rule ("lines" table style draws only bottom borders).
        // Only the line(s) right above the rule count: a paragraph further up (the text before the table) is not
        // part of the header.
        var topY = rules[0].Y;
        var candidates = lines.Where(l => InRegion(l) && l.Bottom >= topY - 1 && l.Baseline <= topY + medianBand * 1.2).OrderBy(l => l.Baseline).ToList();
        var above = new List<PdfLine>();
        foreach (var line in candidates)
        {
            if (above.Count > 0 && (line.Baseline - above[^1].Baseline > above[^1].Size * 1.6 || Math.Abs(line.Size - above[^1].Size) > 1))
            {
                break;
            }

            above.Add(line);
        }

        if (above.Count > 0 && above.All(l => l.Segments.Count >= 2 && !Straddles(l.Words, boundaries)))
        {
            bands.Insert(0, (above.Max(l => l.Top) + 1, topY, true));
        }

        if (bands.Count < 2)
        {
            return null;
        }

        var columns = boundaries.Count + 1;
        var built = new List<(DetectedRow Row, List<PdfLine> Lines, double Top, int Words, int Straddling)>();
        foreach (var band in bands)
        {
            var bandLines = lines.Where(l => InRegion(l) && l.Baseline < band.Top && l.Baseline > band.Bottom).ToList();
            var row = new DetectedRow { IsHeader = band.HeaderAbove };
            for (var c = 0; c < columns; c++)
            {
                row.Cells.Add([]);
            }

            var words = 0;
            var straddling = 0;
            foreach (var word in bandLines.SelectMany(l => l.Words))
            {
                words++;
                if (Straddles([word], boundaries))
                {
                    straddling++;
                }

                row.Cells[ColumnOf(word.CenterX, boundaries)].Add(word);
            }

            built.Add((row, bandLines, band.Top, words, straddling));
        }

        // Trim bands at either end that are really prose (empty or crossing column boundaries).
        static bool Bad((DetectedRow Row, List<PdfLine> Lines, double Top, int Words, int Straddling) b) => b.Words == 0 || b.Straddling > b.Words * 0.2;
        while (built.Count > 0 && Bad(built[0]))
        {
            built.RemoveAt(0);
        }

        while (built.Count > 0 && Bad(built[^1]))
        {
            built.RemoveAt(built.Count - 1);
        }

        if (built.Count < 2 || built.Any(b => b.Words == 0))
        {
            return null;
        }

        var totalWords = built.Sum(b => b.Words);
        var totalStraddling = built.Sum(b => b.Straddling);
        var totalCells = built.Count * columns;
        var filledCells = built.Sum(b => b.Row.Cells.Count(c => c.Count > 0));
        if (filledCells < totalCells * 0.5 || totalStraddling > totalWords * 0.1)
        {
            return null;
        }

        var table = new DetectedTable { Top = built[0].Top };
        foreach (var b in built)
        {
            table.Rows.Add(b.Row);
            table.Lines.AddRange(b.Lines);
        }

        MarkHeader(table, page, rules);
        ComputeAlignments(table, columns);
        return table;
    }

    private static List<double> ColumnBoundaries(PageContent page, List<RuleRow> rules, double left, double right, List<(double Top, double Bottom, bool HeaderAbove)> bands, IReadOnlyList<PdfLine> lines, FontStatistics stats)
    {
        var top = rules[0].Y;
        var bottom = rules[^1].Y;
        var result = new List<double>();

        // 1. Vertical rules inside the region.
        foreach (var v in page.VerticalRules)
        {
            if (v.X > left + 3 && v.X < right - 3 && Math.Min(v.Top, top) - Math.Max(v.Bottom, bottom) > 4)
            {
                AddDistinct(result, v.X);
            }
        }

        // 2. Joints where one cell's border ends and the next begins.
        if (result.Count == 0)
        {
            var joints = new Dictionary<double, int>();
            foreach (var row in rules)
            {
                for (var k = 0; k + 1 < row.Segments.Count; k++)
                {
                    if (Math.Abs(row.Segments[k].Right - row.Segments[k + 1].Left) <= 1.5)
                    {
                        var x = Math.Round((row.Segments[k].Right + row.Segments[k + 1].Left) / 4) * 2;
                        joints[x] = joints.GetValueOrDefault(x) + 1;
                    }
                }
            }

            foreach (var (x, count) in joints)
            {
                if (count >= rules.Count * 0.5 && x > left + 3 && x < right - 3)
                {
                    AddDistinct(result, x);
                }
            }
        }

        // 3. Gaps that no text in the region crosses.
        if (result.Count == 0)
        {
            var words = lines.Where(l => l.Baseline < top && l.Baseline > bottom && l.Left >= left - 2 && l.Right <= right + 2)
                .SelectMany(l => l.Words).OrderBy(w => w.Left).ToList();
            var minGap = Math.Max(6, stats.BodySize * 0.8);
            double? coveredRight = null;
            foreach (var w in words)
            {
                if (coveredRight is { } cr && w.Left - cr >= minGap)
                {
                    AddDistinct(result, (cr + w.Left) / 2);
                }

                coveredRight = coveredRight is null ? w.Right : Math.Max(coveredRight.Value, w.Right);
            }
        }

        result.Sort();
        return result;
    }

    private static void AddDistinct(List<double> list, double x)
    {
        if (!list.Any(v => Math.Abs(v - x) < 2))
        {
            list.Add(x);
        }
    }

    private static bool Straddles(IEnumerable<PdfWord> words, List<double> boundaries) =>
        words.Any(w => boundaries.Any(b => w.Left < b - 1 && w.Right > b + 1));

    private static int ColumnOf(double x, List<double> boundaries)
    {
        var c = 0;
        while (c < boundaries.Count && x > boundaries[c])
        {
            c++;
        }

        return c;
    }

    private static void MarkHeader(DetectedTable table, PageContent page, List<RuleRow> rules)
    {
        if (table.Rows.Count < 2 || table.Rows[0].IsHeader)
        {
            return;
        }

        var first = table.Rows[0];
        var firstBold = BoldFraction(first);
        var restBold = table.Rows.Skip(1).Select(BoldFraction).DefaultIfEmpty(0).Average();
        if (firstBold >= 0.6 && restBold < 0.5)
        {
            first.IsHeader = true;
            return;
        }

        if (rules.Count >= 3 && rules[1].Thickness > rules[2].Thickness + 0.2)
        {
            first.IsHeader = true;
            return;
        }

        // A shaded first row in an otherwise plain table.
        var firstWords = first.Cells.SelectMany(c => c).ToList();
        var secondWords = table.Rows[1].Cells.SelectMany(c => c).ToList();
        if (firstWords.Count > 0 && secondWords.Count > 0)
        {
            var shadedFirst = page.Boxes.Any(b => b.Contains(firstWords[0].CenterX, firstWords[0].CenterY));
            var shadedSecond = page.Boxes.Any(b => b.Contains(secondWords[0].CenterX, secondWords[0].CenterY));
            first.IsHeader = shadedFirst && !shadedSecond;
        }
    }

    private static double BoldFraction(DetectedRow row)
    {
        var glyphs = row.Cells.SelectMany(c => c).SelectMany(w => w.Glyphs).ToList();
        return glyphs.Count == 0 ? 0 : glyphs.Count(g => (g.Flags & FontFlags.Bold) != 0) / (double)glyphs.Count;
    }

    private static void ComputeAlignments(DetectedTable table, int columns)
    {
        var body = table.Rows.Where(r => !r.IsHeader).ToList();
        for (var c = 0; c < columns; c++)
        {
            var cells = body.Where(r => c < r.Cells.Count && r.Cells[c].Count > 0).Select(r => r.Cells[c]).ToList();
            if (cells.Count < 2)
            {
                table.Alignments.Add(ColumnAlignment.Default);
                continue;
            }

            var lefts = cells.Select(ws => ws.Min(w => w.Left)).ToList();
            var rights = cells.Select(ws => ws.Max(w => w.Right)).ToList();
            var leftSpread = lefts.Max() - lefts.Min();
            var rightSpread = rights.Max() - rights.Min();
            var centers = lefts.Zip(rights, (l, r) => (l + r) / 2).ToList();
            var centerSpread = centers.Max() - centers.Min();
            if (leftSpread <= 1.5)
            {
                table.Alignments.Add(ColumnAlignment.Left);
            }
            else if (rightSpread <= 1.5)
            {
                table.Alignments.Add(ColumnAlignment.Right);
            }
            else if (centerSpread <= 1.5)
            {
                table.Alignments.Add(ColumnAlignment.Center);
            }
            else
            {
                table.Alignments.Add(ColumnAlignment.Default);
            }
        }

        if (table.Alignments.All(a => a is ColumnAlignment.Default or ColumnAlignment.Left))
        {
            // Plain left alignment carries no information worth exporting.
            if (table.Alignments.All(a => a == ColumnAlignment.Default))
            {
                table.Alignments.Clear();
            }
        }
    }

    // ---------------------------------------------------------------- aligned

    private static IEnumerable<DetectedTable> DetectAligned(List<PdfLine> lines, FontStatistics stats)
    {
        var i = 0;
        while (i < lines.Count)
        {
            if (!IsAlignedCandidate(lines[i]))
            {
                i++;
                continue;
            }

            var n = lines[i].Segments.Count;
            var j = i + 1;
            while (j < lines.Count && IsAlignedCandidate(lines[j]) && lines[j].Segments.Count == n
                && lines[j].BoxId == lines[i].BoxId && lines[j - 1].Baseline - lines[j].Baseline <= Math.Max(lines[j].Size, stats.BodySize) * 3.2
                && lines[j - 1].Baseline > lines[j].Baseline)
            {
                j++;
            }

            if (j - i >= 3)
            {
                var run = lines.GetRange(i, j - i);
                var table = BuildAligned(run, n);
                if (table is not null)
                {
                    yield return table;
                    i = j;
                    continue;
                }
            }

            i++;
        }
    }

    private static bool IsAlignedCandidate(PdfLine line) => line.Segments.Count >= 2 && !line.IsMono && line.BarId is null;

    private static DetectedTable? BuildAligned(List<PdfLine> run, int n)
    {
        for (var k = 0; k < n; k++)
        {
            var lefts = run.Select(l => l.Segments[k].Left).ToList();
            var rights = run.Select(l => l.Segments[k].Right).ToList();
            var centers = run.Select(l => l.Segments[k].Center).ToList();
            var aligned = lefts.Max() - lefts.Min() <= 3 || rights.Max() - rights.Min() <= 3 || centers.Max() - centers.Min() <= 3;
            if (!aligned)
            {
                return null;
            }

            if (k > 0)
            {
                var prevRight = run.Max(l => l.Segments[k - 1].Right);
                var minLeft = run.Min(l => l.Segments[k].Left);
                if (minLeft - prevRight < run[0].Size * 0.5)
                {
                    return null;
                }
            }
        }

        // Prose split by a gutter would have long segments everywhere; a table has at least one column of short cells.
        var shortColumn = Enumerable.Range(0, n).Any(k => run.Average(l => l.Segments[k].Words.Count) <= 4);
        if (!shortColumn)
        {
            return null;
        }

        var table = new DetectedTable { Top = run[0].Top };
        foreach (var line in run)
        {
            table.Lines.Add(line);
            var row = new DetectedRow();
            foreach (var segment in line.Segments)
            {
                row.Cells.Add([.. segment.Words]);
            }

            table.Rows.Add(row);
        }

        var firstBold = BoldFraction(table.Rows[0]);
        var restBold = table.Rows.Skip(1).Select(BoldFraction).Average();
        table.Rows[0].IsHeader = firstBold >= 0.6 && restBold < 0.5;
        ComputeAlignments(table, n);
        return table;
    }
}
