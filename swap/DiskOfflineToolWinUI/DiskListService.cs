using DiskManager.Models;
using DiskManager.Services;

namespace DiskOfflineToolWinUI;

/// <summary>
/// 与 WPF 版完全相同的业务动作，底层复用主程序已验证的实现
/// （原生 IOCTL 优先，失败退回系统存储接口）。
/// </summary>
internal static class DiskListService
{
    /// <summary>读取当前全部磁盘。</summary>
    public static IReadOnlyList<DiskItem> Capture(out IReadOnlyList<string> warnings)
    {
        InventorySnapshot snapshot = DiskInventory.Capture();
        warnings = snapshot.Warnings;
        return snapshot.Disks;
    }

    /// <summary>脱机或联机指定磁盘。</summary>
    public static OperationResult SetOffline(int diskNumber, bool offline, BlockResolutionOptions? options = null)
        => DiskOperations.SetOffline(diskNumber, offline, persist: true, options);

    /// <summary>移除磁盘：虚拟磁盘走"分离 VHD"，其他设备走"弹出"。</summary>
    public static OperationResult Remove(DiskItem disk, BlockResolutionOptions? options = null)
        => DiskOperations.RemoveDisk(disk, options);
}
