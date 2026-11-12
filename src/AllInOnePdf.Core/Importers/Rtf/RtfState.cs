using AllInOnePdf.Core.Documents;

namespace AllInOnePdf.Core.Importers.Rtf;

/// <summary>Where the text of the current group goes.</summary>
internal enum RtfDestination
{
    /// <summary>Document content (body, footnote, field result).</summary>
    Content,
    FontTable,
    StyleSheet,
    ListTable,
    ListOverrideTable,
    Info,
    InfoField,
    CreationTime,
    FieldInstruction,
    Picture,
    LegacyListText,
    LegacyList,
    UnicodePair,
    Bookmark,
    /// <summary>Text and properties are read but not used.</summary>
    Ignore,
}

/// <summary>What has to happen when the group that started a destination closes.</summary>
internal enum RtfCloser
{
    None,
    FontTable,
    StyleEntry,
    List,
    ListLevel,
    ListOverride,
    InfoField,
    Field,
    FieldResult,
    Picture,
    Footnote,
    LegacyList,
    Bookmark,
}

internal enum LegacyListKind { None, Bullet, Numbered }

/// <summary>Character formatting of the current group.</summary>
internal record struct RtfCharState(
    bool Bold,
    bool Italic,
    bool Underline,
    bool Strike,
    bool Superscript,
    bool Subscript,
    bool SmallCaps,
    bool Highlight,
    bool Hidden,
    int Font,
    int FontSize)
{
    /// <summary>Font -1 means "the document default font" (<c>\deffN</c>).</summary>
    public static RtfCharState Default => new(false, false, false, false, false, false, false, false, false, -1, 24);

    public readonly InlineStyle Style =>
        (Bold ? InlineStyle.Bold : 0)
        | (Italic ? InlineStyle.Italic : 0)
        | (Underline ? InlineStyle.Underline : 0)
        | (Strike ? InlineStyle.Strikethrough : 0)
        | (Superscript ? InlineStyle.Superscript : 0)
        | (Subscript ? InlineStyle.Subscript : 0)
        | (SmallCaps ? InlineStyle.SmallCaps : 0)
        | (Highlight ? InlineStyle.Highlight : 0);
}

/// <summary>Paragraph formatting of the current group.</summary>
internal record struct RtfParaState(
    TextAlignment Alignment,
    int OutlineLevel,
    int StyleId,
    bool InTable,
    int NestLevel,
    int ListLevel,
    int ListOverride,
    int LeftIndent,
    int FirstIndent,
    bool PageBreakBefore,
    LegacyListKind Legacy,
    int LegacyLevel,
    int LegacyStart)
{
    public static RtfParaState Default => new(TextAlignment.Left, -1, 0, false, 0, 0, 0, 0, 0, false, LegacyListKind.None, 0, 1);

    public readonly bool IsInTable => InTable || NestLevel >= 1;
}

/// <summary>Everything a group inherits from its parent, plus what its own closing brace must finish.</summary>
internal record struct RtfGroupState(
    RtfCharState Char,
    RtfParaState Para,
    int UnicodeSkip,
    RtfDestination Destination,
    RtfDestination SavedDestination,
    int LinkId,
    RtfCloser Closer);

internal sealed class RtfFont
{
    public int Number { get; init; }
    public string Name { get; set; } = string.Empty;
    public int? Charset { get; set; }
    public int? CodePage { get; set; }
    public bool Modern { get; set; }

    public bool IsSymbol => Charset == 2 || Name.Equals("Symbol", StringComparison.OrdinalIgnoreCase) || IsWingdings;

    public bool IsWingdings => Name.StartsWith("Wingdings", StringComparison.OrdinalIgnoreCase) || Name.StartsWith("Webdings", StringComparison.OrdinalIgnoreCase);

    /// <summary>A fixed-pitch code font (by family or well-known name); East Asian fixed-pitch fonts do not count.</summary>
    public bool IsMonospace
    {
        get
        {
            if (RtfEncodings.IsEastAsian(Charset))
            {
                return false;
            }

            var n = Name;
            return (Modern && !IsSymbol)
                || n.Contains("Courier", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Consolas", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Lucida Console", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Lucida Sans Typewriter", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Menlo", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Monaco", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Cascadia", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Inconsolata", StringComparison.OrdinalIgnoreCase)
                || n.EndsWith(" Mono", StringComparison.OrdinalIgnoreCase)
                || n.Contains(" Mono ", StringComparison.OrdinalIgnoreCase)
                || n.Contains(" Code", StringComparison.OrdinalIgnoreCase);
        }
    }
}

internal enum RtfStyleRole { None, Heading, Title, Subtitle, Quote, Code }

internal sealed class RtfStyle
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int OutlineLevel { get; set; } = -1;
    public int BasedOn { get; set; } = -1;
    public int Font { get; set; } = -1;
}

internal sealed class RtfListDefinition
{
    public int Id { get; set; }
    public List<RtfListLevel> Levels { get; } = [];
}

internal sealed class RtfListLevel
{
    public int NumberFormat { get; set; }
    public int StartAt { get; set; } = 1;

    /// <summary>23 = bullet, 255 = no number, 0xFFFF = none; everything else counts.</summary>
    public bool Ordered => NumberFormat is not (23 or 255 or 0xFFFF);
}

/// <summary>Properties of a <c>\pict</c> group while it is read.</summary>
internal sealed class RtfPicture
{
    public string? MediaType { get; set; }
    public bool Drawing { get; set; }
    public int PixelWidth { get; set; }
    public int PixelHeight { get; set; }
    public int GoalWidth { get; set; }
    public int GoalHeight { get; set; }
    public int ScaleX { get; set; } = 100;
    public MemoryStream Data { get; } = new();
    public int PendingNibble { get; set; } = -1;
    public bool TooLarge { get; set; }
}

internal sealed class RtfField
{
    public System.Text.StringBuilder Instruction { get; } = new();
}
