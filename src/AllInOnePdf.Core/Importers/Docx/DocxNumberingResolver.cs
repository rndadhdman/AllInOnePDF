using System.Globalization;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace AllInOnePdf.Core.Importers.Docx;

internal readonly record struct ListLevelInfo(bool Ordered, int Start, int IndentTwips);

/// <summary><c>numId</c> → abstract numbering → level: bullet or numbered, start value (with overrides) and indentation.</summary>
internal sealed class DocxNumberingResolver
{
    private readonly Dictionary<int, NumberingInstance> _instances = [];
    private readonly Dictionary<int, AbstractNum> _abstracts = [];
    private readonly Dictionary<(int, int), ListLevelInfo?> _cache = [];

    public DocxNumberingResolver(MainDocumentPart main)
    {
        var numbering = main.NumberingDefinitionsPart?.Numbering;
        if (numbering is null)
        {
            return;
        }

        foreach (var a in numbering.Elements<AbstractNum>())
        {
            if (a.AbstractNumberId?.Value is { } id)
            {
                _abstracts.TryAdd(id, a);
            }
        }

        foreach (var n in numbering.Elements<NumberingInstance>())
        {
            if (n.NumberID?.Value is { } id)
            {
                _instances.TryAdd(id, n);
            }
        }

        // numStyleLink: an abstract list that only points at a numbering style; follow it to the real definition.
        foreach (var (id, a) in _abstracts.ToList())
        {
            var link = a.GetFirstChild<NumberingStyleLink>()?.Val?.Value;
            if (link is null || a.Elements<Level>().Any())
            {
                continue;
            }

            var target = _abstracts.Values.FirstOrDefault(x => x.GetFirstChild<StyleLink>()?.Val?.Value == link && x.Elements<Level>().Any());
            if (target is not null)
            {
                _abstracts[id] = target;
            }
        }
    }

    public ListLevelInfo? Get(int numId, int level)
    {
        if (numId == 0)
        {
            return null;
        }

        if (_cache.TryGetValue((numId, level), out var cached))
        {
            return cached;
        }

        ListLevelInfo? result = null;
        if (_instances.TryGetValue(numId, out var instance) && instance.AbstractNumId?.Val?.Value is { } abstractId && _abstracts.TryGetValue(abstractId, out var abs))
        {
            var lvl = abs.Elements<Level>().FirstOrDefault(l => l.LevelIndex?.Value == level);
            var over = instance.Elements<LevelOverride>().FirstOrDefault(o => o.LevelIndex?.Value == level);
            if (over?.Level is { } overrideLevel)
            {
                lvl = overrideLevel;
            }

            var format = lvl?.NumberingFormat?.Val?.Value;
            var text = lvl?.LevelText?.Val?.Value ?? string.Empty;
            var ordered = format is not null && format != NumberFormatValues.Bullet && format != NumberFormatValues.None
                && text.Contains('%', StringComparison.Ordinal);
            var start = over?.StartOverrideNumberingValue?.Val?.Value ?? lvl?.StartNumberingValue?.Val?.Value ?? 1;
            var indent = lvl?.PreviousParagraphProperties?.Indentation is { } ind
                ? ParseTwips(ind.Left?.Value ?? ind.Start?.Value)
                : 720 * (level + 1);
            result = new ListLevelInfo(ordered, start, indent);
        }

        _cache[(numId, level)] = result;
        return result;
    }

    public static int ParseTwips(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
