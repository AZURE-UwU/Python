using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using DiskManager.Models;
using DiskManager.SelfTest;
using DiskManager.Services;

namespace DiskOfflineTool;

/// <summary>
/// 自检模式（DiskOfflineTool.exe &lt;工作目录&gt; --selftest）：
/// 在自建的测试 VHDX 上验证"列磁盘 → 脱机 → 联机"这条工具的核心流程。
///
/// 安全约束：只操作"测试前不存在 + 总线为虚拟 + 容量与设定值一致 + 非系统盘"的那块磁盘，
/// 任一不符立即中止，不做任何写操作。
/// </summary>
internal static class SelfTestMode
{
    private static readonly List<string> Log = [];

    private static int _passed;
    private static int _failed;
    private static bool _testDiskEjected;

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    public static int Run(string[] args)
    {
        AttachConsole(-1); // 从命令行启动时把输出接回父控制台

        string workspace = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))
            ?? @"C:\Users\AZURE\Documents\ChatGPT\swap";

        string testRoot = Path.Combine(workspace, "TestDisks");
        Directory.CreateDirectory(testRoot);
        string vhdxPath = Path.Combine(testRoot, "offline-tool-test.vhdx");
        string logPath = Path.Combine(testRoot, "offline-tool-selftest.log");

        Write($"磁盘脱机工具自检：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Write($"管理员权限：{(Elevation.IsElevated ? "是" : "否")}");

        if (!Elevation.IsElevated)
        {
            Write("× 未提权，无法执行自检。");
            return Finish(logPath);
        }

        try
        {
            // ---------- 1. 列出磁盘 ----------
            Write("\n===== 1. 磁盘列表 =====");
            IReadOnlyList<DiskItem> disks = DiskListService.Capture(out IReadOnlyList<string> warnings);
            Check("能列出磁盘", disks.Count > 0, $"共 {disks.Count} 块");

            foreach (DiskItem disk in disks)
            {
                Write(
                    $"  磁盘 {disk.Number}｜{disk.FriendlyName}｜{disk.BusTypeName}｜{disk.CapacityText}" +
                    $"｜分区 {disk.Partitions.Count}｜{disk.StatusText}｜{disk.StyleText}");
            }

            foreach (string warning in warnings)
            {
                Write($"  警告：{warning}");
            }

            HashSet<int> existing = [.. disks.Select(d => d.Number)];

            // ---------- 2. 准备测试盘 ----------
            Write("\n===== 2. 准备测试磁盘 =====");
            VhdHelper.TryDetach(vhdxPath, out _, out _);
            Thread.Sleep(1000);

            if (!VhdHelper.TryCreateAndAttach(vhdxPath, out string mountError, out string mountOutput))
            {
                Write($"× 挂载测试 VHDX 失败：{mountError}");
                _failed++;
                return Finish(logPath);
            }

            Write($"  VHD 命令输出：{mountOutput.Replace('\r', ' ').Replace('\n', ' ').Trim()}");
            Thread.Sleep(4000);

            DiskItem? testDisk = DiskListService.Capture(out _).FirstOrDefault(d => !existing.Contains(d.Number));
            Check("发现新挂载的测试磁盘", testDisk is not null);

            if (testDisk is null)
            {
                return Finish(logPath);
            }

            bool virtualBus = testDisk.BusTypeName.Contains("虚拟", StringComparison.Ordinal);
            long expected = VhdHelper.TestDiskSizeMb * 1024L * 1024L;
            bool sizeOk = Math.Abs(testDisk.Size - expected) < 64L * 1024 * 1024;
            bool notSystem = !testDisk.IsSystemDisk && !testDisk.IsBootDisk;

            Check("安全锁：虚拟总线", virtualBus, testDisk.BusTypeName);
            Check("安全锁：容量与测试盘一致", sizeOk, testDisk.CapacityText);
            Check("安全锁：非系统/启动磁盘", notSystem);

            if (!virtualBus || !sizeOk || !notSystem)
            {
                Write("× 安全检查未通过，已中止，未做任何写操作。");
                return Finish(logPath);
            }

            int number = testDisk.Number;
            Check("测试磁盘初始为联机", !testDisk.IsOffline);

            // 安全闸门必须先放行测试盘，再拦下系统盘 —— 这里只做判断，不真的执行脱机。
            OperationResult guardOnTestDisk = DiskOperations.CheckOfflineSafety(number);
            Check("安全闸门允许脱机测试盘", guardOnTestDisk.Success, guardOnTestDisk.Message);

            // ---------- 3. 脱机 ----------
            Write("\n===== 3. 执行脱机 =====");
            OperationResult offline = DiskListService.SetOffline(number, offline: true);
            Check("脱机调用成功", offline.Success, offline.Message);
            Thread.Sleep(1500);

            DiskItem? afterOffline = Reload(number);
            Check("列表显示为脱机", afterOffline?.IsOffline == true, $"状态：{afterOffline?.StatusText}");

            // ---------- 4. 联机 ----------
            Write("\n===== 4. 执行联机 =====");
            OperationResult online = DiskListService.SetOffline(number, offline: false);
            Check("联机调用成功", online.Success, online.Message);
            Thread.Sleep(1500);

            DiskItem? afterOnline = Reload(number);
            Check("列表显示为联机", afterOnline?.IsOffline == false, $"状态：{afterOnline?.StatusText}");

            // ---------- 5. 系统盘保护 ----------
            Write("\n===== 5. 系统盘保护 =====");
            DiskItem? systemDisk = DiskListService.Capture(out _).FirstOrDefault(d => d.IsSystemDisk || d.IsBootDisk);
            if (systemDisk is null)
            {
                Write("  （本机未识别到系统盘，跳过此项）");
            }
            else
            {
                Check("系统盘被标记为不可脱机", !DiskListService.CanOffline(systemDisk), $"磁盘 {systemDisk.Number}");

                OperationResult refused = DiskOperations.CheckOfflineSafety(systemDisk.Number);
                Check("系统盘被安全闸门拒绝脱机", !refused.Success, refused.Message);
            }

            // ---------- 6. 安全闸门对每块磁盘的判定（只判断，不执行） ----------
            Write("\n===== 6. 安全闸门总览 =====");
            int refusedCount = 0;
            int allowedCount = 0;
            int pageFileDiskNumber = -1;

            foreach (DiskItem disk in DiskListService.Capture(out _))
            {
                OperationResult verdict = DiskOperations.CheckOfflineSafety(disk.Number);
                Write($"  磁盘 {disk.Number}（{disk.FriendlyName}）：{(verdict.Success ? "允许脱机" : $"拒绝 —— {verdict.Message}")}");

                if (verdict.Success)
                {
                    allowedCount++;
                }
                else
                {
                    refusedCount++;
                    if (verdict.Message.Contains("分页文件", StringComparison.Ordinal))
                    {
                        pageFileDiskNumber = disk.Number;
                    }
                }
            }

            Check("至少拦下一块不可脱机的磁盘", refusedCount > 0, $"拒绝 {refusedCount} 块，允许 {allowedCount} 块");

            if (pageFileDiskNumber >= 0)
            {
                Check("承载分页文件的磁盘被拒绝", true, $"磁盘 {pageFileDiskNumber}");
            }

            // ---------- 7. 弹出能力（只做判断；真正弹出只在不支持时被拒绝，不会误弹） ----------
            Write("\n===== 7. 弹出能力检查 =====");
            foreach (DiskItem disk in DiskListService.Capture(out _))
            {
                string state = disk.SupportsEject
                    ? "支持弹出"
                    : $"不支持弹出（{disk.EjectBlockReason}）";

                Write($"  磁盘 {disk.Number}（{disk.FriendlyName}）：能力位 0x{disk.Capabilities:X8}　{state}");

                if (!disk.SupportsEject)
                {
                    // 设备不支持安全移除时，弹出必须先被拒绝（这条路径不会触发真正的移除）。
                    OperationResult refused = DiskOperations.Eject(disk.Number);
                    Check($"磁盘 {disk.Number} 的弹出请求被拒绝", !refused.Success, refused.Message);
                }
            }

            // ---------- 8. 真正弹出一次：拿测试盘做靶子，弹完它应该从列表里消失 ----------
            Write("\n===== 8. 弹出实测（用测试盘）=====");
            DiskItem? ejectTarget = DiskListService.Capture(out _).FirstOrDefault(d => d.Number == number);

            if (ejectTarget is null)
            {
                Write("  （测试盘已不在列表中，跳过）");
            }
            else if (!ejectTarget.CanEject)
            {
                Write($"  （测试盘不支持弹出：{ejectTarget.EjectBlockReason}，跳过实测）");
            }
            else
            {
                Write(
                    $"  测试盘能力位 0x{ejectTarget.Capabilities:X8}，" +
                    $"类型={(ejectTarget.IsVirtualDisk ? "虚拟磁盘（走分离）" : "物理设备（走弹出）")}，准备移除…");

                // 自检直接给出宿主文件路径，不依赖自动扫描（测试盘目录较深）
                // 自检无人值守：直接"确认过"要先分离挡路的虚拟磁盘，并把宿主文件路径交给它
                BlockResolutionOptions options = new()
                {
                    DetachBlockingVirtualDiskConfirmed = true,
                    PreResolvedVirtualDiskFile = vhdxPath,
                };

                OperationResult ejected = DiskOperations.RemoveDisk(ejectTarget, options);
                Thread.Sleep(2500);

                if (ejected.Success)
                {
                    // 移除成功：磁盘应从列表中消失
                    bool disappeared = DiskListService.Capture(out _).All(d => d.Number != number);
                    Check($"{ejectTarget.EjectActionName}测试盘成功，且已从磁盘列表消失", disappeared, ejected.Message);
                    _testDiskEjected = disappeared;
                }
                else
                {
                    Check("移除失败时给出了可读原因", ejected.Message.Length > 0, ejected.Message);

                    bool stillThere = DiskListService.Capture(out _).Any(d => d.Number == number);
                    Check("被否决后测试盘仍正常在列表中（未被破坏）", stillThere);
                }
            }
        }
        catch (Exception ex)
        {
            _failed++;
            Write($"× 自检异常：{ex}");
        }
        finally
        {
            if (_testDiskEjected)
            {
                // 第 8 步已经通过"弹出"把测试盘移除了，不需要再卸载一次。
                Write("\n测试 VHDX 已通过弹出移除（文件保留，未删除）。");
            }
            else if (VhdHelper.TryDetach(vhdxPath, out string detachError, out _))
            {
                Write("\n测试 VHDX 已卸载（文件保留，未删除）。");
            }
            else
            {
                _failed++;
                Write($"\n× 卸载测试 VHDX 失败：{detachError}");
            }
        }

        return Finish(logPath);
    }

    private static DiskItem? Reload(int diskNumber)
        => DiskListService.Capture(out _).FirstOrDefault(d => d.Number == diskNumber);

    private static void Check(string name, bool condition, string detail = "")
    {
        string suffix = detail.Length > 0 ? $"　[{detail}]" : string.Empty;
        if (condition)
        {
            _passed++;
            Write($"  √ {name}{suffix}");
        }
        else
        {
            _failed++;
            Write($"  × {name}{suffix}");
        }
    }

    private static void Write(string line)
    {
        Console.WriteLine(line);
        Log.Add(line);
    }

    private static int Finish(string logPath)
    {
        Write($"\n自检结果：通过 {_passed} 项，失败 {_failed} 项");

        try
        {
            File.WriteAllLines(logPath, Log, new UTF8Encoding(true));
            Write($"日志：{logPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"日志写入失败：{ex.Message}");
        }

        return _failed == 0 ? 0 : 1;
    }
}
