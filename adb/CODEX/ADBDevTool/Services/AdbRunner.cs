/// @file    AdbRunner.cs
/// @author  Bowen
/// @module  ADBDevTool / adb 进程调用层
///
/// 外部契约（可核验指针，来源 platform-tools 37.0.1）：
/// 1. `adb devices -l` 首行为 "List of devices attached"，其后每台设备一列，列间以制表符分隔。
/// 2. `adb mdns services` 打印 mDNS 缓存快照后立即退出；无服务时只有表头行 "List of discovered mdns services"。
/// 3. adb 文本输出按 UTF-8 解码，设备型号可能含非 ASCII 字符。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ADBDevTool.Services;

internal sealed record AdbResult(int ExitCode, string StandardOutput, string StandardError, string Arguments)
{
    /// <summary>调用方直接展示的一行结果。</summary>
    public string Summary
    {
        get
        {
            var text = string.IsNullOrWhiteSpace(StandardError) ? StandardOutput : StandardError;
            text = text.Replace("\r", string.Empty).Trim();
            if (text.Length == 0)
            {
                return ExitCode == 0 ? "ok" : $"exit={ExitCode}";
            }

            var lines = text.Split('\n');
            return lines.Length == 1 ? text : $"{lines[0]} (+{lines.Length - 1} 行)";
        }
    }
}

internal sealed class AdbRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    public AdbRunner(string adbPath)
    {
        AdbPath = adbPath;
    }

    public string AdbPath { get; }

    /// <summary>按优先级查找 adb：程序目录下的 platform-tools 优先，其次 PATH 与 Android SDK 默认位置。</summary>
    public static string? LocateAdb()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "platform-tools", "adb.exe"),
            Path.Combine(AppContext.BaseDirectory, "adb.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Android",
                "Sdk",
                "platform-tools",
                "adb.exe"),
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
                var candidate = Path.Combine(directory.Trim(), "adb.exe");
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

    public Task<AdbResult> RunAsync(string arguments, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        => RunAsync(arguments, timeout ?? DefaultTimeout, cancellationToken, arguments);

    private async Task<AdbResult> RunAsync(
        string arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string displayArguments)
    {
        var startInfo = new ProcessStartInfo(AdbPath, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // 外部契约：adb 37.0.1 在启动 daemon 时读取环境变量 ADB_MDNS_AUTO_CONNECT，
        // 值为 "0" 时不自动连接 mDNS 发现的设备（实测：daemon 以该值启动后 18 秒内 devices 仍为空）。
        // 关掉它，界面上的「连接 / 断开连接」才由用户决定；对已由其它工具启动的 daemon 无效，需点「重启 adb」。
        startInfo.Environment["ADB_MDNS_AUTO_CONNECT"] = "0";

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new AdbResult(-1, string.Empty, ex.Message, displayArguments);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var exitTask = process.WaitForExitAsync(CancellationToken.None);

        var finished = await Task.WhenAny(exitTask, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);
        var timedOut = finished != exitTask;

        if (timedOut)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // 进程可能已自行退出。
            }
        }

        var stdout = await ReadSafeAsync(stdoutTask).ConfigureAwait(false);
        var stderr = await ReadSafeAsync(stderrTask).ConfigureAwait(false);

        if (timedOut)
        {
            return new AdbResult(-1, stdout, $"命令超时（{timeout.TotalSeconds:0} 秒）：{stderr}".Trim(), displayArguments);
        }

        int exitCode;
        try
        {
            exitCode = process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            exitCode = -1;
        }

        return new AdbResult(exitCode, stdout, stderr, displayArguments);
    }

    private static async Task<string> ReadSafeAsync(Task<string> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch
        {
            return string.Empty;
        }
    }
}
