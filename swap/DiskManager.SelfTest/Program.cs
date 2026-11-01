using System.IO;
using System.Text;
using DiskManager.Interop;
using DiskManager.Models;
using DiskManager.Services;

namespace DiskManager.SelfTest;

/// <summary>
/// 自测入口：创建测试用 VHDX → 在它上面把全部写操作跑一遍 → 卸载。
/// 全程只动自己挂上来的那块虚拟磁盘，现成的物理磁盘只读、只记录编号。
/// </summary>
internal static class Program
{
    private static readonly List<string> Log = [];

    private static int _passed;
    private static int _failed;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        string workspace = args.Length > 0
            ? args[0]
            : @"C:\Users\AZURE\Documents\ChatGPT\swap";

        string testRoot = Path.Combine(workspace, "TestDisks");
        Directory.CreateDirectory(testRoot);
        string vhdxPath = Path.Combine(testRoot, "dm-selftest.vhdx");
        string logPath = Path.Combine(testRoot, "selftest.log");

        // 每次自测都用全新的虚拟磁盘，保证"未初始化状态"这一项检查有意义。
        // 旧文件按本机约定移动到 BACKUP\DEL，不做删除。
        ArchiveOldTestDisks(testRoot);

        Write($"磁盘管理器自测开始：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Write($"管理员权限：{(Elevation.IsElevated ? "是" : "否")}");
        Write($"测试磁盘文件：{vhdxPath}");

        if (args.Contains("--diagnose"))
        {
            Diagnostics.Run(workspace, vhdxPath, Write);
            SaveLog(logPath);
            Write($"诊断日志已保存：{logPath}");
            return 0;
        }

        // 纯界面逻辑回归测试，不需要管理员权限（不碰任何磁盘）。
        if (args.Contains("--dialogs"))
        {
            Write("===== 对话框线程安全回归测试 =====");
            int dialogResult = DialogThreadTests.Run(Write);
            Write($"结果：{(dialogResult == 0 ? "通过" : "失败")}");
            SaveLog(Path.Combine(testRoot, "dialogs-test.log"));
            return dialogResult;
        }

        // 设备枚举诊断，不需要管理员权限。
        if (args.Contains("--devices"))
        {
            DeviceProbe.Run(Write);
            SaveLog(Path.Combine(testRoot, "devices.log"));
            Write($"日志：{Path.Combine(testRoot, "devices.log")}");
            return 0;
        }

        // 虚拟磁盘文件诊断，不需要管理员权限。
        if (args.Contains("--vhd"))
        {
            VhdProbe.Run(Write);
            SaveLog(Path.Combine(testRoot, "vhd.log"));
            Write($"日志：{Path.Combine(testRoot, "vhd.log")}");
            return 0;
        }

        // 端到端复现"更改驱动器号"流程（需要管理员权限，只在自建测试盘上操作）。
        if (args.Contains("--driveletter"))
        {
            Write("===== 更改驱动器号 端到端测试 =====");
            int flowResult = DriveLetterFlowTest.Run(vhdxPath, Write);
            SaveLog(Path.Combine(testRoot, "driveletter-test.log"));
            return flowResult;
        }

        if (!Elevation.IsElevated)
        {
            Write("× 未以管理员身份运行，无法执行任何磁盘操作，测试终止。");
            SaveLog(logPath);
            return 2;
        }

        // ---------- 记录测试前就存在的磁盘，后续只允许操作"新增的那一块" ----------
        InventorySnapshot before = DiskInventory.Capture();
        HashSet<int> existingDisks = [.. before.Disks.Select(d => d.Number)];
        Write($"测试前已存在的磁盘：{string.Join(", ", existingDisks.OrderBy(n => n))}（本次测试不会修改它们）");

        if (!VhdHelper.TryCreateAndAttach(vhdxPath, out string vhdError, out string vhdOutput))
        {
            Write($"× {vhdError}");
            SaveLog(logPath);
            return 3;
        }

        Write($"VHD 命令输出：{vhdOutput.Replace('\r', ' ').Replace('\n', ' ').Trim()}");
        Write("已创建并挂载测试 VHDX，等待系统识别…");
        Thread.Sleep(3000);

        int exitCode = 0;
        try
        {
            // 卷管理器有时要几秒才会把新磁盘暴露出来，最多等 20 秒。
            InventorySnapshot after = DiskInventory.Capture();
            DiskItem? testDisk = null;
            for (int attempt = 0; attempt < 10 && testDisk is null; attempt++)
            {
                after = DiskInventory.Capture();
                testDisk = after.Disks.FirstOrDefault(d => !existingDisks.Contains(d.Number));
                if (testDisk is null)
                {
                    Thread.Sleep(2000);
                }
            }

            Write($"枚举到的磁盘：{string.Join("，", after.Disks.Select(d => $"[{d.Number}] {d.FriendlyName} {d.CapacityText} 总线={d.BusTypeName} 分区={d.Partitions.Count}"))}");
            foreach (string warning in after.Warnings)
            {
                Write($"  警告：{warning}");
            }

            if (testDisk is null)
            {
                Write("× 没有发现新挂载的测试磁盘，测试终止。");
                SaveLog(logPath);
                return 4;
            }

            if (!SafetyCheck(testDisk, out string reason))
            {
                Write($"× 安全检查未通过：{reason}");
                Write("  为避免影响真实磁盘，测试立即终止，未执行任何写操作。");
                SaveLog(logPath);
                return 5;
            }

            Write($"√ 已锁定测试磁盘：磁盘 {testDisk.Number}（{testDisk.FriendlyName}，{testDisk.CapacityText}，总线 {testDisk.BusTypeName}）");

            RunAllTests(testDisk, after, vhdxPath);
        }
        catch (Exception ex)
        {
            Write($"× 测试过程中出现异常：{ex}");
            _failed++;
            exitCode = 6;
        }
        finally
        {
            if (VhdHelper.TryDetach(vhdxPath, out string detachError, out string detachOutput))
            {
                Write($"√ 测试 VHDX 已卸载（文件保留，未删除）：{detachOutput.Trim()}");
            }
            else
            {
                Write($"× {detachError}");
                _failed++;
            }

            Write($"\n测试结果：通过 {_passed} 项，失败 {_failed} 项");
            SaveLog(logPath);
            Write($"日志已保存：{logPath}");
        }

        return _failed == 0 ? exitCode : 1;
    }

    /// <summary>三道保险，全部通过才允许对这块磁盘做写操作。</summary>
    private static bool SafetyCheck(DiskItem disk, out string reason)
    {
        if (disk.BusTypeName is not ("文件支持的虚拟磁盘" or "虚拟"))
        {
            reason = $"总线类型为 {disk.BusTypeName}，不是虚拟磁盘。";
            return false;
        }

        long expected = VhdHelper.TestDiskSizeMb * 1024L * 1024L;
        if (Math.Abs(disk.Size - expected) > 64L * 1024 * 1024)
        {
            reason = $"容量 {disk.CapacityText} 与测试盘设定（{VhdHelper.TestDiskSizeMb} MB）不符。";
            return false;
        }

        if (disk.IsSystemDisk || disk.IsBootDisk)
        {
            reason = "该磁盘包含系统卷或启动卷。";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>按顺序跑完所有功能项。</summary>
    private static void RunAllTests(DiskItem testDisk, InventorySnapshot initial, string vhdxPath)
    {
        DumpInventory($"自测开始时（磁盘 {testDisk.Number}）");

        Write("\n===== 1. 初始状态 =====");
        Check("测试磁盘初始为未初始化状态", !testDisk.IsInitialized, $"当前状态：{testDisk.StatusText}");
        Check("测试磁盘初始为联机状态", !testDisk.IsOffline, $"IsOffline={testDisk.IsOffline}");

        Write("\n===== 2. 原生 IOCTL：脱机 / 联机 =====");
        OperationResult guardOnTestDisk = DiskOperations.CheckOfflineSafety(testDisk.Number);
        Check("安全闸门允许脱机测试盘", guardOnTestDisk.Success, guardOnTestDisk.Message);

        OperationResult offline = DiskOperations.SetOffline(testDisk.Number, offline: true);
        Check("脱机操作调用成功", offline.Success, offline.Message);
        Thread.Sleep(1500);

        DiskItem? reloaded = ReloadDisk(testDisk.Number);
        Check("脱机后属性位已置为脱机", reloaded is { IsOffline: true }, $"IsOffline={reloaded?.IsOffline}");

        OperationResult online = DiskOperations.SetOffline(testDisk.Number, offline: false);
        Check("联机操作调用成功", online.Success, online.Message);
        Thread.Sleep(1500);

        reloaded = ReloadDisk(testDisk.Number);
        Check("联机后磁盘恢复联机", reloaded is { IsOffline: false }, $"IsOffline={reloaded?.IsOffline}");

        Write("\n===== 3. 磁盘只读属性 =====");
        Check("标记为只读成功", DiskOperations.SetReadOnly(testDisk.Number, true).Success);
        Thread.Sleep(800);
        Check("只读属性已生效", ReloadDisk(testDisk.Number)?.IsReadOnly == true);
        Check("清除只读成功", DiskOperations.SetReadOnly(testDisk.Number, false).Success);
        Thread.Sleep(800);
        Check("只读属性已清除", ReloadDisk(testDisk.Number)?.IsReadOnly == false);

        Write("\n===== 4. 初始化磁盘（GPT） =====");
        OperationResult init = DiskOperations.Initialize(testDisk.Number, PartitionStyle.Gpt);
        Check("初始化 GPT 成功", init.Success, init.Message);
        Thread.Sleep(1500);
        Check("分区表类型为 GPT", ReloadDisk(testDisk.Number)?.Style == PartitionStyle.Gpt);

        Write("\n===== 5. 新建简单卷（含格式化） =====");
        DiskItem fresh = ReloadDisk(testDisk.Number) ?? testDisk;
        char letter = FindFreeLetter();
        long volumeSize = 512L * 1024 * 1024;

        OperationResult create = VolumeOperations.CreateSimpleVolume(
            fresh,
            volumeSize,
            offset: null,
            letter,
            "NTFS",
            "DMSELFTEST",
            quickFormat: true,
            allocationUnitSize: 0);

        Check("创建并格式化新卷成功", create.Success, create.Message);
        Thread.Sleep(2000);

        VolumeItem? testVolume = FindVolumeByLetter(letter);
        Check("新卷出现在卷列表中", testVolume is not null);
        Check("新卷文件系统为 NTFS", testVolume?.FileSystem == "NTFS", $"实际：{testVolume?.FileSystem}");
        Check("新卷标签为 DMSELFTEST", testVolume?.Label == "DMSELFTEST", $"实际：{testVolume?.Label}");
        Check(
            "新卷容量符合预期",
            testVolume is not null && Math.Abs(testVolume.Size - volumeSize) < 64L * 1024 * 1024,
            $"实际：{testVolume?.CapacityText}");

        DumpInventory("创建新卷之后");

        if (testVolume is null)
        {
            Write("× 未找到新建的卷，跳过后续卷级测试。");
            _failed++;
            return;
        }

        Write("\n===== 6. 写入文件验证卷可用 =====");
        string probeFile = $"{letter}:\\dm-selftest.txt";
        bool written = TryWriteProbeFile(probeFile, out string writeError);
        Check("可以在新卷上创建文件", written, writeError);

        Write("\n===== 7. 扩展卷 =====");
        DiskItem diskWithVolume = ReloadDisk(testDisk.Number)!;
        VolumeItem current = FindVolumeByLetter(letter)!;
        long extension = 256L * 1024 * 1024;
        long extendTarget = current.Size + extension;

        OperationResult extend = VolumeOperations.ExtendVolume(current, extendTarget);
        Check("扩展卷调用成功", extend.Success, extend.Message);
        Thread.Sleep(2000);

        current = FindVolumeByLetter(letter) ?? current;
        Check("扩展后容量增大", current.Size > volumeSize, $"扩展后：{current.CapacityText}");

        Write("\n===== 8. 压缩卷 =====");
        current = FindVolumeByLetter(letter)!;
        long shrinkTarget = current.Size - (128L * 1024 * 1024);
        OperationResult shrink = VolumeOperations.ShrinkVolume(current, shrinkTarget);
        Check("压缩卷调用成功", shrink.Success, shrink.Message);
        Thread.Sleep(2000);

        VolumeItem afterShrink = FindVolumeByLetter(letter) ?? current;
        Check("压缩后容量减小", afterShrink.Size < current.Size, $"压缩后：{afterShrink.CapacityText}");

        Write("\n===== 9. 更改驱动器号 =====");
        char newLetter = FindFreeLetter();
        OperationResult rename = VolumeOperations.ChangeDriveLetter(
            afterShrink,
            ChangeLetterMode.Assign,
            newLetter,
            string.Empty);
        Check("分配新驱动器号成功", rename.Success, rename.Message);
        Thread.Sleep(1500);

        afterShrink = FindVolumeByLetter(letter) ?? afterShrink;
        VolumeItem? renamed = FindVolumeByLetter(newLetter);
        Check("新驱动器号已生效", renamed is not null, $"查找 {newLetter}: 结果：{renamed?.DisplayName}");

        OperationResult removeLetter = VolumeOperations.ChangeDriveLetter(
            renamed ?? afterShrink,
            ChangeLetterMode.Remove,
            null,
            string.Empty);
        Check("删除驱动器号成功", removeLetter.Success, removeLetter.Message);
        Thread.Sleep(1200);

        Write("\n===== 10. 卷在脱机磁盘上的行为 =====");
        Check("再次脱机成功", DiskOperations.SetOffline(testDisk.Number, true).Success);
        Thread.Sleep(1500);
        InventorySnapshot offlineSnapshot = DiskInventory.Capture();
        DiskItem? offlineDisk = offlineSnapshot.Disks.FirstOrDefault(d => d.Number == testDisk.Number);
        Check("磁盘图中显示为脱机", offlineDisk?.IsOffline == true);
        Check("再次联机成功", DiskOperations.SetOffline(testDisk.Number, false).Success);
        Thread.Sleep(1500);

        Write("\n===== 11. 删除卷 =====");
        VolumeItem? toDelete = VolumeItems().FirstOrDefault(v => v.DiskNumbers.Contains(testDisk.Number));
        if (toDelete is null)
        {
            Check("删除卷前能找到目标卷", false, "卷列表中找不到测试磁盘上的卷");
        }
        else
        {
            OperationResult deleteResult = VolumeOperations.DeleteVolume(toDelete, forceRemoveMountPoints: true);
            Check("删除卷成功", deleteResult.Success, deleteResult.Message);
            Thread.Sleep(2000);

            DiskItem? afterDelete = ReloadDisk(testDisk.Number);
            Check("删除后分区数为 0", afterDelete?.Partitions.Count == 0, $"剩余分区：{afterDelete?.Partitions.Count}");
        }

        Write("\n===== 12. 转换为 MBR 磁盘 =====");
        OperationResult convert = DiskOperations.ConvertStyle(testDisk.Number, PartitionStyle.Mbr);
        Check("转换为 MBR 成功", convert.Success, convert.Message);
        Thread.Sleep(1500);
        Check("分区表类型为 MBR", ReloadDisk(testDisk.Number)?.Style == PartitionStyle.Mbr);
        DumpRawView(testDisk.Number);

        Write("\n===== 13. 清除磁盘 =====");
        OperationResult clear = DiskOperations.ClearDisk(testDisk.Number, zeroOut: false);
        Check("清除磁盘成功", clear.Success, clear.Message);
        Thread.Sleep(1500);

        Write("\n===== 14. MBR 磁盘上再建一次卷（覆盖另一条代码路径） =====");
        DiskItem mbrDisk = ReloadDisk(testDisk.Number) ?? testDisk;
        if (!mbrDisk.IsInitialized)
        {
            Check("重新初始化 MBR", DiskOperations.Initialize(testDisk.Number, PartitionStyle.Mbr).Success);
            Thread.Sleep(1200);
            mbrDisk = ReloadDisk(testDisk.Number) ?? mbrDisk;
        }

        char secondLetter = FindFreeLetter();
        OperationResult mbrCreate = VolumeOperations.CreateSimpleVolume(
            mbrDisk,
            256L * 1024 * 1024,
            offset: null,
            secondLetter,
            "NTFS",
            "DMSELFTEST2",
            quickFormat: true,
            allocationUnitSize: 0);
        Check("MBR 磁盘上创建卷成功", mbrCreate.Success, mbrCreate.Message);
        Thread.Sleep(2000);
        Check("MBR 卷可访问", FindVolumeByLetter(secondLetter) is not null);

        Write("\n===== 15. 未分配空间统计 =====");
        DiskItem finalDisk = ReloadDisk(testDisk.Number) ?? mbrDisk;
        long used = finalDisk.Partitions.Sum(p => p.Length);
        Check(
            "分区总长度不超过磁盘容量",
            used <= finalDisk.Size,
            $"分区合计 {SizeFormatter.Format(used)} / 磁盘 {finalDisk.CapacityText}");

        _ = vhdxPath;

        // ---------- 收尾说明 ----------
        Write("\n提示：测试结束后仍会残留一个测试卷，属于预期行为（测试盘随 VHDX 一起卸载）。");
    }

    /// <summary>写入一个探针文件，验证卷真的可读写。</summary>
    private static bool TryWriteProbeFile(string path, out string error)
    {
        try
        {
            File.WriteAllText(path, "disk manager self test", Encoding.UTF8);
            string content = File.ReadAllText(path);
            error = string.Empty;
            return content.Contains("self test", StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static DiskItem? ReloadDisk(int number)
    {
        InventorySnapshot snapshot = DiskInventory.Capture();
        return snapshot.Disks.FirstOrDefault(d => d.Number == number);
    }

    private static List<VolumeItem> VolumeItems() => [.. DiskInventory.Capture().Volumes];

    private static VolumeItem? FindVolumeByLetter(char letter)
        => VolumeItems().FirstOrDefault(v => v.MountPoints.Any(m => m.Length >= 2 && char.ToUpperInvariant(m[0]) == char.ToUpperInvariant(letter)));

    /// <summary>找一个当前空闲、且不常用的盘符（优先靠后，避免干扰真实盘符）。</summary>
    private static char FindFreeLetter()
    {
        HashSet<char> used = VolumeDevice.GetUsedDriveLetters();
        for (char letter = 'Y'; letter >= 'D'; letter--)
        {
            if (!used.Contains(letter))
            {
                return letter;
            }
        }

        return 'Z';
    }

    private static void Check(string name, bool condition, string detail = "")
    {
        if (condition)
        {
            _passed++;
            Write($"  √ {name}{(detail.Length > 0 ? $"　[{detail}]" : string.Empty)}");
        }
        else
        {
            _failed++;
            Write($"  × {name}{(detail.Length > 0 ? $"　[{detail}]" : string.Empty)}");
        }
    }

    /// <summary>把磁盘/分区/卷的对应关系完整打出来，便于定位匹配问题。</summary>
    private static void DumpInventory(string title)
    {
        Write($"\n--- 清单转储：{title} ---");
        InventorySnapshot snapshot = DiskInventory.Capture();

        foreach (DiskItem disk in snapshot.Disks)
        {
            Write($"  磁盘 {disk.Number}：样式={disk.Style} 容量={disk.CapacityText} 分区数={disk.Partitions.Count}");
            foreach (PartitionItem partition in disk.Partitions)
            {
                Write(
                    $"    分区 #{partition.Number} 偏移={partition.Offset} 长度={partition.Length} " +
                    $"类型={partition.TypeName} 对应卷={(partition.Volume?.DisplayName ?? "（无）")}");
            }
        }

        foreach (VolumeItem volume in snapshot.Volumes)
        {
            Write(
                $"  卷：{volume.DisplayName} 文件系统={volume.FileSystem} 容量={volume.CapacityText} " +
                $"磁盘=[{string.Join(",", volume.DiskNumbers)}] 分区数={volume.Partitions.Count} " +
                $"挂载点=[{string.Join(",", volume.MountPoints)}]");
        }

        foreach (string warning in snapshot.Warnings)
        {
            Write($"  警告：{warning}");
        }
    }

    /// <summary>打印原生视图与 0 号扇区签名，用于核对"未初始化"判定。</summary>
    private static void DumpRawView(int diskNumber)
    {
        DiskItem? disk = ReloadDisk(diskNumber);
        Write($"  [原始视图] 样式={disk?.Style} MBR签名=0x{disk?.MbrSignature:X8} 分区数={disk?.Partitions.Count}");

        using DiskHandle? handle = DiskHandle.TryOpen(diskNumber, writeAccess: false, out _);
        if (handle is not null && handle.TryReadMbrSignature(out bool hasSignature, out _))
        {
            Write($"  [0 号扇区] 0x55AA 结束标志={(hasSignature ? "有" : "无")}");
        }

        Diagnostics.DumpWmiDiskFor(diskNumber, Write);
    }

    private static void Write(string line)
    {
        Console.WriteLine(line);
        Log.Add(line);
    }

    private static void SaveLog(string path)
    {
        try
        {
            File.WriteAllLines(path, Log, new UTF8Encoding(true));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"日志写入失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 把上一轮遗留的测试 VHDX 移到 BACKUP\DEL（本机约定：不删除文件）。
    /// 仍被系统占用的文件会移动失败，忽略即可。
    /// </summary>
    private static void ArchiveOldTestDisks(string testRoot)
    {
        try
        {
            string backupDir = Path.Combine(testRoot, "BACKUP", "DEL");
            Directory.CreateDirectory(backupDir);

            foreach (string file in Directory.GetFiles(testRoot, "*.vhdx"))
            {
                try
                {
                    File.Move(file, Path.Combine(backupDir, Path.GetFileName(file)), overwrite: true);
                }
                catch
                {
                    // 文件仍被挂载或占用，跳过
                }
            }
        }
        catch
        {
            // 归档失败不影响测试
        }
    }
}
