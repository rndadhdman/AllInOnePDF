namespace AllInOnePdf.Core.Importers.Pdf;

/// <summary>
/// Groups words into visual lines by vertical overlap (superscripts and subscripts join their line), orders the
/// words left to right and splits each line into segments at wide gaps. Lines come out top to bottom.
/// O(n log n) in the number of words.
/// </summary>
internal static class LineAssembler
{
    /// <summary>How many of the most recent lines a word is compared with (superscripts arrive before their line).</summary>
    private const int Lookback = 4;

    public static List<PdfLine> Assemble(IEnumerable<PdfWord> words, int pageNumber)
    {
        var sorted = words.OrderByDescending(w => w.Baseline).ThenBy(w => w.Left).ToList();
        var lines = new List<PdfLine>();
        var bands = new List<(double Bottom, double Top, double Baseline, double Size)>();

        foreach (var word in sorted)
        {
            var target = -1;
            for (var i = lines.Count - 1; i >= 0 && i >= lines.Count - Lookback; i--)
            {
                var band = bands[i];
                var overlap = Math.Min(band.Top, word.Top) - Math.Max(band.Bottom, word.Bottom);
                var minHeight = Math.Max(0.1, Math.Min(band.Top - band.Bottom, word.Top - word.Bottom));
                var maxSize = Math.Max(band.Size, word.Size);
                if (overlap >= minHeight * 0.5 && Math.Abs(band.Baseline - word.Baseline) < maxSize * 0.6)
                {
                    target = i;
                    break;
                }
            }

            if (target < 0)
            {
                var line = new PdfLine { Page = pageNumber };
                line.Words.Add(word);
                lines.Add(line);
                bands.Add((word.Bottom, word.Top, word.Baseline, word.Size));
            }
            else
            {
                lines[target].Words.Add(word);
                var b = bands[target];

                // Keep the band anchored on the larger text so tiny superscripts do not widen it much.
                var baseline = word.Size > b.Size ? word.Baseline : b.Baseline;
                bands[target] = (Math.Min(b.Bottom, word.Bottom), Math.Max(b.Top, word.Top), baseline, Math.Max(b.Size, word.Size));
            }
        }

        foreach (var line in lines)
        {
            line.Update();
        }

        lines.Sort((a, b) =>
        {
            var c = b.Baseline.CompareTo(a.Baseline);
            return c != 0 ? c : a.Left.CompareTo(b.Left);
        });
        return lines;
    }
}
