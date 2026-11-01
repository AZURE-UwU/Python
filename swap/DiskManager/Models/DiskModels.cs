using System.ComponentModel;
using System.Runtime.CompilerServices;
using DiskManager.Interop;

namespace DiskManager.Models;

/// <summary>卷在界面上的状态。</summary>
public enum VolumeState
{
    Healthy,
    NoMedia,
    Offline,
    Failed,
    Unknown,
    Unformatted,
}

/// <summary>磁盘在界面上的状态。</summary>
public enum DiskState
{
    Online,
    Offline,
    NoMedia,
    NotInitialized,
    ReadOnly,
}

/// <summary>可通知属性变更的基类。</summary>
public abstract class NotifyObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(propertyName);
        return true;
    }
}

/// <summary>磁盘图上的一个分区（含未分配空间之外的所有已分配区域）。</summary>
public sealed class PartitionItem : NotifyObject
{
    public int Number { get; init; }
    public long Offset { get; init; }
    public long Length { get; init; }
    public PartitionStyle Style { get; init; }
    public Guid GptType { get; init; }
    public byte MbrType { get; init; }
    public bool IsBootIndicator { get; init; }
    public ulong GptAttributes { get; init; }
    public string GptName { get; init; } = string.Empty;

    /// <summary>分区所属磁盘号。</summary>
    public int DiskNumber { get; init; }

    /// <summary>分区类型的中文名称。</summary>
    public string TypeName { get; init; } = string.Empty;

    /// <summary>该分区对应的卷（无文件系统或无对应卷时为 null）。</summary>
    public VolumeItem? Volume { get; set; }

    /// <summary>是否未分配区域之外的"未使用"分区。</summary>
    public bool IsEfiSystem => GptType == GptTypeGuids.EfiSystem;

    public bool IsMicrosoftReserved => GptType == GptTypeGuids.MicrosoftReserved;

    public bool IsRecovery => GptType == GptTypeGuids.Recovery;

    private bool _isSelected;

    /// <summary>是否在磁盘图中被选中。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    /// <summary>卷在磁盘图中显示的主文本（盘符或卷标）。</summary>
    public string MapTitle => Volume?.MapTitle ?? TypeName;

    /// <summary>卷在磁盘图中显示的容量文本。</summary>
    public string MapSize => Volume?.CapacityText ?? SizeFormatter.Format(Length);
}

/// <summary>一块磁盘。</summary>
public sealed class DiskItem : NotifyObject
{
    public int Number { get; init; }
    public string FriendlyName { get; init; } = string.Empty;
    public string BusTypeName { get; init; } = string.Empty;
    public string Serial { get; init; } = string.Empty;
    public bool IsRemovable { get; init; }
    public bool IsSystemDisk { get; init; }
    public bool IsBootDisk { get; init; }
    public long Size { get; init; }
    public uint BytesPerSector { get; init; } = 512;
    public PartitionStyle Style { get; init; }
    public Guid GptDiskId { get; init; }
    public uint MbrSignature { get; init; }
    public DiskAttribute Attributes { get; init; }

    public List<PartitionItem> Partitions { get; } = [];

    /// <summary>
    /// 不允许脱机的原因（空字符串表示允许脱机）。
    /// 由枚举阶段算好，界面据此把"脱机"置灰并给出解释，避免用户点了才被拒。
    /// </summary>
    public string OfflineBlockReason { get; set; } = string.Empty;

    /// <summary>是否支持"弹出"（可安全移除的设备）。由枚举阶段根据设备能力位算出。</summary>
    public bool SupportsEject { get; set; }

    /// <summary>设备能力位原始值（CM_DEVCAP_*），用于诊断显示。</summary>
    public uint Capabilities { get; set; }

    /// <summary>总线类型原始值（STORAGE_BUS_TYPE），0x0E=虚拟、0x0F=文件支持的虚拟磁盘。</summary>
    public int BusType { get; set; }

    /// <summary>是否为挂载中的虚拟磁盘（VHD / VHDX）。</summary>
    public bool IsVirtualDisk { get; set; }

    /// <summary>不允许弹出的原因（空字符串表示可以弹出）。</summary>
    public string EjectBlockReason { get; set; } = string.Empty;

    /// <summary>磁盘号文本："磁盘 0"。</summary>
    public string NumberText => $"磁盘 {Number}";

    /// <summary>分区表类型文本。</summary>
    public string StyleText => Style switch
    {
        PartitionStyle.Gpt => "GPT",
        PartitionStyle.Mbr => "MBR",
        _ => string.Empty,
    };

    public bool IsOffline => Attributes.HasFlag(DiskAttribute.Offline);

    public bool IsReadOnly => Attributes.HasFlag(DiskAttribute.ReadOnly);

    public bool IsInitialized => Style is PartitionStyle.Mbr or PartitionStyle.Gpt;

    /// <summary>
    /// 状态文本：脱机 / 无媒体 / 未初始化 / 联机。
    /// 顺序有讲究：先看脱机、再看有没有介质，最后才看是否初始化——
    /// 否则"未初始化但已脱机"的磁盘会显示成"未初始化"，把脱机状态藏起来。
    /// </summary>
    public string StatusText => IsOffline
        ? "脱机"
        : Size <= 0
            ? "无媒体"
            : !IsInitialized && Style == PartitionStyle.Raw
                ? "未初始化"
                : "联机";

    public string CapacityText => SizeFormatter.Format(Size);

    /// <summary>磁盘图标题栏第一行："磁盘 0" + "基本"。</summary>
    public string MapHeaderTitle => $"{NumberText} 基本";

    /// <summary>磁盘图标题栏第二行：容量 + 状态 + 分区表类型。</summary>
    public string MapHeaderDetail
    {
        get
        {
            List<string> parts = [CapacityText];
            if (IsReadOnly)
            {
                parts.Add("只读");
            }

            parts.Add(StatusText);
            if (IsInitialized)
            {
                parts.Add(StyleText);
            }

            return string.Join("  ·  ", parts);
        }
    }

    /// <summary>
    /// 磁盘是否可执行"脱机"操作。
    /// 已脱机的磁盘只能联机；系统盘、承载在用的虚拟磁盘文件、承载分页文件的磁盘一律不允许脱机。
    /// </summary>
    public bool CanOffline => !IsOffline && Size > 0 && OfflineBlockReason.Length == 0;

    /// <summary>
    /// 是否可以执行"移除"。
    /// 虚拟磁盘走"分离 VHD"，普通设备走"弹出"，两者的限制都记录在 EjectBlockReason 里。
    /// </summary>
    public bool CanEject => EjectBlockReason.Length == 0;

    /// <summary>移除动作的名称：虚拟磁盘是"分离 VHD"，其他设备是"弹出"。</summary>
    public string EjectActionName => IsVirtualDisk ? "分离 VHD" : "弹出";

    /// <summary>移除菜单项文本。</summary>
    public string EjectMenuText => IsVirtualDisk ? "分离 VHD(_E)…" : "弹出(_E)…";

    public string OfflineMenuText => IsOffline ? "联机(_O)" : "脱机(_O)";
}

/// <summary>界面上的一行"卷"。</summary>
public sealed class VolumeItem : NotifyObject
{
    /// <summary>卷 GUID 路径，作为唯一标识。</summary>
    public string VolumeName { get; init; } = string.Empty;

    public List<string> MountPoints { get; init; } = [];

    /// <summary>盘符文本，如 "C:"；无盘符时为空。</summary>
    public string LetterText { get; init; } = string.Empty;

    public string Label { get; init; } = string.Empty;
    public string FileSystem { get; init; } = string.Empty;
    public long Size { get; init; }
    public long FreeSpace { get; init; }
    public bool IsAccessible { get; init; }
    public VolumeState State { get; init; }

    /// <summary>卷的分区类型中文名（简单 / 跨区 / 系统 / 启动 / EFI 系统分区 ...）。</summary>
    public string TypeName { get; init; } = string.Empty;

    /// <summary>卷的组成方式：简单 / 跨区。</summary>
    public string LayoutText { get; init; } = "简单";

    /// <summary>容错：是 / 否。</summary>
    public string FaultToleranceText { get; init; } = "否";

    /// <summary>开销：例如 0%。</summary>
    public string OverheadText { get; init; } = "0%";

    /// <summary>卷涉及的磁盘号。</summary>
    public List<int> DiskNumbers { get; init; } = [];

    /// <summary>卷对应的分区（用于与磁盘图联动）。</summary>
    public List<PartitionItem> Partitions { get; init; } = [];

    /// <summary>排序用的起始偏移（越小越靠前）。</summary>
    public long OffsetForSort { get; init; }

    public bool IsSystemVolume { get; init; }
    public bool IsBootVolume { get; init; }

    /// <summary>第一列"卷"：卷标 + 盘符，与磁盘管理一致。</summary>
    public string DisplayName
    {
        get
        {
            string label = string.IsNullOrWhiteSpace(Label) ? string.Empty : Label;
            string letter = LetterText;

            if (label.Length > 0 && letter.Length > 0)
            {
                return $"{label} ({letter})";
            }

            if (letter.Length > 0)
            {
                return letter;
            }

            if (label.Length > 0)
            {
                return label;
            }

            return "（无卷标）";
        }
    }

    /// <summary>磁盘图里的主文本：优先盘符，其次卷标。</summary>
    public string MapTitle => LetterText.Length > 0 ? LetterText : DisplayName;

    public string CapacityText => SizeFormatter.Format(Size);

    public string FreeSpaceText => IsAccessible ? SizeFormatter.Format(FreeSpace) : string.Empty;

    /// <summary>可用百分比，与磁盘管理一样取整显示。</summary>
    public string FreePercentText
    {
        get
        {
            if (!IsAccessible || Size <= 0)
            {
                return string.Empty;
            }

            return $"{Math.Round(FreeSpace * 100.0 / Size)}%";
        }
    }

    /// <summary>状态列文本。</summary>
    public string StatusText => State switch
    {
        VolumeState.Healthy => "状态良好",
        VolumeState.NoMedia => "无媒体",
        VolumeState.Offline => "脱机",
        VolumeState.Failed => "失败",
        VolumeState.Unformatted => "未格式化",
        _ => "未知",
    };

    /// <summary>文件系统列文本，RAW/未格式化卷显示为空，与磁盘管理一致。</summary>
    public string FileSystemText => State == VolumeState.Unformatted ? string.Empty : FileSystem;

    /// <summary>"磁盘"列文本：磁盘 0 或 磁盘 0, 磁盘 1。</summary>
    public string DiskText => DiskNumbers.Count == 0
        ? string.Empty
        : string.Join(", ", DiskNumbers.Select(n => $"磁盘 {n}"));
}

/// <summary>系统内置分区类型 GUID 常量。</summary>
internal static class GptTypeGuids
{
    public static readonly Guid EfiSystem = new("C12A7328-F81F-11D2-BA4B-00A0C93EC93B");
    public static readonly Guid MicrosoftReserved = new("E3C9E316-0B5C-4DB8-817D-F92DF00215AE");
    public static readonly Guid BasicData = new("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7");
    public static readonly Guid Recovery = new("DE94BBA4-06D1-4D40-A16A-BFD50179D6AC");
    public static readonly Guid LdmMetadata = new("5808C8AA-7E8F-42E0-85D2-E1E90434CFB3");
    public static readonly Guid LdmData = new("AF9B60A0-1431-4F62-BC68-3311714A69AD");
    public static readonly Guid StorageSpaces = new("E75CAF8F-F680-4CEE-AFA3-B001E56EFC2D");
    public static readonly Guid MicrosoftBasicData = new("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7");
    public static readonly Guid WindowsRecovery = new("DE94BBA4-06D1-4D40-A16A-BFD50179D6AC");
}
