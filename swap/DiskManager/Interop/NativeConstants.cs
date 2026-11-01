using System.ComponentModel;

namespace DiskManager.Interop;

/// <summary>
/// 磁盘 / 卷相关 IOCTL 控制码。
/// 数值全部按 CTL_CODE(DeviceType, Function, Method, Access) 现场计算，
/// 已与 Windows SDK 10.0.26100 的 winioctl.h 逐条核对，不要凭记忆硬编码。
/// </summary>
internal static class IoCtl
{
    private const uint DeviceDisk = 0x00000007;
    private const uint DeviceStorage = 0x0000002D;
    private const uint DeviceVolume = 0x00000056;
    private const uint DeviceFileSystem = 0x00000009;

    private const uint MethodBuffered = 0;
    private const uint AnyAccess = 0;
    private const uint ReadAccess = 1;
    private const uint WriteAccess = 2;

    private static uint Ctl(uint device, uint function, uint method, uint access)
        => (device << 16) | (access << 14) | (function << 2) | method;

    /// <summary>读取磁盘几何信息与总容量（DISK_GEOMETRY_EX）。</summary>
    public static readonly uint DiskGetDriveGeometryEx = Ctl(DeviceDisk, 0x0028, MethodBuffered, AnyAccess);

    /// <summary>读取分区表布局（DRIVE_LAYOUT_INFORMATION_EX）。</summary>
    public static readonly uint DiskGetDriveLayoutEx = Ctl(DeviceDisk, 0x0014, MethodBuffered, AnyAccess);

    /// <summary>写回分区表布局，用于增删分区、修改盘符。</summary>
    public static readonly uint DiskSetDriveLayoutEx = Ctl(DeviceDisk, 0x0015, MethodBuffered, ReadAccess | WriteAccess);

    /// <summary>初始化磁盘（写 MBR/GPT 签名）。</summary>
    public static readonly uint DiskCreateDisk = Ctl(DeviceDisk, 0x0016, MethodBuffered, ReadAccess | WriteAccess);

    /// <summary>读取磁盘或卷的字节长度（GET_LENGTH_INFORMATION）。</summary>
    public static readonly uint DiskGetLengthInfo = Ctl(DeviceDisk, 0x0017, MethodBuffered, ReadAccess);

    /// <summary>查询磁盘当前是否可写。</summary>
    public static readonly uint DiskIsWritable = Ctl(DeviceDisk, 0x0009, MethodBuffered, AnyAccess);

    /// <summary>读取磁盘属性（脱机 / 只读）。</summary>
    public static readonly uint DiskGetDiskAttributes = Ctl(DeviceDisk, 0x003C, MethodBuffered, AnyAccess);

    /// <summary>设置磁盘属性：脱机、联机、只读，本程序的核心接口。</summary>
    public static readonly uint DiskSetDiskAttributes = Ctl(DeviceDisk, 0x003D, MethodBuffered, ReadAccess | WriteAccess);

    /// <summary>通知系统重新枚举分区，分区表写回后必须调用。</summary>
    public static readonly uint DiskUpdateProperties = Ctl(DeviceDisk, 0x0050, MethodBuffered, AnyAccess);

    /// <summary>由设备句柄反查 PhysicalDriveN 编号。</summary>
    public static readonly uint StorageGetDeviceNumber = Ctl(DeviceStorage, 0x0420, MethodBuffered, AnyAccess);

    /// <summary>查询存储设备描述符（厂商、型号、总线类型、可移动介质）。</summary>
    public static readonly uint StorageQueryProperty = Ctl(DeviceStorage, 0x0500, MethodBuffered, AnyAccess);

    /// <summary>查询卷由哪些磁盘区间组成（VOLUME_DISK_EXTENTS）。</summary>
    public static readonly uint VolumeGetDiskExtents = Ctl(DeviceVolume, 0x0000, MethodBuffered, AnyAccess);

    /// <summary>让卷脱机（卸载文件系统），弹出设备前用来释放占用。</summary>
    public static readonly uint VolumeOffline = Ctl(DeviceVolume, 0x0003, MethodBuffered, ReadAccess | WriteAccess);

    /// <summary>让卷重新联机。</summary>
    public static readonly uint VolumeOnline = Ctl(DeviceVolume, 0x0002, MethodBuffered, ReadAccess | WriteAccess);

    /// <summary>读取 NTFS 卷详细信息，用于"属性"对话框。</summary>
    public static readonly uint FsCtlGetNtfsVolumeData = Ctl(DeviceFileSystem, 25, MethodBuffered, AnyAccess);
}

/// <summary>文件与设备打开时使用的访问标志。</summary>
internal static class Win32Access
{
    public const uint GenericRead = 0x80000000;
    public const uint GenericWrite = 0x40000000;

    public const uint FileShareRead = 0x00000001;
    public const uint FileShareWrite = 0x00000002;
    public const uint FileShareDelete = 0x00000004;

    public const uint OpenExisting = 3;

    public const uint FileAttributeNormal = 0x00000080;
}

/// <summary>分区表类型（PARTITION_STYLE）。</summary>
public enum PartitionStyle : uint
{
    Mbr = 0,
    Gpt = 1,
    Raw = 2,
}

/// <summary>磁盘属性位（DISK_ATTRIBUTE_*）。</summary>
[Flags]
public enum DiskAttribute : ulong
{
    None = 0x0000000000000000,
    Offline = 0x0000000000000001,
    ReadOnly = 0x0000000000000002,
}

/// <summary>存储查询类型（STORAGE_QUERY_TYPE）。</summary>
internal enum StorageQueryType
{
    PropertyStandardQuery = 0,
    PropertyExistsQuery = 1,
    PropertyMaskQuery = 2,
    PropertyQueryMaxDefined = 3,
}

/// <summary>存储属性编号（STORAGE_PROPERTY_ID），仅列出本程序用到的部分。</summary>
internal enum StoragePropertyId
{
    StorageDeviceProperty = 0,
    StorageAdapterProperty = 1,
}

/// <summary>统一把 Win32 错误码翻译成中文提示，避免界面弹出一串英文。</summary>
internal static class Win32Error
{
    public const int ErrorAccessDenied = 5;
    public const int ErrorNotReady = 21;
    public const int ErrorWriteProtect = 19;
    public const int ErrorInvalidParameter = 87;
    public const int ErrorInvalidFunction = 1;
    public const int ErrorNotSupported = 50;
    public const int ErrorMediaChanged = 1110;
    public const int ErrorNoMediaInDrive = 1152;
    public const int ErrorFileNotFound = 2;
    public const int ErrorMoreData = 234;
    public const int ErrorInsufficientBuffer = 122;
    public const int ErrorDiskFull = 112;
    public const int ErrorDeviceNotConnected = 1167;
    public const int ErrorDeviceNotReady = 1168;
    public const int ErrorLockViolation = 33;
    public const int ErrorSharingViolation = 32;
    public const int ErrorCrc = 23;

    /// <summary>取当前线程最后一条 Win32 错误的中文描述。</summary>
    public static string LastMessage() => Describe(System.Runtime.InteropServices.Marshal.GetLastWin32Error());

    /// <summary>把错误码翻译为中文说明；无法识别时回退系统文本。</summary>
    public static string Describe(int code) => code switch
    {
        ErrorAccessDenied => "拒绝访问（需要以管理员身份运行，或该磁盘正被系统占用）",
        ErrorNotReady => "设备未就绪",
        ErrorWriteProtect => "介质受写保护",
        ErrorInvalidParameter => "参数无效（该操作在当前磁盘上不受支持）",
        ErrorInvalidFunction => "功能不受支持",
        ErrorNotSupported => "系统不支持该操作",
        ErrorMediaChanged => "介质已更改，请刷新后重试",
        ErrorNoMediaInDrive => "驱动器中没有介质",
        ErrorFileNotFound => "找不到指定的设备",
        ErrorMoreData => "数据缓冲区不足",
        ErrorInsufficientBuffer => "系统缓冲区不足",
        ErrorDiskFull => "磁盘空间不足",
        ErrorDeviceNotConnected => "设备未连接",
        ErrorDeviceNotReady => "设备尚未就绪",
        ErrorLockViolation => "该磁盘已被锁定（卷正在使用中）",
        ErrorSharingViolation => "资源正被其他程序占用",
        ErrorCrc => "数据校验错误",
        _ => new Win32Exception(code).Message,
    };
}
