using System.Text;
using MarkdownToWord.Clipboard;
using MarkdownToWord.Core;
using MarkdownToWord.Core.Docx;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;

namespace MarkdownToWord.Cli;

/// <summary>
/// 无界面的命令行转换入口，方便批处理与自动化验证：
///   MarkdownToWord.exe --convert input.md output.docx [选项]
/// 选项：
///   --no-math          不解析 LaTeX 公式（按原文输出）
///   --no-highlight     关闭代码高亮
///   --no-remote        禁止下载远程图片
///   --number-headings  标题自动编号
///   --page=A4|Letter|A5|Legal
///   --margin=2.54      页边距（厘米）
/// </summary>
internal static class CliRunner
{
    public static int Run(string[] args)
    {
        if (args.Length >= 2 && args[0].Equals("--math-probe", StringComparison.OrdinalIgnoreCase))
        {
            Console.OutputEncoding = Encoding.UTF8;
            var officeMath = LatexToOmml.ToOfficeMath(args[1]);
            Console.WriteLine(officeMath.OuterXml);
            return 0;
        }

        // 与界面里的「复制 Word」等价：生成 docx 并放进剪贴板（用于脚本与自动化验证）
        if (args.Length >= 2 && args[0].Equals("--copy", StringComparison.OrdinalIgnoreCase))
        {
            Console.OutputEncoding = Encoding.UTF8;
            WinRT.ComWrappersSupport.InitializeComWrappers();

            var source = Path.GetFullPath(args[1]);
            if (!File.Exists(source))
            {
                Console.Error.WriteLine("找不到输入文件：" + source);
                return 3;
            }

            var copyText = File.ReadAllText(source, Encoding.UTF8);
            var copyOptions = new ConversionOptions { ClipboardMode = ParseClipboardMode(args) };
            var copyBaseDirectory = Path.GetDirectoryName(source);

            // 带格式的正文：HTML 剪贴板（静态数据，进程退出也有内容）
            if (copyOptions.ClipboardMode == ClipboardMode.RichText)
            {
                var warnings = RichTextClipboard.Copy(copyText, copyOptions, copyBaseDirectory);
                Console.WriteLine("已复制带格式的正文（HTML，公式为 Word 原生公式）");
                foreach (var warning in warnings)
                {
                    Console.WriteLine("警告：" + warning);
                }

                return 0;
            }

            var copyConverter = new MarkdownConverter(copyOptions);

            Directory.CreateDirectory(WordClipboard.Folder);
            var target = Path.Combine(WordClipboard.Folder, WordClipboard.BuildFileName(source));

            var copyStats = copyConverter.ConvertToFile(copyText, target, copyBaseDirectory);
            WordClipboard.SetDocument(target);

            Console.WriteLine($"已复制 Word 内容：{target}");
            Console.WriteLine($"标题 {copyStats.Headings}｜段落 {copyStats.Paragraphs}｜公式 {copyStats.Equations}｜图片 {copyStats.Images}");

            // 剪贴板是按需渲染的，进程退出内容就没了；--hold 让脚本能顶住一会儿让人去粘贴
            var hold = GetHoldSeconds(args);
            if (hold > 0)
            {
                Console.WriteLine($"保持剪贴板 {hold} 秒，按 Ctrl+C 可提前结束…");
                Console.Out.Flush();
                ShellFileClipboard.KeepAlive(TimeSpan.FromSeconds(hold));
            }

            return 0;
        }

        var positional = new List<string>();
        var options = new ConversionOptions();
        var validate = false;
        var stdout = Console.Out;
        Console.OutputEncoding = Encoding.UTF8;

        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--no-math":
                    options.EnableMath = false;
                    break;
                case "--no-highlight":
                    options.SyntaxHighlight = false;
                    break;
                case "--no-remote":
                    options.AllowRemoteImages = false;
                    break;
                case "--number-headings":
                    options.NumberHeadings = true;
                    break;
                case "--validate":
                    validate = true;
                    break;
                case "--help":
                case "-h":
                    PrintUsage(stdout);
                    return 0;
                default:
                    if (arg.StartsWith("--page=", StringComparison.OrdinalIgnoreCase))
                    {
                        if (Enum.TryParse<PageSizePreset>(arg[7..], true, out var preset))
                        {
                            options.PageSize = preset;
                        }
                    }
                    else if (arg.StartsWith("--margin=", StringComparison.OrdinalIgnoreCase))
                    {
                        if (double.TryParse(arg[9..], out var margin) && margin is >= 0.5 and <= 6)
                        {
                            options.MarginCm = margin;
                        }
                    }
                    else
                    {
                        positional.Add(arg);
                    }

                    break;
            }
        }

        if (positional.Count < 2)
        {
            Console.Error.WriteLine("DEBUG args = " + string.Join(" | ", args));
            PrintUsage(stdout);
            return 2;
        }

        var input = Path.GetFullPath(positional[0]);
        var output = Path.GetFullPath(positional[1]);

        if (!File.Exists(input))
        {
            Console.Error.WriteLine($"找不到输入文件：{input}");
            return 3;
        }

        var markdown = File.ReadAllText(input, Encoding.UTF8);
        var converter = new MarkdownConverter(options);

        try
        {
            var stats = converter.ConvertToFile(markdown, output, Path.GetDirectoryName(input));
            stdout.WriteLine($"已生成：{output}");
            stdout.WriteLine(
                $"标题 {stats.Headings}｜段落 {stats.Paragraphs}｜列表 {stats.Lists}｜表格 {stats.Tables}｜" +
                $"代码块 {stats.CodeBlocks}｜公式 {stats.Equations}｜图片 {stats.Images}｜脚注 {stats.Footnotes}");

            foreach (var warning in stats.Warnings)
            {
                stdout.WriteLine("警告：" + warning);
            }

            if (validate)
            {
                stdout.WriteLine();
                stdout.WriteLine(ValidateDocx(output));
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("转换失败：" + ex);
            return 1;
        }
    }

    /// <summary>用 OpenXML SDK 自带的校验器检查产物是否符合 OOXML 架构。</summary>
    private static string ValidateDocx(string path)
    {
        using var document = WordprocessingDocument.Open(path, false);
        var validator = new OpenXmlValidator();
        var errors = validator.Validate(document).Take(40).ToList();

        if (errors.Count == 0)
        {
            return "OOXML 校验：通过（无错误）";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"OOXML 校验：发现 {errors.Count} 处问题");
        foreach (var error in errors)
        {
            sb.AppendLine($"  - [{error.ErrorType}] {error.Description}");
            sb.AppendLine($"    路径：{error.Path?.XPath}");
        }

        return sb.ToString();
    }

    private static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("Markdown to Word 离线版 — 命令行模式");
        writer.WriteLine("用法：MarkdownToWord.exe --convert <input.md> <output.docx> [选项]");
        writer.WriteLine("选项：--no-math --no-highlight --no-remote --number-headings --page=A4 --margin=2.54");
        writer.WriteLine("      MarkdownToWord.exe --copy <input.md> [--copy-as=text|doc] [--hold=秒]");
    }

    /// <summary>取 --copy-as=text|doc：决定「复制 Word」放带格式的正文，还是放整份 docx（默认正文）。</summary>
    private static ClipboardMode ParseClipboardMode(string[] args)
    {
        foreach (var arg in args)
        {
            if (!arg.StartsWith("--copy-as=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return arg[10..].Trim().ToLowerInvariant() switch
            {
                "text" or "richtext" or "html" => ClipboardMode.RichText,
                _ => ClipboardMode.Document,
            };
        }

        return ClipboardMode.RichText;
    }

    /// <summary>取 --hold=N（秒）：命令行复制到剪贴板后顶住进程的时长。</summary>
    private static int GetHoldSeconds(string[] args)
    {
        foreach (var arg in args)
        {
            if (!arg.StartsWith("--hold=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (int.TryParse(arg[7..], out var seconds))
            {
                return Math.Clamp(seconds, 0, 3600);
            }
        }

        return 0;
    }
}
