using System.Runtime.InteropServices;
using System.Text;
using DiskManager.Interop;

namespace DiskManager.SelfTest;

/// <summary>
/// 设备枚举诊断（不需要管理员权限）：把 SetupDi 每一步的返回值、错误码、
/// 设备节点句柄（DevInst）与能力位（CM_DEVCAP_*）都打出来，
/// 用于排查"弹出"按钮为什么总是灰的。
/// </summary>
internal static class DeviceProbe
{
    public static void Run(Action<string> write)
    {
        write("=== 设备枚举诊断（SetupDi）===");
        Dictionary<int, DiskDeviceEntry> devices = DeviceEnumerator.Enumerate();
        write($"DeviceEnumerator 枚举到 {devices.Count} 个磁盘设备");

        foreach ((int number, DiskDeviceEntry entry) in devices.OrderBy(p => p.Key))
        {
            write(
                $"  磁盘 {number}：{entry.FriendlyName}　DevInst={entry.DevInst}　" +
                $"能力位=0x{entry.Capabilities:X8}（查询 CR=0x{entry.CapabilityQueryResult:X} 长度={entry.CapabilityQueryLength}）" +
                $"　支持弹出={entry.SupportsEject}");
        }

        write("\n=== 逐项原始调用结果 ===");
        ProbeRaw(write);

        write("\n=== 弹出目标解析（沿设备树向上找具备弹出能力的节点）===");
        ProbeEjectTargets(devices, write);
    }

    /// <summary>
    /// 逐级打印设备树并标出"弹出请求会发给哪个节点"。
    /// USB 硬盘盒 / U 盘的可弹出能力在父设备上，弹磁盘节点会被系统判为非法请求。
    /// </summary>
    private static void ProbeEjectTargets(Dictionary<int, DiskDeviceEntry> devices, Action<string> write)
    {
        foreach ((int number, DiskDeviceEntry entry) in devices.OrderBy(p => p.Key))
        {
            write($"  磁盘 {number}（{entry.FriendlyName}）能力位 0x{entry.Capabilities:X8}");

            uint current = entry.DevInst;
            uint target = entry.DevInst;
            string targetReason = "未找到具备弹出能力的上层设备，回退到磁盘节点";

            for (int depth = 0; depth < 8 && current != 0; depth++)
            {
                uint caps = ReadCapabilities(current);
                StringBuilder id = new(512);
                NativeMethods.CM_Get_Device_IDW(current, id, (uint)id.Capacity, 0);

                bool ejectable = (caps & (NativeMethods.CmDevCapEjectSupported | NativeMethods.CmDevCapRemovable)) != 0;
                write($"      第 {depth} 层：能力位 0x{caps:X8}{(ejectable ? "  ← 可弹出（选为弹出目标）" : string.Empty)}　{id}");

                if (ejectable)
                {
                    target = current;
                    targetReason = depth == 0 ? "磁盘节点自身可弹出" : $"第 {depth} 层父设备";
                    break;
                }

                uint result = NativeMethods.CM_Get_Parent(out uint parent, current, 0);
                if (result != 0 || parent == 0 || parent == current)
                {
                    break;
                }

                current = parent;
            }

            write($"      ⇒ 弹出请求将发送到：{targetReason}（DevInst={target}）");
        }
    }

    /// <summary>读取某设备节点的能力位。</summary>
    private static uint ReadCapabilities(uint devInst)
    {
        byte[] buffer = new byte[4];
        uint length = (uint)buffer.Length;
        uint result = NativeMethods.CM_Get_DevNode_Registry_PropertyW(
            devInst,
            NativeMethods.CmDrpCapabilities,
            out _,
            buffer,
            ref length,
            0);

        return result == 0 && length >= 4 ? BitConverter.ToUInt32(buffer, 0) : 0;
    }

    /// <summary>手工重放一遍 SetupDi 调用链，逐步打印结果与错误码。</summary>
    private static void ProbeRaw(Action<string> write)
    {
        Guid interfaceGuid = NativeMethods.GuidDevInterfaceDisk;
        IntPtr deviceInfoSet = NativeMethods.SetupDiGetClassDevsW(
            ref interfaceGuid,
            null,
            IntPtr.Zero,
            NativeMethods.DigcfPresent | NativeMethods.DigcfDeviceInterface);

        if (deviceInfoSet == new IntPtr(-1) || deviceInfoSet == IntPtr.Zero)
        {
            write($"  SetupDiGetClassDevs 失败，错误码={Marshal.GetLastWin32Error()}");
            return;
        }

        try
        {
            for (uint index = 0; index < 8; index++)
            {
                NativeMethods.SpDeviceInterfaceData interfaceData = new()
                {
                    cbSize = Marshal.SizeOf<NativeMethods.SpDeviceInterfaceData>(),
                };

                if (!NativeMethods.SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref interfaceGuid, index, ref interfaceData))
                {
                    write($"  第 {index} 项：枚举结束（错误码={Marshal.GetLastWin32Error()}）");
                    break;
                }

                NativeMethods.SpDevInfoData probe = new() { cbSize = Marshal.SizeOf<NativeMethods.SpDevInfoData>() };
                bool probeOk = NativeMethods.SetupDiGetDeviceInterfaceDetailW(
                    deviceInfoSet, ref interfaceData, IntPtr.Zero, 0, out int requiredSize, ref probe);
                int probeError = Marshal.GetLastWin32Error();

                write($"  第 {index} 项：明细探测 ok={probeOk} requiredSize={requiredSize} 错误码={probeError}");

                if (requiredSize <= 0)
                {
                    continue;
                }

                IntPtr buffer = Marshal.AllocHGlobal(requiredSize);
                try
                {
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                    NativeMethods.SpDevInfoData info = new() { cbSize = Marshal.SizeOf<NativeMethods.SpDevInfoData>() };
                    bool detailOk = NativeMethods.SetupDiGetDeviceInterfaceDetailW(
                        deviceInfoSet, ref interfaceData, buffer, requiredSize, out _, ref info);
                    int detailError = Marshal.GetLastWin32Error();

                    write(
                        $"      明细读取 ok={detailOk} 错误码={detailError} " +
                        $"DevInst={info.DevInst} cbSize={info.cbSize}");

                    // 方式一：SetupDi 读能力位
                    byte[] caps = new byte[4];
                    bool capsOk = NativeMethods.SetupDiGetDeviceRegistryPropertyW(
                        deviceInfoSet, ref info, NativeMethods.SpdrpCapabilities,
                        out uint regType, caps, caps.Length, out int capsSize);
                    int capsError = Marshal.GetLastWin32Error();
                    uint capsValue = capsSize >= 4 ? BitConverter.ToUInt32(caps, 0) : 0;

                    write(
                        $"      [SetupDi] 能力位 ok={capsOk} 错误码={capsError} 类型={regType} " +
                        $"长度={capsSize} 值=0x{capsValue:X8}");

                    // 方式二：CfgMgr 读能力位（更权威）
                    if (info.DevInst != 0)
                    {
                        byte[] cmCaps = new byte[4];
                        uint cmLength = (uint)cmCaps.Length; // in/out：先填缓冲区大小
                        uint cmResult = NativeMethods.CM_Get_DevNode_Registry_PropertyW(
                            info.DevInst,
                            NativeMethods.CmDrpCapabilities,
                            out _,
                            cmCaps,
                            ref cmLength,
                            0);

                        uint cmValue = cmLength >= 4 ? BitConverter.ToUInt32(cmCaps, 0) : 0;
                        write($"      [CfgMgr] 能力位 CR=0x{cmResult:X} 长度={cmLength} 值=0x{cmValue:X8}");

                        // 设备实例 ID，便于人工核对
                        StringBuilder instanceId = new(512);
                        if (NativeMethods.CM_Get_Device_IDW(info.DevInst, instanceId, (uint)instanceId.Capacity, 0) == 0)
                        {
                            write($"      设备实例：{instanceId}");
                        }

                        // 用不同访问方式尝试打开设备接口路径，定位"枚举为空"的原因
                        string devicePath = Marshal.PtrToStringUni(IntPtr.Add(buffer, IntPtr.Size == 8 ? 8 : 4)) ?? string.Empty;
                        write($"      设备路径：{devicePath}");
                        TryOpen(write, devicePath, 0, "access=0");
                        TryOpen(write, devicePath, 0x00000080, "access=FILE_READ_ATTRIBUTES");
                        TryOpen(write, devicePath, 0x80000000, "access=GENERIC_READ");
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }
        finally
        {
            NativeMethods.SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    /// <summary>尝试用指定访问方式打开设备路径，并尝试取设备号。</summary>
    private static void TryOpen(Action<string> write, string path, uint access, string label)
    {
        using Microsoft.Win32.SafeHandles.SafeFileHandle handle = NativeMethods.CreateFileW(
            path,
            access,
            Win32Access.FileShareRead | Win32Access.FileShareWrite,
            IntPtr.Zero,
            Win32Access.OpenExisting,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            write($"      [打开 {label}] 失败 错误码={Marshal.GetLastWin32Error()}");
            return;
        }

        bool ok = DiskIo.TryIoctl(handle, IoCtl.StorageGetDeviceNumber, out StorageDeviceNumber number, out _, out int ioctlError);
        write(ok
            ? $"      [打开 {label}] 成功，设备号={number.DeviceNumber}"
            : $"      [打开 {label}] 成功但取设备号失败 错误码={ioctlError}");
    }
}
