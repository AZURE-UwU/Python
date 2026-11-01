using System.Diagnostics;
using DiskManager.Interop;
using DiskManager.Models;

namespace DiskManager.Services;

/// <summary>驱动器号变更方式。</summary>
internal enum ChangeLetterMode
{
    Assign,
    Remove,
    MountFolder,
}

/// <summary>
/// 组合型卷操作：把"建分区 → 分配盘符 → 格式化"这类多步骤流程收在一处，
/// 中间任何一步失败都会把失败原因带出来，不回滚已成功的步骤（与系统行为一致）。
/// </summary>
internal static class VolumeOperations
{
    /// <summary>新建简单卷：创建分区 → 格式化；未指定盘符时用临时盘符格式化后移除。</summary>
    public static OperationResult CreateSimpleVolume(
        DiskItem disk,
        long size,
        long? offset,
        char? driveLetter,
        string fileSystem,
        string label,
        bool quickFormat,
        uint allocationUnitSize)
    {
        char? workingLetter = driveLetter ?? FindTemporaryLetter();
        if (!workingLetter.HasValue)
        {
            return OperationResult.Fail("系统中已经没有可用的驱动器号，无法创建并格式化新卷。");
        }

        HashSet<long> offsetsBefore = GetPartitionOffsets(disk.Number);

        // 注意：不要在创建分区时同时要求系统分配盘符。
        // 实测（Windows 11 26100）MSFT_Disk.CreatePartition 带 AssignDriveLetter=true 会返回
        // "拒绝访问"，而不带该参数一定成功；盘符改由底层挂载接口随后分配。
        StorageOperations.RefreshDisk(disk.Number);
        OperationResult create = StorageOperations.CreatePartition(
            disk.Number,
            size,
            offset,
            driveLetter: null,
            useMaximumSize: false,
            disk.Style);

        if (!create.Success)
        {
            return create;
        }

        DiskOperations.NotifyDiskChanged(disk.Number);
        WaitForVolumeManager();

        // 定位刚创建的分区（按起始偏移与创建前对比）。
        long? newOffset = WaitForNewPartition(disk.Number, offsetsBefore);
        if (newOffset is null)
        {
            return OperationResult.Fail("分区已创建，但未能定位新分区，请刷新后手动分配驱动器号并格式化。");
        }

        int partitionNumber = GetPartitionNumber(disk.Number, newOffset.Value);
        OperationResult assign = AssignLetter(disk.Number, partitionNumber, newOffset.Value, workingLetter.Value);
        if (!assign.Success)
        {
            return OperationResult.Fail($"分区已创建，但分配驱动器号失败：{assign.Message}");
        }

        WaitForVolumeManager();

        OperationResult format = StorageOperations.FormatVolume(workingLetter.Value, fileSystem, label, quickFormat, allocationUnitSize);
        if (!format.Success)
        {
            OperationResult fallback = FormatWithFormatCom(workingLetter.Value, fileSystem, label, quickFormat);
            if (!fallback.Success)
            {
                return OperationResult.Fail($"分区已创建，但格式化失败：{format.Message}");
            }
        }

        // 用户选择"不分配驱动器号"时，格式化完成后把临时盘符摘掉。
        if (!driveLetter.HasValue)
        {
            OperationResult remove = DiskOperations.RemoveMountPoint($"{workingLetter.Value}:\\");
            if (!remove.Success)
            {
                return OperationResult.Fail($"卷已创建并格式化，但临时盘符 {workingLetter.Value}: 未能移除：{remove.Message}");
            }
        }

        DiskOperations.NotifyDiskChanged(disk.Number);
        return OperationResult.Ok();
    }

    /// <summary>读取某块磁盘现有分区的起始偏移集合。</summary>
    private static HashSet<long> GetPartitionOffsets(int diskNumber)
    {
        DiskItem? disk = DiskInventory.Capture().Disks.FirstOrDefault(d => d.Number == diskNumber);
        return disk is null ? [] : [.. disk.Partitions.Select(p => p.Offset)];
    }

    /// <summary>等待系统把新分区暴露出来，返回它的起始偏移。</summary>
    private static long? WaitForNewPartition(int diskNumber, HashSet<long> before)
    {
        for (int attempt = 0; attempt < 12; attempt++)
        {
            DiskItem? disk = DiskInventory.Capture().Disks.FirstOrDefault(d => d.Number == diskNumber);
            if (disk is not null)
            {
                foreach (PartitionItem partition in disk.Partitions)
                {
                    if (!before.Contains(partition.Offset))
                    {
                        return partition.Offset;
                    }
                }
            }

            Thread.Sleep(500);
        }

        return null;
    }

    /// <summary>取指定偏移处的分区号。</summary>
    private static int GetPartitionNumber(int diskNumber, long offset)
    {
        DiskItem? disk = DiskInventory.Capture().Disks.FirstOrDefault(d => d.Number == diskNumber);
        return disk?.Partitions.FirstOrDefault(p => p.Offset == offset)?.Number ?? 0;
    }

    /// <summary>
    /// 分配驱动器号：优先用底层挂载接口（SetVolumeMountPoint），
    /// 找不到卷 GUID 时退回存储接口的 AddAccessPath。
    /// </summary>
    private static OperationResult AssignLetter(int diskNumber, int partitionNumber, long offset, char letter)
    {
        // 主路径：存储接口 AddAccessPath。
        // 刚建好的分区在卷管理器里还没有对应的卷设备，此时原生挂载点接口拿不到卷 GUID，
        // 而 AddAccessPath 直接作用于分区，实测可用。
        if (partitionNumber > 0)
        {
            OperationResult wmi = StorageOperations.AddAccessPath(diskNumber, partitionNumber, $"{char.ToUpperInvariant(letter)}:\\");
            if (wmi.Success)
            {
                return wmi;
            }

            // 兜底：原生挂载点接口（需要卷已经出现在卷管理器里）。
            string? volumeName = WaitForVolumeName(diskNumber, offset);
            if (volumeName is not null)
            {
                OperationResult native = DiskOperations.AssignDriveLetter(letter, volumeName);
                if (native.Success)
                {
                    return native;
                }

                return OperationResult.Fail($"{wmi.Message}；{native.Message}");
            }

            return wmi;
        }

        return OperationResult.Fail("找不到新分区对应的卷。");
    }

    /// <summary>等待新分区对应的卷出现，返回卷 GUID 路径。</summary>
    private static string? WaitForVolumeName(int diskNumber, long offset)
    {
        for (int attempt = 0; attempt < 12; attempt++)
        {
            foreach (RawVolume volume in VolumeDevice.Enumerate())
            {
                if (volume.Extents.Any(e => (int)e.DiskNumber == diskNumber && e.StartingOffset == offset))
                {
                    return volume.VolumeName;
                }
            }

            Thread.Sleep(400);
        }

        return null;
    }

    /// <summary>格式化失败时的兜底：调用系统自带的 format 命令。</summary>
    private static OperationResult FormatWithFormatCom(char letter, string fileSystem, string label, bool quick)
    {
        try
        {
            List<string> arguments =
            [
                $"{char.ToUpperInvariant(letter)}:",
                $"/FS:{fileSystem}",
                "/Y",
                quick ? "/Q" : "/P:1",
            ];

            if (label.Length > 0)
            {
                arguments.Add($"/V:{label}");
            }

            ProcessStartInfo startInfo = new()
            {
                FileName = "format.com",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using Process process = Process.Start(startInfo)!;
            process.StandardInput.WriteLine("Y");
            process.StandardInput.Close();
            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(600_000);

            return process.ExitCode == 0
                ? OperationResult.Ok()
                : OperationResult.Fail(output.Trim());
        }
        catch (Exception ex)
        {
            return OperationResult.Fail(ex.Message);
        }
    }

    /// <summary>删除卷（分区）。</summary>
    public static OperationResult DeleteVolume(VolumeItem volume, bool forceRemoveMountPoints)
    {
        PartitionItem? partition = volume.Partitions.FirstOrDefault();
        if (partition is null)
        {
            return OperationResult.Fail("找不到该卷对应的分区信息，请刷新后重试。");
        }

        if (forceRemoveMountPoints)
        {
            foreach (string mountPoint in volume.MountPoints.Where(m => m.Length >= 2 && m[1] == ':'))
            {
                // 盘符删除失败不阻断流程，删除分区时系统会一并清理。
                DiskOperations.RemoveMountPoint(mountPoint);
            }
        }

        OperationResult result = StorageOperations.DeletePartition(partition.DiskNumber, partition.Number);
        DiskOperations.NotifyDiskChanged(partition.DiskNumber);
        WaitForVolumeManager();
        return result;
    }

    /// <summary>格式化卷。</summary>
    public static OperationResult FormatVolume(VolumeItem volume, string fileSystem, string label, bool quick, uint allocationUnitSize)
    {
        char? letter = volume.MountPoints
            .Where(m => m.Length >= 2 && m[1] == ':')
            .Select(m => (char?)m[0])
            .FirstOrDefault();

        if (!letter.HasValue)
        {
            return OperationResult.Fail("该卷没有驱动器号，无法格式化。请先分配驱动器号。");
        }

        OperationResult result = StorageOperations.FormatVolume(letter.Value, fileSystem, label, quick, allocationUnitSize);
        if (!result.Success)
        {
            // 存储接口失败时退回系统自带 format 命令，尽量让用户的意图完成。
            OperationResult fallback = FormatWithFormatCom(letter.Value, fileSystem, label, quick);
            if (fallback.Success)
            {
                result = fallback;
            }
        }

        WaitForVolumeManager();
        return result;
    }

    /// <summary>扩展卷：把分区扩展到紧邻的未分配空间。</summary>
    public static OperationResult ExtendVolume(VolumeItem volume, long newSize)
    {
        PartitionItem? partition = volume.Partitions.FirstOrDefault();
        if (partition is null)
        {
            return OperationResult.Fail("找不到该卷对应的分区信息，请刷新后重试。");
        }

        OperationResult result = StorageOperations.ResizePartition(partition.DiskNumber, partition.Number, newSize);
        DiskOperations.NotifyDiskChanged(partition.DiskNumber);
        WaitForVolumeManager();
        return result;
    }

    /// <summary>压缩卷：把分区缩小，空出来的部分变成未分配空间。</summary>
    public static OperationResult ShrinkVolume(VolumeItem volume, long newSize)
    {
        PartitionItem? partition = volume.Partitions.FirstOrDefault();
        if (partition is null)
        {
            return OperationResult.Fail("找不到该卷对应的分区信息，请刷新后重试。");
        }

        OperationResult result = StorageOperations.ResizePartition(partition.DiskNumber, partition.Number, newSize);
        DiskOperations.NotifyDiskChanged(partition.DiskNumber);
        WaitForVolumeManager();
        return result;
    }

    /// <summary>更改驱动器号：分配、删除或装入文件夹。</summary>
    public static OperationResult ChangeDriveLetter(
        VolumeItem volume,
        ChangeLetterMode mode,
        char? letter,
        string folderPath)
    {
        switch (mode)
        {
            case ChangeLetterMode.Assign:
                {
                    if (!letter.HasValue)
                    {
                        return OperationResult.Fail("没有指定驱动器号。");
                    }

                    // 换盘符要先把原来的盘符摘掉：
                    // 系统自带的 Set-Partition -NewDriveLetter 也是这个顺序，
                    // 否则存储接口会以"访问路径冲突"为由拒绝。
                    string? existing = volume.MountPoints.FirstOrDefault(m => m.Length >= 2 && m[1] == ':');
                    if (existing is not null && !string.Equals(existing[..2], $"{letter.Value}:", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!DiskOperations.RemoveMountPoint(existing).Success)
                        {
                            return OperationResult.Fail($"无法移除原有驱动器号 {existing}。");
                        }

                        WaitForVolumeManager();
                    }

                    // 主路径走存储接口：实测给已有卷换盘符时，原生挂载点接口会返回"参数无效"。
                    PartitionItem? partition = volume.Partitions.FirstOrDefault();
                    if (partition is not null)
                    {
                        OperationResult wmi = StorageOperations.AddAccessPath(
                            partition.DiskNumber,
                            partition.Number,
                            $"{char.ToUpperInvariant(letter.Value)}:\\");

                        if (wmi.Success)
                        {
                            return wmi;
                        }

                        OperationResult native = DiskOperations.AssignDriveLetter(letter.Value, volume.VolumeName);
                        return native.Success ? native : OperationResult.Fail($"{wmi.Message}；{native.Message}");
                    }

                    return DiskOperations.AssignDriveLetter(letter.Value, volume.VolumeName);
                }

            case ChangeLetterMode.Remove:
                {
                    string? mountPoint = volume.MountPoints.FirstOrDefault(m => m.Length >= 2 && m[1] == ':');
                    if (mountPoint is null)
                    {
                        return OperationResult.Fail("该卷当前没有驱动器号。");
                    }

                    return DiskOperations.RemoveMountPoint(mountPoint);
                }

            case ChangeLetterMode.MountFolder:
                return DiskOperations.MountToFolder(folderPath, volume.VolumeName);

            default:
                return OperationResult.Fail("不支持的驱动器号操作。");
        }
    }

    /// <summary>删除卷时可以顺便清理的挂载点列表。</summary>
    public static IEnumerable<string> GetDriveLetterMountPoints(VolumeItem volume)
        => volume.MountPoints.Where(m => m.Length >= 2 && m[1] == ':');

    /// <summary>找一个空闲盘符临时使用（优先 Z 往 A 找，避免占用常用盘符）。</summary>
    private static char? FindTemporaryLetter()
    {
        HashSet<char> used = VolumeDevice.GetUsedDriveLetters();
        for (char letter = 'Z'; letter >= 'D'; letter--)
        {
            if (!used.Contains(letter))
            {
                return letter;
            }
        }

        return null;
    }

    /// <summary>
    /// 等待卷管理器完成挂载或卸载。
    /// 新建分区后立刻格式化经常因"设备未就绪"失败，短暂等待即可稳定。
    /// </summary>
    private static void WaitForVolumeManager() => Thread.Sleep(400);
}
