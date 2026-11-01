using DiskManager.Interop;
using DiskManager.Models;

namespace DiskManagerWinUI;

/// <summary>
/// 界面调试用的假数据（只在带 --demo 启动时使用，不接触任何真实磁盘）。
/// 用途：不需要管理员权限也能把卷列表、磁盘图、右键菜单完整渲染出来，方便调界面。
/// </summary>
internal static class DemoData
{
    private const long Mb = 1024L * 1024;
    private const long Gb = Mb * 1024;

    public static (List<DiskItem> Disks, List<VolumeItem> Volumes) Create()
    {
        List<DiskItem> disks = [];
        List<VolumeItem> volumes = [];

        // 磁盘 0：系统盘（EFI + Windows 卷 + 未分配）
        DiskItem systemDisk = new()
        {
            Number = 0,
            FriendlyName = "Samsung SSD 990 PRO 2TB",
            BusTypeName = "NVMe",
            Size = 1907L * Gb,
            BytesPerSector = 512,
            Style = PartitionStyle.Gpt,
            Attributes = DiskAttribute.None,
            IsSystemDisk = true,
            IsBootDisk = true,
        };

        VolumeItem windowsVolume = Volume(
            @"\\?\Volume{demo-windows}\", "Windows", "C:", "NTFS", 1862L * Gb, 412L * Gb, "启动", 0);
        VolumeItem efiVolume = Volume(
            @"\\?\Volume{demo-efi}\", "SYSTEM", string.Empty, "FAT32", 260 * Mb, 180 * Mb, "EFI 系统分区", 0);

        AddPartition(systemDisk, 1, 1 * Mb, 260 * Mb, GptTypeGuids.EfiSystem, "EFI 系统分区", efiVolume, "EFI 系统分区");
        AddPartition(systemDisk, 2, 300 * Mb, 1862L * Gb, GptTypeGuids.BasicData, "Windows", windowsVolume, "启动");

        volumes.Add(windowsVolume);
        volumes.Add(efiVolume);
        disks.Add(systemDisk);

        // 磁盘 1：数据盘（一个卷 + 一段未分配）
        DiskItem dataDisk = new()
        {
            Number = 1,
            FriendlyName = "WDC WD40EZAZ-00SF3B0",
            BusTypeName = "SATA",
            Size = 3726L * Gb,
            Style = PartitionStyle.Gpt,
            OfflineBlockReason = "上有正在使用的虚拟磁盘文件（D:\\VHDX\\Win11.vhdx），脱机或移除会让该虚拟磁盘一起失效",
            EjectBlockReason = "该设备不支持弹出（内部固定磁盘，或驱动未提供安全移除）",
        };

        VolumeItem dataVolume = Volume(
            @"\\?\Volume{demo-data}\", "DATA", "D:", "NTFS", 3000L * Gb, 1200L * Gb, "简单", 1);
        AddPartition(dataDisk, 1, 1 * Mb, 3000L * Gb, GptTypeGuids.BasicData, "DATA", dataVolume, "简单");
        volumes.Add(dataVolume);
        disks.Add(dataDisk);

        // 磁盘 2：未初始化磁盘
        DiskItem rawDisk = new()
        {
            Number = 2,
            FriendlyName = "Msft Virtual Disk",
            BusTypeName = "文件支持的虚拟磁盘",
            Size = 2048L * Mb,
            Style = PartitionStyle.Raw,
            IsVirtualDisk = true,
            BusType = 0x0F,
            SupportsEject = true,
        };
        disks.Add(rawDisk);

        // 磁盘 3：移动硬盘（可弹出，含恢复分区）
        DiskItem usbDisk = new()
        {
            Number = 3,
            FriendlyName = "SAMSUNG USB_SSD SCSI Disk Device",
            BusTypeName = "USB",
            Size = 476L * Gb,
            Style = PartitionStyle.Gpt,
            SupportsEject = true,
            Capabilities = 0xE0,
        };

        VolumeItem usbVolume = Volume(
            @"\\?\Volume{demo-usb}\", "备份盘", "E:", "exFAT", 470L * Gb, 300L * Gb, "简单", 3);
        AddPartition(usbDisk, 1, 1 * Mb, 470L * Gb, GptTypeGuids.BasicData, "备份盘", usbVolume, "简单");
        volumes.Add(usbVolume);
        disks.Add(usbDisk);

        return (disks, volumes);
    }

    private static VolumeItem Volume(
        string volumeName,
        string label,
        string letter,
        string fileSystem,
        long size,
        long free,
        string typeName,
        int diskNumber)
        => new()
        {
            VolumeName = volumeName,
            MountPoints = letter.Length > 0 ? [$"{letter}\\\\"] : [],
            LetterText = letter,
            Label = label,
            FileSystem = fileSystem,
            Size = size,
            FreeSpace = free,
            IsAccessible = true,
            State = VolumeState.Healthy,
            TypeName = typeName,
            LayoutText = "简单",
            DiskNumbers = [diskNumber],
            IsSystemVolume = typeName == "EFI 系统分区",
            IsBootVolume = typeName == "启动",
        };

    private static void AddPartition(
        DiskItem disk,
        int number,
        long offset,
        long length,
        Guid gptType,
        string gptName,
        VolumeItem volume,
        string typeName)
    {
        PartitionItem partition = new()
        {
            Number = number,
            DiskNumber = disk.Number,
            Offset = offset,
            Length = length,
            Style = PartitionStyle.Gpt,
            GptType = gptType,
            GptName = gptName,
            TypeName = typeName,
            Volume = volume,
        };

        disk.Partitions.Add(partition);
        volume.Partitions.Add(partition);
    }
}
