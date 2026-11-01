namespace DiskManager.Interop;

/// <summary>从 IOCTL 读到的原始分区信息（尚未翻译成界面用的中文名称）。</summary>
internal sealed class RawPartition
{
    public int Number { get; set; }
    public long Offset { get; set; }
    public long Length { get; set; }
    public PartitionStyle Style { get; set; }

    /// <summary>GPT 分区类型 GUID；MBR 分区此值为空。</summary>
    public Guid GptType { get; set; }

    /// <summary>GPT 分区名，可能为空。</summary>
    public string GptName { get; set; } = string.Empty;

    /// <summary>GPT 属性位（隐藏、只读、无默认盘符等）。</summary>
    public ulong GptAttributes { get; set; }

    /// <summary>MBR 分区类型字节。</summary>
    public byte MbrType { get; set; }

    public bool BootIndicator { get; set; }
    public bool RecognizedPartition { get; set; }
}

/// <summary>一块磁盘的完整分区表。</summary>
internal sealed class RawDiskLayout
{
    public PartitionStyle Style { get; set; } = PartitionStyle.Raw;

    /// <summary>GPT 磁盘唯一标识。</summary>
    public Guid GptDiskId { get; set; }

    /// <summary>GPT 可用区间长度。</summary>
    public long GptUsableLength { get; set; }

    /// <summary>MBR 磁盘签名。</summary>
    public uint MbrSignature { get; set; }

    public List<RawPartition> Partitions { get; } = [];
}

/// <summary>存储设备描述符里抽出来的关键信息。</summary>
internal sealed class StorageDeviceInfo
{
    public string Vendor { get; set; } = string.Empty;
    public string Product { get; set; } = string.Empty;
    public string Revision { get; set; } = string.Empty;
    public string Serial { get; set; } = string.Empty;
    public int BusType { get; set; }
    public bool Removable { get; set; }

    /// <summary>总线类型的中文名称。</summary>
    public string BusTypeName => BusType switch
    {
        0x00 => "未知",
        0x01 => "SCSI",
        0x02 => "ATAPI",
        0x03 => "ATA",
        0x04 => "IEEE 1394",
        0x05 => "SSA",
        0x06 => "光纤通道",
        0x07 => "USB",
        0x08 => "RAID",
        0x09 => "iSCSI",
        0x0A => "SAS",
        0x0B => "SATA",
        0x0C => "SD",
        0x0D => "MMC",
        0x0E => "虚拟",
        0x0F => "文件支持的虚拟磁盘",
        0x10 => "存储空间",
        0x11 => "NVMe",
        0x12 => "SCM",
        0x13 => "UFS",
        0x14 => "NVMe-oF",
        _ => "其他",
    };
}
