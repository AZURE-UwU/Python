using System.Runtime.InteropServices;

namespace DiskManager.Interop;

/*
 * 本文件所有结构体都按 Windows SDK 的原生布局逐个核对过。
 *
 * 三个容易写错的地方（都已在下方用显式布局处理）：
 *   1) C 语言里的 union，在 C# 顺序布局里会变成"两个字段前后排开"，
 *      必须先定义成 Explicit 布局再用 FieldOffset 让它们重叠。
 *   2) PARTITION_INFORMATION_EX 里含 LARGE_INTEGER，整体按 8 字节对齐，
 *      所以 StartingOffset 前有 4 字节填充、联合体从偏移 32 开始，整体 144 字节。
 *   3) DRIVE_LAYOUT_INFORMATION_EX 头部固定 48 字节，之后才是分区数组。
 */

/// <summary>DISK_GEOMETRY，磁盘物理几何信息。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DiskGeometry
{
    public long Cylinders;
    public int MediaType;
    public uint TracksPerCylinder;
    public uint SectorsPerTrack;
    public uint BytesPerSector;
}

/// <summary>DISK_GEOMETRY_EX，几何信息 + 真实容量（定长部分 32 字节）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DiskGeometryEx
{
    public DiskGeometry Geometry;
    public long DiskSize;
}

/// <summary>GET_LENGTH_INFORMATION。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GetLengthInformation
{
    public long Length;
}

/// <summary>SET_DISK_ATTRIBUTES，脱机 / 联机 / 只读 的输入结构（40 字节）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SetDiskAttributes
{
    public uint Version;
    public byte Persist;
    public byte Reserved1A;
    public byte Reserved1B;
    public byte Reserved1C;
    public ulong Attributes;
    public ulong AttributesMask;
    public uint Reserved2A;
    public uint Reserved2B;
    public uint Reserved2C;
    public uint Reserved2D;

    /// <summary>按原生结构大小填写 Version 字段并构造请求。</summary>
    public static SetDiskAttributes Create(DiskAttribute attributes, DiskAttribute mask, bool persist) => new()
    {
        Version = (uint)Marshal.SizeOf<SetDiskAttributes>(),
        Persist = persist ? (byte)1 : (byte)0,
        Attributes = (ulong)attributes,
        AttributesMask = (ulong)mask,
    };
}

/// <summary>
/// 旧版 32 位属性定义（Attributes 为 DWORD）。
/// 少数驱动只认旧版本，失败时作为兜底重试。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SetDiskAttributesLegacy
{
    public uint Version;
    public byte Persist;
    public byte Reserved1A;
    public byte Reserved1B;
    public byte Reserved1C;
    public uint Attributes;
    public uint AttributesMask;
    public uint Reserved2A;
    public uint Reserved2B;
    public uint Reserved2C;
    public uint Reserved2D;

    public static SetDiskAttributesLegacy Create(DiskAttribute attributes, DiskAttribute mask, bool persist) => new()
    {
        Version = (uint)Marshal.SizeOf<SetDiskAttributesLegacy>(),
        Persist = persist ? (byte)1 : (byte)0,
        Attributes = (uint)attributes,
        AttributesMask = (uint)mask,
    };
}

/// <summary>CREATE_DISK_MBR。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CreateDiskMbr
{
    public uint Signature;
}

/// <summary>CREATE_DISK_GPT。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CreateDiskGpt
{
    public Guid DiskId;
    public uint MaxPartitionCount;
}

/// <summary>CREATE_DISK，初始化磁盘时写入（原生 24 字节）。</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct CreateDisk
{
    [FieldOffset(0)] public uint PartitionStyle;
    [FieldOffset(4)] public CreateDiskMbr Mbr;
    [FieldOffset(4)] public CreateDiskGpt Gpt;
}

/// <summary>PARTITION_INFORMATION_MBR（24 字节）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PartitionInformationMbr
{
    public byte PartitionType;
    public byte BootIndicator;
    public byte RecognizedPartition;
    public uint HiddenSectors;
    public Guid PartitionId;
}

/// <summary>PARTITION_INFORMATION_GPT（112 字节，Name 为 36 个宽字符）。</summary>
[StructLayout(LayoutKind.Sequential, Size = 112)]
internal unsafe struct PartitionInformationGpt
{
    public Guid PartitionType;
    public Guid PartitionId;
    public ulong Attributes;
    public fixed char Name[36];

    /// <summary>读取 GPT 分区名（到第一个空字符为止）。</summary>
    public readonly string GetName()
    {
        fixed (char* p = Name)
        {
            return Marshal.PtrToStringUni((IntPtr)p, 36)?.TrimEnd('\0') ?? string.Empty;
        }
    }
}

/// <summary>PARTITION_INFORMATION_EX，单个分区描述（原生 144 字节）。</summary>
[StructLayout(LayoutKind.Explicit, Size = 144)]
internal unsafe struct PartitionInformationEx
{
    [FieldOffset(0)] public uint PartitionStyle;
    [FieldOffset(8)] public long StartingOffset;
    [FieldOffset(16)] public long PartitionLength;
    [FieldOffset(24)] public uint PartitionNumber;
    [FieldOffset(28)] public byte RewritePartition;
    [FieldOffset(29)] public byte IsServicePartition;
    [FieldOffset(32)] public PartitionInformationMbr Mbr;
    [FieldOffset(32)] public PartitionInformationGpt Gpt;
}

/// <summary>DRIVE_LAYOUT_INFORMATION_MBR（8 字节）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DriveLayoutInformationMbr
{
    public uint Signature;
    public uint CheckSum;
}

/// <summary>DRIVE_LAYOUT_INFORMATION_GPT（40 字节）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DriveLayoutInformationGpt
{
    public Guid DiskId;
    public long StartingUsableOffset;
    public long UsableLength;
    public uint MaxPartitionCount;
}

/// <summary>DRIVE_LAYOUT_INFORMATION_EX 定长头部（48 字节），分区数组紧随其后。</summary>
[StructLayout(LayoutKind.Explicit, Size = 48)]
internal struct DriveLayoutHeader
{
    [FieldOffset(0)] public uint PartitionStyle;
    [FieldOffset(4)] public uint PartitionCount;
    [FieldOffset(8)] public DriveLayoutInformationMbr Mbr;
    [FieldOffset(8)] public DriveLayoutInformationGpt Gpt;
}

/// <summary>STORAGE_DEVICE_NUMBER，由设备句柄反查磁盘编号。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct StorageDeviceNumber
{
    public uint DeviceType;
    public uint DeviceNumber;
    public uint PartitionNumber;
}

/// <summary>STORAGE_PROPERTY_QUERY，查询属性时的输入。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct StoragePropertyQuery
{
    public uint PropertyId;
    public uint QueryType;
    public byte AdditionalParameters;
}

/// <summary>STORAGE_DEVICE_DESCRIPTOR 定长头部，后接各字符串区（偏移量按字节计）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct StorageDeviceDescriptor
{
    public uint Version;
    public uint Size;
    public byte DeviceType;
    public byte DeviceTypeModifier;
    public byte RemovableMedia;
    public byte CommandQueueing;
    public uint VendorIdOffset;
    public uint ProductIdOffset;
    public uint ProductRevisionOffset;
    public uint SerialNumberOffset;
    public int BusType;
    public uint RawPropertiesLength;
}

/// <summary>DISK_EXTENT，卷在某一磁盘上的连续区间（24 字节）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DiskExtent
{
    public uint DiskNumber;
    public long StartingOffset;
    public long ExtentLength;
}

/// <summary>NTFS_VOLUME_DATA_BUFFER，用于"属性"对话框读取 NTFS 细节。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NtfsVolumeData
{
    public long VolumeSerialNumber;
    public long NumberSectors;
    public long TotalClusters;
    public long FreeClusters;
    public long TotalReserved;
    public uint BytesPerSector;
    public uint BytesPerCluster;
    public uint BytesPerFileRecordSegment;
    public uint ClustersPerFileRecordSegment;
    public long MftValidDataLength;
    public long MftStartLcn;
    public long Mft2StartLcn;
    public long MftZoneStart;
    public long MftZoneEnd;
}
