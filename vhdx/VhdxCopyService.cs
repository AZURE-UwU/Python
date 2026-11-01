using System.Diagnostics;
using System.Security.Principal;

namespace VhdxCopier;

public sealed record CopyProgressInfo(
    long TotalBytes,
    long BytesCopied,
    double BytesPerSecond,
    TimeSpan? Remaining);

public sealed record CopyResult(
    string DestinationPath,
    bool UsedVss,
    TimeSpan Elapsed,
    long BytesCopied);

public sealed class VhdxCopyService
{
    public async Task<CopyResult> CopyAsync(
        string sourcePath,
        string destinationPath,
        bool useVssIfLocked,
        IProgress<CopyProgressInfo> progress,
        IProgress<string>? status,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("源 VHDX 不存在。", sourcePath);
        }

        var sourceInfo = new FileInfo(sourcePath);
        var copySource = sourcePath;
        var usedVss = false;
        string? shadowId = null;

        try
        {
            status?.Report("正在检查源文件占用状态...");
            var isLocked = !CanOpenExclusive(sourcePath);

            if (isLocked)
            {
                if (!useVssIfLocked)
                {
                    throw new IOException(
                        "源 VHDX 正在被占用，且未启用 VSS 快照。请关闭占用它的程序，或勾选 VSS 选项。");
                }

                if (!IsAdministrator())
                {
                    throw new UnauthorizedAccessException(
                        "创建 VSS 快照需要管理员权限。请以管理员身份运行本程序。");
                }

                var volumeRoot = Path.GetPathRoot(sourcePath);
                if (string.IsNullOrEmpty(volumeRoot) ||
                    volumeRoot.StartsWith(@"\\", StringComparison.Ordinal))
                {
                    throw new NotSupportedException(
                        "源 VHDX 在网络共享或非本地卷上，本机 VSS 无法快照该路径。请改用文件服务器快照或 WinPE 离线复制。");
                }

                status?.Report("源文件被占用，正在创建 VSS 快照...");
                var shadow = await Task
                    .Run(() => VssShadow.Create(volumeRoot), cancellationToken)
                    .ConfigureAwait(false);
                shadowId = shadow.ShadowId;
                copySource = shadow.BuildSourcePath(sourcePath);
                usedVss = true;
                status?.Report("VSS 快照已创建，开始复制...");
            }
            else
            {
                status?.Report("源文件未被占用，直接复制...");
            }

            var stopwatch = Stopwatch.StartNew();
            var lastReportAt = TimeSpan.Zero;
            var context = new NativeCopy.CopyContext(
                cancellationToken,
                sample =>
                {
                    var now = stopwatch.Elapsed;
                    if (now - lastReportAt < TimeSpan.FromMilliseconds(120) &&
                        sample.BytesCopied < sample.TotalBytes)
                    {
                        return;
                    }

                    lastReportAt = now;
                    var total = sample.TotalBytes > 0 ? sample.TotalBytes : sourceInfo.Length;
                    var speed = now.TotalSeconds > 0
                        ? sample.BytesCopied / now.TotalSeconds
                        : 0;
                    var remaining = speed > 0 && total > sample.BytesCopied
                        ? TimeSpan.FromSeconds((total - sample.BytesCopied) / speed)
                        : (TimeSpan?)null;

                    progress.Report(new CopyProgressInfo(
                        total,
                        sample.BytesCopied,
                        speed,
                        remaining));
                })
            {
                TotalBytes = sourceInfo.Length
            };

            var result = await Task
                .Run(
                    () => NativeCopy.Copy(copySource, destinationPath, context),
                    cancellationToken)
                .ConfigureAwait(false);

            stopwatch.Stop();
            var totalBytes = result.TotalBytes > 0 ? result.TotalBytes : sourceInfo.Length;
            var copiedBytes = result.BytesCopied > 0 ? result.BytesCopied : totalBytes;
            var elapsed = stopwatch.Elapsed;

            progress.Report(new CopyProgressInfo(
                totalBytes,
                copiedBytes,
                copiedBytes / Math.Max(0.001, elapsed.TotalSeconds),
                TimeSpan.Zero));

            return new CopyResult(destinationPath, usedVss, elapsed, copiedBytes);
        }
        finally
        {
            if (shadowId is not null)
            {
                try
                {
                    VssShadow.Delete(shadowId);
                }
                catch
                {
                }
            }
        }
    }

    public static string FormatBytes(long bytes)
    {
        var units = new[] { "B", "KB", "MB", "GB", "TB" };
        var value = (double)Math.Max(0, bytes);
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }

    private static bool CanOpenExclusive(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None,
                1,
                FileOptions.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}
