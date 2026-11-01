using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DiskManager.Interop;

/// <summary>
/// 一块物理磁盘的句柄包装，提供脱机/联机、分区表读取、初始化等底层能力。
/// 生命周期短：每次刷新或每次操作时打开、用完即释放，避免长期占用导致脱机失败。
/// </summary>
internal sealed class DiskHandle : IDisposable
{
    /// <summary>分区表缓冲上限，防止异常设备把内存吃光。</summary>
    private const int MaxLayoutBufferSize = 4 * 1024 * 1024;

    private DiskHandle(SafeFileHandle handle, int number, bool writable)
    {
        Handle = handle;
        Number = number;
        IsWritableHandle = writable;
    }

    public SafeFileHandle Handle { get; }

    /// <summary>PhysicalDriveN 里的 N。</summary>
    public int Number { get; }

    /// <summary>句柄是否带写权限；脱机/联机必须为 true。</summary>
    public bool IsWritableHandle { get; }

    /// <summary>打开一块物理磁盘。磁盘被占用时可能失败，用 shareRead/shareWrite 尽量放宽。</summary>
    public static DiskHandle? TryOpen(int number, bool writeAccess, out string error)
    {
        string path = $@"\\.\PhysicalDrive{number}";
        uint access = Win32Access.GenericRead | (writeAccess ? Win32Access.GenericWrite : 0);

        SafeFileHandle handle = NativeMethods.CreateFileW(
            path,
            access,
            Win32Access.FileShareRead | Win32Access.FileShareWrite,
            IntPtr.Zero,
            Win32Access.OpenExisting,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            int code = Marshal.GetLastWin32Error();
            handle.Dispose();
            error = $"打开 {path} 失败：{Win32Error.Describe(code)}";
            return null;
        }

        error = string.Empty;
        return new DiskHandle(handle, number, writeAccess);
    }

    /// <summary>读取磁盘总容量与扇区大小。</summary>
    public bool TryGetGeometry(out long size, out uint bytesPerSector, out string error)
    {
        size = 0;
        bytesPerSector = 0;

        if (!DiskIo.TryIoctl(Handle, IoCtl.DiskGetDriveGeometryEx, out DiskGeometryEx geometry, out _, out int code))
        {
            error = Win32Error.Describe(code);
            return false;
        }

        size = geometry.DiskSize;
        bytesPerSector = geometry.Geometry.BytesPerSector;
        error = string.Empty;
        return true;
    }

    /// <summary>读取磁盘属性（脱机 / 只读）。</summary>
    public bool TryGetAttributes(out DiskAttribute attributes, out string error)
    {
        attributes = DiskAttribute.None;
        byte[] output = new byte[64];

        if (!DiskIo.TryIoctl(Handle, IoCtl.DiskGetDiskAttributes, null, output, out int returned, out int code))
        {
            error = Win32Error.Describe(code);
            return false;
        }

        // DISK_ATTRIBUTES 结构与 SET_DISK_ATTRIBUTES 前 16 字节一致：Version、Persist、保留、Attributes。
        if (returned >= 16)
        {
            ulong value = BitConverter.ToUInt64(output, 8);
            attributes = (DiskAttribute)value;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// 设置磁盘属性，脱机 / 联机的核心实现。
    /// 先按新版 40 字节结构提交，若驱动只认旧版 32 字节结构则自动重试一次。
    /// </summary>
    public bool TrySetAttributes(DiskAttribute attributes, DiskAttribute mask, bool persist, out string error)
    {
        if (!IsWritableHandle)
        {
            error = "句柄没有写权限，无法修改磁盘属性（需要管理员权限）。";
            return false;
        }

        SetDiskAttributes request = SetDiskAttributes.Create(attributes, mask, persist);
        byte[] input = DiskIo.ToBytes(ref request);

        if (DiskIo.TryIoctl(Handle, IoCtl.DiskSetDiskAttributes, input, null, out _, out int code))
        {
            error = string.Empty;
            return true;
        }

        // 旧版本结构兜底：Windows 早期版本的 Attributes 字段是 32 位。
        if (code == Win32Error.ErrorInvalidParameter)
        {
            SetDiskAttributesLegacy legacy = SetDiskAttributesLegacy.Create(attributes, mask, persist);
            byte[] legacyInput = DiskIo.ToBytes(ref legacy);
            if (DiskIo.TryIoctl(Handle, IoCtl.DiskSetDiskAttributes, legacyInput, null, out _, out int retryCode))
            {
                error = string.Empty;
                return true;
            }
            code = retryCode;
        }

        error = Win32Error.Describe(code);
        return false;
    }

    /// <summary>读取分区表；无有效分区表时 Style 为 Raw。</summary>
    public bool TryGetLayout(out RawDiskLayout layout, out string error)
    {
        layout = new RawDiskLayout();
        int bufferSize = 4096;

        while (bufferSize <= MaxLayoutBufferSize)
        {
            byte[] buffer = new byte[bufferSize];
            if (DiskIo.TryIoctl(Handle, IoCtl.DiskGetDriveLayoutEx, null, buffer, out int returned, out int code))
            {
                return TryParseLayout(buffer, returned, layout, out error);
            }

            if (code is Win32Error.ErrorInsufficientBuffer or Win32Error.ErrorMoreData)
            {
                // 系统会回填实际需要的长度，取两者中较大的那个继续扩容。
                bufferSize = Math.Max(bufferSize * 2, returned + PartitionInformationExSize);
                continue;
            }

            error = Win32Error.Describe(code);
            return false;
        }

        error = "分区表数据过大，已超过安全上限。";
        return false;
    }

    /// <summary>初始化磁盘：写入空 MBR 或空 GPT 签名。</summary>
    public bool TryInitialize(PartitionStyle style, out string error)
    {
        if (!IsWritableHandle)
        {
            error = "句柄没有写权限，无法初始化磁盘（需要管理员权限）。";
            return false;
        }

        if (style is not (PartitionStyle.Mbr or PartitionStyle.Gpt))
        {
            error = "只支持初始化为主引导记录(MBR)或 GUID 分区表(GPT)。";
            return false;
        }

        CreateDisk request = new() { PartitionStyle = (uint)style };
        if (style == PartitionStyle.Gpt)
        {
            request.Gpt.DiskId = Guid.NewGuid();
            request.Gpt.MaxPartitionCount = 128;
        }
        else
        {
            // MBR 签名取随机值，避免与现存磁盘签名冲突（签名相同会导致系统把二者视为同一磁盘）。
            request.Mbr.Signature = (uint)Random.Shared.Next(1, int.MaxValue);
        }

        byte[] input = DiskIo.ToBytes(ref request);
        if (!DiskIo.TryIoctl(Handle, IoCtl.DiskCreateDisk, input, null, out _, out int code))
        {
            error = Win32Error.Describe(code);
            return false;
        }

        // 分区表变了，通知系统重新枚举，否则"未初始化"状态会一直挂在缓存里。
        DiskIo.TryIoctl(Handle, IoCtl.DiskUpdateProperties, out _);
        error = string.Empty;
        return true;
    }

    /// <summary>通知系统重新读取分区表与卷信息。</summary>
    public void NotifyLayoutChanged() => DiskIo.TryIoctl(Handle, IoCtl.DiskUpdateProperties, out _);

    /// <summary>
    /// 读取 0 号扇区，判断是否存在有效的 MBR 结束标志（偏移 510 处的 0x55AA）。
    /// 系统就是靠这个标志区分"未初始化"和"已初始化但还没分区"，这里保持一致。
    /// </summary>
    public bool TryReadMbrSignature(out bool hasSignature, out string error)
    {
        hasSignature = false;
        byte[] sector = new byte[512];

        if (!NativeMethods.SetFilePointerEx(Handle, 0, IntPtr.Zero, NativeMethods.FileBegin))
        {
            error = Win32Error.LastMessage();
            return false;
        }

        if (!NativeMethods.ReadFile(Handle, sector, sector.Length, out int read, IntPtr.Zero) || read < 512)
        {
            error = Win32Error.LastMessage();
            return false;
        }

        hasSignature = sector[510] == 0x55 && sector[511] == 0xAA;
        error = string.Empty;
        return true;
    }

    /// <summary>查询存储设备描述符（厂商、型号、序列号、总线类型）。</summary>
    public bool TryQueryDeviceInfo(out StorageDeviceInfo info, out string error)
    {
        info = new StorageDeviceInfo();
        StoragePropertyQuery query = new() { PropertyId = (uint)StoragePropertyId.StorageDeviceProperty, QueryType = (uint)StorageQueryType.PropertyStandardQuery };
        byte[] input = DiskIo.ToBytes(ref query);
        byte[] buffer = new byte[1024];

        if (!DiskIo.TryIoctl(Handle, IoCtl.StorageQueryProperty, input, buffer, out int returned, out int code))
        {
            error = Win32Error.Describe(code);
            return false;
        }

        if (returned < Marshal.SizeOf<StorageDeviceDescriptor>())
        {
            error = "存储设备描述符数据不完整。";
            return false;
        }

        StorageDeviceDescriptor descriptor = MemoryMarshal.Read<StorageDeviceDescriptor>(buffer.AsSpan(0, returned));
        info.Vendor = ReadAnsi(buffer, descriptor.VendorIdOffset);
        info.Product = ReadAnsi(buffer, descriptor.ProductIdOffset);
        info.Revision = ReadAnsi(buffer, descriptor.ProductRevisionOffset);
        info.Serial = ReadAnsi(buffer, descriptor.SerialNumberOffset);
        info.BusType = descriptor.BusType;
        info.Removable = descriptor.RemovableMedia != 0;
        error = string.Empty;
        return true;
    }

    /// <summary>由设备句柄反查磁盘编号，用于把设备接口路径与 PhysicalDriveN 对应起来。</summary>
    public bool TryGetDeviceNumber(out int number, out int partition, out string error)
    {
        number = -1;
        partition = -1;

        if (!DiskIo.TryIoctl(Handle, IoCtl.StorageGetDeviceNumber, out StorageDeviceNumber deviceNumber, out _, out int code))
        {
            error = Win32Error.Describe(code);
            return false;
        }

        number = (int)deviceNumber.DeviceNumber;
        partition = (int)deviceNumber.PartitionNumber;
        error = string.Empty;
        return true;
    }

    public void Dispose() => Handle.Dispose();

    private const int PartitionInformationExSize = 144;

    /// <summary>按 48 字节头部 + N×144 字节分区项的固定布局解析分区表。</summary>
    private static bool TryParseLayout(byte[] buffer, int returned, RawDiskLayout layout, out string error)
    {
        if (returned < 48)
        {
            error = "分区表返回数据不完整。";
            return false;
        }

        Span<byte> span = buffer.AsSpan(0, returned);
        DriveLayoutHeader header = MemoryMarshal.Read<DriveLayoutHeader>(span);
        layout.Style = (PartitionStyle)header.PartitionStyle;

        switch (layout.Style)
        {
            case PartitionStyle.Gpt:
                layout.GptDiskId = header.Gpt.DiskId;
                layout.GptUsableLength = header.Gpt.UsableLength;
                break;
            case PartitionStyle.Mbr:
                layout.MbrSignature = header.Mbr.Signature;
                break;
        }

        int count = (int)header.PartitionCount;
        int available = (returned - 48) / PartitionInformationExSize;
        if (count > available)
        {
            error = $"分区表声明了 {count} 个分区，但只收到 {available} 个，数据可能被截断。";
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            PartitionInformationEx entry = MemoryMarshal.Read<PartitionInformationEx>(span.Slice(48 + (i * PartitionInformationExSize), PartitionInformationExSize));
            RawPartition partition = new()
            {
                Number = (int)entry.PartitionNumber,
                Offset = entry.StartingOffset,
                Length = entry.PartitionLength,
                Style = (PartitionStyle)entry.PartitionStyle,
            };

            if (partition.Style == PartitionStyle.Gpt)
            {
                partition.GptType = entry.Gpt.PartitionType;
                partition.GptName = entry.Gpt.GetName();
                partition.GptAttributes = entry.Gpt.Attributes;
            }
            else if (partition.Style == PartitionStyle.Mbr)
            {
                partition.MbrType = entry.Mbr.PartitionType;
                partition.BootIndicator = entry.Mbr.BootIndicator != 0;
                partition.RecognizedPartition = entry.Mbr.RecognizedPartition != 0;
            }

            layout.Partitions.Add(partition);
        }

        error = string.Empty;
        return true;
    }

    /// <summary>按字节偏移读取以 0 结尾的 ASCII 字符串（存储描述符里的厂商/型号等）。</summary>
    private static string ReadAnsi(byte[] buffer, uint offset)
    {
        if (offset == 0 || offset >= buffer.Length)
        {
            return string.Empty;
        }

        int end = (int)offset;
        while (end < buffer.Length && buffer[end] != 0)
        {
            end++;
        }

        return Encoding.ASCII.GetString(buffer, (int)offset, end - (int)offset).Trim();
    }
}
