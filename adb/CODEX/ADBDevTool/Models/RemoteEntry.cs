/// @file    RemoteEntry.cs
/// @author  Bowen
/// @module  ADBDevTool / 手机端文件条目

namespace ADBDevTool.Models;

// x:Bind 的 x:DataType 需在 XAML 编译器里可解析，故类型为 public。
public sealed class RemoteEntry
{
    public required string Name { get; init; }

    public required string FullPath { get; init; }

    public required bool IsDirectory { get; init; }

    public required bool IsLink { get; init; }

    /// <summary>字节数；目录或无权限读取时为 0。</summary>
    public required long Size { get; init; }

    /// <summary>原样保留 ls 输出的 "yyyy-MM-dd HH:mm"；解析不出时为空串。</summary>
    public required string Modified { get; init; }

    public string SizeText => IsDirectory ? "—" : FormatSize(Size);

    /// <summary>Segoe MDL2 Assets 的字形：文件夹 / 文件 / 符号链接。</summary>
    public string Glyph => IsDirectory ? "\uE8B7" : IsLink ? "\uE71B" : "\uE8A5";

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };
}
