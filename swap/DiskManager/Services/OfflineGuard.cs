using System.IO;
using System.Management;
using System.Text;
using DiskManager.Interop;
using DiskManager.Models;

namespace DiskManager.Services;

/// <summary>
/// 脱机安全闸门：判断某块磁盘"能不能安全脱机"。
///
/// 背景（实测踩出来的）：
///   内核不会阻止你把系统正在使用的磁盘脱机。把宿主盘抽掉的那一刻，
///   系统会失去自己的文件系统，直接 0x22 FILE_SYSTEM 蓝屏。
///   系统磁盘管理之所以看起来安全，是它把按钮灰掉了——软件层的保护，不是驱动层的。
///
/// 因此这里独立做一层判断，脱机前必须通过：
///   ① 含系统分区（EFI 系统分区 / MBR 活动分区）
///   ② 含启动卷（Windows 目录所在卷）
///   ③ 承载本程序或系统目录所在卷
///   ④ 上有正在被使用的虚拟磁盘文件（.vhd/.vhdx）——本机系统就是从 A:\ 上的 VHDX 启动的
///   ⑤ 上有分页文件（pagefile.sys）
/// 返回空字符串表示允许脱机，否则返回拒绝原因（不含磁盘号，由调用方拼接）。
/// </summary>
internal static class OfflineGuard
{
    /// <summary>不允许移除/脱机的原因类别。</summary>
    internal enum BlockKind
    {
        None,
        SystemPartition,
        BootVolume,
        ProtectedVolume,
        InUseVirtualDisk,
        PageFile,
    }

    /// <summary>结构化的拦截结果：界面可以据此给出"先分离 VHD 再继续"之类的出路。</summary>
    internal sealed class Block
    {
        public static readonly Block None = new(BlockKind.None, string.Empty);

        private Block(BlockKind kind, string reason)
        {
            Kind = kind;
            Reason = reason;
        }

        public BlockKind Kind { get; }

        /// <summary>原因描述（不含磁盘号，由调用方拼接）。</summary>
        public string Reason { get; }

        /// <summary>补充信息，例如挡路的虚拟磁盘文件路径。</summary>
        public string Detail { get; init; } = string.Empty;

        /// <summary>当原因是在用虚拟磁盘时：该文件对应的磁盘号（-1 表示未知）。</summary>
        public int VirtualDiskNumber { get; init; } = -1;

        public bool IsBlocked => Kind != BlockKind.None;

        public static Block Create(BlockKind kind, string reason, string detail = "", int virtualDiskNumber = -1)
            => new(kind, reason) { Detail = detail, VirtualDiskNumber = virtualDiskNumber };
    }

    /// <summary>判断能否脱机；返回 null 表示允许，否则返回拒绝原因。</summary>
    public static string? GetBlockReason(DiskItem disk)
    {
        Block block = Evaluate(disk);
        return block.IsBlocked ? block.Reason : null;
    }

    /// <summary>结构化评估：既能拿到原因，也能知道"是在用的虚拟磁盘文件挡路"这种可解的情况。</summary>
    public static Block Evaluate(DiskItem disk)
    {
        using IDisposable scope = OperationLog.Scope($"OfflineGuard 评估磁盘 {disk.Number}");

        if (disk.IsSystemDisk)
        {
            return Block.Create(BlockKind.SystemPartition, "含系统分区（Windows 启动文件所在），脱机会导致系统无法工作");
        }

        if (disk.IsBootDisk)
        {
            return Block.Create(BlockKind.BootVolume, "含启动卷（Windows 目录所在），脱机会立即导致系统崩溃（蓝屏 0x22）");
        }

        List<RawVolume> volumesOnDisk = GetVolumesOnDisk(disk.Number);

        // ③ 本程序自身运行目录、Windows 目录所在卷
        HashSet<string> protectedVolumes = [];
        AddVolumeOfPath(protectedVolumes, Environment.SystemDirectory);
        AddVolumeOfPath(protectedVolumes, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        AddVolumeOfPath(protectedVolumes, AppContext.BaseDirectory);

        foreach (RawVolume volume in volumesOnDisk)
        {
            if (protectedVolumes.Contains(volume.VolumeName))
            {
                return Block.Create(
                    BlockKind.ProtectedVolume,
                    $"上有系统或本程序正在使用的卷（{volume.VolumeName.TrimEnd('\\')}）");
            }
        }

        // ④ 正在被使用的虚拟磁盘文件（本机系统就是从这种文件启动的）
        if (TryFindInUseVirtualDisk(volumesOnDisk, out string virtualDiskPath))
        {
            int backingDisk = VirtualDiskOperations.TryGetPhysicalDiskNumber(virtualDiskPath, out int mapped, out _) ? mapped : -1;

            return Block.Create(
                BlockKind.InUseVirtualDisk,
                $"上有正在使用的虚拟机/镜像文件（{virtualDiskPath}），" +
                "脱机或移除会让使用它的虚拟机一起失效（也可能是它占着磁盘导致无法弹出）",
                virtualDiskPath,
                backingDisk);
        }

        // ⑤ 分页文件
        if (TryFindPageFile(volumesOnDisk, out string pageFilePath))
        {
            return Block.Create(BlockKind.PageFile, $"上有分页文件（{pageFilePath}），脱机会导致系统崩溃");
        }

        return Block.None;
    }

    /// <summary>把原因拼成给用户看的完整句子。</summary>
    public static string Describe(DiskItem disk)
        => GetBlockReason(disk) is { } reason ? $"磁盘 {disk.Number} {reason}。" : string.Empty;

    /// <summary>取某块磁盘上的所有卷。</summary>
    private static List<RawVolume> GetVolumesOnDisk(int diskNumber)
    {
        List<RawVolume> volumes = [];
        foreach (RawVolume volume in VolumeDevice.Enumerate())
        {
            if (volume.Extents.Any(e => (int)e.DiskNumber == diskNumber))
            {
                volumes.Add(volume);
            }
        }

        return volumes;
    }

    /// <summary>把路径所在卷的 GUID 加入保护集合。</summary>
    private static void AddVolumeOfPath(HashSet<string> volumes, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string root = Path.GetPathRoot(path) ?? string.Empty;
        if (root.Length < 3)
        {
            return;
        }

        StringBuilder buffer = new(260);
        if (NativeMethods.GetVolumeNameForVolumeMountPointW(root, buffer, buffer.Capacity))
        {
            volumes.Add(buffer.ToString());
        }
    }

    /// <summary>查找分页文件是否落在这些卷上。</summary>
    private static bool TryFindPageFile(List<RawVolume> volumesOnDisk, out string pageFilePath)
    {
        pageFilePath = string.Empty;
        HashSet<string> names = [.. volumesOnDisk.Select(v => v.VolumeName)];

        try
        {
            using ManagementObjectSearcher searcher = new(@"root\cimv2", "SELECT Name FROM Win32_PageFileUsage");
            foreach (ManagementBaseObject item in searcher.Get())
            {
                if (item["Name"] is not string name || name.Length < 3)
                {
                    continue;
                }

                string root = Path.GetPathRoot(name) ?? string.Empty;
                if (root.Length < 3)
                {
                    continue;
                }

                StringBuilder buffer = new(260);
                if (NativeMethods.GetVolumeNameForVolumeMountPointW(root, buffer, buffer.Capacity)
                    && names.Contains(buffer.ToString()))
                {
                    pageFilePath = name;
                    return true;
                }
            }
        }
        catch
        {
            // 查询失败按"没有分页文件"处理：其他几项检查已经覆盖主要风险。
        }

        return false;
    }

    /// <summary>
    /// 查找这些卷上"正在被使用的"虚拟磁盘文件。
    /// 判定方法：以独占方式尝试打开（FileShare.None）——能被独占打开说明没有任何程序（包括 VHD 驱动）在用。
    /// </summary>
    private static bool TryFindInUseVirtualDisk(List<RawVolume> volumesOnDisk, out string virtualDiskPath)
    {
        virtualDiskPath = string.Empty;

        foreach (RawVolume volume in volumesOnDisk)
        {
            foreach (string mountPoint in volume.MountPoints.Where(m => m.Length >= 3 && m[1] == ':'))
            {
                foreach (string file in VirtualDiskFileScanner.EnumerateUnder(mountPoint))
                {
                    if (VirtualDiskFileScanner.IsFileInUse(file))
                    {
                        virtualDiskPath = file;
                        return true;
                    }
                }
            }
        }

        return false;
    }
}
