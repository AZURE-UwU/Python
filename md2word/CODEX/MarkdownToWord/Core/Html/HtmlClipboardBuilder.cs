using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.Mathematics;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdownToWord.Core.Docx;

namespace MarkdownToWord.Core.Html;

/// <summary>
/// 把 Markdown 渲染成"能直接粘进 Word 的正文"的 HTML：
/// 结构（标题 / 列表 / 表格 / 引用 / 代码）交给 Markdig，公式换成 Word 认的 OMML（m:oMath），
/// 图片以 base64 data URI 内嵌。这样在 Word 里 Ctrl+V 得到的是带格式正文，公式还是原生可编辑公式。
///
/// 有意不做的事：不写 @page / WordSection1 这类页面属性——粘贴正文时应该沿用目标文档的页面设置。
/// </summary>
internal static class HtmlClipboardBuilder
{
    private const double PixelsPerCm = 96.0 / 2.54;

    /// <summary>
    /// 生成完整的 HTML 文档（含 &lt;!--StartFragment--&gt; 标记），供剪贴板包装成 CF_HTML。
    /// </summary>
    public static string BuildHtml(
        string markdown,
        ConversionOptions options,
        string? baseDirectory,
        out List<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(options);

        warnings = [];
        var normalized = options.EnableMath ? MathDelimiterNormalizer.Normalize(markdown) : markdown ?? string.Empty;
        var pipeline = MarkdownPipelineFactory.Get(options.EnableMath);
        var document = Markdown.Parse(normalized, pipeline);

        using var images = new ImageResolver(options, baseDirectory);
        ResolveImages(document, images, ContentWidthPixels(options));
        warnings.AddRange(images.Warnings);

        var body = new StringBuilder();
        using (var writer = new StringWriter(body))
        {
            var renderer = new HtmlRenderer(writer);
            pipeline.Setup(renderer);

            // 公式：默认渲染器会把 LaTeX 原样吐出来，这里换成 Word 认得的 OMML
            renderer.ObjectRenderers.RemoveAll(r => r is HtmlMathInlineRenderer or HtmlMathBlockRenderer);
            renderer.ObjectRenderers.Add(new OmmlMathInlineRenderer(options));
            renderer.ObjectRenderers.Add(new OmmlMathBlockRenderer(options));

            // 代码块：默认只有 <pre><code>，这里带上和导出 docx 一致的语法高亮配色
            renderer.ObjectRenderers.RemoveAll(r => r is CodeBlockRenderer);
            renderer.ObjectRenderers.Add(new HighlightCodeBlockRenderer(options));
            renderer.ObjectRenderers.Add(new HighlightFencedCodeBlockRenderer(options));

            renderer.Render(document);
            writer.Flush();
        }

        return Wrap(Polish(body.ToString()), options, ContentWidthPixels(options));
    }

    /// <summary>
    /// Markdig 输出的 &lt;del&gt; / &lt;ins&gt; / &lt;mark&gt; / &lt;input type=checkbox&gt; 这些标签
    /// Word 的 HTML 粘贴不认——实测会把内容一起丢掉。统一换成它认的 span + CSS 和符号。
    /// </summary>
    private static string Polish(string fragment)
    {
        var polished = StyleAlertBlocks(PolishAlerts(StyleBlockquotes(fragment)));
        return polished
            .Replace("<del>", "<span style=\"text-decoration:line-through\">", StringComparison.Ordinal)
            .Replace("</del>", "</span>", StringComparison.Ordinal)
            .Replace("<ins>", "<span style=\"text-decoration:underline\">", StringComparison.Ordinal)
            .Replace("</ins>", "</span>", StringComparison.Ordinal)
            .Replace("<mark>", "<span style=\"background:#FFF3A3\">", StringComparison.Ordinal)
            .Replace("</mark>", "</span>", StringComparison.Ordinal)
            .Replace(
                "<input disabled=\"disabled\" type=\"checkbox\" checked=\"checked\" />",
                "☑ ",
                StringComparison.Ordinal)
            .Replace(
                "<input disabled=\"disabled\" type=\"checkbox\" />",
                "☐ ",
                StringComparison.Ordinal);
    }

    private static string StyleBlockquotes(string fragment)
    {
        var result = new StringBuilder(fragment.Length);
        var position = 0;
        var depth = 0;

        foreach (Match match in Regex.Matches(fragment, @"<[^>]+>"))
        {
            result.Append(fragment, position, match.Index - position);
            var tag = match.Value;
            if (tag.StartsWith("<blockquote", StringComparison.OrdinalIgnoreCase))
            {
                depth++;
            }
            else if (tag.StartsWith("</blockquote", StringComparison.OrdinalIgnoreCase))
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (depth > 0 &&
                     (tag.StartsWith("<p", StringComparison.OrdinalIgnoreCase) ||
                      tag.StartsWith("<li", StringComparison.OrdinalIgnoreCase)))
            {
                var margin = depth * 10;
                var style = $"margin-left:{margin}pt;border-left:3pt solid #d0d7de;mso-border-left-alt:solid #d0d7de 3.0pt;padding-left:10pt;mso-padding-alt:0cm 0cm 0cm 10pt;color:#57606a;";
                tag = AddInlineStyle(tag, style);
            }

            if (!tag.StartsWith("<blockquote", StringComparison.OrdinalIgnoreCase) &&
                !tag.StartsWith("</blockquote", StringComparison.OrdinalIgnoreCase))
            {
                result.Append(tag);
            }

            position = match.Index + match.Length;
        }

        result.Append(fragment, position, fragment.Length - position);
        return result.ToString();
    }

    private static string AddInlineStyle(string tag, string style)
    {
        if (Regex.IsMatch(tag, @"\sstyle=""[^""]*""", RegexOptions.IgnoreCase))
        {
            return Regex.Replace(
                tag,
                @"(\sstyle="")([^""]*)("")",
                match => match.Groups[1].Value +
                         match.Groups[2].Value.TrimEnd(';') +
                         ";" +
                         style +
                         match.Groups[3].Value,
                RegexOptions.IgnoreCase);
        }

        var close = tag.EndsWith("/>", StringComparison.Ordinal) ? tag.Length - 2 : tag.Length - 1;
        return tag.Insert(close, $" style=\"{style}\"");
    }

    private static string PolishAlerts(string fragment)
    {
        var withoutSvg = Regex.Replace(
            fragment,
            @"<svg\b[\s\S]*?</svg>",
            string.Empty,
            RegexOptions.IgnoreCase);

        return Regex.Replace(
            withoutSvg,
            @"<div class=""markdown-alert markdown-alert-(?<kind>[a-z]+)"">\s*<p class=""markdown-alert-title"">(?<title>[^<]*)</p>",
            match =>
            {
                var kind = match.Groups["kind"].Value.ToLowerInvariant();
                var prefix = "#0969da";
                var icon = "ℹ";
                switch (kind)
                {
                    case "tip":
                        prefix = "#1a7f37";
                        icon = "✓";
                        break;
                    case "important":
                        prefix = "#8250df";
                        icon = "◆";
                        break;
                    case "warning":
                        prefix = "#9a6700";
                        icon = "⚠";
                        break;
                    case "caution":
                        prefix = "#cf222e";
                        icon = "✕";
                        break;
                }

                var title = WebUtility.HtmlDecode(match.Groups["title"].Value).Trim();
                return $"<div class=\"markdown-alert markdown-alert-{kind}\" style=\"margin:6pt 0;padding-left:9pt;border-left:4pt solid {prefix};mso-border-left-alt:solid {prefix} 4.0pt\">" +
                       $"<p style=\"margin:0 0 3pt 0;font-weight:bold;color:{prefix}\">{icon} {title}</p>";
            },
            RegexOptions.IgnoreCase);
    }

    private static string StyleAlertBlocks(string fragment)
    {
        var result = new StringBuilder(fragment.Length);
        var position = 0;
        var colors = new Stack<string>();

        foreach (Match match in Regex.Matches(fragment, @"<[^>]+>"))
        {
            result.Append(fragment, position, match.Index - position);
            var tag = match.Value;
            if (tag.StartsWith("<div class=\"markdown-alert", StringComparison.OrdinalIgnoreCase))
            {
                var colorMatch = Regex.Match(tag, @"border-left:4pt solid (?<color>#[0-9a-fA-F]{6})");
                colors.Push(colorMatch.Success ? colorMatch.Groups["color"].Value : "#0969da");
            }
            else if (colors.Count > 0 && tag.StartsWith("<div", StringComparison.OrdinalIgnoreCase))
            {
                colors.Push(colors.Peek());
            }
            else if (colors.Count > 0 &&
                     (tag.StartsWith("<p", StringComparison.OrdinalIgnoreCase) ||
                      tag.StartsWith("<li", StringComparison.OrdinalIgnoreCase)))
            {
                var color = colors.Peek();
                var style = $"margin-left:10pt;border-left:4pt solid {color};mso-border-left-alt:solid {color} 4.0pt;padding-left:9pt;mso-padding-alt:0cm 0cm 0cm 9pt;";
                tag = AddInlineStyle(tag, style);
            }
            else if (colors.Count > 0 && tag.StartsWith("</div", StringComparison.OrdinalIgnoreCase))
            {
                colors.Pop();
            }

            result.Append(tag);
            position = match.Index + match.Length;
        }

        result.Append(fragment, position, fragment.Length - position);
        return result.ToString();
    }

    /// <summary>正文可用宽度（像素）：按页面宽度减左右页边距估算，用来限制图片和公式块的显示尺寸。</summary>
    public static int ContentWidthPixels(ConversionOptions options)
    {
        var (widthCm, heightCm) = options.PageSize switch
        {
            PageSizePreset.A5 => (14.8, 21.0),
            PageSizePreset.Legal => (21.59, 35.56),
            PageSizePreset.Letter => (21.59, 27.94),
            _ => (21.0, 29.7),   // A4
        };

        var pageWidthCm = options.Orientation == PageOrientationPreset.Landscape ? heightCm : widthCm;
        var contentCm = Math.Max(4.0, pageWidthCm - (2 * Math.Clamp(options.MarginCm, 0.5, 6)));
        return (int)Math.Round(contentCm * PixelsPerCm);
    }

    /// <summary>把文档里的图片解析出来落到磁盘，换成 file:// 链接（Word 粘贴 HTML 时是按 URL 取图的）。</summary>
    private static void ResolveImages(MarkdownDocument document, ImageResolver images, int contentWidthPx)
    {
        foreach (var link in document.Descendants<LinkInline>())
        {
            if (!link.IsImage || string.IsNullOrWhiteSpace(link.Url))
            {
                continue;
            }

            var resolved = images.Resolve(link.Url);
            if (resolved is null)
            {
                continue;   // 解析不了就保留原样，让 Word 自己去试
            }

            link.Url = $"data:{MimeType(resolved.Kind)};base64,{Convert.ToBase64String(resolved.Data)}";

            // 太宽的图片按正文宽度缩一下，免得粘进来撑破版面
            if (resolved.PixelWidth > contentWidthPx && contentWidthPx > 0)
            {
                link.GetAttributes().AddProperty("width", contentWidthPx.ToString());
            }
        }
    }

    private static string MimeType(ImageKind kind) => kind switch
    {
        ImageKind.Png => "image/png",
        ImageKind.Jpeg => "image/jpeg",
        ImageKind.Gif => "image/gif",
        ImageKind.Bmp => "image/bmp",
        ImageKind.Tiff => "image/tiff",
        ImageKind.WebP => "image/webp",
        _ => "application/octet-stream",
    };

    /// <summary>套上 Word 认得的 HTML 外壳（命名空间、字体字号、表格/代码的基础样式）。</summary>
    private static string Wrap(string fragment, ConversionOptions options, int contentWidthPx)
    {
        var eastAsia = string.IsNullOrWhiteSpace(options.BodyFontEastAsia) ? "等线" : options.BodyFontEastAsia;
        var latin = string.IsNullOrWhiteSpace(options.BodyFont) ? "Calibri" : options.BodyFont;
        var bodySize = Math.Clamp(options.BodyFontSizePt, 6, 36).ToString("0.##");
        var codeSize = Math.Clamp(options.CodeFontSizePt, 6, 36).ToString("0.##");

        var css = new StringBuilder();
        css.AppendLine($"body {{ font-family: \"{eastAsia}\", \"{latin}\", sans-serif; font-size: {bodySize}pt; line-height: 1.25; }}");
        css.AppendLine("p { margin: 0 0 6pt 0; }");
        css.AppendLine("h1 { font-size: 20pt; }");
        css.AppendLine("h2 { font-size: 16pt; }");
        css.AppendLine("h3 { font-size: 13pt; }");
        css.AppendLine("h4, h5, h6 { font-size: 12pt; }");
        css.AppendLine("blockquote { margin: 0 0 6pt 10pt; padding-left: 10pt; border-left: 3pt solid #d0d7de; mso-border-left-alt: solid #d0d7de 3.0pt; mso-padding-alt: 0cm 0cm 0cm 10pt; color: #57606a; }");
        css.AppendLine("table { border-collapse: collapse; margin: 6pt 0; }");
        css.AppendLine("th, td { border: 1pt solid #9aa4af; padding: 3pt 6pt; vertical-align: top; text-align: left; }");
        css.AppendLine("th { background: #eef2f6; }");
        css.AppendLine($"pre {{ font-family: Consolas, \"Courier New\", monospace; font-size: {codeSize}pt; background: #f6f8fa; border: 1pt solid #e1e4e8; padding: 6pt; white-space: pre-wrap; }}");
        css.AppendLine($"code {{ font-family: Consolas, \"Courier New\", monospace; font-size: {codeSize}pt; }}");
        css.AppendLine($"img {{ max-width: {contentWidthPx}px; }}");
        css.AppendLine("hr { border: none; border-top: 1pt solid #d0d7de; }");

        var html = new StringBuilder();
        html.AppendLine("<html xmlns:o=\"urn:schemas-microsoft-com:office:office\" " +
                        "xmlns:w=\"urn:schemas-microsoft-com:office:word\" " +
                        "xmlns=\"http://www.w3.org/TR/REC-html40\">");
        html.AppendLine("<head>");
        html.AppendLine("<meta http-equiv=\"Content-Type\" content=\"text/html; charset=utf-8\">");
        html.AppendLine("<meta name=\"ProgId\" content=\"Word.Document\">");
        html.AppendLine("<meta name=\"Generator\" content=\"MarkdownToWord\">");
        html.AppendLine("<title>Markdown 转 Word</title>");
        html.AppendLine("<style>");
        html.Append(css);
        html.AppendLine("</style>");
        html.AppendLine("</head>");
        html.AppendLine("<body>");
        html.AppendLine("<!--StartFragment-->");
        html.Append(fragment);
        if (!fragment.EndsWith('\n'))
        {
            html.AppendLine();
        }

        html.AppendLine("<!--EndFragment-->");
        html.AppendLine("</body>");
        html.AppendLine("</html>");
        return html.ToString();
    }

    /// <summary>把 LaTeX 转成 Microsoft 365 支持的 MathML 3 Presentation MathML。</summary>
    private static string MathML(string latex, bool display, ConversionOptions options)
    {
        var officeMath = LatexToOmml.ToOfficeMath(latex);
        return OmmlToMathML.Convert(officeMath, display, options.BodyFontSizePt);
    }

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

    // ------------------------------------------------------------ 自定义渲染器

    private sealed class OmmlMathInlineRenderer(ConversionOptions options) : HtmlObjectRenderer<MathInline>
    {
        protected override void Write(HtmlRenderer renderer, MathInline math)
        {
            var latex = math.Content.ToString().Trim();
            if (latex.Length == 0)
            {
                return;
            }

            if (!options.EnableMath)
            {
                renderer.WriteEscape("$" + latex + "$");
                return;
            }

            renderer.Write(MathML(latex, display: false, options));
        }
    }

    private sealed class OmmlMathBlockRenderer(ConversionOptions options) : HtmlObjectRenderer<MathBlock>
    {
        protected override void Write(HtmlRenderer renderer, MathBlock block)
        {
            var latex = GetLeafText(block).Trim();
            if (latex.Length == 0)
            {
                return;
            }

            if (!options.EnableMath)
            {
                renderer.WriteEscape("$$" + latex + "$$");
                return;
            }

            renderer.Write("<p style=\"text-align:center\">");
            renderer.Write(MathML(latex, display: true, options));
            renderer.Write("</p>");
        }
    }

    private sealed class HighlightCodeBlockRenderer(ConversionOptions options) : CodeBlockRenderer
    {
        protected override void Write(HtmlRenderer renderer, CodeBlock codeBlock)
            => WriteCode(renderer, (codeBlock as FencedCodeBlock)?.Info, GetLeafText(codeBlock), options);
    }

    private sealed class HighlightFencedCodeBlockRenderer(ConversionOptions options)
        : HtmlObjectRenderer<FencedCodeBlock>
    {
        protected override void Write(HtmlRenderer renderer, FencedCodeBlock block)
            => WriteCode(renderer, block.Info, GetLeafText(block), options);
    }

    private static void WriteCode(HtmlRenderer renderer, string? language, string code, ConversionOptions options)
    {
        renderer.Write("<pre>");

        var text = code.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');
        if (text.Length == 0)
        {
            renderer.Write("</pre>");
            return;
        }

        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                renderer.Write("\n");
            }

            var line = lines[i];
            if (!options.SyntaxHighlight)
            {
                renderer.WriteEscape(line);
                continue;
            }

            foreach (var token in CodeHighlighter.Tokenize(line, language))
            {
                if (string.IsNullOrEmpty(token.Color) && !token.Bold && !token.Italic)
                {
                    renderer.WriteEscape(token.Text);
                    continue;
                }

                var style = new StringBuilder();
                if (!string.IsNullOrEmpty(token.Color))
                {
                    style.Append("color:#").Append(token.Color).Append(';');
                }

                if (token.Bold)
                {
                    style.Append("font-weight:bold;");
                }

                if (token.Italic)
                {
                    style.Append("font-style:italic;");
                }

                renderer.Write("<span style=\"" + style + "\">");
                renderer.WriteEscape(token.Text);
                renderer.Write("</span>");
            }
        }

        renderer.Write("</pre>");
    }
}
