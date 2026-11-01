using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Markdig.Extensions.Alerts;
using Markdig.Extensions.Abbreviations;
using Markdig.Extensions.DefinitionLists;
using Markdig.Extensions.Figures;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.SmartyPants;
using Markdig.Extensions.TaskLists;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using A = DocumentFormat.OpenXml.Drawing;
using M = DocumentFormat.OpenXml.Math;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using MdFootnote = Markdig.Extensions.Footnotes.Footnote;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
using W = DocumentFormat.OpenXml.Wordprocessing;
using WP = DocumentFormat.OpenXml.Drawing.Wordprocessing;

namespace MarkdownToWord.Core.Docx;

/// <summary>转换过程中的统计信息。</summary>
public sealed class ConversionStats
{
    public int Headings { get; set; }

    public int Paragraphs { get; set; }

    public int Lists { get; set; }

    public int Tables { get; set; }

    public int CodeBlocks { get; set; }

    public int Equations { get; set; }

    public int Images { get; set; }

    public int Footnotes { get; set; }

    public List<string> Warnings { get; } = [];
}

/// <summary>
/// 把 Markdig 语法树翻译成 WordprocessingML。
/// 所有"Markdown 语法 → Word 排版"的映射都集中在这个类里。
/// </summary>
internal sealed class DocxWriter
{
    private static readonly Regex HtmlTagRegex = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex HtmlImageRegex = new(
        "<img[^>]*src\\s*=\\s*[\"']([^\"']+)[\"'][^>]*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HtmlBreakRegex = new("<br\\s*/?>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly ConversionOptions _opt;
    private readonly StylesBuilder _styles;
    private readonly NumberingBuilder _numbering = new();
    private readonly ImageResolver _images;
    private readonly Dictionary<int, MdFootnote> _footnotes = [];
    private readonly int[] _headingCounters = new int[6];
    private readonly ConversionStats _stats = new();
    private Dictionary<string, string> _frontMatter = new(StringComparer.OrdinalIgnoreCase);

    private MainDocumentPart _main = null!;
    private Body _body = null!;
    private int _contentWidthTwips;
    private int _imageCounter;
    private int _footnoteCounter;

    public DocxWriter(ConversionOptions options, string? baseDirectory)
    {
        _opt = options;
        _styles = new StylesBuilder(options);
        _images = new ImageResolver(options, baseDirectory);
    }

    public ConversionStats Write(MarkdownDocument document, Stream output)
    {
        using var package = WordprocessingDocument.Create(output, WordprocessingDocumentType.Document);
        _main = package.AddMainDocumentPart();
        _main.Document = new W.Document();
        _body = new Body();

        var stylesPart = _main.AddNewPart<StyleDefinitionsPart>();
        stylesPart.Styles = _styles.Build();

        if (_opt.EnableMath)
        {
            var settingsPart = _main.AddNewPart<DocumentSettingsPart>();
            settingsPart.Settings = new Settings(
                new M.MathProperties(new M.MathFont { Val = "Cambria Math" }));
            settingsPart.Settings.Save();
        }

        _contentWidthTwips = ComputeContentWidthTwips();
        _frontMatter = ExtractFrontMatter(document);

        if (_opt.InsertFrontMatterTitle && _frontMatter.TryGetValue("title", out var docTitle) &&
            !string.IsNullOrWhiteSpace(docTitle))
        {
            var titleParagraph = NewParagraph(new Context(), StylesBuilder.TitleStyle);
            titleParagraph.AppendChild(CreateTextRun(docTitle, new RunFormat()));
            _body.AppendChild(titleParagraph);
        }

        var ctx = new Context();
        foreach (var block in document)
        {
            RenderBlock(block, _body, ctx);
        }

        var numberingPart = _main.AddNewPart<NumberingDefinitionsPart>();
        numberingPart.Numbering = _numbering.Build();

        if (_footnotes.Count > 0)
        {
            WriteFootnotes();
        }

        _body.AppendChild(BuildSectionProperties());
        _main.Document.Append(_body);
        _main.Document.Save();

        ApplyPackageProperties(package, document);

        _stats.Warnings.AddRange(_images.Warnings);
        _images.Dispose();
        return _stats;
    }

    // ------------------------------------------------------------- 上下文

    private sealed class Context
    {
        public string? ForcedStyle { get; set; }

        public int ListLevel { get; set; } = -1;

        public int? NumId { get; set; }

        public bool SuppressNumbering { get; set; }

        public bool InTable { get; set; }

        public bool InFootnote { get; set; }

        public Inline? SkipInline { get; set; }

        public Context Clone() => (Context)MemberwiseClone();
    }

    // ------------------------------------------------------------- 块级渲染

    private void RenderBlock(Block block, OpenXmlCompositeElement target, Context ctx)
    {
        switch (block)
        {
            case HeadingBlock heading:
                RenderHeading(heading, target, ctx);
                break;

            case ParagraphBlock paragraph:
                RenderParagraph(paragraph, target, ctx);
                break;

            case MathBlock mathBlock:
                RenderMathBlock(mathBlock, target, ctx);
                break;

            case YamlFrontMatterBlock:
                break;

            case FencedCodeBlock fenced:
                RenderCodeBlock(fenced.Info, GetLeafText(fenced), target, ctx);
                break;

            case CodeBlock code:
                RenderCodeBlock(null, GetLeafText(code), target, ctx);
                break;

            case AlertBlock alert:
                RenderAlert(alert, target, ctx);
                break;

            case QuoteBlock quote:
                RenderQuote(quote, target, ctx);
                break;

            case ListBlock list:
                RenderList(list, target, ctx);
                break;

            case MdTable table:
                RenderTable(table, target, ctx);
                break;

            case ThematicBreakBlock:
                RenderThematicBreak(target);
                break;

            case HtmlBlock html:
                RenderHtmlBlock(GetLeafText(html), target, ctx);
                break;

            case Figure figure:
                RenderFigure(figure, target, ctx);
                break;

            case DefinitionList definitions:
                RenderDefinitionList(definitions, target, ctx);
                break;

            case FootnoteGroup:
            case LinkReferenceDefinitionGroup:
            case BlankLineBlock:
            case EmptyBlock:
                break;

            default:
                RenderUnknown(block, target, ctx);
                break;
        }
    }

    private void RenderUnknown(Block block, OpenXmlCompositeElement target, Context ctx)
    {
        if (block is ContainerBlock container)
        {
            foreach (var child in container)
            {
                RenderBlock(child, target, ctx);
            }

            return;
        }

        if (block is LeafBlock leaf)
        {
            var text = GetLeafText(leaf);
            if (!string.IsNullOrWhiteSpace(text))
            {
                var p = NewParagraph(ctx);
                p.AppendChild(CreateTextRun(text, new RunFormat()));
                target.AppendChild(p);
                _stats.Paragraphs++;
            }
        }
    }

    private void RenderHeading(HeadingBlock heading, OpenXmlCompositeElement target, Context ctx)
    {
        var level = Math.Clamp(heading.Level, 1, 6);
        var styleId = _opt.UseWordHeadingStyles ? StylesBuilder.HeadingStyleId(level) : null;
        var p = NewParagraph(ctx, styleId);

        if (_opt.NumberHeadings)
        {
            var prefix = NextHeadingNumber(level);
            if (prefix.Length > 0)
            {
                var fmt = new RunFormat { Bold = true, Color = level <= 3 ? "1F3864" : "2F5496" };
                p.AppendChild(CreateTextRun(prefix + " ", fmt));
            }
        }

        RenderInlines(heading.Inline, p, ctx, new RunFormat(), skip: null);

        if (!p.HasChildren)
        {
            p.AppendChild(CreateTextRun(GetLeafText(heading), new RunFormat()));
        }

        target.AppendChild(p);
        _stats.Headings++;
    }

    private string NextHeadingNumber(int level)
    {
        _headingCounters[level - 1]++;
        for (var i = level; i < 6; i++)
        {
            _headingCounters[i] = 0;
        }

        var parts = new List<string>();
        for (var i = 0; i < level; i++)
        {
            parts.Add(_headingCounters[i].ToString());
        }

        return string.Join('.', parts);
    }

    private void RenderParagraph(ParagraphBlock paragraph, OpenXmlCompositeElement target, Context ctx)
    {
        var numbered = ctx.ListLevel >= 0 && !ctx.SuppressNumbering && ctx.NumId is > 0;
        var p = NewParagraph(ctx, null, numbered, ctx.NumId ?? 0, ctx.ListLevel);
        RenderInlines(paragraph.Inline, p, ctx, new RunFormat(), ctx.SkipInline);

        if (!p.HasChildren)
        {
            p.AppendChild(CreateTextRun(string.Empty, new RunFormat()));
        }

        target.AppendChild(p);
        _stats.Paragraphs++;
    }

    private void RenderQuote(QuoteBlock quote, OpenXmlCompositeElement target, Context ctx)
    {
        var inner = ctx.Clone();
        inner.ForcedStyle = StylesBuilder.QuoteStyle;

        var any = false;
        foreach (var child in quote)
        {
            RenderBlock(child, target, inner);
            any = true;
        }

        if (!any)
        {
            target.AppendChild(NewParagraph(inner));
        }
    }

    private void RenderAlert(AlertBlock alert, OpenXmlCompositeElement target, Context ctx)
    {
        var kind = alert.Kind.ToString().ToUpperInvariant();
        var (label, color) = kind switch
        {
            "NOTE" => ("注意", "0969DA"),
            "TIP" => ("提示", "1A7F37"),
            "IMPORTANT" => ("重要", "8250DF"),
            "WARNING" => ("警告", "9A6700"),
            "CAUTION" => ("小心", "CF222E"),
            _ => ("提示", "0969DA"),
        };

        var inner = ctx.Clone();
        inner.ForcedStyle = StylesBuilder.QuoteStyle;

        var labelParagraph = NewParagraph(inner);
        labelParagraph.AppendChild(CreateTextRun($"[{label}]", new RunFormat { Bold = true, Color = color }));
        target.AppendChild(labelParagraph);

        foreach (var child in alert)
        {
            RenderBlock(child, target, inner);
        }
    }

    private void RenderThematicBreak(OpenXmlCompositeElement target)
    {
        var properties = new ParagraphProperties(
            new ParagraphBorders(
                new BottomBorder
                {
                    Val = BorderValues.Single,
                    Size = 6,
                    Space = 1,
                    Color = "D0D7DE",
                }),
            new SpacingBetweenLines { Before = "160", After = "160" });

        target.AppendChild(new W.Paragraph(properties));
    }

    private void RenderCodeBlock(string? language, string code, OpenXmlCompositeElement target, Context ctx)
    {
        var p = NewParagraph(ctx, StylesBuilder.CodeBlockStyle);
        var lines = code.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        var count = lines.Length;
        while (count > 1 && lines[count - 1].Length == 0)
        {
            count--;
        }

        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                p.AppendChild(new Run(new Break()));
            }

            var line = lines[i];
            if (!_opt.SyntaxHighlight)
            {
                p.AppendChild(CreateTextRun(line.Length == 0 ? " " : line, new RunFormat { Code = true }, useCodeStyle: true));
                continue;
            }

            foreach (var token in CodeHighlighter.Tokenize(line, language))
            {
                var fmt = new RunFormat
                {
                    Code = true,
                    Color = token.Color,
                    Bold = token.Bold,
                    Italic = token.Italic,
                };
                p.AppendChild(CreateTextRun(token.Text, fmt, useCodeStyle: true));
            }
        }

        if (!p.HasChildren)
        {
            p.AppendChild(CreateTextRun(" ", new RunFormat { Code = true }, useCodeStyle: true));
        }

        target.AppendChild(p);
        _stats.CodeBlocks++;
    }

    private void RenderMathBlock(MathBlock mathBlock, OpenXmlCompositeElement target, Context ctx)
    {
        var latex = GetLeafText(mathBlock).Trim();
        var p = NewParagraph(ctx, null, center: true);

        if (_opt.EnableMath && latex.Length > 0)
        {
            p.AppendChild(LatexToOmml.ToMathParagraph(latex));
            _stats.Equations++;
        }
        else
        {
            p.AppendChild(CreateTextRun(latex, new RunFormat { Code = true }));
        }

        target.AppendChild(p);
    }

    private void RenderHtmlBlock(string html, OpenXmlCompositeElement target, Context ctx)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return;
        }

        var images = HtmlImageRegex.Matches(html);
        if (images.Count > 0)
        {
            foreach (Match match in images)
            {
                var p = NewParagraph(ctx, null, center: true);
                AppendImage(p, match.Groups[1].Value, null);
                target.AppendChild(p);
            }

            html = HtmlImageRegex.Replace(html, string.Empty);
        }

        html = HtmlBreakRegex.Replace(html, "\n");
        var text = HtmlTagRegex.Replace(html, string.Empty);
        text = System.Net.WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, "[ \t]+", " ");
        text = Regex.Replace(text, "\n{3,}", "\n\n").Trim();

        if (text.Length == 0)
        {
            return;
        }

        foreach (var chunk in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var p = NewParagraph(ctx);
            var parts = chunk.Split('\n');
            for (var i = 0; i < parts.Length; i++)
            {
                if (i > 0)
                {
                    p.AppendChild(new Run(new Break()));
                }

                p.AppendChild(CreateTextRun(parts[i], new RunFormat()));
            }

            target.AppendChild(p);
            _stats.Paragraphs++;
        }
    }

    private void RenderFigure(Figure figure, OpenXmlCompositeElement target, Context ctx)
    {
        foreach (var child in figure)
        {
            switch (child)
            {
                case FigureCaption caption:
                    {
                        var p = NewParagraph(ctx, StylesBuilder.ImageCaptionStyle);
                        RenderInlines(caption.Inline, p, ctx, new RunFormat(), skip: null);
                        target.AppendChild(p);
                        break;
                    }

                case ParagraphBlock paragraph:
                    {
                        var p = NewParagraph(ctx, null, center: true);
                        RenderInlines(paragraph.Inline, p, ctx, new RunFormat(), skip: null);
                        target.AppendChild(p);
                        break;
                    }

                default:
                    RenderBlock(child, target, ctx);
                    break;
            }
        }
    }

    private void RenderDefinitionList(DefinitionList list, OpenXmlCompositeElement target, Context ctx)
    {
        foreach (var item in list)
        {
            if (item is not DefinitionItem definitionItem)
            {
                RenderBlock(item, target, ctx);
                continue;
            }

            foreach (var child in definitionItem)
            {
                switch (child)
                {
                    case DefinitionTerm term:
                        {
                            var p = NewParagraph(ctx);
                            RenderInlines(term.Inline, p, ctx, new RunFormat { Bold = true }, skip: null);
                            target.AppendChild(p);
                            break;
                        }

                    case ParagraphBlock paragraph:
                        {
                            var p = NewParagraph(ctx, null, indent: 360);
                            RenderInlines(paragraph.Inline, p, ctx, new RunFormat(), skip: null);
                            target.AppendChild(p);
                            break;
                        }

                    default:
                        RenderBlock(child, target, ctx);
                        break;
                }
            }
        }
    }

    // ------------------------------------------------------------- 列表

    private void RenderList(ListBlock list, OpenXmlCompositeElement target, Context ctx)
    {
        var level = Math.Clamp(ctx.ListLevel + 1, 0, 4);
        var isTask = false;
        foreach (var item in list)
        {
            if (item is ListItemBlock listItem && FindTaskList(listItem) is not null)
            {
                isTask = true;
                break;
            }
        }

        int numId;
        if (isTask)
        {
            numId = _numbering.CreatePlainList();
        }
        else if (list.IsOrdered)
        {
            var start = 1;
            if (!string.IsNullOrEmpty(list.OrderedStart))
            {
                _ = int.TryParse(list.OrderedStart.Trim().TrimEnd('.', ')'), out start);
            }

            numId = _numbering.CreateOrderedList(start);
        }
        else
        {
            numId = _numbering.CreateBulletList();
        }

        foreach (var item in list)
        {
            if (item is ListItemBlock listItem)
            {
                RenderListItem(listItem, numId, level, target, ctx);
            }
            else
            {
                RenderBlock(item, target, ctx);
            }
        }

        _stats.Lists++;
    }

    private static TaskList? FindTaskList(ListItemBlock? item)
    {
        if (item is null || item.Count == 0)
        {
            return null;
        }

        var first = item.FirstOrDefault() as LeafBlock;
        return first?.Inline?.FirstChild as TaskList;
    }

    private void RenderListItem(ListItemBlock item, int numId, int level, OpenXmlCompositeElement target, Context ctx)
    {
        var child = ctx.Clone();
        child.ListLevel = level;
        child.NumId = numId;

        var first = true;
        var emitted = false;

        foreach (var block in item)
        {
            if (block is ListBlock nested)
            {
                var nestedCtx = child.Clone();
                nestedCtx.SuppressNumbering = false;
                nestedCtx.SkipInline = null;
                RenderList(nested, target, nestedCtx);
                first = false;
                continue;
            }

            var blockCtx = child.Clone();
            blockCtx.SuppressNumbering = !first;

            RenderBlock(block, target, blockCtx);
            emitted = true;
            first = false;
        }

        if (!emitted)
        {
            var p = NewParagraph(child, null, true, numId, level);
            p.AppendChild(CreateTextRun(string.Empty, new RunFormat()));
            target.AppendChild(p);
        }
    }

    // ------------------------------------------------------------- 表格

    private void RenderTable(MdTable table, OpenXmlCompositeElement target, Context ctx)
    {
        var textWidth = _contentWidthTwips;
        var columnCount = Math.Max(1, table.ColumnDefinitions.Count);

        var widths = new int[columnCount];
        var weights = new double[columnCount];
        var hasExplicit = false;

        for (var i = 0; i < columnCount; i++)
        {
            var definition = table.ColumnDefinitions[i];
            if (definition.Width > 0)
            {
                hasExplicit = true;
                weights[i] = definition.Width;
            }
        }

        if (!hasExplicit)
        {
            var measured = MeasureColumnWidths(table, columnCount);
            var total = measured.Sum();
            for (var i = 0; i < columnCount; i++)
            {
                weights[i] = total > 0 ? measured[i] / total : 1.0 / columnCount;
            }
        }
        else
        {
            var sum = weights.Sum();
            for (var i = 0; i < columnCount; i++)
            {
                weights[i] = sum > 0 ? weights[i] / sum : 1.0 / columnCount;
            }
        }

        var assigned = 0;
        for (var i = 0; i < columnCount; i++)
        {
            widths[i] = Math.Max(600, (int)Math.Round(textWidth * weights[i]));
            assigned += widths[i];
        }

        widths[columnCount - 1] = Math.Max(600, widths[columnCount - 1] + (textWidth - assigned));

        var wordTable = new W.Table();
        wordTable.AppendChild(BuildTableProperties());

        var grid = new TableGrid();
        foreach (var width in widths)
        {
            grid.AppendChild(new GridColumn { Width = width.ToString() });
        }

        wordTable.AppendChild(grid);

        var rowIndex = 0;
        foreach (var row in table.OfType<MdTableRow>())
        {
            var wordRow = new W.TableRow();
            var rowProperties = new TableRowProperties();
            if (row.IsHeader)
            {
                rowProperties.AppendChild(new TableHeader());
            }

            rowProperties.AppendChild(new CantSplit());
            wordRow.AppendChild(rowProperties);

            var cellIndex = 0;
            foreach (var cell in row.OfType<MdTableCell>())
            {
                var span = Math.Max(1, cell.ColumnSpan);
                var cellWidth = 0;
                for (var k = 0; k < span; k++)
                {
                    var index = Math.Min(cellIndex + k, widths.Length - 1);
                    cellWidth += widths[index];
                }

                var wordCell = new W.TableCell();
                var cellProperties = new TableCellProperties(
                    new TableCellWidth { Width = cellWidth.ToString(), Type = TableWidthUnitValues.Dxa });

                if (span > 1)
                {
                    cellProperties.AppendChild(new GridSpan { Val = span });
                }

                if (row.IsHeader && _opt.TableHeaderShading)
                {
                    cellProperties.AppendChild(new Shading { Val = ShadingPatternValues.Clear, Fill = "F2F4F7" });
                }
                else if (_opt.TableZebraStripes && rowIndex % 2 == 1)
                {
                    cellProperties.AppendChild(new Shading { Val = ShadingPatternValues.Clear, Fill = "FAFBFC" });
                }

                cellProperties.AppendChild(new TableCellVerticalAlignment { Val = TableVerticalAlignmentValues.Center });

                wordCell.AppendChild(cellProperties);

                var cellCtx = ctx.Clone();
                cellCtx.InTable = true;
                cellCtx.ForcedStyle = row.IsHeader ? StylesBuilder.TableHeaderTextStyle : StylesBuilder.TableTextStyle;

                var before = wordCell.ChildElements.Count;
                foreach (var block in cell)
                {
                    RenderBlock(block, wordCell, cellCtx);
                }

                if (wordCell.ChildElements.Count <= before)
                {
                    wordCell.AppendChild(NewParagraph(cellCtx));
                }

                wordRow.AppendChild(wordCell);
                cellIndex += span;
            }

            wordTable.AppendChild(wordRow);
            rowIndex++;
        }

        target.AppendChild(wordTable);

        var spacer = new W.Paragraph(
            new ParagraphProperties(
                new SpacingBetweenLines { After = "0", Line = "120", LineRule = LineSpacingRuleValues.Auto }));
        target.AppendChild(spacer);

        _stats.Tables++;
    }

    private static TableProperties BuildTableProperties()
    {
        return new TableProperties(
            new TableStyle { Val = StylesBuilder.TableGridStyle },
            new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct },
            new TableLayout { Type = TableLayoutValues.Fixed },
            new TableCellMarginDefault(
                new TopMargin { Width = "60", Type = TableWidthUnitValues.Dxa },
                new TableCellLeftMargin { Width = 108, Type = TableWidthValues.Dxa },
                new BottomMargin { Width = "60", Type = TableWidthUnitValues.Dxa },
                new TableCellRightMargin { Width = 108, Type = TableWidthValues.Dxa }),
            new TableLook { Val = "04A0" });
    }

    private static double[] MeasureColumnWidths(MdTable table, int columnCount)
    {
        var widths = new double[columnCount];
        foreach (var row in table.OfType<MdTableRow>())
        {
            var index = 0;
            foreach (var cell in row.OfType<MdTableCell>())
            {
                if (index >= columnCount)
                {
                    break;
                }

                var longest = 0;
                foreach (var line in GetBlockText(cell).Split('\n'))
                {
                    longest = Math.Max(longest, TextUtils.DisplayWidth(line));
                }

                widths[index] = Math.Max(widths[index], Math.Clamp(longest, 3, 46));
                index++;
            }
        }

        for (var i = 0; i < columnCount; i++)
        {
            if (widths[i] <= 0)
            {
                widths[i] = 6;
            }
        }

        return widths;
    }

    private static string GetBlockText(Block block)
    {
        var sb = new StringBuilder();
        Collect(block);
        return sb.ToString();

        void Collect(Block current)
        {
            switch (current)
            {
                case LeafBlock leaf when leaf.Inline is not null:
                    CollectInline(leaf.Inline);
                    break;
                case ContainerBlock container:
                    foreach (var child in container)
                    {
                        Collect(child);
                    }

                    break;
            }
        }

        void CollectInline(Inline inline)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    sb.Append(literal.Content.ToString());
                    break;
                case CodeInline code:
                    sb.Append(code.Content);
                    break;
                case LineBreakInline:
                    sb.Append('\n');
                    break;
                case HtmlEntityInline entity:
                    sb.Append(entity.Transcoded.ToString());
                    break;
                case ContainerInline container:
                    for (var child = container.FirstChild; child is not null; child = child.NextSibling)
                    {
                        CollectInline(child);
                    }

                    break;
            }
        }
    }

    // ------------------------------------------------------------- 行内渲染

    private sealed class RunFormat
    {
        public bool Bold { get; set; }

        public bool Italic { get; set; }

        public bool Strike { get; set; }

        public bool Underline { get; set; }

        public bool Superscript { get; set; }

        public bool Subscript { get; set; }

        public bool Code { get; set; }

        public bool Highlight { get; set; }

        public string? Color { get; set; }

        public RunFormat Clone() => (RunFormat)MemberwiseClone();
    }

    private void RenderInlines(
        ContainerInline? container,
        OpenXmlCompositeElement target,
        Context ctx,
        RunFormat format,
        Inline? skip)
    {
        if (container is null)
        {
            return;
        }

        for (var inline = container.FirstChild; inline is not null; inline = inline.NextSibling)
        {
            if (ReferenceEquals(inline, skip))
            {
                continue;
            }

            switch (inline)
            {
                case LiteralInline literal:
                    AppendText(target, literal.Content.ToString(), format);
                    break;

                case CodeInline code:
                    {
                        var fmt = format.Clone();
                        fmt.Code = true;
                        AppendText(target, code.Content, fmt);
                        break;
                    }

                case EmphasisInline emphasis:
                    RenderInlines(emphasis, target, ctx, ApplyEmphasis(format, emphasis), skip);
                    break;

                case LinkInline link when link.IsImage:
                    AppendImage(target, link.Url ?? string.Empty, link.Title);
                    break;

                case LinkInline link:
                    AppendLink(link, target, ctx, format);
                    break;

                case AutolinkInline autolink:
                    AppendAutolink(autolink, target, format);
                    break;

                case HtmlInline html:
                    AppendHtmlInline(html.Tag, target, format);
                    break;

                case LineBreakInline lineBreak:
                    if (lineBreak.IsHard || _opt.PreserveHardLineBreaks)
                    {
                        target.AppendChild(new Run(new Break()));
                    }
                    else
                    {
                        AppendText(target, " ", format);
                    }

                    break;

                case HtmlEntityInline entity:
                    AppendText(target, entity.Transcoded.ToString(), format);
                    break;

                case MathInline math:
                    AppendInlineMath(math, target, format);
                    break;

                case FootnoteLink footnoteLink when !footnoteLink.IsBackLink:
                    AppendFootnoteReference(footnoteLink, target);
                    break;

                case FootnoteLink:
                    break;

                case TaskList task:
                    AppendText(target, task.Checked ? "\u2611 " : "\u2610 ", format);
                    break;

                case SmartyPant smarty:
                    AppendText(target, MapSmartyPant(smarty), format);
                    break;

                case AbbreviationInline abbreviation:
                    AppendText(target, abbreviation.Abbreviation?.Label ?? string.Empty, format);
                    break;

                case ContainerInline nested:
                    RenderInlines(nested, target, ctx, format, skip);
                    break;

                default:
                    {
                        var text = inline.ToString();
                        if (!string.IsNullOrEmpty(text))
                        {
                            AppendText(target, text, format);
                        }

                        break;
                    }
            }
        }
    }

    private static RunFormat ApplyEmphasis(RunFormat format, EmphasisInline emphasis)
    {
        var fmt = format.Clone();
        switch (emphasis.DelimiterChar)
        {
            case '*':
            case '_':
                if (emphasis.DelimiterCount >= 2)
                {
                    fmt.Bold = true;
                }
                else
                {
                    fmt.Italic = true;
                }

                break;

            case '~':
                if (emphasis.DelimiterCount >= 2)
                {
                    fmt.Strike = true;
                }
                else
                {
                    fmt.Subscript = true;
                }

                break;

            case '^':
                fmt.Superscript = true;
                break;

            case '=':
                fmt.Highlight = true;
                break;

            case '+':
                fmt.Underline = true;
                break;
        }

        return fmt;
    }

    private void AppendInlineMath(MathInline math, OpenXmlCompositeElement target, RunFormat format)
    {
        var latex = math.Content.ToString().Trim();
        if (latex.Length == 0)
        {
            return;
        }

        if (_opt.EnableMath)
        {
            target.AppendChild(LatexToOmml.ToOfficeMath(latex));
            _stats.Equations++;
        }
        else
        {
            AppendText(target, "$" + latex + "$", format);
        }
    }

    private void AppendLink(LinkInline link, OpenXmlCompositeElement target, Context ctx, RunFormat format)
    {
        var url = link.Url ?? string.Empty;
        var isExternal = Uri.TryCreate(url, UriKind.Absolute, out var uri)
                         && (uri.Scheme == Uri.UriSchemeHttp
                             || uri.Scheme == Uri.UriSchemeHttps
                             || uri.Scheme == Uri.UriSchemeMailto
                             || uri.Scheme == Uri.UriSchemeFtp);

        if (!isExternal)
        {
            var fmt = format.Clone();
            fmt.Color ??= "0969DA";
            fmt.Underline = true;
            RenderInlines(link, target, ctx, fmt, skip: null);
            return;
        }

        var relationship = _main.AddHyperlinkRelationship(uri!, true);
        var hyperlink = new Hyperlink { Id = relationship.Id };
        RenderInlines(link, hyperlink, ctx, format, skip: null);

        if (!hyperlink.HasChildren)
        {
            hyperlink.AppendChild(CreateTextRun(url, format));
        }

        target.AppendChild(hyperlink);
    }

    private void AppendAutolink(AutolinkInline autolink, OpenXmlCompositeElement target, RunFormat format)
    {
        var url = autolink.Url;
        var full = autolink.IsEmail ? "mailto:" + url : url;
        if (Uri.TryCreate(full, UriKind.Absolute, out var uri))
        {
            var relationship = _main.AddHyperlinkRelationship(uri, true);
            var hyperlink = new Hyperlink { Id = relationship.Id };
            hyperlink.AppendChild(CreateTextRun(url, format));
            target.AppendChild(hyperlink);
        }
        else
        {
            AppendText(target, url, format);
        }
    }

    private void AppendHtmlInline(string tag, OpenXmlCompositeElement target, RunFormat format)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return;
        }

        var lower = tag.ToLowerInvariant();
        if (lower.StartsWith("<br", StringComparison.Ordinal))
        {
            target.AppendChild(new Run(new Break()));
            return;
        }

        if (lower.StartsWith("<img", StringComparison.Ordinal))
        {
            var match = HtmlImageRegex.Match(tag.EndsWith('>') ? tag : tag + ">");
            if (match.Success)
            {
                AppendImage(target, match.Groups[1].Value, null);
            }
        }
    }

    private void AppendImage(OpenXmlCompositeElement target, string url, string? title)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        var image = _images.Resolve(url);
        OpenXmlElement content;

        if (image is null)
        {
            content = CreateTextRun(
                $"[图片：{SanitizeImageLabel(url)}]",
                new RunFormat { Italic = true, Color = "6A737D" });
        }
        else
        {
            content = BuildImageRun(image, title);
            _stats.Images++;
        }

        if (target is W.Paragraph or Hyperlink)
        {
            target.AppendChild(content);
        }
        else
        {
            var p = new W.Paragraph(
                new ParagraphProperties(new Justification { Val = JustificationValues.Center }));
            p.AppendChild(content);
            target.AppendChild(p);
        }
    }

    private Run BuildImageRun(ResolvedImage image, string? title)
    {
        var partType = image.Kind switch
        {
            ImageKind.Jpeg => ImagePartType.Jpeg,
            ImageKind.Gif => ImagePartType.Gif,
            ImageKind.Bmp => ImagePartType.Bmp,
            ImageKind.Tiff => ImagePartType.Tiff,
            _ => ImagePartType.Png,
        };

        var imagePart = _main.AddImagePart(partType);
        using (var stream = new MemoryStream(image.Data))
        {
            imagePart.FeedData(stream);
        }

        var relationshipId = _main.GetIdOfPart(imagePart);
        var (cx, cy) = ComputeImageExtent(image);
        var id = ++_imageCounter;
        var name = $"Picture {id}";

        var drawing = new Drawing(
            new WP.Inline(
                new WP.Extent { Cx = cx, Cy = cy },
                new WP.DocProperties
                {
                    Id = (uint)id,
                    Name = name,
                    Description = string.IsNullOrWhiteSpace(title) ? "image" : title,
                },
                new A.Graphic(
                    new A.GraphicData(
                        new PIC.Picture(
                            new PIC.NonVisualPictureProperties(
                                new PIC.NonVisualDrawingProperties { Id = (uint)(id + 1000), Name = name },
                                new PIC.NonVisualPictureDrawingProperties()),
                            new PIC.BlipFill(
                                new A.Blip { Embed = relationshipId },
                                new A.Stretch(new A.FillRectangle())),
                            new PIC.ShapeProperties(
                                new A.Transform2D(
                                    new A.Offset { X = 0L, Y = 0L },
                                    new A.Extents { Cx = cx, Cy = cy }),
                                new A.PresetGeometry(new A.AdjustValueList())
                                {
                                    Preset = A.ShapeTypeValues.Rectangle,
                                })))
                    {
                        Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture",
                    }))
            {
                DistanceFromTop = 0U,
                DistanceFromBottom = 0U,
                DistanceFromLeft = 0U,
                DistanceFromRight = 0U,
            });

        return new Run(drawing);
    }

    private (long Cx, long Cy) ComputeImageExtent(ResolvedImage image)
    {
        const long emuPerPixel = 9525;      // 96 DPI 下的 1 像素
        const long emuPerInch = 914400;

        var maxWidth = _contentWidthTwips * emuPerInch / 1440;
        var maxHeight = (long)(9.5 * emuPerInch);

        var width = image.PixelWidth > 0 ? image.PixelWidth * emuPerPixel : 4 * emuPerInch;
        var height = image.PixelHeight > 0 ? image.PixelHeight * emuPerPixel : 3 * emuPerInch;

        if (width > maxWidth)
        {
            var scale = (double)maxWidth / width;
            width = maxWidth;
            height = (long)(height * scale);
        }

        if (height > maxHeight)
        {
            var scale = (double)maxHeight / height;
            height = maxHeight;
            width = (long)(width * scale);
        }

        return (Math.Max(1, width), Math.Max(1, height));
    }

    private static string SanitizeImageLabel(string url)
    {
        var name = url;
        var slash = name.LastIndexOf('/');
        if (slash >= 0 && slash < name.Length - 1)
        {
            name = name[(slash + 1)..];
        }

        return name.Length > 60 ? name[..60] + "…" : name;
    }

    private void AppendFootnoteReference(FootnoteLink link, OpenXmlCompositeElement target)
    {
        var id = link.Footnote?.Order ?? 0;
        if (id <= 0)
        {
            id = ++_footnoteCounter;
        }

        if (link.Footnote is not null)
        {
            _footnotes[id] = link.Footnote;
        }

        var run = new Run(
            new RunProperties(new RunStyle { Val = StylesBuilder.FootnoteReferenceStyle }),
            new FootnoteReference { Id = id });

        target.AppendChild(run);
        _stats.Footnotes++;
    }

    private static string MapSmartyPant(SmartyPant pant)
    {
        return pant.Type.ToString() switch
        {
            "Ellipsis" => "\u2026",
            "EmDash" => "\u2014",
            "EnDash" => "\u2013",
            "LeftQuote" or "LeftDoubleQuote" => "\u201C",
            "RightQuote" or "RightDoubleQuote" => "\u201D",
            "LeftSingleQuote" => "\u2018",
            "RightSingleQuote" => "\u2019",
            "Apostrophe" => "\u2019",
            _ => pant.OpeningCharacter.ToString(),
        };
    }

    // ------------------------------------------------------------- 段落与 Run

    private static W.Paragraph NewParagraph(
        Context ctx,
        string? styleId = null,
        bool numbered = false,
        int numId = 0,
        int level = 0,
        bool center = false,
        int indent = 0)
    {
        var properties = new ParagraphProperties();
        var style = styleId ?? ctx.ForcedStyle;

        if (!string.IsNullOrEmpty(style) && style != StylesBuilder.Normal)
        {
            properties.AppendChild(new ParagraphStyleId { Val = style });
        }

        if (numbered && numId > 0)
        {
            properties.AppendChild(new NumberingProperties(
                new NumberingLevelReference { Val = level },
                new NumberingId { Val = numId }));
        }
        else if (ctx.ListLevel >= 0)
        {
            properties.AppendChild(new Indentation
            {
                Left = (720 + (360 * ctx.ListLevel)).ToString(),
            });
        }
        else if (indent > 0)
        {
            properties.AppendChild(new Indentation { Left = indent.ToString() });
        }

        if (center)
        {
            properties.AppendChild(new Justification { Val = JustificationValues.Center });
        }

        var paragraph = new W.Paragraph();
        if (properties.HasChildren)
        {
            paragraph.AppendChild(properties);
        }

        return paragraph;
    }

    private void AppendText(OpenXmlCompositeElement target, string text, RunFormat format)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        target.AppendChild(CreateTextRun(text, format));
    }

    private Run CreateTextRun(string text, RunFormat format, bool useCodeStyle = false)
    {
        var run = new Run();
        var properties = new RunProperties();

        if (format.Code && !useCodeStyle)
        {
            properties.AppendChild(new RunStyle { Val = StylesBuilder.CodeCharStyle });
        }

        if (format.Code && useCodeStyle)
        {
            properties.AppendChild(new RunFonts
            {
                Ascii = _opt.MonoFont,
                HighAnsi = _opt.MonoFont,
                EastAsia = _opt.MonoFont,
            });
        }

        if (format.Bold)
        {
            properties.AppendChild(new Bold());
        }

        if (format.Italic)
        {
            properties.AppendChild(new Italic());
        }

        if (format.Strike)
        {
            properties.AppendChild(new Strike());
        }

        if (!string.IsNullOrEmpty(format.Color))
        {
            properties.AppendChild(new Color { Val = format.Color });
        }

        if (format.Highlight)
        {
            properties.AppendChild(new Highlight { Val = HighlightColorValues.Yellow });
        }

        if (format.Underline)
        {
            properties.AppendChild(new Underline { Val = UnderlineValues.Single });
        }

        if (format.Superscript)
        {
            properties.AppendChild(new VerticalTextAlignment { Val = VerticalPositionValues.Superscript });
        }
        else if (format.Subscript)
        {
            properties.AppendChild(new VerticalTextAlignment { Val = VerticalPositionValues.Subscript });
        }

        if (properties.HasChildren)
        {
            run.AppendChild(properties);
        }

        run.AppendChild(new Text(TextUtils.SanitizeXml(text)) { Space = SpaceProcessingModeValues.Preserve });
        return run;
    }

    // ------------------------------------------------------------- 脚注

    private void WriteFootnotes()
    {
        var part = _main.AddNewPart<FootnotesPart>();
        var footnotes = new Footnotes();

        footnotes.AppendChild(new W.Footnote(
            new W.Paragraph(new Run(new SeparatorMark())))
        {
            Id = -1,
            Type = FootnoteEndnoteValues.Separator,
        });

        footnotes.AppendChild(new W.Footnote(
            new W.Paragraph(new Run(new ContinuationSeparatorMark())))
        {
            Id = 0,
            Type = FootnoteEndnoteValues.ContinuationSeparator,
        });

        foreach (var (id, footnote) in _footnotes.OrderBy(kv => kv.Key))
        {
            var element = new W.Footnote { Id = id };
            var ctx = new Context { ForcedStyle = StylesBuilder.FootnoteTextStyle, InFootnote = true };
            var first = true;

            foreach (var block in footnote)
            {
                var before = element.ChildElements.Count;
                RenderBlock(block, element, ctx);

                if (first && element.ChildElements.Count > before &&
                    element.ChildElements[before] is W.Paragraph paragraph)
                {
                    // pPr 必须永远是段落的第一个子元素
                    var insertAt = paragraph.FirstChild is ParagraphProperties ? 1 : 0;
                    paragraph.InsertAt(
                        new Run(
                            new RunProperties(new RunStyle { Val = StylesBuilder.FootnoteReferenceStyle }),
                            new FootnoteReferenceMark()),
                        insertAt);
                    paragraph.InsertAt(
                        new Run(new Text(" ") { Space = SpaceProcessingModeValues.Preserve }),
                        insertAt + 1);
                    first = false;
                }
            }

            if (!element.HasChildren)
            {
                element.AppendChild(new W.Paragraph(new Run(new FootnoteReferenceMark())));
            }

            footnotes.AppendChild(element);
        }

        part.Footnotes = footnotes;
        part.Footnotes.Save();
    }

    // ------------------------------------------------------------- 页面与文档属性

    private int ComputeContentWidthTwips()
    {
        var (width, height) = PageSizeTwips(_opt.PageSize);
        if (_opt.Orientation == PageOrientationPreset.Landscape)
        {
            width = height;
        }

        return width - (2 * TextUtils.CmToTwips(_opt.MarginCm));
    }

    private SectionProperties BuildSectionProperties()
    {
        var (width, height) = PageSizeTwips(_opt.PageSize);
        var landscape = _opt.Orientation == PageOrientationPreset.Landscape;
        if (landscape)
        {
            (width, height) = (height, width);
        }

        var margin = TextUtils.CmToTwips(_opt.MarginCm);

        return new SectionProperties(
            new PageSize
            {
                Width = (uint)width,
                Height = (uint)height,
                Orient = landscape ? PageOrientationValues.Landscape : PageOrientationValues.Portrait,
            },
            new PageMargin
            {
                Top = margin,
                Bottom = margin,
                Left = (uint)margin,
                Right = (uint)margin,
                Header = 720,
                Footer = 720,
                Gutter = 0,
            });
    }

    private static (int Width, int Height) PageSizeTwips(PageSizePreset preset)
    {
        return preset switch
        {
            PageSizePreset.A4 => (11906, 16838),
            PageSizePreset.A5 => (8391, 11906),
            PageSizePreset.Letter => (12240, 15840),
            PageSizePreset.Legal => (12240, 20160),
            _ => (11906, 16838),
        };
    }

    private void ApplyPackageProperties(WordprocessingDocument package, MarkdownDocument document)
    {
        var frontMatter = _frontMatter;
        var properties = package.PackageProperties;

        var title = !string.IsNullOrWhiteSpace(_opt.DocumentTitle)
            ? _opt.DocumentTitle
            : frontMatter.GetValueOrDefault("title", string.Empty);

        if (string.IsNullOrWhiteSpace(title))
        {
            var firstHeading = document.Descendants<HeadingBlock>().FirstOrDefault();
            title = firstHeading is not null ? GetLeafText(firstHeading).Trim() : "Markdown 文档";
        }

        properties.Title = title;

        var author = !string.IsNullOrWhiteSpace(_opt.DocumentAuthor)
            ? _opt.DocumentAuthor
            : frontMatter.GetValueOrDefault("author", string.Empty);

        if (!string.IsNullOrWhiteSpace(author))
        {
            properties.Creator = author;
            properties.LastModifiedBy = author;
        }

        properties.Subject = frontMatter.GetValueOrDefault("subject", string.Empty);
        properties.Keywords = frontMatter.GetValueOrDefault("keywords", frontMatter.GetValueOrDefault("tags", string.Empty));
        properties.Description = frontMatter.GetValueOrDefault("description", string.Empty);
        properties.Category = frontMatter.GetValueOrDefault("category", string.Empty);
        properties.Created = DateTime.Now;
        properties.Modified = DateTime.Now;
    }

    private static Dictionary<string, string> ExtractFrontMatter(MarkdownDocument document)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var block = document.Descendants<YamlFrontMatterBlock>().FirstOrDefault();
        if (block is null)
        {
            return result;
        }

        foreach (var line in GetLeafText(block).Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var separator = trimmed.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var key = trimmed[..separator].Trim().TrimStart('-').Trim();
            var value = trimmed[(separator + 1)..].Trim().Trim('"', '\'');
            if (value.StartsWith('[') && value.EndsWith(']'))
            {
                value = value[1..^1].Trim();
            }

            if (key.Length > 0 && !result.ContainsKey(key))
            {
                result[key] = value;
            }
        }

        return result;
    }

    // ------------------------------------------------------------- 工具

    /// <summary>取叶子块（代码块、HTML 块、front matter 等）的原始文本。</summary>
    private static string GetLeafText(LeafBlock leaf)
    {
        var lines = leaf.Lines;
        if (lines.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0)
            {
                sb.Append('\n');
            }

            sb.Append(lines.Lines[i].Slice.ToString());
        }

        return sb.ToString();
    }
}
