using System.Globalization;
using AllInOnePdf.Core.Themes;
using DocumentFormat.OpenXml.Wordprocessing;

namespace AllInOnePdf.Core.Exporters.Docx;

/// <summary>
/// Numbering definitions: one abstract list each for bullets (glyph per <see cref="BulletStyle"/>), numbers
/// (format per <see cref="NumberStyle"/>), task lists (no marker; the checkbox is text) and, when the theme numbers
/// headings, a multilevel heading list linked to the heading styles. Every model list gets its own instance with a
/// start override so numbering restarts where the model says.
/// </summary>
internal sealed class DocxNumbering
{
    private const int BulletAbstractId = 1;
    private const int OrderedAbstractId = 2;
    private const int TaskAbstractId = 3;
    private const int HeadingAbstractId = 4;

    private readonly PdfTheme _theme;
    private readonly List<(int NumId, int AbstractId, int Level, int? Start)> _instances = [];
    private int _nextNumId = 1;

    public DocxNumbering(PdfTheme theme)
    {
        _theme = theme;
        if (theme.Headings.NumberHeadings)
        {
            HeadingNumId = _nextNumId++;
            _instances.Add((HeadingNumId.Value, HeadingAbstractId, 0, null));
        }
    }

    public int? HeadingNumId { get; }

    public bool HasLists => _instances.Count > 0;

    /// <summary>Creates a list instance; ordered lists restart at <paramref name="start"/> on <paramref name="level"/>.</summary>
    public int NewList(bool ordered, bool task, int level, int start)
    {
        var id = _nextNumId++;
        var abstractId = task ? TaskAbstractId : ordered ? OrderedAbstractId : BulletAbstractId;
        _instances.Add((id, abstractId, level, ordered ? Math.Max(0, start) : null));
        return id;
    }

    public Numbering Build()
    {
        var numbering = new Numbering();
        numbering.AppendChild(Abstract(BulletAbstractId, lvl => BulletLevel(lvl)));
        numbering.AppendChild(Abstract(OrderedAbstractId, lvl => OrderedLevel(lvl)));
        numbering.AppendChild(Abstract(TaskAbstractId, lvl => TaskLevel(lvl)));
        if (HeadingNumId is not null)
        {
            numbering.AppendChild(Abstract(HeadingAbstractId, lvl => HeadingLevel(lvl), levels: 6));
        }

        foreach (var (numId, abstractId, level, start) in _instances)
        {
            var instance = new NumberingInstance(new AbstractNumId { Val = abstractId }) { NumberID = numId };
            if (start is { } s)
            {
                instance.AppendChild(new LevelOverride(new StartOverrideNumberingValue { Val = s }) { LevelIndex = level });
            }

            numbering.AppendChild(instance);
        }

        return numbering;
    }

    private static AbstractNum Abstract(int id, Func<int, Level> level, int levels = 9)
    {
        var a = new AbstractNum(new MultiLevelType { Val = MultiLevelValues.HybridMultilevel }) { AbstractNumberId = id };
        for (var i = 0; i < levels; i++)
        {
            a.AppendChild(level(i));
        }

        return a;
    }

    private Level BulletLevel(int lvl) => ListLevel(lvl, NumberFormatValues.Bullet, Bullet(_theme.Lists.Bullet));

    private Level TaskLevel(int lvl) => ListLevel(lvl, NumberFormatValues.None, string.Empty);

    private Level OrderedLevel(int lvl)
    {
        var token = "%" + (lvl + 1).ToString(CultureInfo.InvariantCulture);
        var (format, text) = _theme.Lists.Numbering switch
        {
            NumberStyle.DecimalParen => (NumberFormatValues.Decimal, token + ")"),
            NumberStyle.LowerAlpha => (NumberFormatValues.LowerLetter, token + "."),
            NumberStyle.UpperAlpha => (NumberFormatValues.UpperLetter, token + "."),
            NumberStyle.LowerRoman => (NumberFormatValues.LowerRoman, token + "."),
            NumberStyle.UpperRoman => (NumberFormatValues.UpperRoman, token + "."),
            _ => (NumberFormatValues.Decimal, token + "."),
        };
        return ListLevel(lvl, format, text);
    }

    private Level ListLevel(int lvl, NumberFormatValues format, string text)
    {
        var indent = Math.Max(9, _theme.Lists.IndentPt);
        var level = new Level
        {
            LevelIndex = lvl,
            StartNumberingValue = new StartNumberingValue { Val = 1 },
            NumberingFormat = new NumberingFormat { Val = format },
            LevelText = new LevelText { Val = text },
            LevelJustification = new LevelJustification { Val = LevelJustificationValues.Left },
            PreviousParagraphProperties = new PreviousParagraphProperties(new Indentation
            {
                Left = DocxUnits.TwipsString(indent * (lvl + 1)),
                Hanging = DocxUnits.TwipsString(indent),
            }),
        };
        var marker = _theme.Lists.MarkerColor ?? _theme.Colors.Accent;
        if (DocxUnits.Hex(marker) is { } color)
        {
            level.NumberingSymbolRunProperties = new NumberingSymbolRunProperties(new Color { Val = color });
        }

        return level;
    }

    private Level HeadingLevel(int lvl)
    {
        var text = string.Join(".", Enumerable.Range(1, lvl + 1).Select(i => "%" + i.ToString(CultureInfo.InvariantCulture)));
        return new Level
        {
            LevelIndex = lvl,
            StartNumberingValue = new StartNumberingValue { Val = 1 },
            NumberingFormat = new NumberingFormat { Val = NumberFormatValues.Decimal },
            ParagraphStyleIdInLevel = new ParagraphStyleIdInLevel { Val = DocxStyleIds.Heading(lvl + 1) },
            LevelSuffix = new LevelSuffix { Val = LevelSuffixValues.Space },
            LevelText = new LevelText { Val = text },
            LevelJustification = new LevelJustification { Val = LevelJustificationValues.Left },
            PreviousParagraphProperties = new PreviousParagraphProperties(new Indentation { Left = "0", FirstLine = "0" }),
        };
    }

    public static string Bullet(BulletStyle style) => style switch
    {
        BulletStyle.Circle => "◦",
        BulletStyle.Square => "▪",
        BulletStyle.Dash => "–",
        BulletStyle.Arrow => "›",
        _ => "•",
    };
}
