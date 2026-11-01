using System.Runtime.InteropServices;
using System.Text;
using DiskManager.Interop;
using DiskManager.Services;

namespace DiskManager.SelfTest;

/// <summary>
/// 虚拟磁盘诊断（不需要管理员权限）：扫描 .vhd/.vhdx，逐个用 virtdisk 接口
/// 打开并问出它挂到了哪个物理磁盘，用来验证"分离 VHD"那条代码路径。
/// </summary>
internal static class VhdProbe
{
    public static void Run(Action<string> write)
    {
        write("=== 虚拟磁盘文件扫描 ===");
        int count = 0;

        foreach (string file in VirtualDiskFileScanner.EnumerateAll())
        {
            count++;
            bool inUse = VirtualDiskFileScanner.IsFileInUse(file);
            List<string> holders = FileLockInspector.GetLockingProcesses([file]);
            string holderText = holders.Count > 0 ? string.Join("、", holders) : "（未检测到）";

            string openInfo = Describe(file, out string physicalPath);
            write(
                $"  {file}\n" +
                $"      占用={inUse}　占用进程={holderText}　{openInfo}　" +
                $"物理路径={(physicalPath.Length > 0 ? physicalPath : "（未挂载）")}");
        }

        write($"共扫描到 {count} 个虚拟磁盘文件。");
    }

    /// <summary>打开虚拟磁盘并取物理路径，返回可读的说明。</summary>
    private static string Describe(string file, out string physicalPath)
    {
        physicalPath = string.Empty;

        NativeMethods.VirtualStorageType storageType = new()
        {
            DeviceId = file.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase)
                ? NativeMethods.VirtualStorageTypeDeviceVhdx
                : NativeMethods.VirtualStorageTypeDeviceVhd,
            VendorId = NativeMethods.VirtualStorageVendorId,
        };

        NativeMethods.OpenVirtualDiskParameters parameters = new()
        {
            Version = NativeMethods.OpenVirtualDiskVersion1,
            RWDepth = NativeMethods.VirtualDiskRwDepthDefault,
        };

        uint result = NativeMethods.OpenVirtualDisk(
            ref storageType,
            file,
            NativeMethods.VirtualDiskAccessGetInfo,
            NativeMethods.OpenVirtualDiskFlagNone,
            ref parameters,
            out Microsoft.Win32.SafeHandles.SafeFileHandle handle);

        using (handle)
        {
            if (result != 0)
            {
                return $"OpenVirtualDisk 失败（0x{result:X8}）";
            }

            StringBuilder path = new(64);
            uint sizeInBytes = (uint)(path.Capacity * sizeof(char));
            uint pathResult = NativeMethods.GetVirtualDiskPhysicalPath(handle, ref sizeInBytes, path);

            if (pathResult != 0)
            {
                return $"取物理路径失败（0x{pathResult:X8}）";
            }

            physicalPath = path.ToString();
            return $"打开成功（缓冲区需 {sizeInBytes} 字节）";
        }
    }
}
