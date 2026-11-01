using Markdig;
using MarkdownToWord.Core.Docx;

namespace MarkdownToWord.Core;

/// <summary>
/// 对外门面：Markdown 文本 → DOCX 文件。
/// 预览与导出共用同一套 Markdig 管线，保证所见即所得。
/// </summary>
public sealed class MarkdownConverter
{
    public MarkdownConverter(ConversionOptions? options = null)
    {
        Options = options ?? new ConversionOptions();
    }

    public ConversionOptions Options { get; }

    /// <summary>把 Markdown 文本写入 <paramref name="output"/>。</summary>
    /// <param name="markdown">Markdown 源文本。</param>
    /// <param name="output">目标流（DOCX）。</param>
    /// <param name="baseDirectory">相对图片路径的基准目录，通常是被打开文件所在目录。</param>
    public ConversionStats Convert(string markdown, Stream output, string? baseDirectory = null)
    {
        var pipeline = MarkdownPipelineFactory.Get(Options.EnableMath);
        var normalized = Options.EnableMath ? MathDelimiterNormalizer.Normalize(markdown) : markdown ?? string.Empty;
        var document = Markdown.Parse(normalized, pipeline);

        var writer = new DocxWriter(Options, baseDirectory);
        return writer.Write(document, output);
    }

    /// <summary>把 Markdown 文本转换成 DOCX 文件。</summary>
    public ConversionStats ConvertToFile(string markdown, string outputPath, string? baseDirectory = null)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // OpenXML SDK 3.x 要求包流可读写（它按 ZIP 包方式打开）
        using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        return Convert(markdown, stream, baseDirectory);
    }

    /// <summary>在后台线程完成转换（耗时主要来自远程图片下载）。</summary>
    public Task<ConversionStats> ConvertToFileAsync(
        string markdown,
        string outputPath,
        string? baseDirectory = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ConvertToFile(markdown, outputPath, baseDirectory);
        }, cancellationToken);
    }

    /// <summary>生成预览用的 HTML 片段。</summary>
    public static string BuildPreviewFragment(string markdown, bool enableMath)
    {
        var pipeline = MarkdownPipelineFactory.Get(enableMath);
        var normalized = enableMath ? MathDelimiterNormalizer.Normalize(markdown) : markdown ?? string.Empty;
        return Markdown.ToHtml(normalized, pipeline);
    }

    /// <summary>Markdown → 纯文本（用于字数统计）。</summary>
    public static string ToPlainText(string markdown)
    {
        var pipeline = MarkdownPipelineFactory.Get(false);

        // Markdig 的纯文本渲染会把 \( \) \[ \] 的反斜杠当转义去掉（\(C_1\) → (C_1)）。
        // 用私用区字符占位，渲染完再还原，纯文本里保留 LaTeX 定界符原文。
        var guarded = (markdown ?? string.Empty)
            .Replace(@"\(", InlineMathOpen, StringComparison.Ordinal)
            .Replace(@"\)", InlineMathClose, StringComparison.Ordinal)
            .Replace(@"\[", BlockMathOpen, StringComparison.Ordinal)
            .Replace(@"\]", BlockMathClose, StringComparison.Ordinal);

        return Markdown.ToPlainText(guarded, pipeline)
            .Replace(InlineMathOpen, @"\(", StringComparison.Ordinal)
            .Replace(InlineMathClose, @"\)", StringComparison.Ordinal)
            .Replace(BlockMathOpen, @"\[", StringComparison.Ordinal)
            .Replace(BlockMathClose, @"\]", StringComparison.Ordinal);
    }

    private const string InlineMathOpen = "\uE000";
    private const string InlineMathClose = "\uE001";
    private const string BlockMathOpen = "\uE002";
    private const string BlockMathClose = "\uE003";
}
