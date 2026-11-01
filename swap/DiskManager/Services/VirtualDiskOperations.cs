using System.Management;
using System.Text;
using DiskManager.Interop;

namespace DiskManager.Services;

/// <summary>
/// 虚拟磁盘（VHD / VHDX）操作。
///
/// 为什么不用"弹出"：挂载中的 VHD 虽然报告支持弹出（能力位 0x62），
/// 但 PnP 弹出请求会被系统以 PNP_VetoIllegalDeviceRequest(8) 否决——
/// 虚拟磁盘的正解是"分离"（DetachVirtualDisk），也就是资源管理器/磁盘管理里的那个动作。
///
/// 分离流程：扫描候选 .vhd/.vhdx → 用 OpenVirtualDisk + GetVirtualDiskPhysicalPath
/// 确认它对应的是目标物理磁盘 → DetachVirtualDisk。
/// </summary>
internal static class VirtualDiskOperations
{
    /// <summary>判断某块磁盘是不是"挂载中的虚拟磁盘"。</summary>
    public static bool IsVirtualDiskBacked(int diskNumber)
    {
        using DiskHandle? handle = DiskHandle.TryOpen(diskNumber, writeAccess: false, out _);
        if (handle is null || !handle.TryQueryDeviceInfo(out StorageDeviceInfo info, out _))
        {
            return false;
        }

        return info.BusType is 0x0E or 0x0F; // BusTypeVirtual / BusTypeFileBackedVirtual
    }

    /// <summary>找出某块虚拟磁盘对应的 VHD/VHDX 宿主文件。</summary>
    public static bool TryFindBackingFile(int diskNumber, out string filePath, out string error)
    {
        filePath = string.Empty;
        error = string.Empty;

        foreach (string candidate in VirtualDiskFileScanner.EnumerateVirtualDisks())
        {
            if (TryGetPhysicalDiskNumber(candidate, out int physicalDisk, out _) && physicalDisk == diskNumber)
            {
                filePath = candidate;
                return true;
            }
        }

        error = $"没有找到与磁盘 {diskNumber} 对应的 VHD/VHDX 文件。" +
                "可能是文件所在目录层级过深（扫描上限 4 层），或它不是挂载的虚拟磁盘。";
        return false;
    }

    /// <summary>
    /// 分离指定虚拟磁盘（等价于「分离 VHD」）。
    /// 不传文件路径时自动扫描查找；传入时（用户在界面上手动指定）会先校验它确实对应这块磁盘。
    /// </summary>
    public static OperationResult Detach(int diskNumber, string? explicitFilePath = null)
    {
        using IDisposable scope = OperationLog.Scope($"Detach 磁盘 {diskNumber}");

        string filePath;

        if (!string.IsNullOrWhiteSpace(explicitFilePath))
        {
            if (!TryGetPhysicalDiskNumber(explicitFilePath, out int mappedDisk, out string mapError))
            {
                return OperationResult.Fail($"无法确认 {explicitFilePath} 是磁盘 {diskNumber} 的宿主文件：{mapError}");
            }

            if (mappedDisk != diskNumber)
            {
                return OperationResult.Fail(
                    $"{explicitFilePath} 对应的是磁盘 {mappedDisk}，不是磁盘 {diskNumber}，已中止以免分离错误的磁盘。");
            }

            filePath = explicitFilePath;
        }
        else if (!TryFindBackingFile(diskNumber, out filePath, out string findError))
        {
            return OperationResult.Fail(findError);
        }

        uint[] accessMasks =
        [
            NativeMethods.VirtualDiskAccessDetach | NativeMethods.VirtualDiskAccessGetInfo,
            NativeMethods.VirtualDiskAccessNone,
            NativeMethods.VirtualDiskAccessGetInfo,
        ];

        string lastError = string.Empty;
        foreach (uint accessMask in accessMasks)
        {
            OperationResult result = TryDetachWithAccess(filePath, accessMask, out string error);
            if (result.Success)
            {
                return result;
            }

            lastError = error;
        }

        return OperationResult.Fail($"分离虚拟磁盘失败：{lastError}");
    }

    /// <summary>
    /// 带兜底的分离：先自动查找宿主文件，找不到时调用 picker 让用户手动指定。
    /// picker 由界面层提供（打开文件选择框），服务层不依赖界面。
    /// </summary>
    public static OperationResult DetachWithFallback(int diskNumber, Func<string?> picker)
    {
        if (TryFindBackingFile(diskNumber, out string found, out _))
        {
            return Detach(diskNumber, found);
        }

        string? picked = picker();
        return string.IsNullOrWhiteSpace(picked)
            ? OperationResult.Fail($"未能自动找到磁盘 {diskNumber} 的 VHD/VHDX 宿主文件，且没有手动指定，已取消。")
            : Detach(diskNumber, picked);
    }

    /// <summary>用指定访问权限打开并分离。</summary>
    private static OperationResult TryDetachWithAccess(string filePath, uint accessMask, out string error)
    {
        uint deviceId = filePath.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase)
            ? NativeMethods.VirtualStorageTypeDeviceVhdx
            : NativeMethods.VirtualStorageTypeDeviceVhd;

        NativeMethods.VirtualStorageType storageType = new()
        {
            DeviceId = deviceId,
            VendorId = NativeMethods.VirtualStorageVendorId,
        };

        NativeMethods.OpenVirtualDiskParameters parameters = new()
        {
            Version = NativeMethods.OpenVirtualDiskVersion1,
            RWDepth = NativeMethods.VirtualDiskRwDepthDefault,
        };

        uint openResult = NativeMethods.OpenVirtualDisk(
            ref storageType,
            filePath,
            accessMask,
            NativeMethods.OpenVirtualDiskFlagNone,
            ref parameters,
            out Microsoft.Win32.SafeHandles.SafeFileHandle handle);

        using (handle)
        {
            if (openResult != 0)
            {
                error = $"打开 {filePath} 失败（返回码 0x{openResult:X8}：{Win32Error.Describe((int)openResult)}）";
                return OperationResult.Fail(error);
            }

            // 分离同样可能阻塞（文件被别的进程占着时系统会等待），加超时保护
            uint detachResult = 0;
            bool completed = Task.Run(() =>
            {
                detachResult = NativeMethods.DetachVirtualDisk(
                    handle,
                    NativeMethods.DetachVirtualDiskFlagNone,
                    0);
            }).Wait(TimeSpan.FromSeconds(15));

            if (!completed)
            {
                error = $"分离 {filePath} 超时：系统仍在等待文件释放，通常是有程序（例如 Hyper-V 的 vmwp.exe）占用它。";
                return OperationResult.Fail(error);
            }

            if (detachResult == 0)
            {
                error = string.Empty;
                return OperationResult.Ok($"已分离虚拟磁盘：{filePath}");
            }

            error = $"分离 {filePath} 失败（返回码 0x{detachResult:X8}：{Win32Error.Describe((int)detachResult)}）";
            return OperationResult.Fail(error);
        }
    }

    /// <summary>打开虚拟磁盘文件，问出它挂到了哪个物理磁盘。</summary>
    public static bool TryGetPhysicalDiskNumber(string filePath, out int diskNumber, out string error)
    {
        diskNumber = -1;
        error = string.Empty;

        uint deviceId = filePath.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase)
            ? NativeMethods.VirtualStorageTypeDeviceVhdx
            : NativeMethods.VirtualStorageTypeDeviceVhd;

        NativeMethods.VirtualStorageType storageType = new()
        {
            DeviceId = deviceId,
            VendorId = NativeMethods.VirtualStorageVendorId,
        };

        NativeMethods.OpenVirtualDiskParameters parameters = new()
        {
            Version = NativeMethods.OpenVirtualDiskVersion1,
            RWDepth = NativeMethods.VirtualDiskRwDepthDefault,
        };

        uint openResult = NativeMethods.OpenVirtualDisk(
            ref storageType,
            filePath,
            NativeMethods.VirtualDiskAccessGetInfo,
            NativeMethods.OpenVirtualDiskFlagNone,
            ref parameters,
            out Microsoft.Win32.SafeHandles.SafeFileHandle handle);

        using (handle)
        {
            if (openResult != 0)
            {
                error = Win32Error.Describe((int)openResult);
                return false;
            }

            StringBuilder path = new(64);
            uint sizeInBytes = (uint)(path.Capacity * sizeof(char));
            uint result = NativeMethods.GetVirtualDiskPhysicalPath(handle, ref sizeInBytes, path);
            if (result != 0)
            {
                error = Win32Error.Describe((int)result);
                return false;
            }

            return TryParsePhysicalDriveNumber(path.ToString(), out diskNumber);
        }
    }

    /// <summary>把 "\\.\PhysicalDrive4" 解析成磁盘号 4。</summary>
    private static bool TryParsePhysicalDriveNumber(string physicalPath, out int diskNumber)
    {
        diskNumber = -1;
        const string prefix = @"\\.\PhysicalDrive";

        if (!physicalPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return int.TryParse(physicalPath.AsSpan(prefix.Length), out diskNumber);
    }
}
