using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Wordprocessing;

namespace AllInOnePdf.Core.Exporters.Docx;

/// <summary>
/// Run formatting appended in schema order, so the same description fills <c>w:rPr</c> in styles, doc defaults,
/// numbering levels and runs without the validator complaining about element order.
/// </summary>
internal sealed class RunFormat
{
    public string? StyleId { get; set; }
    public string? Font { get; set; }
    public bool? Bold { get; set; }
    public bool? Italic { get; set; }
    public bool Caps { get; set; }
    public bool SmallCaps { get; set; }
    public bool Strike { get; set; }
    public bool NoProof { get; set; }
    public string? Color { get; set; }
    /// <summary>Letter spacing in points.</summary>
    public double? SpacingPt { get; set; }
    public double? SizePt { get; set; }
    public bool Highlight { get; set; }
    public bool Underline { get; set; }
    public string? Shading { get; set; }
    public bool Superscript { get; set; }
    public bool Subscript { get; set; }
    public string? Language { get; set; }

    public T AppendTo<T>(T target)
        where T : OpenXmlCompositeElement
    {
        if (StyleId is not null)
        {
            target.AppendChild(new RunStyle { Val = StyleId });
        }

        if (Font is not null)
        {
            target.AppendChild(new RunFonts { Ascii = Font, HighAnsi = Font, ComplexScript = Font, EastAsia = Font });
        }

        if (Bold is { } b)
        {
            target.AppendChild(new Bold { Val = OnOffValue.FromBoolean(b) });
            target.AppendChild(new BoldComplexScript { Val = OnOffValue.FromBoolean(b) });
        }

        if (Italic is { } i)
        {
            target.AppendChild(new Italic { Val = OnOffValue.FromBoolean(i) });
            target.AppendChild(new ItalicComplexScript { Val = OnOffValue.FromBoolean(i) });
        }

        if (Caps)
        {
            target.AppendChild(new Caps());
        }

        if (SmallCaps)
        {
            target.AppendChild(new SmallCaps());
        }

        if (Strike)
        {
            target.AppendChild(new Strike());
        }

        if (NoProof)
        {
            target.AppendChild(new NoProof());
        }

        if (DocxUnits.Hex(Color) is { } color)
        {
            target.AppendChild(new Color { Val = color });
        }

        if (SpacingPt is { } sp && Math.Abs(sp) >= 0.05)
        {
            target.AppendChild(new Spacing { Val = DocxUnits.Twips(sp) });
        }

        if (SizePt is { } size)
        {
            target.AppendChild(new FontSize { Val = DocxUnits.HalfPoints(size) });
            target.AppendChild(new FontSizeComplexScript { Val = DocxUnits.HalfPoints(size) });
        }

        if (Highlight && target is RunProperties)
        {
            target.AppendChild(new Highlight { Val = HighlightColorValues.Yellow });
        }

        if (Underline)
        {
            target.AppendChild(new Underline { Val = UnderlineValues.Single });
        }

        if (DocxUnits.Hex(Shading) is { } fill)
        {
            target.AppendChild(new Shading { Val = ShadingPatternValues.Clear, Color = "auto", Fill = fill });
        }

        if (Superscript || Subscript)
        {
            target.AppendChild(new VerticalTextAlignment { Val = Superscript ? VerticalPositionValues.Superscript : VerticalPositionValues.Subscript });
        }

        if (Language is not null)
        {
            target.AppendChild(new Languages { Val = Language });
        }

        return target;
    }

    public RunProperties ToRunProperties() => AppendTo(new RunProperties());

    public bool IsEmpty => StyleId is null && Font is null && Bold is null && Italic is null && !Caps && !SmallCaps && !Strike && !NoProof
        && Color is null && SpacingPt is null && SizePt is null && !Highlight && !Underline && Shading is null && !Superscript && !Subscript && Language is null;
}

/// <summary>Side of a paragraph border.</summary>
internal readonly record struct BorderSpec(string Color, double WidthPt, double SpacePt = 4);

/// <summary>Paragraph formatting appended in schema order (see <see cref="RunFormat"/>).</summary>
internal sealed class ParaFormat
{
    public string? StyleId { get; set; }
    public bool KeepNext { get; set; }
    public bool KeepLines { get; set; }
    public bool PageBreakBefore { get; set; }
    public bool? WidowControl { get; set; }
    public (int NumId, int Level)? Numbering { get; set; }
    public BorderSpec? BorderTop { get; set; }
    public BorderSpec? BorderLeft { get; set; }
    public BorderSpec? BorderBottom { get; set; }
    public BorderSpec? BorderRight { get; set; }
    public string? Shading { get; set; }
    /// <summary>Tab stops: position in points and alignment.</summary>
    public List<(double Pt, TabStopValues Kind)> Tabs { get; } = [];
    public double? SpaceBeforePt { get; set; }
    public double? SpaceAfterPt { get; set; }
    public double? LineHeight { get; set; }
    public double? IndentLeftPt { get; set; }
    public double? IndentRightPt { get; set; }
    public double? FirstLinePt { get; set; }
    public double? HangingPt { get; set; }
    public bool ContextualSpacing { get; set; }
    public JustificationValues? Justification { get; set; }
    public int? OutlineLevel { get; set; }

    public T AppendTo<T>(T target)
        where T : OpenXmlCompositeElement
    {
        if (StyleId is not null)
        {
            target.AppendChild(new ParagraphStyleId { Val = StyleId });
        }

        if (KeepNext)
        {
            target.AppendChild(new KeepNext());
        }

        if (KeepLines)
        {
            target.AppendChild(new KeepLines());
        }

        if (PageBreakBefore)
        {
            target.AppendChild(new PageBreakBefore());
        }

        if (WidowControl is { } wc)
        {
            target.AppendChild(new WidowControl { Val = OnOffValue.FromBoolean(wc) });
        }

        if (Numbering is { } num)
        {
            target.AppendChild(new NumberingProperties(
                new NumberingLevelReference { Val = num.Level },
                new NumberingId { Val = num.NumId }));
        }

        if (BorderTop is not null || BorderLeft is not null || BorderBottom is not null || BorderRight is not null)
        {
            var borders = new ParagraphBorders();
            if (BorderTop is { } t)
            {
                borders.AppendChild(Border<TopBorder>(t));
            }

            if (BorderLeft is { } l)
            {
                borders.AppendChild(Border<LeftBorder>(l));
            }

            if (BorderBottom is { } bo)
            {
                borders.AppendChild(Border<BottomBorder>(bo));
            }

            if (BorderRight is { } r)
            {
                borders.AppendChild(Border<RightBorder>(r));
            }

            target.AppendChild(borders);
        }

        if (DocxUnits.Hex(Shading) is { } fill)
        {
            target.AppendChild(new Shading { Val = ShadingPatternValues.Clear, Color = "auto", Fill = fill });
        }

        if (Tabs.Count > 0)
        {
            target.AppendChild(new Tabs(Tabs.Select(t => new TabStop { Val = t.Kind, Position = DocxUnits.Twips(t.Pt) })));
        }

        if (SpaceBeforePt is not null || SpaceAfterPt is not null || LineHeight is not null)
        {
            var spacing = new SpacingBetweenLines();
            if (SpaceBeforePt is { } before)
            {
                spacing.Before = DocxUnits.TwipsString(Math.Max(0, before));
            }

            if (SpaceAfterPt is { } after)
            {
                spacing.After = DocxUnits.TwipsString(Math.Max(0, after));
            }

            if (LineHeight is { } line)
            {
                spacing.Line = DocxUnits.Line(line);
                spacing.LineRule = LineSpacingRuleValues.Auto;
            }

            target.AppendChild(spacing);
        }

        if (IndentLeftPt is not null || IndentRightPt is not null || FirstLinePt is not null || HangingPt is not null)
        {
            var ind = new Indentation();
            if (IndentLeftPt is { } left)
            {
                ind.Left = DocxUnits.TwipsString(left);
            }

            if (IndentRightPt is { } right)
            {
                ind.Right = DocxUnits.TwipsString(right);
            }

            if (HangingPt is { } hanging)
            {
                ind.Hanging = DocxUnits.TwipsString(hanging);
            }
            else if (FirstLinePt is { } first)
            {
                ind.FirstLine = DocxUnits.TwipsString(Math.Max(0, first));
            }

            target.AppendChild(ind);
        }

        if (ContextualSpacing)
        {
            target.AppendChild(new ContextualSpacing());
        }

        if (Justification is { } jc)
        {
            target.AppendChild(new Justification { Val = jc });
        }

        if (OutlineLevel is { } level)
        {
            target.AppendChild(new OutlineLevel { Val = level });
        }

        return target;
    }

    public ParagraphProperties ToParagraphProperties() => AppendTo(new ParagraphProperties());

    private static T Border<T>(BorderSpec spec)
        where T : BorderType, new() => new()
        {
            Val = BorderValues.Single,
            Size = DocxUnits.Eighths(spec.WidthPt),
            Space = DocxUnits.BorderSpace(spec.SpacePt),
            Color = DocxUnits.Hex(spec.Color, "000000"),
        };
}
