using DiskManager.Models;
using DiskManager.Services;

namespace DiskOfflineTool;

/// <summary>
/// 本工具用到的全部业务动作，只有"列磁盘"和"脱机/联机"两件事。
/// 底层直接复用主程序已验证的实现（原生 IOCTL 优先，失败退回系统存储接口）。
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

    /// <summary>弹出（安全移除）指定磁盘。</summary>
    public static OperationResult Eject(int diskNumber)
        => DiskOperations.Eject(diskNumber);

    /// <summary>移除磁盘：虚拟磁盘走"分离 VHD"，其他设备走"弹出"。</summary>
    public static OperationResult Remove(DiskItem disk, BlockResolutionOptions? options = null)
        => DiskOperations.RemoveDisk(disk, options);

    /// <summary>该磁盘是否允许脱机（系统盘 / 启动盘不允许）。</summary>
    public static bool CanOffline(DiskItem disk) => disk.CanOffline;
}
