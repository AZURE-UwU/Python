namespace MarkdownToWord.Core;

/// <summary>页面尺寸预设。</summary>
public enum PageSizePreset
{
    A4,
    Letter,
    A5,
    Legal,
}

/// <summary>页面方向。</summary>
public enum PageOrientationPreset
{
    Portrait,
    Landscape,
}

/// <summary>「复制 Word」往剪贴板里放什么。</summary>
public enum ClipboardMode
{
    /// <summary>放生成好的 .docx 文件：粘进 Word 是插入整份文档，粘到文件夹会得到该文件。</summary>
    Document,

    /// <summary>放 HTML 富文本：粘进 Word 是在光标处插入带格式的正文（公式仍是 Word 原生公式）。</summary>
    RichText,
}

/// <summary>
/// Markdown 转 Word 的全部可调参数。默认值面向"中文技术文档"场景。
/// </summary>
public sealed class ConversionOptions
{
    // ---------- 字体 ----------
    /// <summary>正文西文字体。</summary>
    public string BodyFont { get; set; } = "Calibri";

    /// <summary>正文中文字体。</summary>
    public string BodyFontEastAsia { get; set; } = "等线";

    /// <summary>标题西文字体。</summary>
    public string HeadingFont { get; set; } = "Calibri";

    /// <summary>标题中文字体。</summary>
    public string HeadingFontEastAsia { get; set; } = "等线";

    /// <summary>等宽字体（代码、行内代码）。</summary>
    public string MonoFont { get; set; } = "Consolas";

    /// <summary>正文字号（磅）。</summary>
    public double BodyFontSizePt { get; set; } = 11.0;

    /// <summary>代码字号（磅）。</summary>
    public double CodeFontSizePt { get; set; } = 9.5;

    /// <summary>行距倍数（1.0 ~ 2.0）。</summary>
    public double LineSpacing { get; set; } = 1.15;

    /// <summary>段后间距（磅）。</summary>
    public double ParagraphSpacingAfterPt { get; set; } = 8.0;

    // ---------- 页面 ----------
    public PageSizePreset PageSize { get; set; } = PageSizePreset.A4;

    public PageOrientationPreset Orientation { get; set; } = PageOrientationPreset.Portrait;

    /// <summary>页边距（厘米）。</summary>
    public double MarginCm { get; set; } = 2.54;

    // ---------- 内容开关 ----------
    /// <summary>把 H1-H6 转换为 Word 内置 Heading 1-6 样式（可用导航窗格）。</summary>
    public bool UseWordHeadingStyles { get; set; } = true;

    /// <summary>为标题自动加编号（1 / 1.1 / 1.1.1）。</summary>
    public bool NumberHeadings { get; set; }

    /// <summary>代码块语法高亮（关键字、字符串、注释着色）。</summary>
    public bool SyntaxHighlight { get; set; } = true;

    /// <summary>解析 LaTeX 数学公式并输出为 Word 原生公式（OMML）。</summary>
    public bool EnableMath { get; set; } = true;

    /// <summary>允许下载 http(s) 远程图片；关闭后远程图片只保留占位文本。</summary>
    public bool AllowRemoteImages { get; set; } = true;

    /// <summary>单张远程图片下载超时（秒）。</summary>
    public int RemoteImageTimeoutSeconds { get; set; } = 10;

    /// <summary>表格是否自动适应窗口宽度。</summary>
    public bool TableAutoFit { get; set; } = true;

    /// <summary>表格首行作为表头并加底纹。</summary>
    public bool TableHeaderShading { get; set; } = true;

    /// <summary>偶数行加浅色底纹（斑马纹）。</summary>
    public bool TableZebraStripes { get; set; } = true;

    /// <summary>把 YAML front matter 的 title 输出为文档标题。</summary>
    public bool InsertFrontMatterTitle { get; set; } = true;

    /// <summary>YAML front matter 的其它字段写入文档属性（作者/主题/关键字）。</summary>
    public bool MapFrontMatterToProperties { get; set; } = true;

    /// <summary>把 ¶ 级硬换行保留为换行（否则合成一个空格）。</summary>
    public bool PreserveHardLineBreaks { get; set; } = true;

    /// <summary>文档标题（为空时用 front matter title 或"Markdown 文档"）。</summary>
    public string DocumentTitle { get; set; } = string.Empty;

    /// <summary>文档作者。</summary>
    public string DocumentAuthor { get; set; } = string.Empty;

    // ---------- 剪贴板 ----------
    /// <summary>「复制 Word」放进剪贴板的形态：整份文档，还是带格式的正文。</summary>
    public ClipboardMode ClipboardMode { get; set; } = ClipboardMode.RichText;
}
