/// @file    ScrcpyRunner.cs
/// @author  Bowen
/// @module  ADBDevTool / scrcpy 投屏进程
///
/// 外部契约（可核验指针，来源 scrcpy 3.3.3 win64 发行包与官方 doc/develop.md）：
/// 1. scrcpy 读取环境变量 ADB 作为 adb 可执行文件路径；实测设置该变量后进程列表里没有出现它自带的 adb.exe。
/// 2. `-s <serial>` 指定设备，`--window-title <标题>` 设置窗口标题。
/// 3. scrcpy.exe 是控制台程序，从无控制台的 GUI 进程启动时 Windows 会给它新建控制台窗口；
///    Win11 默认把它交给 Windows Terminal（窗口类 CASCADIA_HOSTING_WINDOW_CLASS），否则是 ConsoleWindowClass。
/// 4. 只设 CreateNoWindow = true 会让 scrcpy 立刻以退出码 1 退出（拿不到有效的标准输出句柄）；
///    必须同时重定向 stdout/stderr，此时实测无任何控制台窗口、SDL 投屏窗口正常、日志可从管道读出。
/// 5. 不能用 ProcessStartInfo.WindowStyle = Minimized 代替隐藏：STARTUPINFO 的显示状态会被进程的第一个
///    窗口继承，实测 scrcpy 的 SDL 投屏窗口也会一起最小化。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace ADBDevTool.Services;

internal static class ScrcpyRunner
{
    /// <summary>按优先级查找 scrcpy：程序目录下的 scrcpy 子目录优先（随程序分发），其次 PATH。</summary>
    public static string? Locate()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "scrcpy", "scrcpy.exe"),
            Path.Combine(AppContext.BaseDirectory, "scrcpy.exe"),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), "scrcpy.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // PATH 中的非法路径片段直接跳过。
            }
        }

        return null;
    }

    /// <summary>启动 scrcpy。调用方负责在结束时结束该进程。</summary>
    /// <param name="showConsole">是否保留 scrcpy 自己的控制台窗口；false 时不创建窗口，但输出会被重定向并经 onOutput 回调。</param>
    public static Process Start(
        string scrcpyPath,
        string adbPath,
        string serial,
        string windowTitle,
        bool turnScreenOff,
        bool showConsole,
        int maxFps,
        int bitRateMbps,
        int maxSize,
        string keyboardMode,
        Action<string>? onOutput)
    {
        var startInfo = new ProcessStartInfo(scrcpyPath)
        {
            UseShellExecute = false,
            CreateNoWindow = !showConsole,
            RedirectStandardOutput = !showConsole,
            RedirectStandardError = !showConsole,
            // scrcpy 按 UTF-8 输出；不指定的话 .NET 会按系统 OEM 代码页解码，中文路径会变乱码。
            StandardOutputEncoding = showConsole ? null : Encoding.UTF8,
            StandardErrorEncoding = showConsole ? null : Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(scrcpyPath) ?? AppContext.BaseDirectory,
        };

        // 用 ArgumentList 而不是拼字符串：窗口标题含空格和中文，拼串会被拆成多个参数。
        startInfo.ArgumentList.Add("-s");
        startInfo.ArgumentList.Add(serial);
        startInfo.ArgumentList.Add("--window-title");
        startInfo.ArgumentList.Add(windowTitle);
        if (turnScreenOff)
        {
            startInfo.ArgumentList.Add("-S");
        }

        if (maxFps > 0)
        {
            startInfo.ArgumentList.Add($"--max-fps={maxFps}");
        }

        if (bitRateMbps > 0)
        {
            startInfo.ArgumentList.Add($"--video-bit-rate={bitRateMbps}M");
        }

        if (maxSize > 0)
        {
            startInfo.ArgumentList.Add($"--max-size={maxSize}");
        }

        if (!string.IsNullOrWhiteSpace(keyboardMode)
            && !string.Equals(keyboardMode, "auto", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add($"--keyboard={keyboardMode}");
        }

        // 每秒往标准输出打一行实际帧率；调用方据此更新界面上的实时帧率显示。
        startInfo.ArgumentList.Add("--print-fps");

        startInfo.Environment["ADB"] = adbPath;

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("scrcpy 进程启动失败。");
        process.EnableRaisingEvents = true;

        if (!showConsole && onOutput is not null)
        {
            process.OutputDataReceived += (_, args) => Forward(onOutput, args.Data);
            process.ErrorDataReceived += (_, args) => Forward(onOutput, args.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        return process;
    }

    private static void Forward(Action<string> onOutput, string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        onOutput(line);
    }
}
