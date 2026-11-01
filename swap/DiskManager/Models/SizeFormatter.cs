using System.Globalization;

namespace DiskManager.Models;

/// <summary>容量文本格式化，输出风格与系统"磁盘管理"一致（如 1863 GB、260 MB）。</summary>
internal static class SizeFormatter
{
    private const long Kb = 1024L;
    private const long Mb = Kb * 1024;
    private const long Gb = Mb * 1024;
    private const long Tb = Gb * 1024;

    /// <summary>把字节数格式化为带单位的短文本。</summary>
    public static string Format(long bytes)
    {
        if (bytes <= 0)
        {
            return "0";
        }

        (double value, string unit) = bytes switch
        {
            >= Tb => (bytes / (double)Tb, "TB"),
            >= Gb => (bytes / (double)Gb, "GB"),
            >= Mb => (bytes / (double)Mb, "MB"),
            >= Kb => (bytes / (double)Kb, "KB"),
            _ => ((double)bytes, "字节"),
        };

        // 磁盘管理只保留有效数字：大于 100 取整，大于 10 保留一位，其余保留两位。
        string text = value >= 100 ? value.ToString("0", CultureInfo.CurrentCulture)
            : value >= 10 ? value.ToString("0.#", CultureInfo.CurrentCulture)
            : value.ToString("0.##", CultureInfo.CurrentCulture);

        return $"{text} {unit}";
    }

    /// <summary>把字节数格式化为 MB，用于"扩展卷/压缩卷"输入框。</summary>
    public static long ToMegabytes(long bytes) => bytes / Mb;

    public static long FromMegabytes(long megabytes) => megabytes * Mb;

    /// <summary>把字节数换算成 MB 文本（保留整数）。</summary>
    public static string FormatMegabytes(long bytes) => (bytes / Mb).ToString(CultureInfo.CurrentCulture);
}
