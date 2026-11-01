using System.Text;
using System.Text.RegularExpressions;

namespace MarkdownToWord.Core;

/// <summary>
/// 把 LaTeX 风格的行内/独立公式定界符 \(...\) 与 \[...\] 归一化成 Markdig 认识的 $...$ / $$...$$。
///
/// 为什么需要它：Markdig 的数学扩展只认 $ 与 $$，但大量中文技术文档（以及本机 AGENTS 约定）
/// 用 \( \) 写行内公式，不转换的话会原样显示成 "(a \ne 0)" 这种文本。
/// 围栏代码块内的内容不会被改动。
/// </summary>
internal static class MathDelimiterNormalizer
{
    private static readonly Regex InlineMathRegex = new(
        @"\\\((?<body>.+?)\\\)",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex DisplayMathRegex = new(
        @"\\\[(?<body>.+?)\\\]",
        RegexOptions.Compiled | RegexOptions.Singleline);

    public static string Normalize(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown) ||
            (!markdown.Contains("\\(", StringComparison.Ordinal) &&
             !markdown.Contains("\\[", StringComparison.Ordinal)))
        {
            return markdown ?? string.Empty;
        }

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var result = new StringBuilder(markdown.Length + 64);
        var buffer = new StringBuilder();
        string? openFence = null;

        void Flush()
        {
            if (buffer.Length == 0)
            {
                return;
            }

            var text = buffer.ToString();
            text = DisplayMathRegex.Replace(text, m => "\n$$\n" + m.Groups["body"].Value.Trim() + "\n$$\n");
            text = InlineMathRegex.Replace(text, m => "$" + m.Groups["body"].Value.Trim() + "$");

            result.Append(text);
            buffer.Clear();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            if (openFence is null)
            {
                if (trimmed.StartsWith("```", StringComparison.Ordinal) ||
                    trimmed.StartsWith("~~~", StringComparison.Ordinal))
                {
                    Flush();
                    openFence = trimmed[..3];
                    result.Append(line);
                    if (i < lines.Length - 1)
                    {
                        result.Append('\n');
                    }

                    continue;
                }

                buffer.Append(line);
                if (i < lines.Length - 1)
                {
                    buffer.Append('\n');
                }
            }
            else
            {
                result.Append(line);
                if (i < lines.Length - 1)
                {
                    result.Append('\n');
                }

                if (trimmed.StartsWith(openFence, StringComparison.Ordinal))
                {
                    openFence = null;
                }
            }
        }

        Flush();
        return result.ToString();
    }
}
