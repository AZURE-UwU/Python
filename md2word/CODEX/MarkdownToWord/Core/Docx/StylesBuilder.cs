using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Wordprocessing;

namespace MarkdownToWord.Core.Docx;

/// <summary>样式表构造：一次性生成 styles.xml 的全部内容。</summary>
internal sealed class StylesBuilder
{
    public const string Normal = "Normal";
    public const string TitleStyle = "Title";
    public const string QuoteStyle = "Quote";
    public const string CodeBlockStyle = "CodeBlock";
    public const string CodeCharStyle = "CodeChar";
    public const string ListParagraphStyle = "ListParagraph";
    public const string TableTextStyle = "TableText";
    public const string TableHeaderTextStyle = "TableHeaderText";
    public const string ImageCaptionStyle = "ImageCaption";
    public const string HyperlinkStyle = "Hyperlink";
    public const string FootnoteTextStyle = "FootnoteText";
    public const string FootnoteReferenceStyle = "FootnoteReference";
    public const string TableGridStyle = "TableGrid";

    private readonly ConversionOptions _opt;
    private readonly string _bodyHalf;
    private readonly string _codeHalf;

    public StylesBuilder(ConversionOptions options)
    {
        _opt = options;
        _bodyHalf = TextUtils.HalfPoints(options.BodyFontSizePt);
        _codeHalf = TextUtils.HalfPoints(options.CodeFontSizePt);
    }

    public static string HeadingStyleId(int level) => $"Heading{Math.Clamp(level, 1, 6)}";

    public Styles Build()
    {
        var styles = new Styles
        { };

        styles.AppendChild(BuildDocDefaults());
        styles.AppendChild(ParagraphStyle(Normal, "Normal", null, null, null, null, false, 0, false));
        styles.AppendChild(BuildTitle());
        for (var level = 1; level <= 6; level++)
        {
            styles.AppendChild(BuildHeading(level));
        }

        styles.AppendChild(BuildListParagraph());
        styles.AppendChild(BuildQuote());
        styles.AppendChild(BuildCodeBlock());
        styles.AppendChild(BuildTableText());
        styles.AppendChild(BuildTableHeaderText());
        styles.AppendChild(BuildImageCaption());
        styles.AppendChild(BuildHyperlink());
        styles.AppendChild(BuildCodeChar());
        styles.AppendChild(BuildFootnoteText());
        styles.AppendChild(BuildFootnoteReference());
        styles.AppendChild(BuildTableGrid());
        return styles;
    }

    // ---------------------------------------------------------------- 默认值

    private DocDefaults BuildDocDefaults()
    {
        var line = ((int)Math.Round(240 * _opt.LineSpacing, MidpointRounding.AwayFromZero)).ToString();
        return new DocDefaults(
            new RunPropertiesDefault(
                new RunPropertiesBaseStyle(
                    BuildRunFonts(_opt.BodyFont, _opt.BodyFontEastAsia),
                    new FontSize { Val = _bodyHalf },
                    new FontSizeComplexScript { Val = _bodyHalf })),
            new ParagraphPropertiesDefault(
                new ParagraphPropertiesBaseStyle(
                    new SpacingBetweenLines
                    {
                        After = TextUtils.PtToTwips(_opt.ParagraphSpacingAfterPt).ToString(),
                        Line = line,
                        LineRule = LineSpacingRuleValues.Auto,
                    })));
    }

    private Style BuildTitle()
    {
        var size = TextUtils.HalfPoints(_opt.BodyFontSizePt * 2.2);
        var rp = new StyleRunProperties(
            BuildRunFonts(_opt.HeadingFont, _opt.HeadingFontEastAsia),
            new Bold(),
            new Color { Val = "1F3864" },
            new FontSize { Val = size },
            new FontSizeComplexScript { Val = size });

        var pp = new StyleParagraphProperties(
            new KeepNext(),
            new SpacingBetweenLines { Before = "0", After = "240", Line = "240", LineRule = LineSpacingRuleValues.Auto },
            new OutlineLevel { Val = 9 });

        return ParagraphStyle(TitleStyle, "Title", Normal, Normal, pp, rp, false, 10, true);
    }

    private Style BuildHeading(int level)
    {
        var scale = level switch
        {
            1 => 2.0,
            2 => 1.6,
            3 => 1.36,
            4 => 1.2,
            5 => 1.08,
            _ => 1.0,
        };

        var size = TextUtils.HalfPoints(_opt.BodyFontSizePt * scale);
        var before = level switch { 1 => 320, 2 => 280, 3 => 240, 4 => 200, 5 => 160, _ => 160 };
        var after = level switch { 1 => 160, 2 => 140, 3 => 120, 4 => 100, 5 => 80, _ => 80 };
        var color = level <= 3 ? "1F3864" : "2F5496";

        var rp = new StyleRunProperties(
            BuildRunFonts(_opt.HeadingFont, _opt.HeadingFontEastAsia),
            new Bold(),
            new Color { Val = color },
            new FontSize { Val = size },
            new FontSizeComplexScript { Val = size });

        var pp = new StyleParagraphProperties(
            new KeepNext(),
            new KeepLines());

        if (level == 1)
        {
            pp.Append(new ParagraphBorders(
                new BottomBorder
                {
                    Val = BorderValues.Single,
                    Size = 6,
                    Space = 4,
                    Color = "BFD0E4",
                }));
        }

        pp.Append(
            new SpacingBetweenLines
            {
                Before = before.ToString(),
                After = after.ToString(),
                Line = ((int)Math.Round(240 * Math.Min(_opt.LineSpacing, 1.15))).ToString(),
                LineRule = LineSpacingRuleValues.Auto,
            });

        pp.Append(new OutlineLevel { Val = level - 1 });

        return ParagraphStyle(
            HeadingStyleId(level),
            $"heading {level}",
            Normal,
            Normal,
            pp,
            rp,
            false,
            9,
            level == 1);
    }

    private Style BuildListParagraph()
    {
        var pp = new StyleParagraphProperties(
            new SpacingBetweenLines
            {
                Before = "0",
                After = "60",
                Line = ((int)Math.Round(240 * _opt.LineSpacing)).ToString(),
                LineRule = LineSpacingRuleValues.Auto,
            },
            new ContextualSpacing());

        return ParagraphStyle(ListParagraphStyle, "List Paragraph", Normal, Normal, pp, null, false, 34, false);
    }

    private Style BuildQuote()
    {
        var rp = new StyleRunProperties(new Italic(), new Color { Val = "57606A" });

        var pp = new StyleParagraphProperties(
            new KeepLines(),
            new ParagraphBorders(
                new LeftBorder
                {
                    Val = BorderValues.Single,
                    Size = 18,
                    Space = 10,
                    Color = "D0D7DE",
                }),
            new Shading { Val = ShadingPatternValues.Clear, Fill = "F6F8FA" },
            new SpacingBetweenLines { Before = "120", After = "120", Line = "276", LineRule = LineSpacingRuleValues.Auto },
            new Indentation { Left = "360", Right = "240" });

        return ParagraphStyle(QuoteStyle, "Quote", Normal, Normal, pp, rp, false, 29, false);
    }

    private Style BuildCodeBlock()
    {
        var rp = new StyleRunProperties(
            BuildRunFonts(_opt.MonoFont, _opt.MonoFont),
            new Color { Val = "24292F" },
            new FontSize { Val = _codeHalf },
            new FontSizeComplexScript { Val = _codeHalf });

        var pp = new StyleParagraphProperties(
            new KeepLines(),
            new ParagraphBorders(
                new TopBorder { Val = BorderValues.Single, Size = 4, Space = 6, Color = "D8DEE4" },
                new LeftBorder { Val = BorderValues.Single, Size = 4, Space = 6, Color = "D8DEE4" },
                new BottomBorder { Val = BorderValues.Single, Size = 4, Space = 6, Color = "D8DEE4" },
                new RightBorder { Val = BorderValues.Single, Size = 4, Space = 6, Color = "D8DEE4" }),
            new Shading { Val = ShadingPatternValues.Clear, Fill = "F6F8FA" },
            new SpacingBetweenLines { Before = "80", After = "160", Line = "240", LineRule = LineSpacingRuleValues.Auto },
            new Indentation { Left = "120", Right = "120" });

        return ParagraphStyle(CodeBlockStyle, "Code Block", Normal, Normal, pp, rp, true, 20, false);
    }

    private Style BuildTableText()
    {
        var pp = new StyleParagraphProperties(
            new SpacingBetweenLines { Before = "30", After = "30", Line = "252", LineRule = LineSpacingRuleValues.Auto });

        return ParagraphStyle(TableTextStyle, "Table Text", Normal, Normal, pp, null, true, 21, false);
    }

    private Style BuildTableHeaderText()
    {
        var rp = new StyleRunProperties(new Bold(), new Color { Val = "24292F" });
        var pp = new StyleParagraphProperties(
            new SpacingBetweenLines { Before = "30", After = "30", Line = "252", LineRule = LineSpacingRuleValues.Auto });

        return ParagraphStyle(TableHeaderTextStyle, "Table Header Text", TableTextStyle, TableTextStyle, pp, rp, true, 22, false);
    }

    private Style BuildImageCaption()
    {
        var size = TextUtils.HalfPoints(Math.Max(8, _opt.BodyFontSizePt - 1.5));
        var rp = new StyleRunProperties(
            new Italic(),
            new Color { Val = "57606A" },
            new FontSize { Val = size });

        var pp = new StyleParagraphProperties(
            new SpacingBetweenLines { Before = "40", After = "160", Line = "240", LineRule = LineSpacingRuleValues.Auto },
            new Justification { Val = JustificationValues.Center });

        return ParagraphStyle(ImageCaptionStyle, "Image Caption", Normal, Normal, pp, rp, true, 23, false);
    }

    private Style BuildHyperlink()
    {
        var rp = new StyleRunProperties(
            new Color { Val = "0969DA" },
            new Underline { Val = UnderlineValues.Single });

        return CharacterStyle(HyperlinkStyle, "Hyperlink", null, rp, false, 0);
    }

    private Style BuildCodeChar()
    {
        var size = TextUtils.HalfPoints(Math.Max(8, _opt.BodyFontSizePt - 0.5));
        var rp = new StyleRunProperties(
            BuildRunFonts(_opt.MonoFont, _opt.MonoFont),
            new Color { Val = "B31D28" },
            new FontSize { Val = size },
            new FontSizeComplexScript { Val = size },
            new Shading { Val = ShadingPatternValues.Clear, Fill = "F1F2F4" });

        return CharacterStyle(CodeCharStyle, "Code Char", null, rp, false, 35);
    }

    private Style BuildFootnoteText()
    {
        var rp = new StyleRunProperties(new FontSize { Val = "18" }, new FontSizeComplexScript { Val = "18" });
        var pp = new StyleParagraphProperties(
            new SpacingBetweenLines { Before = "0", After = "0", Line = "240", LineRule = LineSpacingRuleValues.Auto });

        return ParagraphStyle(FootnoteTextStyle, "footnote text", Normal, Normal, pp, rp, false, 99, false);
    }

    private Style BuildFootnoteReference()
    {
        var rp = new StyleRunProperties(new VerticalTextAlignment { Val = VerticalPositionValues.Superscript });
        return CharacterStyle(FootnoteReferenceStyle, "footnote reference", null, rp, false, 99);
    }

    private Style BuildTableGrid()
    {
        var style = new Style
        {
            Type = StyleValues.Table,
            StyleId = TableGridStyle,
            Default = true,
            CustomStyle = false,
        };

        style.AppendChild(new StyleName { Val = "Table Grid" });
        style.AppendChild(new StyleTableProperties(
            new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 4, Color = "BFBFBF" },
                new LeftBorder { Val = BorderValues.Single, Size = 4, Color = "BFBFBF" },
                new BottomBorder { Val = BorderValues.Single, Size = 4, Color = "BFBFBF" },
                new RightBorder { Val = BorderValues.Single, Size = 4, Color = "BFBFBF" },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4, Color = "D9D9D9" },
                new InsideVerticalBorder { Val = BorderValues.Single, Size = 4, Color = "D9D9D9" })));

        return style;
    }

    // ---------------------------------------------------------------- 辅助构造

    private static RunFonts BuildRunFonts(string ascii, string eastAsia)
    {
        return new RunFonts
        {
            Ascii = ascii,
            HighAnsi = ascii,
            EastAsia = string.IsNullOrWhiteSpace(eastAsia) ? ascii : eastAsia,
            ComplexScript = ascii,
        };
    }

    private static Style ParagraphStyle(
        string id,
        string name,
        string? basedOn,
        string? next,
        StyleParagraphProperties? paragraphProperties,
        StyleRunProperties? runProperties,
        bool isCustom,
        int uiPriority,
        bool primary)
    {
        var style = new Style
        {
            Type = StyleValues.Paragraph,
            StyleId = id,
            CustomStyle = isCustom,
        };

        style.AppendChild(new StyleName { Val = name });
        if (basedOn is not null)
        {
            style.AppendChild(new BasedOn { Val = basedOn });
        }

        if (next is not null)
        {
            style.AppendChild(new NextParagraphStyle { Val = next });
        }

        if (uiPriority > 0)
        {
            style.AppendChild(new UIPriority { Val = uiPriority });
        }

        if (primary)
        {
            style.AppendChild(new PrimaryStyle());
        }

        if (paragraphProperties is not null)
        {
            style.AppendChild(paragraphProperties);
        }

        if (runProperties is not null)
        {
            style.AppendChild(runProperties);
        }

        return style;
    }

    private static Style CharacterStyle(
        string id,
        string name,
        string? basedOn,
        StyleRunProperties? runProperties,
        bool isCustom,
        int uiPriority)
    {
        var style = new Style
        {
            Type = StyleValues.Character,
            StyleId = id,
            CustomStyle = isCustom,
        };

        style.AppendChild(new StyleName { Val = name });
        if (basedOn is not null)
        {
            style.AppendChild(new BasedOn { Val = basedOn });
        }

        if (uiPriority > 0)
        {
            style.AppendChild(new UIPriority { Val = uiPriority });
        }

        if (runProperties is not null)
        {
            style.AppendChild(runProperties);
        }

        return style;
    }
}
