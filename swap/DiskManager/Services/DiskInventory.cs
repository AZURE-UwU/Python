using System.Text;
using DiskManager.Interop;
using DiskManager.Models;

namespace DiskManager.Services;

/// <summary>一次枚举得到的完整快照，界面一次性替换即可。</summary>
internal sealed class InventorySnapshot
{
    public List<DiskItem> Disks { get; } = [];
    public List<VolumeItem> Volumes { get; } = [];

    /// <summary>枚举过程中遇到的非致命问题（例如某块磁盘被占用读不到）。</summary>
    public List<string> Warnings { get; } = [];
}

/// <summary>
/// 磁盘/卷枚举服务：把底层 IOCTL 的原始数据组装成界面模型。
/// 全程只读，不做任何写操作；单块磁盘失败只记录警告，不阻断整次刷新。
/// </summary>
internal static class DiskInventory
{
    /// <summary>枚举的最大磁盘编号，超过范围按不存在处理。</summary>
    private const int MaxDiskNumber = 63;

    /// <summary>连续多少次打开失败就认为后面没有磁盘了。</summary>
    private const int ConsecutiveOpenFailures = 4;

    public static InventorySnapshot Capture()
    {
        using IDisposable scope = OperationLog.Scope("DiskInventory.Capture");

        InventorySnapshot snapshot = new();
        Dictionary<int, DiskDeviceEntry> devices = DeviceEnumerator.Enumerate();
        List<RawVolume> rawVolumes = VolumeDevice.Enumerate();

        // ---------- 第一步：逐块磁盘读取容量、属性、分区表 ----------
        List<RawDisk> rawDisks = ReadDisks(devices, snapshot);

        // ---------- 第二步：识别系统卷与启动卷 ----------
        string bootVolumeName = GetWindowsVolumeName();
        string systemVolumeName = ResolveSystemVolumeName(rawDisks, rawVolumes, bootVolumeName);

        // ---------- 第三步：建立 (磁盘号, 起始偏移) → 分区 的索引 ----------
        Dictionary<(int Disk, long Offset), RawPartition> partitionIndex = [];
        foreach (RawDisk disk in rawDisks)
        {
            foreach (RawPartition partition in disk.Layout.Partitions)
            {
                partitionIndex[(disk.Number, partition.Offset)] = partition;
            }
        }

        // ---------- 第四步：由卷的磁盘区间反查分区，组装卷行 ----------
        Dictionary<string, VolumeItem> volumeByPath = [];
        Dictionary<(int Disk, long Offset), VolumeItem> volumeByPartition = [];
        Dictionary<int, DiskAttribute> diskAttributes = rawDisks.ToDictionary(d => d.Number, d => d.Attributes);

        foreach (RawVolume volume in rawVolumes)
        {
            if (volume.Extents.Count == 0)
            {
                continue;
            }

            RawPartition? anchorPartition = null;
            foreach (DiskExtent extent in volume.Extents)
            {
                if (partitionIndex.TryGetValue(((int)extent.DiskNumber, extent.StartingOffset), out RawPartition? partition))
                {
                    anchorPartition ??= partition;
                    volumeByPartition[((int)extent.DiskNumber, partition.Offset)] = null!; // 先占位，稍后回填
                }
            }

            VolumeItem item = BuildVolumeItem(
                volume,
                anchorPartition,
                volume.VolumeName == systemVolumeName,
                volume.VolumeName == bootVolumeName,
                diskAttributes);

            volumeByPath[volume.VolumeName] = item;

            foreach (DiskExtent extent in volume.Extents)
            {
                if (partitionIndex.TryGetValue(((int)extent.DiskNumber, extent.StartingOffset), out RawPartition? partition))
                {
                    volumeByPartition[((int)extent.DiskNumber, partition.Offset)] = item;
                }
            }
        }

        // ---------- 第五步：组装磁盘与分区，完成卷↔分区双向关联 ----------
        foreach (RawDisk disk in rawDisks)
        {
            bool isSystemDisk = false;
            bool isBootDisk = false;
            List<PartitionItem> partitionItems = [];

            foreach (RawPartition partition in disk.Layout.Partitions)
            {
                volumeByPartition.TryGetValue((disk.Number, partition.Offset), out VolumeItem? volume);

                if (volume is not null)
                {
                    isSystemDisk |= volume.IsSystemVolume;
                    isBootDisk |= volume.IsBootVolume;
                }

                PartitionItem item = new()
                {
                    Number = partition.Number,
                    DiskNumber = disk.Number,
                    Offset = partition.Offset,
                    Length = partition.Length,
                    Style = partition.Style,
                    GptType = partition.GptType,
                    MbrType = partition.MbrType,
                    IsBootIndicator = partition.BootIndicator,
                    GptAttributes = partition.GptAttributes,
                    GptName = partition.GptName,
                    TypeName = volume?.TypeName ?? PartitionTypeNames.Describe(partition, false, false),
                    Volume = volume,
                };

                partitionItems.Add(item);

                if (volume is not null)
                {
                    volume.Partitions.Add(item);
                }
            }

            DiskItem diskItem = new()
            {
                Number = disk.Number,
                FriendlyName = disk.FriendlyName,
                BusTypeName = disk.BusTypeName,
                Serial = disk.Serial,
                IsRemovable = disk.IsRemovable,
                Size = disk.Size,
                BytesPerSector = disk.BytesPerSector,
                Style = disk.Layout.Style,
                GptDiskId = disk.Layout.GptDiskId,
                MbrSignature = disk.Layout.MbrSignature,
                Attributes = disk.Attributes,
                IsSystemDisk = isSystemDisk,
                IsBootDisk = isBootDisk,
            };

            diskItem.Partitions.AddRange(partitionItems);

            // 计算"能否脱机"的原因：界面据此置灰并解释，避免点下去才被拒。
            diskItem.OfflineBlockReason = OfflineGuard.GetBlockReason(diskItem) ?? string.Empty;

            // 弹出保护：设备本身要支持安全移除，且不能承载系统或启动卷。
            diskItem.SupportsEject = disk.SupportsEject;
            diskItem.Capabilities = disk.Capabilities;
            diskItem.BusType = disk.BusType;
            diskItem.IsVirtualDisk = disk.BusType is 0x0E or 0x0F; // 虚拟 / 文件支持的虚拟磁盘

            // 虚拟磁盘走"分离 VHD"（不依赖设备是否支持弹出），其余设备才需要弹出能力位。
            diskItem.EjectBlockReason = diskItem.IsSystemDisk || diskItem.IsBootDisk
                ? "该磁盘承载系统或启动卷，不能移除"
                : diskItem.IsVirtualDisk || disk.SupportsEject
                    ? string.Empty
                    : "该设备不支持弹出（内部固定磁盘，或驱动未提供安全移除）";

            snapshot.Disks.Add(diskItem);
        }

        snapshot.Volumes.AddRange(volumeByPath.Values
            .OrderBy(v => v.DiskNumbers.FirstOrDefault())
            .ThenBy(v => v.OffsetForSort));

        snapshot.Disks.Sort((a, b) => a.Number.CompareTo(b.Number));
        return snapshot;
    }

    /// <summary>逐个打开物理磁盘，读取容量、属性、分区表与设备信息。</summary>
    private static List<RawDisk> ReadDisks(Dictionary<int, DiskDeviceEntry> devices, InventorySnapshot snapshot)
    {
        List<RawDisk> rawDisks = [];
        int consecutiveFailures = 0;

        for (int number = 0; number <= MaxDiskNumber; number++)
        {
            using DiskHandle? handle = DiskHandle.TryOpen(number, writeAccess: false, out string openError);
            if (handle is null)
            {
                consecutiveFailures++;
                if (consecutiveFailures >= ConsecutiveOpenFailures)
                {
                    break;
                }

                // 一块都没读到时把原因留下，界面会提示（通常是没提权）。
                if (rawDisks.Count == 0)
                {
                    snapshot.Warnings.Add(openError);
                }

                continue;
            }

            consecutiveFailures = 0;
            RawDisk disk = new() { Number = number };

            if (handle.TryGetGeometry(out long size, out uint sector, out string geometryError))
            {
                disk.Size = size;
                disk.BytesPerSector = sector;
            }
            else
            {
                snapshot.Warnings.Add($"磁盘 {number}：{geometryError}");
            }

            if (handle.TryGetAttributes(out DiskAttribute attributes, out _))
            {
                disk.Attributes = attributes;
            }

            if (handle.TryGetLayout(out RawDiskLayout layout, out string layoutError))
            {
                disk.Layout = layout;

                // 未初始化的磁盘会被底层控制码报告成 "MBR + 无分区"，
                // 用 0 号扇区有没有 0x55AA 结束标志来区分，与磁盘管理的判定一致。
                if (layout.Style == PartitionStyle.Mbr
                    && layout.Partitions.Count == 0
                    && handle.TryReadMbrSignature(out bool hasMbrSignature, out _)
                    && !hasMbrSignature)
                {
                    layout.Style = PartitionStyle.Raw;
                }
            }
            else
            {
                snapshot.Warnings.Add($"磁盘 {number}：{layoutError}");
            }

            if (devices.TryGetValue(number, out DiskDeviceEntry? device))
            {
                disk.FriendlyName = !string.IsNullOrWhiteSpace(device.FriendlyName) ? device.FriendlyName : device.DeviceDescription;
                disk.SupportsEject = device.SupportsEject;
                disk.Capabilities = device.Capabilities;
            }

            if (handle.TryQueryDeviceInfo(out StorageDeviceInfo info, out _))
            {
                disk.BusTypeName = info.BusTypeName;
                disk.BusType = info.BusType;
                disk.Serial = info.Serial;
                disk.IsRemovable = info.Removable;

                if (string.IsNullOrWhiteSpace(disk.FriendlyName))
                {
                    disk.FriendlyName = $"{info.Vendor} {info.Product}".Trim();
                }
            }

            rawDisks.Add(disk);
        }

        return rawDisks;
    }

    /// <summary>组装一行卷信息。</summary>
    private static VolumeItem BuildVolumeItem(
        RawVolume volume,
        RawPartition? partition,
        bool isSystem,
        bool isBoot,
        Dictionary<int, DiskAttribute> diskAttributes)
    {
        List<int> diskNumbers = [.. volume.Extents.Select(e => (int)e.DiskNumber).Distinct().OrderBy(n => n)];
        bool anyDiskOffline = diskNumbers.Any(n => diskAttributes.TryGetValue(n, out DiskAttribute a) && a.HasFlag(DiskAttribute.Offline));

        string letterText = volume.DriveLetters.Length > 0
            ? string.Join(", ", volume.DriveLetters.Select(c => $"{c}:"))
            : string.Empty;

        string layout = volume.Extents.Count > 1 ? "跨区" : "简单";

        return new VolumeItem
        {
            VolumeName = volume.VolumeName,
            MountPoints = [.. volume.MountPoints],
            LetterText = letterText,
            Label = volume.Label,
            FileSystem = volume.FileSystem,
            Size = volume.Size,
            FreeSpace = volume.FreeSpace,
            IsAccessible = volume.IsAccessible,
            State = ResolveState(volume, anyDiskOffline),
            TypeName = partition is not null
                ? PartitionTypeNames.Describe(partition, isSystem, isBoot)
                : isBoot ? "启动"
                : isSystem ? "系统"
                : layout,
            LayoutText = layout,
            DiskNumbers = diskNumbers,
            IsSystemVolume = isSystem,
            IsBootVolume = isBoot,
            OffsetForSort = volume.Extents.Count > 0 ? volume.Extents[0].StartingOffset : 0,
        };
    }

    /// <summary>综合卷可读性与所在磁盘状态判定卷状态。</summary>
    private static VolumeState ResolveState(RawVolume volume, bool anyDiskOffline)
    {
        if (anyDiskOffline)
        {
            return VolumeState.Offline;
        }

        if (volume.Size <= 0 && !volume.IsAccessible)
        {
            return VolumeState.NoMedia;
        }

        if (string.IsNullOrEmpty(volume.FileSystem))
        {
            return VolumeState.Unformatted;
        }

        return volume.IsAccessible ? VolumeState.Healthy : VolumeState.Unknown;
    }

    /// <summary>取 Windows 目录所在卷的 GUID 路径，用于标记"启动"卷。</summary>
    private static string GetWindowsVolumeName()
    {
        StringBuilder buffer = new(260);
        if (NativeMethods.GetWindowsDirectoryW(buffer, (uint)buffer.Capacity) == 0)
        {
            return string.Empty;
        }

        string windowsDirectory = buffer.ToString();
        return windowsDirectory.Length < 3 ? string.Empty : GetVolumeNameForRoot(windowsDirectory[..3]);
    }

    /// <summary>把 "C:\" 这样的根路径转成卷 GUID 路径。</summary>
    private static string GetVolumeNameForRoot(string root)
    {
        StringBuilder buffer = new(260);
        return NativeMethods.GetVolumeNameForVolumeMountPointW(root, buffer, buffer.Capacity)
            ? buffer.ToString()
            : string.Empty;
    }

    /// <summary>
    /// 识别系统卷：GPT 磁盘优先看 EFI 系统分区，MBR 磁盘看活动分区；
    /// 都找不到时退化为 Windows 所在卷。
    /// </summary>
    private static string ResolveSystemVolumeName(List<RawDisk> disks, List<RawVolume> volumes, string fallback)
    {
        Dictionary<(int Disk, long Offset), RawVolume> byOffset = [];
        foreach (RawVolume volume in volumes)
        {
            foreach (DiskExtent extent in volume.Extents)
            {
                byOffset[((int)extent.DiskNumber, extent.StartingOffset)] = volume;
            }
        }

        foreach (RawDisk disk in disks)
        {
            foreach (RawPartition partition in disk.Layout.Partitions)
            {
                bool isSystemPartition = partition.Style == PartitionStyle.Gpt
                    ? partition.GptType == GptTypeGuids.EfiSystem
                    : partition.BootIndicator;

                if (isSystemPartition && byOffset.TryGetValue((disk.Number, partition.Offset), out RawVolume? volume))
                {
                    return volume.VolumeName;
                }
            }
        }

        return fallback;
    }

    /// <summary>枚举过程中的中间结构。</summary>
    private sealed class RawDisk
    {
        public int Number { get; init; }
        public string FriendlyName { get; set; } = string.Empty;
        public string BusTypeName { get; set; } = string.Empty;
        public string Serial { get; set; } = string.Empty;
        public bool IsRemovable { get; set; }
        public bool SupportsEject { get; set; }
        public uint Capabilities { get; set; }
        public int BusType { get; set; }
        public long Size { get; set; }
        public uint BytesPerSector { get; set; } = 512;
        public RawDiskLayout Layout { get; set; } = new();
        public DiskAttribute Attributes { get; set; }
    }
}
