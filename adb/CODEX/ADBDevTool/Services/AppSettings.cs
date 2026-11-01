/// @file    AppSettings.cs
/// @author  Bowen
/// @module  ADBDevTool / 用户设置持久化
///
/// 不变量：
/// 1. 设置文件位于 %LOCALAPPDATA%\ADBDevTool\settings.json；不放在程序目录，避免绿色文件夹被放到只读位置时写不进去。
/// 2. 文件缺失或内容损坏时回落到默认值，不抛异常，保证程序能起来。

using System;
using System.IO;
using System.Text.Json;
using System.Text.Encodings.Web;

namespace ADBDevTool.Services;

internal sealed class AppSettings
{
    // 默认编码器会把 "+" 之类的字符转义成 \u002B，配置是本地文件、不进 HTML，用宽松编码器保证可读可手改。
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>投屏时关闭手机屏幕（scrcpy 的 -S / --turn-screen-off）。</summary>
    public bool TurnScreenOffOnMirror { get; set; }

    /// <summary>是否保留 scrcpy 自己的控制台窗口。</summary>
    public bool ShowScrcpyConsole { get; set; }

    /// <summary>投屏窗口的切换快捷键：已最小化则恢复并置于前台，否则最小化。空串表示不设置。</summary>
    public string ToggleShortcut { get; set; } = "Ctrl+Alt+M";

    /// <summary>投屏视频帧率上限，对应 scrcpy 的 --max-fps；0 表示不限制。</summary>
    public int VideoMaxFps { get; set; }

    /// <summary>投屏视频码率（Mbps），对应 scrcpy 的 --video-bit-rate；scrcpy 自身的默认值是 8。</summary>
    public int VideoBitRateMbps { get; set; } = 8;

    /// <summary>投屏分辨率上限（长边像素），对应 scrcpy 的 --max-size；0 表示不限制。</summary>
    public int VideoMaxSize { get; set; }

    /// <summary>
    /// 键盘输入方式，对应 scrcpy 的 --keyboard：auto / uhid / sdk / disabled。
    /// auto 表示不传该参数，由 scrcpy 自己决定。
    /// </summary>
    public string KeyboardMode { get; set; } = "auto";

    /// <summary>从手机取回的文件在 Windows 上的落地目录；空串表示用系统的「下载」目录。</summary>
    public string DownloadDirectory { get; set; } = string.Empty;

    /// <summary>取实际可用的下载目录：配置为空时回落到用户目录下的 Downloads，再不行用「文档」。</summary>
    public string ResolveDownloadDirectory()
    {
        if (!string.IsNullOrWhiteSpace(DownloadDirectory) && Directory.Exists(DownloadDirectory))
        {
            return DownloadDirectory;
        }

        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");
        return Directory.Exists(downloads)
            ? downloads
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ADBDevTool",
        "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch (Exception)
        {
            // 配置损坏或不可读时用默认值。
        }

        return new AppSettings();
    }

    public bool Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, SerializerOptions));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
