using System.Globalization;
using System.Text;

namespace MarkdownToWord.Core;

/// <summary>文本处理小工具：XML 安全字符、全角/半角、长度估算。</summary>
internal static class TextUtils
{
    /// <summary>去掉 XML 非法字符（Word 打开会报错的那些）。</summary>
    public static string SanitizeXml(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '\t' || ch == '\n' || ch == '\r')
            {
                sb.Append(ch);
                continue;
            }

            if (char.IsHighSurrogate(ch))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    sb.Append(ch).Append(text[i + 1]);
                    i++;
                }

                continue;
            }

            if (char.IsSurrogate(ch))
            {
                continue;
            }

            var code = (int)ch;
            if (code >= 0x20 && code != 0xFFFE && code != 0xFFFF)
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }

    /// <summary>估算字符串显示宽度：CJK 记 2，其余记 1。</summary>
    public static int DisplayWidth(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var width = 0;
        foreach (var ch in text)
        {
            width += IsWide(ch) ? 2 : 1;
        }

        return width;
    }

    private static bool IsWide(char ch)
    {
        if (ch < 0x1100)
        {
            return false;
        }

        return ch <= 0x115F                       // 韩文字母
               || ch == 0x2329 || ch == 0x232A
               || (ch >= 0x2E80 && ch <= 0xA4CF)  // CJK 部首 ~ 彝文
               || (ch >= 0xAC00 && ch <= 0xD7A3)  // 韩文音节
               || (ch >= 0xF900 && ch <= 0xFAFF)  // CJK 兼容
               || (ch >= 0xFE30 && ch <= 0xFE6F)  // CJK 兼容形式
               || (ch >= 0xFF00 && ch <= 0xFF60)  // 全角
               || (ch >= 0xFFE0 && ch <= 0xFFE6)
               || (ch >= 0x20000 && ch <= 0x3FFFD);
    }

    /// <summary>把磅值转换为 Word 的半磅整数。</summary>
    public static string HalfPoints(double pt)
    {
        var half = (int)Math.Round(pt * 2, MidpointRounding.AwayFromZero);
        return half.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>把磅值转换为 twips（1pt = 20twips）。</summary>
    public static int PtToTwips(double pt) => (int)Math.Round(pt * 20, MidpointRounding.AwayFromZero);

    /// <summary>把厘米转换为 twips（1cm = 567twips）。</summary>
    public static int CmToTwips(double cm) => (int)Math.Round(cm * 567, MidpointRounding.AwayFromZero);

    /// <summary>文件大小友好显示。</summary>
    public static string HumanSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} {units[unit]}"
            : $"{value:0.##} {units[unit]}";
    }
}
