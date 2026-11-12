namespace AllInOnePdf.Core.Importers.Pdf;

/// <summary>Document-wide typography statistics: body size, body line pitch and the paragraph-break threshold.</summary>
internal sealed class FontStatistics
{
    /// <summary>Most common point size by letter count (proportional fonts only when there are any).</summary>
    public double BodySize { get; private init; } = 11;

    /// <summary>Most common baseline distance between consecutive body-size lines.</summary>
    public double BodyPitch { get; private init; } = 15.4;

    /// <summary>Line pitch as a multiple of the font size (clamped to a sane range).</summary>
    public double PitchFactor => Math.Clamp(BodyPitch / Math.Max(1, BodySize), 1.0, 2.2);

    /// <summary>Largest baseline distance (relative to the font size) that still continues a paragraph.</summary>
    public double ParagraphGapFactor => PitchFactor * 1.3;

    public static FontStatistics Compute(IEnumerable<PageContent> pages)
    {
        var sizes = new Dictionary<double, int>();
        var monoSizes = new Dictionary<double, int>();
        var pageList = pages as IReadOnlyList<PageContent> ?? pages.ToList();
        foreach (var page in pageList)
        {
            foreach (var line in page.ActiveLines)
            {
                var key = Round(line.Size);
                var target = line.IsMono ? monoSizes : sizes;
                target[key] = target.GetValueOrDefault(key) + line.LetterCount;
            }
        }

        var source = sizes.Count > 0 ? sizes : monoSizes;
        if (source.Count == 0)
        {
            return new FontStatistics();
        }

        var body = source.MaxBy(kv => kv.Value).Key;

        var pitches = new Dictionary<double, int>();
        foreach (var page in pageList)
        {
            PdfLine? previous = null;
            foreach (var line in page.ActiveLines)
            {
                if (previous is not null && Math.Abs(previous.Size - body) <= 0.5 && Math.Abs(line.Size - body) <= 0.5)
                {
                    var delta = previous.Baseline - line.Baseline;
                    if (delta > body * 0.9 && delta < body * 2.5)
                    {
                        var key = Math.Round(delta * 2) / 2;
                        pitches[key] = pitches.GetValueOrDefault(key) + 1;
                    }
                }

                previous = line;
            }
        }

        // The line pitch is the smallest distance that occurs often; larger frequent distances are paragraph or
        // list-item spacing (which can outnumber plain line breaks in list-heavy documents).
        double pitch;
        if (pitches.Count == 0)
        {
            pitch = body * 1.4;
        }
        else
        {
            var top = pitches.Values.Max();
            pitch = pitches.Where(kv => kv.Value >= Math.Max(2, top * 0.2)).Select(kv => kv.Key).DefaultIfEmpty(pitches.MaxBy(kv => kv.Value).Key).Min();
        }
        return new FontStatistics { BodySize = body, BodyPitch = pitch };
    }

    public static double Round(double size) => Math.Round(size * 2) / 2;
}
