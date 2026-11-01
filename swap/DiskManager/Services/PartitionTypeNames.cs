using DiskManager.Interop;
using DiskManager.Models;

namespace DiskManager.Services;

/// <summary>把分区类型 GUID / MBR 类型字节翻译成中文名称，风格对齐系统磁盘管理。</summary>
internal static class PartitionTypeNames
{
    /// <summary>GPT 分区类型名称。</summary>
    public static string FromGpt(Guid type) => type switch
    {
        var g when g == GptTypeGuids.EfiSystem => "EFI 系统分区",
        var g when g == GptTypeGuids.MicrosoftReserved => "保留分区",
        var g when g == GptTypeGuids.Recovery => "恢复分区",
        var g when g == GptTypeGuids.LdmMetadata => "逻辑磁盘管理器元数据分区",
        var g when g == GptTypeGuids.LdmData => "逻辑磁盘管理器数据分区",
        var g when g == GptTypeGuids.StorageSpaces => "存储空间分区",
        var g when g == GptTypeGuids.BasicData => "基本数据分区",
        _ => "未知分区",
    };

    /// <summary>MBR 分区类型名称。</summary>
    public static string FromMbr(byte type) => type switch
    {
        0x00 => "空",
        0x05 or 0x0F or 0x85 => "扩展分区",
        0x07 => "NTFS",
        0x0B or 0x0C => "FAT32",
        0x0E => "FAT16",
        0x01 => "FAT12",
        0x06 => "FAT16 (大于 32 MB)",
        0x27 => "恢复分区",
        0x42 => "Windows 动态磁盘",
        0x82 => "Linux 交换分区",
        0x83 => "Linux 分区",
        0x8E => "Linux LVM",
        0xEE => "GPT 保护分区",
        0xEF => "EFI 系统分区",
        _ => "未知分区",
    };

    /// <summary>
    /// 给出界面上"类型"列的文本。
    /// 顺序很讲究：先看分区语义（EFI/保留/恢复），再看系统卷/启动卷，最后才回落到"简单"。
    /// </summary>
    public static string Describe(RawPartition partition, bool isSystemVolume, bool isBootVolume)
    {
        string basic = partition.Style == PartitionStyle.Gpt
            ? FromGpt(partition.GptType)
            : FromMbr(partition.MbrType);

        if (partition.Style == PartitionStyle.Gpt)
        {
            bool isPlainData = partition.GptType == GptTypeGuids.BasicData;
            if (!isPlainData)
            {
                return basic;
            }
        }

        if (isBootVolume)
        {
            return "启动";
        }

        if (isSystemVolume)
        {
            return "系统";
        }

        if (partition.Style == PartitionStyle.Mbr)
        {
            // MBR 磁盘上第 1~4 号分区为主分区，扩展分区内的为逻辑驱动器。
            return partition.Number <= 4 ? "主分区" : "逻辑驱动器";
        }

        return "简单";
    }
}
