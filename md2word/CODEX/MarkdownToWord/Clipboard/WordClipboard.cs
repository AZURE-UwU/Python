namespace MarkdownToWord.Clipboard;

/// <summary>
/// 把"Word 内容"放进剪贴板：不是一段文本，而是生成好的 .docx 文件本身。
///
/// 这样做的原因：粘进 Word 会按"插入文档"处理，表格、图片、以及 Word 原生公式（OMML）
/// 都能完整带过去；如果只放 HTML/RTF 文本，公式会退化成 LaTeX 源码。
///
/// 写剪贴板的具体动作交给 <see cref="ShellFileClipboard"/>：它复刻"资源管理器里复制文件"，
/// 而不是 WinRT 的 DataPackage.SetStorageItems——后者给出的内容缺 FileContents 数据流，
/// Word 取不到内容会直接报"Word 无法获取 … 链接的数据"。
/// </summary>
internal static class WordClipboard
{
    /// <summary>剪贴板用 docx 的存放目录（必须留盘：Word 粘贴时是照着这个路径去读的）。</summary>
    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MarkdownToWord",
        "Clipboard");

    /// <summary>「带格式的正文」模式下，图片落盘的位置（HTML 里按 file:// 引用）。</summary>
    public static string AssetFolder => Path.Combine(Folder, "assets");

    /// <summary>为剪贴板准备一个安全的文件名。</summary>
    public static string BuildFileName(string? sourcePath, string fallback = "document")
    {
        var name = Path.GetFileNameWithoutExtension(sourcePath ?? string.Empty);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = fallback;
        }

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return name + ".docx";
    }

    /// <summary>把 docx 文件写入剪贴板（与在资源管理器里复制该文件等价）。</summary>
    public static void SetDocument(string docxPath)
    {
        ShellFileClipboard.SetFile(docxPath);
    }
}
