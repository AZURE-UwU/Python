using Markdig;
using Markdig.Extensions.EmphasisExtras;

namespace MarkdownToWord.Core;

/// <summary>
/// 统一的 Markdig 管线：预览与导出共用同一套扩展，保证"看到的"和"导出的"一致。
/// </summary>
public static class MarkdownPipelineFactory
{
    private static MarkdownPipeline? _withMath;
    private static MarkdownPipeline? _withoutMath;

    public static MarkdownPipeline Get(bool enableMath)
    {
        if (enableMath)
        {
            return _withMath ??= Build(true);
        }

        return _withoutMath ??= Build(false);
    }

    private static MarkdownPipeline Build(bool enableMath)
    {
        var builder = new MarkdownPipelineBuilder()
            // 基础块级扩展
            .UsePipeTables()
            .UseGridTables()
            .UseDefinitionLists()
            .UseFootnotes()
            .UseYamlFrontMatter()
            // 列表
            .UseTaskLists()
            .UseListExtras()
            // 行内扩展
            // 单个 ~ 不作下标定界符：中文技术文本里 ~ 常作范围号（0.85~15 MHz、104~106），
            // ~~删除线~~、^上标^、++下划线++、==高亮== 照常解析。
            .UseEmphasisExtras(
                EmphasisExtraOptions.Strikethrough |
                EmphasisExtraOptions.Superscript |
                EmphasisExtraOptions.Inserted |
                EmphasisExtraOptions.Marked)
            .UseAutoLinks()
            .UseEmojiAndSmiley()
            .UseAbbreviations()
            .UseCjkFriendlyEmphasis()
            .UseNonAsciiNoEscape()
            // 结构
            .UseAutoIdentifiers()
            .UseCustomContainers()
            .UseAlertBlocks()
            // 排版细节
            .UseSmartyPants()
            .UsePreciseSourceLocation();

        if (enableMath)
        {
            builder.UseMathematics();
        }

        return builder.Build();
    }
}
