using System.IO;
using System.Management;
using DiskManager.Interop;
using DiskManager.Models;
using DiskManager.Services;

namespace DiskManager.SelfTest;

/// <summary>
/// 诊断模式：把"新建分区被拒绝访问"这类问题的现场数据完整打印出来。
/// 只读为主，仅在需要时对测试磁盘做一次创建分区尝试。
/// </summary>
internal static class Diagnostics
{
    public static void Run(string workspace, string vhdxPath, Action<string> write)
    {
        // 上一轮如果异常中断，VHDX 可能还挂着：先卸掉，再记录"测试前存在的磁盘"。
        VhdHelper.TryDetach(vhdxPath, out _, out _);
        Thread.Sleep(1000);

        HashSet<int> existing = [.. DiskInventory.Capture().Disks.Select(d => d.Number)];
        write($"诊断开始，测试前已有磁盘：{string.Join(",", existing)}");

        if (!VhdHelper.TryCreateAndAttach(vhdxPath, out string mountError, out string mountOutput))
        {
            write($"挂载失败：{mountError}");
            return;
        }

        write($"挂载输出：{mountOutput.Replace('\r', ' ').Replace('\n', ' ').Trim()}");
        Thread.Sleep(4000);

        InventorySnapshot snapshot = DiskInventory.Capture();
        foreach (DiskItem disk in snapshot.Disks)
        {
            write(
                $"原生视图：磁盘 {disk.Number} {disk.FriendlyName} 总线={disk.BusTypeName} 容量={disk.CapacityText} " +
                $"样式={disk.Style} 已初始化={disk.IsInitialized} MBR签名=0x{disk.MbrSignature:X8} 分区数={disk.Partitions.Count} " +
                $"脱机={disk.IsOffline} 只读={disk.IsReadOnly} 状态={disk.StatusText}");
        }

        DiskItem? test = snapshot.Disks.FirstOrDefault(d => !existing.Contains(d.Number) && d.BusTypeName.Contains("虚拟", StringComparison.Ordinal));
        if (test is null)
        {
            write("没有找到测试磁盘，诊断结束。");
            VhdHelper.TryDetach(vhdxPath, out _, out _);
            return;
        }

        write($"\n--- 测试磁盘：磁盘 {test.Number} ---");
        DumpWmiDisk(test.Number, write);

        write("\n--- 尝试通过存储接口初始化（若未初始化）---");
        if (!test.IsInitialized)
        {
            OperationResult init = StorageOperations.InitializeDisk(test.Number, PartitionStyle.Gpt);
            write($"WMI 初始化结果：成功={init.Success} 消息={init.Message}");
            Thread.Sleep(2000);
            DumpWmiDisk(test.Number, write);
        }
        else
        {
            write("磁盘已初始化，跳过。");
        }

        write("\n--- 尝试通过存储接口创建 512MB 分区 ---");
        OperationResult create = StorageOperations.CreatePartition(
            test.Number,
            512L * 1024 * 1024,
            offset: null,
            driveLetter: null,
            useMaximumSize: false,
            PartitionStyle.Gpt);

        write($"创建分区结果：成功={create.Success} 消息={create.Message}");
        Thread.Sleep(2000);

        InventorySnapshot after = DiskInventory.Capture();
        DiskItem? reloaded = after.Disks.FirstOrDefault(d => d.Number == test.Number);
        write($"创建后：样式={reloaded?.Style} 分区数={reloaded?.Partitions.Count}");

        write("\n--- 卸载测试磁盘 ---");
        if (VhdHelper.TryDetach(vhdxPath, out string detachError, out string detachOutput))
        {
            write($"已卸载：{detachOutput.Trim()}");
        }
        else
        {
            write($"卸载失败：{detachError}");
        }

        ProbeFreshVhd(workspace, write);
        ProbeOfflineSequence(vhdxPath, write);
        ProbeDriveLetter(vhdxPath, write);
        ProbeConvertStyle(vhdxPath, write);
    }

    /// <summary>观察"GPT 转 MBR"之后三方视图（原生 IOCTL / 存储接口 / 0 号扇区）是否一致。</summary>
    private static void ProbeConvertStyle(string vhdxPath, Action<string> write)
    {
        write("\n--- 转换为 MBR 专项探针 ---");
        VhdHelper.TryDetach(vhdxPath, out _, out _);
        Thread.Sleep(1000);

        HashSet<int> existing = [.. DiskInventory.Capture().Disks.Select(d => d.Number)];
        if (!VhdHelper.TryCreateAndAttach(vhdxPath, out string error, out _))
        {
            write($"挂载失败：{error}");
            return;
        }

        Thread.Sleep(4000);
        DiskItem? test = DiskInventory.Capture().Disks.FirstOrDefault(d => !existing.Contains(d.Number));
        if (test is null)
        {
            write("未发现测试磁盘。");
            VhdHelper.TryDetach(vhdxPath, out _, out _);
            return;
        }

        write($"初始：样式={test.Style} 分区数={test.Partitions.Count}");
        write($"初始化 GPT：成功={StorageOperations.InitializeDisk(test.Number, PartitionStyle.Gpt).Success}");
        Thread.Sleep(2500);
        DumpViews(test.Number, write);

        write($"转换为 MBR：成功={StorageOperations.ConvertStyle(test.Number, PartitionStyle.Mbr).Success}");
        Thread.Sleep(2500);
        DumpViews(test.Number, write);

        // 再建一个分区，看看 MBR 上还能不能正常分区
        OperationResult create = StorageOperations.CreatePartition(
            test.Number,
            256L * 1024 * 1024,
            offset: null,
            driveLetter: null,
            useMaximumSize: false,
            PartitionStyle.Mbr);
        write($"在转换后的磁盘上创建分区：成功={create.Success} 消息={create.Message}");
        Thread.Sleep(2000);
        DumpViews(test.Number, write);

        VhdHelper.TryDetach(vhdxPath, out _, out _);
        write("测试磁盘已卸载。");
    }

    /// <summary>同时打印原生视图、存储接口视图与 0 号扇区签名。</summary>
    private static void DumpViews(int diskNumber, Action<string> write)
    {
        DiskItem? disk = DiskInventory.Capture().Disks.FirstOrDefault(d => d.Number == diskNumber);
        write($"  原生视图：样式={disk?.Style} 已初始化={disk?.IsInitialized} MBR签名=0x{disk?.MbrSignature:X8} 分区数={disk?.Partitions.Count}");

        using (DiskHandle? handle = DiskHandle.TryOpen(diskNumber, writeAccess: false, out string openError))
        {
            if (handle is null)
            {
                write($"  0 号扇区：打开磁盘失败（{openError}）");
            }
            else if (handle.TryReadMbrSignature(out bool hasSignature, out string readError))
            {
                write($"  0 号扇区：0x55AA 结束标志={(hasSignature ? "有" : "无")}");
            }
            else
            {
                write($"  0 号扇区：读取失败（{readError}）");
            }
        }

        DumpWmiDisk(diskNumber, write);
    }

    /// <summary>用一块全新的 VHDX 观察"从未初始化"的磁盘在两层接口里各是什么样子。</summary>
    private static void ProbeFreshVhd(string workspace, Action<string> write)
    {
        string freshPath = Path.Combine(workspace, "TestDisks", "dm-rawtest.vhdx");
        write("\n--- 全新 VHDX 的初始状态观察 ---");

        HashSet<int> existing = [.. DiskInventory.Capture().Disks.Select(d => d.Number)];
        if (!VhdHelper.TryCreateAndAttach(freshPath, out string error, out _))
        {
            write($"挂载全新 VHDX 失败：{error}");
            return;
        }

        Thread.Sleep(4000);
        InventorySnapshot snapshot = DiskInventory.Capture();
        DiskItem? fresh = snapshot.Disks.FirstOrDefault(d => !existing.Contains(d.Number));

        if (fresh is null)
        {
            write("未发现新磁盘。");
        }
        else
        {
            write(
                $"原生视图：磁盘 {fresh.Number} 样式={fresh.Style} 已初始化={fresh.IsInitialized} " +
                $"MBR签名=0x{fresh.MbrSignature:X8} 分区数={fresh.Partitions.Count} 状态={fresh.StatusText}");
            DumpWmiDisk(fresh.Number, write);

            // 顺便验证：全新的、未初始化的磁盘能否直接用存储接口初始化。
            OperationResult init = StorageOperations.InitializeDisk(fresh.Number, PartitionStyle.Gpt);
            write($"对全新磁盘执行 WMI 初始化：成功={init.Success} 消息={init.Message}");
            Thread.Sleep(2000);
            DiskItem? recheck = DiskInventory.Capture().Disks.FirstOrDefault(d => d.Number == fresh.Number);
            write($"初始化后原生视图：样式={recheck?.Style} 分区数={recheck?.Partitions.Count}");
        }

        if (VhdHelper.TryDetach(freshPath, out string detachError, out _))
        {
            write("全新 VHDX 已卸载。");
        }
        else
        {
            write($"全新 VHDX 卸载失败：{detachError}");
        }
    }

    /// <summary>复现自测里的操作顺序：脱机 → 联机 → 建分区，定位"拒绝访问"的来源。</summary>
    private static void ProbeOfflineSequence(string vhdxPath, Action<string> write)
    {
        write("\n--- 复现：脱机/联机之后能否创建分区 ---");

        HashSet<int> existing = [.. DiskInventory.Capture().Disks.Select(d => d.Number)];
        if (!VhdHelper.TryCreateAndAttach(vhdxPath, out string error, out _))
        {
            write($"挂载失败：{error}");
            return;
        }

        Thread.Sleep(4000);
        DiskItem? test = DiskInventory.Capture().Disks.FirstOrDefault(d => !existing.Contains(d.Number));
        if (test is null)
        {
            write("未发现测试磁盘。");
            VhdHelper.TryDetach(vhdxPath, out _, out _);
            return;
        }

        write($"测试磁盘 {test.Number}，当前样式={test.Style} 分区数={test.Partitions.Count}");

        write($"脱机：成功={DiskOperations.SetOffline(test.Number, true).Success}");
        Thread.Sleep(1500);
        write($"联机：成功={DiskOperations.SetOffline(test.Number, false).Success}");
        Thread.Sleep(1500);
        DumpWmiDisk(test.Number, write);

        // A：不分配盘符创建分区
        OperationResult plain = StorageOperations.CreatePartition(
            test.Number,
            256L * 1024 * 1024,
            offset: null,
            driveLetter: null,
            useMaximumSize: false,
            PartitionStyle.Gpt);
        write($"A) 脱机/联机后、不分配盘符创建分区：成功={plain.Success} 消息={plain.Message}");
        Thread.Sleep(1500);

        // B：先刷新存储接口缓存再创建
        if (!plain.Success)
        {
            OperationResult refresh = StorageOperations.RefreshDisk(test.Number);
            write($"B) 刷新存储接口缓存：成功={refresh.Success} 消息={refresh.Message}");
            Thread.Sleep(1500);

            OperationResult retry = StorageOperations.CreatePartition(
                test.Number,
                256L * 1024 * 1024,
                offset: null,
                driveLetter: null,
                useMaximumSize: false,
                PartitionStyle.Gpt);
            write($"B) 刷新后重试创建分区：成功={retry.Success} 消息={retry.Message}");
            Thread.Sleep(1500);
        }

        // C：分配盘符创建分区
        char letter = 'Y';
        OperationResult withLetter = StorageOperations.CreatePartition(
            test.Number,
            128L * 1024 * 1024,
            offset: null,
            driveLetter: letter,
            useMaximumSize: false,
            PartitionStyle.Gpt);
        write($"C) 分配盘符 {letter}: 创建分区：成功={withLetter.Success} 消息={withLetter.Message}");
        Thread.Sleep(1500);

        DiskItem? after = DiskInventory.Capture().Disks.FirstOrDefault(d => d.Number == test.Number);
        write($"最终：分区数={after?.Partitions.Count}");

        VhdHelper.TryDetach(vhdxPath, out _, out _);
        write("测试磁盘已卸载。");
    }

    /// <summary>专查"分配驱动器号被拒绝访问"：看卷是否出现、两条分配路径各自的结果。</summary>
    private static void ProbeDriveLetter(string vhdxPath, Action<string> write)
    {
        write("\n--- 驱动器号分配专项探针 ---");

        HashSet<int> existing = [.. DiskInventory.Capture().Disks.Select(d => d.Number)];
        if (!VhdHelper.TryCreateAndAttach(vhdxPath, out string error, out _))
        {
            write($"挂载失败：{error}");
            return;
        }

        Thread.Sleep(4000);
        DiskItem? test = DiskInventory.Capture().Disks.FirstOrDefault(d => !existing.Contains(d.Number));
        if (test is null)
        {
            write("未发现测试磁盘。");
            VhdHelper.TryDetach(vhdxPath, out _, out _);
            return;
        }

        HashSet<long> before = [.. test.Partitions.Select(p => p.Offset)];
        OperationResult create = StorageOperations.CreatePartition(
            test.Number,
            256L * 1024 * 1024,
            offset: null,
            driveLetter: null,
            useMaximumSize: false,
            test.Style);
        write($"创建 256MB 分区（不分配盘符）：成功={create.Success} 消息={create.Message}");
        Thread.Sleep(3000);

        DiskItem? after = DiskInventory.Capture().Disks.FirstOrDefault(d => d.Number == test.Number);
        PartitionItem? newPartition = after?.Partitions.FirstOrDefault(p => !before.Contains(p.Offset));
        write($"新分区：{(newPartition is null ? "未找到" : $"编号={newPartition.Number} 偏移={newPartition.Offset} 大小={newPartition.Length}")}");

        // 系统当前占用的盘符
        HashSet<char> used = VolumeDevice.GetUsedDriveLetters();
        write($"已占用盘符：{string.Join(" ", used.OrderBy(c => c))}");

        // 新分区对应的卷是否已经出现在卷管理器里
        string? matchedVolume = null;
        foreach (RawVolume volume in VolumeDevice.Enumerate())
        {
            if (newPartition is not null
                && volume.Extents.Any(e => (int)e.DiskNumber == test.Number && e.StartingOffset == newPartition.Offset))
            {
                matchedVolume = volume.VolumeName;
            }
        }

        write($"卷管理器里匹配到的卷：{(matchedVolume ?? "（没有）")}");

        char letter = FindUnusedLetter(used);
        write($"准备使用的盘符：{letter}:");

        if (newPartition is null)
        {
            VhdHelper.TryDetach(vhdxPath, out _, out _);
            return;
        }

        // 对照实验 1：原生 SetVolumeMountPoint（需要卷 GUID）
        if (matchedVolume is not null)
        {
            OperationResult native = DiskOperations.AssignDriveLetter(letter, matchedVolume);
            write($"1) 原生 SetVolumeMountPoint：{native.Success} {native.Message}");
        }

        // 对照实验 2/3/4：直接调用存储接口，观察原始返回码
        CallAddAccessPath(test.Number, newPartition.Number, $"{letter}:\\", includeAssignFlag: true, "2) AddAccessPath(AssignDriveLetter=false)", write);
        CallAddAccessPath(test.Number, newPartition.Number, $"{letter}:\\", includeAssignFlag: false, "3) AddAccessPath(不传 AssignDriveLetter)", write);
        CallAddAccessPath(test.Number, newPartition.Number, $"{letter}:\\", includeAssignFlag: true, "4) 重新查询后 AddAccessPath", write);

        // 直接看一遍分区与卷对象
        DumpPartitionsAndVolumes(test.Number, write);

        Thread.Sleep(1500);
        write($"当前已占用盘符：{string.Join(" ", VolumeDevice.GetUsedDriveLetters().OrderBy(c => c))}");

        VhdHelper.TryDetach(vhdxPath, out _, out _);
        write("测试磁盘已卸载。");
    }

    /// <summary>直接调用 MSFT_Partition.AddAccessPath，并把原始返回码打出来。</summary>
    private static void CallAddAccessPath(int diskNumber, int partitionNumber, string accessPath, bool includeAssignFlag, string title, Action<string> write)
    {
        try
        {
            ManagementScope scope = new(@"\\.\root\Microsoft\Windows\Storage");
            scope.Connect();
            using ManagementObjectSearcher searcher = new(
                scope,
                new ObjectQuery($"SELECT * FROM MSFT_Partition WHERE DiskNumber = {diskNumber} AND PartitionNumber = {partitionNumber}"));

            foreach (ManagementBaseObject item in searcher.Get())
            {
                using ManagementObject partition = (ManagementObject)item;
                using ManagementBaseObject inParams = partition.GetMethodParameters("AddAccessPath");
                inParams["AccessPath"] = accessPath;
                if (includeAssignFlag)
                {
                    inParams["AssignDriveLetter"] = false;
                }

                using ManagementBaseObject outParams = partition.InvokeMethod("AddAccessPath", inParams, null);
                uint code = Convert.ToUInt32(outParams["ReturnValue"]);
                string extended = outParams["ExtendedStatus"] is ManagementBaseObject status ? status["Message"]?.ToString() ?? string.Empty : string.Empty;
                write($"{title}：返回码={code}（0x{code:X8}）{extended}");
                return;
            }

            write($"{title}：找不到分区对象");
        }
        catch (Exception ex)
        {
            write($"{title}：调用异常 {ex.Message}");
        }
    }

    /// <summary>打印指定磁盘上的分区与卷对象，确认卷设备是否已生成。</summary>
    private static void DumpPartitionsAndVolumes(int diskNumber, Action<string> write)
    {
        try
        {
            ManagementScope scope = new(@"\\.\root\Microsoft\Windows\Storage");
            scope.Connect();

            using ManagementObjectSearcher partitions = new(scope, new ObjectQuery($"SELECT * FROM MSFT_Partition WHERE DiskNumber = {diskNumber}"));
            foreach (ManagementBaseObject partition in partitions.Get())
            {
                write($"分区对象：#{partition["PartitionNumber"]} 偏移={partition["Offset"]} 大小={partition["Size"]} 盘符={partition["DriveLetter"]}");
            }

            using ManagementObjectSearcher volumes = new(scope, new ObjectQuery("SELECT * FROM MSFT_Volume"));
            foreach (ManagementBaseObject volume in volumes.Get())
            {
                string path = volume["Path"]?.ToString() ?? string.Empty;
                if (path.Length == 0)
                {
                    continue;
                }

                ulong size = Convert.ToUInt64(volume["Size"] ?? 0UL);
                write(
                    $"全部卷对象：{(volume["DriveLetter"] is null ? "-" : volume["DriveLetter"])} 文件系统={volume["FileSystem"]} " +
                    $"大小={SizeFormatter.Format((long)size)} Path={path}");
            }
        }
        catch (Exception ex)
        {
            write($"查询分区/卷对象失败：{ex.Message}");
        }
    }

    private static char FindUnusedLetter(HashSet<char> used)
    {
        for (char letter = 'Y'; letter >= 'D'; letter--)
        {
            if (!used.Contains(letter))
            {
                return letter;
            }
        }

        return 'Z';
    }

    /// <summary>打印存储管理接口看到的磁盘状态。</summary>
    public static void DumpWmiDiskFor(int diskNumber, Action<string> write) => DumpWmiDisk(diskNumber, write);

    private static void DumpWmiDisk(int diskNumber, Action<string> write)
    {
        try
        {
            ManagementScope scope = new(@"\\.\root\Microsoft\Windows\Storage");
            scope.Connect();
            using ManagementObjectSearcher searcher = new(scope, new ObjectQuery($"SELECT * FROM MSFT_Disk WHERE Number = {diskNumber}"));

            foreach (ManagementBaseObject item in searcher.Get())
            {
                write(
                    $"WMI 视图：PartitionStyle={item["PartitionStyle"]} IsOffline={item["IsOffline"]} IsReadOnly={item["IsReadOnly"]} " +
                    $"NumberOfPartitions={item["NumberOfPartitions"]} LargestFreeExtent={item["LargestFreeExtent"]} " +
                    $"OperationalStatus={string.Join("/", (ushort[])item["OperationalStatus"])} Size={item["Size"]}");
            }
        }
        catch (Exception ex)
        {
            write($"WMI 查询失败：{ex.Message}");
        }
    }
}
