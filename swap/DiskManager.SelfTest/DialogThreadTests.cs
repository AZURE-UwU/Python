using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using DiskManager.Dialogs;
using DiskManager.Interop;
using DiskManager.Models;
using DiskManager.Services;

namespace DiskManager.SelfTest;

/// <summary>
/// 回归测试：对话框的结果属性必须能在后台线程安全读取。
///
/// 背景：写操作是在后台线程执行的，如果对话框的属性实现成"去读 TextBox.Text / CheckBox.IsChecked"，
/// WPF 会抛「调用线程无法访问此对象，因为另一个线程拥有该对象」。
/// 本测试在界面线程创建对话框并触发校验（拍快照），再从后台线程读全部结果属性，
/// 任何一次读取抛异常都说明该对话框又被写回"直接读控件"了。
///
/// 该测试不需要管理员权限。
/// </summary>
internal static class DialogThreadTests
{
    public static int Run(Action<string> write)
    {
        int passed = 0;
        int failed = 0;

        Thread staThread = new(() =>
        {
            try
            {
                RunOnUiThread(write, ref passed, ref failed);
            }
            catch (Exception ex)
            {
                failed++;
                write($"  × 测试自身异常：{ex.GetType().Name}: {ex.Message}");
            }
        });

        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();
        staThread.Join();

        return failed == 0 && passed > 0 ? 0 : 1;
    }

    /// <summary>全部在界面线程（STA）上执行：创建对话框并触发校验拍快照。</summary>
    private static void RunOnUiThread(Action<string> write, ref int passed, ref int failed)
    {
        // WPF 要求 Owner 必须是"显示过"的窗口。测试时把它显示在屏幕外，
        // 既不打扰用户，又能满足这个约束。
        Window owner = new()
        {
            Title = "测试用宿主窗口",
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            ShowInTaskbar = false,
        };

        owner.Show();
        owner.Hide();

        {
            DiskItem disk = new()
            {
                Number = 99,
                FriendlyName = "测试磁盘",
                Size = 2048L * 1024 * 1024,
                Style = PartitionStyle.Gpt,
                BusTypeName = "文件支持的虚拟磁盘",
            };

            VolumeItem volume = new()
            {
                VolumeName = @"\\?\Volume{11111111-2222-3333-4444-555555555555}\",
                MountPoints = [@"Y:\"],
                LetterText = "Y:",
                Label = "TEST",
                FileSystem = "NTFS",
                Size = 512L * 1024 * 1024,
                FreeSpace = 256L * 1024 * 1024,
                IsAccessible = true,
                State = VolumeState.Healthy,
                TypeName = "简单",
                LayoutText = "简单",
                DiskNumbers = [99],
            };

            List<(string Name, Func<object?> Read)> cases = [];

            NewVolumeDialog newVolume = new(owner, disk, 1024L * 1024 * 1024);
            InvokeValidate(newVolume);
            cases.Add(("新建简单卷.RequestedSize", () => newVolume.RequestedSize));
            cases.Add(("新建简单卷.DriveLetter", () => newVolume.DriveLetter));
            cases.Add(("新建简单卷.FileSystem", () => newVolume.FileSystem));
            cases.Add(("新建简单卷.VolumeLabel", () => newVolume.VolumeLabel));
            cases.Add(("新建简单卷.QuickFormat", () => newVolume.QuickFormat));

            FormatVolumeDialog format = new(owner, volume, ["默认", "4096 字节"]);
            InvokeValidate(format);
            cases.Add(("格式化.VolumeLabel", () => format.VolumeLabel));
            cases.Add(("格式化.FileSystem", () => format.FileSystem));
            cases.Add(("格式化.QuickFormat", () => format.QuickFormat));
            cases.Add(("格式化.AllocationUnitSize", () => format.AllocationUnitSize));

            ChangeDriveLetterDialog changeLetter = new(owner, volume);
            InvokeValidate(changeLetter);
            cases.Add(("更改驱动器号.Mode", () => changeLetter.Mode));
            cases.Add(("更改驱动器号.DriveLetter", () => changeLetter.DriveLetter));
            cases.Add(("更改驱动器号.FolderPath", () => changeLetter.FolderPath));

            ExtendVolumeDialog extend = new(owner, volume, 1024L * 1024 * 1024);
            InvokeValidate(extend);
            cases.Add(("扩展卷.NewSize", () => extend.NewSize));

            ShrinkVolumeDialog shrink = new(owner, volume, 256L * 1024 * 1024);
            InvokeValidate(shrink);
            cases.Add(("压缩卷.NewSize", () => shrink.NewSize));

            InitializeDiskDialog initialize = new(owner, disk);
            cases.Add(("初始化磁盘.SelectedStyle", () => initialize.SelectedStyle));

            // 模型级断言：虚拟磁盘的移除动作必须映射到"分离 VHD"
            DiskItem virtualDisk = new()
            {
                Number = 9,
                FriendlyName = "测试虚拟磁盘",
                Size = 1024,
                BusType = 0x0F, // BusTypeFileBackedVirtual
                IsVirtualDisk = true,
            };

            DiskItem physicalDisk = new()
            {
                Number = 8,
                FriendlyName = "测试物理磁盘",
                Size = 1024,
                BusType = 0x0B, // SATA
                IsVirtualDisk = false,
                SupportsEject = true,
            };

            cases.Add(("虚拟磁盘.动作名", () => virtualDisk.EjectActionName));
            cases.Add(("虚拟磁盘.菜单文本", () => virtualDisk.EjectMenuText));
            cases.Add(("物理磁盘.动作名", () => physicalDisk.EjectActionName));

            if (virtualDisk.EjectActionName != "分离 VHD")
            {
                failed++;
                write($"  × 虚拟磁盘的移除动作应为「分离 VHD」，实际为「{virtualDisk.EjectActionName}」");
            }
            else if (physicalDisk.EjectActionName != "弹出")
            {
                failed++;
                write($"  × 物理设备的移除动作应为「弹出」，实际为「{physicalDisk.EjectActionName}」");
            }
            else
            {
                passed++;
                write("  √ 虚拟磁盘映射为「分离 VHD」、物理设备映射为「弹出」");
            }

            // 移除分派冒烟测试：用不存在的磁盘号，确认各分支都返回可读结果而不是抛异常
            DiskItem missingPhysical = new()
            {
                Number = 999,
                FriendlyName = "不存在的物理磁盘",
                Size = 1024,
                BusType = 0x0B,
                IsVirtualDisk = false,
                SupportsEject = true,
            };

            OperationResult physicalResult = DiskOperations.RemoveDisk(missingPhysical);
            if (physicalResult.Success || physicalResult.Message.Length == 0)
            {
                failed++;
                write($"  × 不存在的物理磁盘：期望可读的失败结果，实际 {physicalResult.Success}/{physicalResult.Message}");
            }
            else
            {
                passed++;
                write($"  √ 移除分派（物理设备分支）返回可读结果：{physicalResult.Message}");
            }

            DiskItem missingVirtual = new()
            {
                Number = 998,
                FriendlyName = "不存在的虚拟磁盘",
                Size = 1024,
                BusType = 0x0F,
                IsVirtualDisk = true,
            };

            OperationResult virtualResult = DiskOperations.RemoveDisk(
                missingVirtual,
                new BlockResolutionOptions { DetachBlockingVirtualDiskConfirmed = true, PreResolvedVirtualDiskFile = null });

            if (virtualResult.Success || virtualResult.Message.Length == 0)
            {
                failed++;
                write($"  × 不存在的虚拟磁盘：期望可读的失败结果，实际 {virtualResult.Success}/{virtualResult.Message}");
            }
            else
            {
                passed++;
                write($"  √ 移除分派（虚拟磁盘分支）返回可读结果：{virtualResult.Message}");
            }

            // 关键一步：在后台线程读取上面所有属性。
            List<string> threadErrors = [];
            TextBox canary = new() { Text = "探针" };
            bool canaryThrew = false;

            Thread worker = new(() =>
            {
                // 探针：故意跨线程读一个控件，用来证明"本测试确实能捕捉这类问题"。
                try
                {
                    _ = canary.Text;
                }
                catch (InvalidOperationException)
                {
                    canaryThrew = true;
                }

                foreach ((string name, Func<object?> read) in cases)
                {
                    try
                    {
                        _ = read();
                    }
                    catch (Exception ex)
                    {
                        threadErrors.Add($"{name} → {ex.GetType().Name}: {ex.Message}");
                    }
                }
            });

            worker.Start();
            worker.Join();

            if (canaryThrew)
            {
                passed++;
                write("  √ 检测机制有效（故意跨线程读控件确实会抛异常）");
            }
            else
            {
                failed++;
                write("  × 检测机制失效：本测试没能复现跨线程访问异常，结论不可信");
            }

            if (threadErrors.Count == 0)
            {
                passed++;
                write($"  √ 对话框结果属性可被后台线程安全读取（共 {cases.Count} 个属性）");
            }
            else
            {
                failed++;
                foreach (string error in threadErrors)
                {
                    write($"  × {error}");
                }
            }

            owner.Close();
        }
    }

    /// <summary>调用受保护的 Validate，让对话框在界面线程上把结果拍成快照。</summary>
    private static void InvokeValidate(DialogBase dialog)
    {
        MethodInfo? method = typeof(DialogBase).GetMethod("Validate", BindingFlags.Instance | BindingFlags.NonPublic);
        method?.Invoke(dialog, null);
    }
}
