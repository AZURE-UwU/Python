using System.Diagnostics;
using System.IO;

namespace DiskManager.Services;

/// <summary>
/// 磁盘操作的分步日志。
///
/// 目的：这类工具最麻烦的故障是"卡住不动"——没有日志就只能猜。
/// 这里给每个关键步骤记录进入/离开与耗时，出问题时直接看最后一条"进入但没离开"的记录，
/// 就知道卡在哪一步（例如卡在遍历卷、扫描目录、还是系统弹出请求）。
///
/// 日志写在 %LOCALAPPDATA%\DiskTools\operations.log，纯追加，不做删除。
/// </summary>
internal static class OperationLog
{
    private static readonly object Gate = new();

    /// <summary>日志文件路径（界面出错时会把它显示给用户）。</summary>
    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiskTools",
        "operations.log");

    /// <summary>写一条日志（失败不影响功能）。</summary>
    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 记日志失败不影响磁盘操作
        }
    }

    /// <summary>记录一段耗时；用 using 包住要观察的步骤。</summary>
    public static IDisposable Scope(string name)
        => new ScopeImpl(name);

    private sealed class ScopeImpl : IDisposable
    {
        private readonly string _name;
        private readonly Stopwatch _watch = Stopwatch.StartNew();

        public ScopeImpl(string name)
        {
            _name = name;
            Write($"→ {name}");
        }

        public void Dispose()
        {
            _watch.Stop();
            Write($"← {_name}　耗时 {_watch.ElapsedMilliseconds} ms");
        }
    }
}
