using System.IO;
using System.Reflection;
using System.Windows;
using DiskManager.Dialogs;
using DiskManager.Interop;
using DiskManager.Models;
using DiskManager.Services;

namespace DiskManager.SelfTest;

/// <summary>
/// 端到端复现"更改驱动器号"这条路（用户报告崩溃的那条）。
///
/// 流程与界面完全一致：
///   界面线程构造对话框 → 触发校验拍快照 → 后台线程读结果属性 → 后台线程执行改盘符。
/// 只在自建的测试 VHDX 上操作，且要求管理员权限。
/// </summary>
internal static class DriveLetterFlowTest
{
    private static int _passed;
    private static int _failed;

    public static int Run(string vhdxPath, Action<string> write)
    {
        _passed = 0;
        _failed = 0;

        if (!Elevation.IsElevated)
        {
            write("需要管理员权限才能执行本测试。");
            return 2;
        }

        VhdHelper.TryDetach(vhdxPath, out _, out _);
        Thread.Sleep(1000);

        HashSet<int> existing = [.. DiskInventory.Capture().Disks.Select(d => d.Number)];
        if (!VhdHelper.TryCreateAndAttach(vhdxPath, out string mountError, out _))
        {
            write($"挂载测试 VHDX 失败：{mountError}");
            return 3;
        }

        Thread.Sleep(4000);
        DiskItem? disk = DiskInventory.Capture().Disks
            .FirstOrDefault(d => !existing.Contains(d.Number) && d.BusTypeName.Contains("虚拟", StringComparison.Ordinal));

        if (disk is null)
        {
            write("没有找到测试磁盘。");
            VhdHelper.TryDetach(vhdxPath, out _, out _);
            return 4;
        }

        write($"测试磁盘：磁盘 {disk.Number}（{disk.FriendlyName}，{disk.CapacityText}）");

        try
        {
            if (!disk.IsInitialized)
            {
                DiskOperations.Initialize(disk.Number, PartitionStyle.Gpt);
                Thread.Sleep(2000);
                disk = ReloadDisk(disk.Number) ?? disk;
            }

            const char startLetter = 'Y';
            OperationResult create = VolumeOperations.CreateSimpleVolume(
                disk,
                256L * 1024 * 1024,
                offset: null,
                startLetter,
                "NTFS",
                "DMFLOW",
                quickFormat: true,
                allocationUnitSize: 0);

            Check("准备测试卷（Y:）", create.Success, create.Message);
            Thread.Sleep(2000);

            VolumeItem? volume = FindVolumeByLetter(startLetter);
            Check("测试卷已出现在卷列表", volume is not null);

            if (volume is null)
            {
                return Finish(vhdxPath, write);
            }

            // ---------- 1. 界面线程：构造对话框并触发校验（拍快照） ----------
            ChangeDriveLetterDialog? dialog = null;
            string? dialogError = null;

            Thread staThread = new(() =>
            {
                try
                {
                    Window owner = new()
                    {
                        Title = "测试宿主",
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = -20000,
                        Top = -20000,
                        ShowInTaskbar = false,
                    };

                    owner.Show();
                    owner.Hide();

                    dialog = new ChangeDriveLetterDialog(owner, volume);
                    MethodInfo? validate = typeof(DialogBase).GetMethod("Validate", BindingFlags.Instance | BindingFlags.NonPublic);
                    validate?.Invoke(dialog, null);

                    owner.Close();
                }
                catch (Exception ex)
                {
                    dialogError = ex.Message;
                }
            });

            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();
            staThread.Join();

            Check("界面线程构造对话框并拍快照", dialog is not null && dialogError is null, dialogError ?? string.Empty);

            if (dialog is null)
            {
                return Finish(vhdxPath, write);
            }

            // ---------- 2. 后台线程读取对话框结果（用户崩溃的那一步） ----------
            string readError = string.Empty;
            ChangeLetterMode mode = ChangeLetterMode.Assign;
            char? targetLetter = null;
            string folderPath = string.Empty;

            Task.Run(() =>
            {
                try
                {
                    mode = dialog.Mode;
                    targetLetter = dialog.DriveLetter;
                    folderPath = dialog.FolderPath;
                }
                catch (Exception ex)
                {
                    readError = $"{ex.GetType().Name}: {ex.Message}";
                }
            }).Wait();

            Check("后台线程读取对话框结果属性不报错", readError.Length == 0, readError);

            // ---------- 3. 后台线程执行改盘符 ----------
            write($"  对话框给出的目标盘符：{(targetLetter.HasValue ? targetLetter.Value + ":" : "（不分配）")}");

            OperationResult change = VolumeOperations.ChangeDriveLetter(volume, mode, targetLetter, folderPath);
            Check("更改驱动器号成功", change.Success, change.Message);
            Thread.Sleep(2500);

            if (targetLetter.HasValue)
            {
                Check(
                    $"新驱动器号 {targetLetter.Value}: 已生效",
                    FindVolumeByLetter(targetLetter.Value) is not null,
                    "在卷列表中未找到新盘符");
            }

            // ---------- 4. 再改回去，确认可以反复切换 ----------
            VolumeItem? moved = targetLetter.HasValue ? FindVolumeByLetter(targetLetter.Value) : null;
            if (moved is not null)
            {
                OperationResult back = VolumeOperations.ChangeDriveLetter(moved, ChangeLetterMode.Assign, startLetter, string.Empty);
                Check("再改回 Y: 成功", back.Success, back.Message);
                Thread.Sleep(2000);
                Check("Y: 已恢复", FindVolumeByLetter(startLetter) is not null);
            }
        }
        catch (Exception ex)
        {
            _failed++;
            write($"  × 测试异常：{ex}");
        }

        return Finish(vhdxPath, write);
    }

    private static int Finish(string vhdxPath, Action<string> write)
    {
        write("\n--- 卸载测试磁盘 ---");
        if (VhdHelper.TryDetach(vhdxPath, out string detachError, out _))
        {
            write("已卸载。");
        }
        else
        {
            write($"卸载失败：{detachError}");
        }

        write($"\n测试结果：通过 {_passed} 项，失败 {_failed} 项");
        return _failed == 0 ? 0 : 1;
    }

    private static void Check(string name, bool condition, string detail = "")
    {
        string suffix = detail.Length > 0 ? $"　[{detail}]" : string.Empty;
        if (condition)
        {
            _passed++;
            Console.WriteLine($"  √ {name}{suffix}");
        }
        else
        {
            _failed++;
            Console.WriteLine($"  × {name}{suffix}");
        }
    }

    private static DiskItem? ReloadDisk(int number)
        => DiskInventory.Capture().Disks.FirstOrDefault(d => d.Number == number);

    private static VolumeItem? FindVolumeByLetter(char letter)
        => DiskInventory.Capture().Volumes.FirstOrDefault(v =>
            v.MountPoints.Any(m => m.Length >= 2 && char.ToUpperInvariant(m[0]) == char.ToUpperInvariant(letter)));
}
