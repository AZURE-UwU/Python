using System.IO;
using DiskManager.Interop;

namespace DiskManager.Services;

/// <summary>
/// 虚拟磁盘文件（.vhd / .vhdx）的扫描器。
///
/// 两处要用到：
///   1) 脱机安全闸门：判断某块磁盘上是否有"正在被使用"的虚拟磁盘文件；
///   2) 分离 VHD：找出某块虚拟磁盘对应的宿主文件。
///
/// 扫描是有界的（深度 ≤4 层、每卷 ≤600 个目录），避免在 2 TB 数据盘上耗时过久；
/// 跟随重解析点（junction / 符号链接）会成环，直接跳过。
/// </summary>
internal static class VirtualDiskFileScanner
{
    private const int MaxScanDepth = 8;
    private const int MaxDirectoriesPerVolume = 600;

    /// <summary>
    /// 只找"虚拟磁盘本体"——分离 VHD 时用这个（必须精确，不能把别的文件当虚拟磁盘）。
    /// </summary>
    private static readonly string[] VirtualDiskExtensions = [".vhd", ".vhdx"];

    /// <summary>
    /// 判定"这块盘上有没有虚拟机正在用的文件"——安全闸门用这个。
    ///
    /// 关键：Hyper-V 不只有 .vhdx，还会占用检查点（.avhdx）、
    /// 配置（.vmcx）、保存状态（.vmrs / .vsv）、内存（.bin）、直通磁盘等文件。
    /// 只认 .vhd/.vhdx 会漏掉这些，闸门就会误判为"可以用"，
    /// 结果系统去卸载卷时卡在等这些文件释放（用户看到的就是弹出卡死）。
    /// </summary>
    /// <remarks>
    /// 刻意**不包含** .iso / .bin 这类通用扩展名：
    /// 实测 B: 盘的游戏 .bin、系统镜像 .iso 会被误判成"虚拟机文件"，
    /// 导致正常数据盘也被拒绝脱机。这里只保留虚拟机专属的类型。
    /// </remarks>
    private static readonly string[] MachineFileExtensions =
        [".vhd", ".vhdx", ".avhdx", ".vmcx", ".vmrs", ".vsv", ".vhdpmem", ".vfd"];

    /// <summary>枚举所有带盘符卷上的"虚拟机相关文件"（闸门用）。</summary>
    public static IEnumerable<string> EnumerateAll()
    {
        foreach (RawVolume volume in VolumeDevice.Enumerate())
        {
            foreach (string mountPoint in volume.MountPoints.Where(m => m.Length >= 3 && m[1] == ':'))
            {
                foreach (string file in EnumerateUnder(mountPoint, MachineFileExtensions))
                {
                    yield return file;
                }
            }
        }
    }

    /// <summary>枚举所有带盘符卷上的虚拟磁盘本体（分离 VHD 用）。</summary>
    public static IEnumerable<string> EnumerateVirtualDisks()
    {
        foreach (RawVolume volume in VolumeDevice.Enumerate())
        {
            foreach (string mountPoint in volume.MountPoints.Where(m => m.Length >= 3 && m[1] == ':'))
            {
                foreach (string file in EnumerateUnder(mountPoint, VirtualDiskExtensions))
                {
                    yield return file;
                }
            }
        }
    }

    /// <summary>枚举指定磁盘各卷上的虚拟机相关文件。</summary>
    public static IEnumerable<string> EnumerateOnDisk(int diskNumber)
    {
        foreach (RawVolume volume in VolumeDevice.Enumerate())
        {
            if (!volume.Extents.Any(e => (int)e.DiskNumber == diskNumber))
            {
                continue;
            }

            foreach (string mountPoint in volume.MountPoints.Where(m => m.Length >= 3 && m[1] == ':'))
            {
                foreach (string file in EnumerateUnder(mountPoint, MachineFileExtensions))
                {
                    yield return file;
                }
            }
        }
    }

    /// <summary>广度优先枚举某个根路径下、指定扩展名的文件。</summary>
    public static IEnumerable<string> EnumerateUnder(string root, string[]? extensions = null)
    {
        string[] wanted = extensions ?? MachineFileExtensions;
        Queue<(string Path, int Depth)> pending = [];
        pending.Enqueue((root, 0));
        int visited = 0;

        while (pending.Count > 0 && visited < MaxDirectoriesPerVolume)
        {
            (string current, int depth) = pending.Dequeue();
            visited++;

            string[] files;
            try
            {
                files = Directory.GetFiles(current);
            }
            catch
            {
                continue;
            }

            foreach (string file in files)
            {
                if (wanted.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                {
                    yield return file;
                }
            }

            if (depth >= MaxScanDepth)
            {
                continue;
            }

            string[] directories;
            try
            {
                directories = Directory.GetDirectories(current);
            }
            catch
            {
                continue;
            }

            foreach (string directory in directories)
            {
                try
                {
                    if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
                    {
                        continue;
                    }
                }
                catch
                {
                    continue;
                }

                pending.Enqueue((directory, depth + 1));
            }
        }
    }

    /// <summary>
    /// 文件是否正被其他程序占用。
    /// 判定方法：以独占方式尝试打开——能独占打开说明没人用，打不开说明 VHD 驱动或别的程序正拿着它。
    /// </summary>
    public static bool IsFileInUse(string path)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true; // 共享冲突
        }
        catch (UnauthorizedAccessException)
        {
            return true; // 权限不足时保守处理
        }
        catch
        {
            return true;
        }
    }
}
