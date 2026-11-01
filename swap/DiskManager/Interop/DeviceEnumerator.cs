using System.Runtime.InteropServices;
using System.Text;

namespace DiskManager.Interop;

/// <summary>物理磁盘的设备级信息（来自 SetupDi）。</summary>
internal sealed class DiskDeviceEntry
{
    public int DiskNumber { get; set; } = -1;

    /// <summary>设备接口路径，形如 \\?\scsi#disk&ven_...#{53f56307-...}。</summary>
    public string DevicePath { get; set; } = string.Empty;

    /// <summary>设备管理器里显示的友好名，如 "Msft Virtual Disk"。</summary>
    public string FriendlyName { get; set; } = string.Empty;

    public string DeviceDescription { get; set; } = string.Empty;

    /// <summary>设备节点句柄：弹出设备时要用。</summary>
    public uint DevInst { get; set; }

    /// <summary>设备能力位（CM_DEVCAP_*），用来判断是否支持"弹出"。</summary>
    public uint Capabilities { get; set; }

    /// <summary>能力位查询的原始返回码（诊断用）。</summary>
    public uint CapabilityQueryResult { get; set; }

    /// <summary>能力位查询返回的数据长度（诊断用）。</summary>
    public uint CapabilityQueryLength { get; set; }

    /// <summary>
    /// 是否支持安全弹出。三个能力位任一命中即可：
    ///   EJECTSUPPORTED（支持弹出）、REMOVABLE（可移动介质）、
    ///   SURPRISEREMOVALOK（允许随时移除，USB 硬盘盒 / USB SSD 通常是这一位）。
    /// 实测：USB SSD 报 0xE0（含可随时移除），内部 NVMe 报 0x60（既不可弹出也不可移动），
    /// 只看"可弹出"那一位会把 USB 盘误判成不能弹出。
    /// </summary>
    public bool SupportsEject
        => (Capabilities & (NativeMethods.CmDevCapEjectSupported
            | NativeMethods.CmDevCapRemovable
            | NativeMethods.CmDevCapSurpriseRemovalOk)) != 0;
}

/// <summary>
/// 通过 SetupDi 枚举磁盘设备接口。
/// 拿到三样东西：设备友好名、设备节点句柄（弹出用）、设备能力位。
/// 分区表与容量等仍走 IOCTL，这里只补充设备层信息。
/// </summary>
internal static class DeviceEnumerator
{
    private static readonly IntPtr InvalidHandleValue = new(-1);

    /// <summary>枚举当前所有磁盘设备，返回 磁盘号 → 设备信息 的字典。</summary>
    public static Dictionary<int, DiskDeviceEntry> Enumerate()
    {
        Dictionary<int, DiskDeviceEntry> result = [];
        Guid interfaceGuid = NativeMethods.GuidDevInterfaceDisk;

        IntPtr deviceInfoSet = NativeMethods.SetupDiGetClassDevsW(
            ref interfaceGuid,
            null,
            IntPtr.Zero,
            NativeMethods.DigcfPresent | NativeMethods.DigcfDeviceInterface);

        if (deviceInfoSet == InvalidHandleValue || deviceInfoSet == IntPtr.Zero)
        {
            return result;
        }

        try
        {
            for (uint index = 0; ; index++)
            {
                NativeMethods.SpDeviceInterfaceData interfaceData = new()
                {
                    cbSize = Marshal.SizeOf<NativeMethods.SpDeviceInterfaceData>(),
                };

                if (!NativeMethods.SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref interfaceGuid, index, ref interfaceData))
                {
                    break; // 枚举结束
                }

                InterfaceDetail? detail = GetInterfaceDetail(deviceInfoSet, ref interfaceData);
                if (detail is not { } info)
                {
                    continue;
                }

                // 复制到局部变量：结构体属性不能直接按 ref 传给 P/Invoke。
                NativeMethods.SpDevInfoData deviceInfo = info.DeviceInfo;

                DiskDeviceEntry entry = new()
                {
                    DevicePath = info.Path,
                    DevInst = deviceInfo.DevInst,
                    FriendlyName = GetRegistryString(deviceInfoSet, ref deviceInfo, NativeMethods.SpdrpFriendlyName),
                    DeviceDescription = GetRegistryString(deviceInfoSet, ref deviceInfo, NativeMethods.SpdrpDeviceDesc),
                };

                entry.Capabilities = ReadCapabilities(
                    deviceInfoSet,
                    ref deviceInfo,
                    out uint queryResult,
                    out uint queryLength);

                entry.CapabilityQueryResult = queryResult;
                entry.CapabilityQueryLength = queryLength;

                if (TryGetDiskNumber(info.Path, out int diskNumber))
                {
                    entry.DiskNumber = diskNumber;
                    result[diskNumber] = entry;
                }
            }
        }
        finally
        {
            NativeMethods.SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }

        return result;
    }

    /// <summary>取设备接口明细：设备路径 + 设备信息（含设备节点句柄）。</summary>
    private static InterfaceDetail? GetInterfaceDetail(IntPtr deviceInfoSet, ref NativeMethods.SpDeviceInterfaceData interfaceData)
    {
        NativeMethods.SpDevInfoData probe = new() { cbSize = Marshal.SizeOf<NativeMethods.SpDevInfoData>() };
        NativeMethods.SetupDiGetDeviceInterfaceDetailW(deviceInfoSet, ref interfaceData, IntPtr.Zero, 0, out int requiredSize, ref probe);
        if (requiredSize <= 0)
        {
            return null;
        }

        IntPtr buffer = Marshal.AllocHGlobal(requiredSize);
        try
        {
            // cbSize：64 位填 8、32 位填 6，填错这次调用会直接失败。
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);

            NativeMethods.SpDevInfoData deviceInfo = new() { cbSize = Marshal.SizeOf<NativeMethods.SpDevInfoData>() };
            if (!NativeMethods.SetupDiGetDeviceInterfaceDetailW(deviceInfoSet, ref interfaceData, buffer, requiredSize, out _, ref deviceInfo))
            {
                return null;
            }

            string path = ReadDevicePath(buffer, requiredSize);
            return path.Length == 0 ? null : new InterfaceDetail(path, deviceInfo);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// 从明细缓冲区里读出设备接口路径。
    ///
    /// 不能写死"64 位从偏移 8 开始"：实测 Windows 11 26100 把字符串直接写在偏移 4
    /// （紧跟在 cbSize 之后），按 8 读会吃掉开头的两个反斜杠，得到 "?\scsi#disk&..."
    /// 这种非法路径，CreateFile 报错 123，整个设备枚举会静默变空。
    /// 这里改为扫描前 64 字节找 UTF-16 的 "\\" 起点，不依赖结构体打包方式。
    /// </summary>
    private static string ReadDevicePath(IntPtr buffer, int bufferSize)
    {
        int limit = Math.Min(bufferSize, 64);
        for (int offset = 4; offset + 4 <= limit; offset += 2)
        {
            if (Marshal.ReadInt16(buffer, offset) == 0x5C && Marshal.ReadInt16(buffer, offset + 2) == 0x5C)
            {
                return Marshal.PtrToStringUni(IntPtr.Add(buffer, offset)) ?? string.Empty;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// 读取设备能力位（CM_DEVCAP_*）。
    /// 优先用 CfgMgr 接口（权威），失败再退回 SetupDi 的注册表属性读取。
    /// </summary>
    private static uint ReadCapabilities(
        IntPtr deviceInfoSet,
        ref NativeMethods.SpDevInfoData deviceInfo,
        out uint queryResult,
        out uint queryLength)
    {
        queryResult = 0;
        queryLength = 0;

        if (deviceInfo.DevInst != 0)
        {
            byte[] buffer = new byte[4];

            // pulLength 是 in/out 参数：必须先填缓冲区大小，否则系统按未初始化的长度处理，
            // 会随机返回 CR_BUFFER_SMALL（0x1A），表现就是能力位读回 0。
            queryLength = (uint)buffer.Length;
            queryResult = NativeMethods.CM_Get_DevNode_Registry_PropertyW(
                deviceInfo.DevInst,
                NativeMethods.CmDrpCapabilities,
                out _,
                buffer,
                ref queryLength,
                0);

            if (queryResult == 0 && queryLength >= 4)
            {
                return BitConverter.ToUInt32(buffer, 0);
            }
        }

        return ReadCapabilitiesViaSetupDi(deviceInfoSet, ref deviceInfo);
    }

    /// <summary>备用路径：通过 SetupDi 读取能力位（要求传入设备信息结构，不能传接口结构）。</summary>
    private static uint ReadCapabilitiesViaSetupDi(IntPtr deviceInfoSet, ref NativeMethods.SpDevInfoData deviceInfo)
    {
        byte[] buffer = new byte[4];
        if (!NativeMethods.SetupDiGetDeviceRegistryPropertyW(
                deviceInfoSet,
                ref deviceInfo,
                NativeMethods.SpdrpCapabilities,
                out _,
                buffer,
                buffer.Length,
                out int size) || size < 4)
        {
            return 0;
        }

        return BitConverter.ToUInt32(buffer, 0);
    }

    /// <summary>读取设备注册表属性字符串。</summary>
    private static string GetRegistryString(IntPtr deviceInfoSet, ref NativeMethods.SpDevInfoData deviceInfo, uint property)
    {
        NativeMethods.SetupDiGetDeviceRegistryPropertyW(deviceInfoSet, ref deviceInfo, property, out _, null, 0, out int requiredSize);
        if (requiredSize <= 0)
        {
            return string.Empty;
        }

        byte[] buffer = new byte[requiredSize];
        if (!NativeMethods.SetupDiGetDeviceRegistryPropertyW(
                deviceInfoSet,
                ref deviceInfo,
                property,
                out _,
                buffer,
                buffer.Length,
                out int actualSize))
        {
            return string.Empty;
        }

        return Encoding.Unicode.GetString(buffer, 0, Math.Max(0, actualSize)).TrimEnd('\0').Trim();
    }

    /// <summary>打开设备接口句柄并反查 PhysicalDriveN 编号。</summary>
    private static bool TryGetDiskNumber(string devicePath, out int diskNumber)
    {
        diskNumber = -1;

        using Microsoft.Win32.SafeHandles.SafeFileHandle handle = NativeMethods.CreateFileW(
            devicePath,
            0,
            Win32Access.FileShareRead | Win32Access.FileShareWrite,
            IntPtr.Zero,
            Win32Access.OpenExisting,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            return false;
        }

        if (!DiskIo.TryIoctl(handle, IoCtl.StorageGetDeviceNumber, out StorageDeviceNumber number, out _, out _))
        {
            return false;
        }

        diskNumber = (int)number.DeviceNumber;
        return true;
    }

    /// <summary>设备接口明细的解析结果。</summary>
    private readonly record struct InterfaceDetail(string Path, NativeMethods.SpDevInfoData DeviceInfo);
}
