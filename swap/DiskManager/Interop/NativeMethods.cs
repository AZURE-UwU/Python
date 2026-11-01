using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DiskManager.Interop;

/// <summary>
/// 所有 P/Invoke 声明集中在本文件，便于核对签名。
/// 统一使用 Unicode 版本接口（CreateFileW / FindFirstVolumeW ...）。
/// </summary>
internal static class NativeMethods
{
    // ------------------------------------------------------------------
    // kernel32.dll —— 设备句柄与 IOCTL
    // ------------------------------------------------------------------

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        int nInBufferSize,
        IntPtr lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReadFile(
        SafeFileHandle hFile,
        byte[] lpBuffer,
        int nNumberOfBytesToRead,
        out int lpNumberOfBytesRead,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetFilePointerEx(
        SafeFileHandle hFile,
        long liDistanceToMove,
        IntPtr lpNewFilePointer,
        uint dwMoveMethod);

    public const uint FileBegin = 0;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindFirstVolumeW(StringBuilder lpszVolumeName, int cchBufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FindNextVolumeW(IntPtr hFindVolume, StringBuilder lpszVolumeName, int cchBufferLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FindVolumeClose(IntPtr hFindVolume);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetVolumePathNamesForVolumeNameW(
        string lpszVolumeName,
        [Out] char[] lpszVolumePathNames,
        int cchBufferLength,
        out int lpcchReturnLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetVolumeNameForVolumeMountPointW(
        string lpszVolumeMountPoint,
        StringBuilder lpszVolumeName,
        int cchBufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetVolumeInformationW(
        string lpRootPathName,
        StringBuilder lpVolumeNameBuffer,
        int nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        StringBuilder lpFileSystemNameBuffer,
        int nFileSystemNameSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetDiskFreeSpaceExW(
        string lpDirectoryName,
        out ulong lpFreeBytesAvailableToCaller,
        out ulong lpTotalNumberOfBytes,
        out ulong lpTotalNumberOfFreeBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint GetDriveTypeW(string lpRootPathName);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint GetLogicalDrives();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint GetWindowsDirectoryW(StringBuilder lpBuffer, uint uSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetVolumeMountPointW(string lpszVolumeMountPoint, string lpszVolumeName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteVolumeMountPointW(string lpszVolumeMountPoint);

    /// <summary>驱动器类型：不存在（空盘符）。</summary>
    public const uint DriveNoRootDir = 1;

    // ------------------------------------------------------------------
    // setupapi.dll —— 通过设备接口枚举物理磁盘，拿"设备友好名"
    // ------------------------------------------------------------------

    public const uint DigcfPresent = 0x00000002;
    public const uint DigcfDeviceInterface = 0x00000010;
    public const uint SpdrpFriendlyName = 0x0000000C;
    public const uint SpdrpDeviceDesc = 0x00000000;
    public const uint SpdrpCapabilities = 0x0000000F;
    public const uint SpdrpRemovalPolicy = 0x0000001F;

    /// <summary>设备能力位：支持弹出（可安全移除）。</summary>
    public const uint CmDevCapEjectSupported = 0x00000002;

    /// <summary>设备能力位：可移动介质。</summary>
    public const uint CmDevCapRemovable = 0x00000004;

    /// <summary>设备能力位：允许随时移除（热插拔设备，例如 USB 硬盘盒 / USB SSD）。</summary>
    public const uint CmDevCapSurpriseRemovalOk = 0x00000080;

    [StructLayout(LayoutKind.Sequential)]
    public struct SpDeviceInterfaceData
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    /// <summary>设备信息结构，用于取设备节点句柄 DevInst（弹出设备时需要）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SpDevInfoData
    {
        public int cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SetupDiGetClassDevsW(
        ref Guid classGuid,
        string? enumerator,
        IntPtr hwndParent,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiGetDeviceInterfaceDetailW(
        IntPtr deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData,
        IntPtr deviceInterfaceDetailData,
        int deviceInterfaceDetailDataSize,
        out int requiredSize,
        ref SpDevInfoData deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiGetDeviceRegistryPropertyW(
        IntPtr deviceInfoSet,
        ref SpDevInfoData deviceInfoData,
        uint property,
        out uint propertyRegDataType,
        byte[]? propertyBuffer,
        int propertyBufferSize,
        out int requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    /// <summary>磁盘设备接口类 GUID：{53F56307-B6BF-11D0-94F2-00A0C91EFB8B}</summary>
    public static Guid GuidDevInterfaceDisk => new("53F56307-B6BF-11D0-94F2-00A0C91EFB8B");

    // ------------------------------------------------------------------
    // cfgmgr32.dll —— 弹出设备（等价于任务栏"安全删除硬件"）
    // ------------------------------------------------------------------

    /// <summary>设备被弹出的否决类型（PNP_VETO_TYPE）。</summary>
    public enum PnpVetoType
    {
        Unknown = 0,
        LegacyDevice = 1,
        PendingClose = 2,
        WindowsApp = 3,
        WindowsService = 4,
        OutstandingOpen = 5,
        Device = 6,
        Driver = 7,
        IllegalDeviceRequest = 8,
        InsufficientPower = 9,
        NonDisableable = 10,
        LegacyDriver = 11,
        InsufficientRights = 12,
    }

    /// <summary>
    /// 请求弹出设备：系统会先卸载卷、刷新缓存，再移除设备。
    /// 被否决时返回 CR_REMOVE_VETOED 并通过 pszVetoName 说明是谁占用了设备。
    /// </summary>
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern uint CM_Request_Device_EjectW(
        uint dnDevInst,
        out PnpVetoType pVetoType,
        StringBuilder pszVetoName,
        uint ulNameLength,
        uint ulFlags);

    /// <summary>CONFIGRET：成功。</summary>
    public const uint CrSuccess = 0x00000000;

    /// <summary>CONFIGRET：移除请求被否决。</summary>
    public const uint CrRemoveVetoed = 0x00000017;

    /// <summary>CONFIGRET：设备不可禁用（内部固定设备常见的返回码）。</summary>
    public const uint CrNotDisableable = 0x00000028;

    /// <summary>CONFIGRET：权限不足。</summary>
    public const uint CrAccessDenied = 0x00000033;

    /// <summary>设备注册表属性：能力位（CM_DRP_CAPABILITIES）。</summary>
    /// <remarks>
    /// 注意：CM_DRP_* 与 SPDRP_* 的值并不相同，前者整体比后者大 1
    /// （CM_DRP_DEVICEDESC=1 而 SPDRP_DEVICEDESC=0）。
    /// CM_DRP_CAPABILITIES = 0x10，写成 0x0F 会读到"物理设备对象名"（字符串），
    /// 表现就是能力位永远是 0、弹出按钮一直灰着。
    /// </remarks>
    public const uint CmDrpCapabilities = 0x00000010;

    /// <summary>
    /// 读取设备节点的注册表属性（读能力位用这个，比 SetupDi 那条路更权威）。
    /// </summary>
    /// <remarks>
    /// 注意 pulLength 是 **in/out** 参数：传入前必须先写入缓冲区字节数，
    /// 否则系统看到的是未初始化的长度，会随机返回 CR_BUFFER_SMALL（0x1A），
    /// 表现就是"同一个调用有时成功、有时读回 0"。
    /// </remarks>
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern uint CM_Get_DevNode_Registry_PropertyW(
        uint dnDevInst,
        uint ulProperty,
        out uint pulRegDataType,
        byte[] buffer,
        ref uint pulLength,
        uint ulFlags);

    /// <summary>取设备实例 ID（调试用）。</summary>
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern uint CM_Get_Device_IDW(
        uint dnDevInst,
        StringBuilder buffer,
        uint bufferLength,
        uint ulFlags);

    /// <summary>
    /// 取父设备节点。
    /// 用途：USB 硬盘盒 / U 盘这类设备，"可弹出"能力在**父设备**（USB 设备）上，
    /// 磁盘节点自己并不具备弹出能力；直接弹磁盘节点会被判为"非法设备请求"。
    /// </summary>
    [DllImport("cfgmgr32.dll")]
    public static extern uint CM_Get_Parent(
        out uint pdnDevInst,
        uint dnDevInst,
        uint ulFlags);

    // ------------------------------------------------------------------
    // virtdisk.dll —— 分离 VHD / VHDX（虚拟磁盘不能用 PnP 弹出，只能分离）
    // ------------------------------------------------------------------

    /// <summary>虚拟存储类型标识（VIRTUAL_STORAGE_TYPE）。</summary>
    [StructLayout(LayoutKind.Explicit, Size = 20)]
    public struct VirtualStorageType
    {
        [FieldOffset(0)] public uint DeviceId;
        [FieldOffset(4)] public Guid VendorId;
    }

    /// <summary>打开虚拟磁盘的参数（只用到版本 1 的读写深度，结构体留足空间）。</summary>
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    public struct OpenVirtualDiskParameters
    {
        [FieldOffset(0)] public uint Version;
        [FieldOffset(8)] public uint RWDepth;
    }

    public const uint VirtualStorageTypeDeviceVhd = 2;
    public const uint VirtualStorageTypeDeviceVhdx = 3;
    public const uint OpenVirtualDiskVersion1 = 1;
    public const uint OpenVirtualDiskFlagNone = 0;
    public const uint VirtualDiskAccessNone = 0x00000000;
    public const uint VirtualDiskAccessDetach = 0x00040000;
    public const uint VirtualDiskAccessGetInfo = 0x00080000;
    public const uint VirtualDiskRwDepthDefault = 1;
    public const uint DetachVirtualDiskFlagNone = 0;

    /// <summary>VHD / VHDX 的供应商 GUID：{EC984AEC-A0F9-47E9-901F-71415A66345B}</summary>
    public static Guid VirtualStorageVendorId => new("EC984AEC-A0F9-47E9-901F-71415A66345B");

    /// <remarks>注意导出名不带 W/A 后缀：这个 API 只有 Unicode 版本。</remarks>
    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode, EntryPoint = "OpenVirtualDisk")]
    public static extern uint OpenVirtualDisk(
        ref VirtualStorageType virtualStorageType,
        string path,
        uint virtualDiskAccessMask,
        uint flags,
        ref OpenVirtualDiskParameters parameters,
        out SafeFileHandle handle);

    /// <summary>
    /// 取虚拟磁盘对应的物理路径（形如 \\.\PhysicalDrive4）。
    /// 注意 diskPathSizeInBytes 是 in/out：传入前必须填缓冲区字节数。
    /// </summary>
    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    public static extern uint GetVirtualDiskPhysicalPath(
        SafeFileHandle virtualDiskHandle,
        ref uint diskPathSizeInBytes,
        StringBuilder diskPath);

    /// <summary>分离虚拟磁盘（等价于磁盘管理里的「分离 VHD」）。</summary>
    [DllImport("virtdisk.dll")]
    public static extern uint DetachVirtualDisk(
        SafeFileHandle virtualDiskHandle,
        uint flags,
        uint providerSpecificFlags);
}
