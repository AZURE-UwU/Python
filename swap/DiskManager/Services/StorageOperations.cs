using System.Management;
using DiskManager.Interop;
using DiskManager.Models;

namespace DiskManager.Services;

/// <summary>
/// 写操作实现层：统一走系统存储管理接口（root\Microsoft\Windows\Storage）。
///
/// 为什么不用 diskpart：
///   1) diskpart 是外部进程 + 文本输出，中英文系统下格式不同，解析脆弱；
///   2) Storage WMI 就是 PowerShell（Get-Disk / New-Partition / Format-Volume）底层的同一套接口，
///      返回结构化结果，错误码可翻译；
///   3) 脱机/联机另有原生 IOCTL 实现（见 DiskHandle），WMI 只作为兜底。
/// </summary>
internal static class StorageOperations
{
    private const string StorageNamespace = @"root\Microsoft\Windows\Storage";

    /// <summary>基本数据分区 GPT 类型，新建简单卷时使用。</summary>
    private const string BasicDataGptType = "{EBD0A0A2-B9E5-4433-87C0-68B6B72699C7}";

    /// <summary>MBR 分区类型：0x07 = NTFS。</summary>
    private const ushort NtfsMbrType = 0x07;

    private static ManagementScope CreateScope()
    {
        ManagementScope scope = new($@"\\.\{StorageNamespace}");
        scope.Connect();
        return scope;
    }

    // ------------------------------------------------------------------
    // 磁盘级操作
    // ------------------------------------------------------------------

    /// <summary>初始化磁盘（写入空 MBR 或 GPT 分区表）。</summary>
    public static OperationResult InitializeDisk(int diskNumber, PartitionStyle style)
    {
        using ManagementObject? disk = FindDisk(diskNumber, out OperationResult error);
        if (disk is null)
        {
            return error;
        }

        ushort styleValue = style == PartitionStyle.Gpt ? (ushort)1 : (ushort)0;
        return Invoke(disk, "Initialize", new Dictionary<string, object> { ["PartitionStyle"] = styleValue });
    }

    /// <summary>转换分区表类型（MBR 与 GPT 互转）。不含分区的磁盘转换才安全。</summary>
    public static OperationResult ConvertStyle(int diskNumber, PartitionStyle style)
    {
        using ManagementObject? disk = FindDisk(diskNumber, out OperationResult error);
        if (disk is null)
        {
            return error;
        }

        ushort styleValue = style == PartitionStyle.Gpt ? (ushort)1 : (ushort)0;
        return Invoke(disk, "ConvertStyle", new Dictionary<string, object> { ["PartitionStyle"] = styleValue });
    }

    /// <summary>脱机 / 联机（WMI 兜底路径，优先使用原生 IOCTL）。</summary>
    public static OperationResult SetOffline(int diskNumber, bool offline)
    {
        using ManagementObject? disk = FindDisk(diskNumber, out OperationResult error);
        if (disk is null)
        {
            return error;
        }

        return Invoke(disk, offline ? "Offline" : "Online", []);
    }

    /// <summary>设置磁盘只读属性。</summary>
    public static OperationResult SetDiskReadOnly(int diskNumber, bool readOnly)
    {
        using ManagementObject? disk = FindDisk(diskNumber, out OperationResult error);
        if (disk is null)
        {
            return error;
        }

        return Invoke(disk, "SetAttributes", new Dictionary<string, object> { ["IsReadOnly"] = readOnly });
    }

    /// <summary>清除磁盘上的所有分区信息（危险操作）。</summary>
    public static OperationResult ClearDisk(int diskNumber, bool zeroOut)
    {
        using ManagementObject? disk = FindDisk(diskNumber, out OperationResult error);
        if (disk is null)
        {
            return error;
        }

        return Invoke(disk, "Clear", new Dictionary<string, object>
        {
            ["RemoveData"] = true,
            ["RemoveOEM"] = true,
            ["ZeroOutEntireDisk"] = zeroOut,
            ["RunAsJob"] = false,
        });
    }

    /// <summary>刷新指定磁盘的 Storage WMI 缓存。</summary>
    public static OperationResult RefreshDisk(int diskNumber)
    {
        using ManagementObject? disk = FindDisk(diskNumber, out OperationResult error);
        if (disk is null)
        {
            return error;
        }

        return Invoke(disk, "Refresh", []);
    }

    // ------------------------------------------------------------------
    // 分区 / 卷操作
    // ------------------------------------------------------------------

    /// <summary>
    /// 在磁盘上创建分区。
    /// </summary>
    /// <param name="diskNumber">磁盘号。</param>
    /// <param name="size">分区大小（字节），useMaximumSize 为 true 时忽略。</param>
    /// <param name="offset">起始偏移（字节），null 表示由系统选择。</param>
    /// <param name="driveLetter">要分配的盘符，null 表示不分配。</param>
    /// <param name="useMaximumSize">是否占用该磁盘上最大的可用空间。</param>
    /// <param name="style">分区表类型，决定使用 GptType 还是 MbrType。</param>
    public static OperationResult CreatePartition(
        int diskNumber,
        long size,
        long? offset,
        char? driveLetter,
        bool useMaximumSize,
        PartitionStyle style)
    {
        using ManagementObject? disk = FindDisk(diskNumber, out OperationResult error);
        if (disk is null)
        {
            return error;
        }

        Dictionary<string, object> parameters = new()
        {
            ["UseMaximumSize"] = useMaximumSize,
            ["AssignDriveLetter"] = driveLetter.HasValue,
            ["Size"] = useMaximumSize ? 0UL : (ulong)size,
        };

        // 只传当前分区表用得上的类型参数：
        // MBR 磁盘上传 GptType/IsHidden/IsDAX 会被系统直接拒绝（返回 0xa02e），
        // 因此这里既不传 IsHidden 也不传 IsActive，保持最小参数集。
        if (offset.HasValue)
        {
            parameters["Offset"] = (ulong)offset.Value;
        }

        if (driveLetter.HasValue)
        {
            parameters["DriveLetter"] = driveLetter.Value.ToString();
        }

        if (style == PartitionStyle.Gpt)
        {
            parameters["GptType"] = BasicDataGptType;
        }
        else
        {
            parameters["MbrType"] = NtfsMbrType;
        }

        return Invoke(disk, "CreatePartition", parameters);
    }

    /// <summary>删除分区（等价于磁盘管理里的"删除卷"）。</summary>
    public static OperationResult DeletePartition(int diskNumber, int partitionNumber)
    {
        using ManagementObject? partition = FindPartition(diskNumber, partitionNumber, out OperationResult error);
        if (partition is null)
        {
            return error;
        }

        return Invoke(partition, "DeleteObject", []);
    }

    /// <summary>扩展或压缩分区（扩展卷 / 压缩卷）。</summary>
    public static OperationResult ResizePartition(int diskNumber, int partitionNumber, long newSize)
    {
        using ManagementObject? partition = FindPartition(diskNumber, partitionNumber, out OperationResult error);
        if (partition is null)
        {
            return error;
        }

        return Invoke(partition, "Resize", new Dictionary<string, object> { ["Size"] = (ulong)newSize });
    }

    /// <summary>查询分区支持的最小 / 最大尺寸，压缩卷的"可用压缩空间"来自这里。</summary>
    public static OperationResult GetSupportedSize(int diskNumber, int partitionNumber, out long minimum, out long maximum)
    {
        minimum = 0;
        maximum = 0;

        using ManagementObject? partition = FindPartition(diskNumber, partitionNumber, out OperationResult error);
        if (partition is null)
        {
            return error;
        }

        try
        {
            using ManagementBaseObject inParams = partition.GetMethodParameters("GetSupportedSize");
            using ManagementBaseObject outParams = partition.InvokeMethod("GetSupportedSize", inParams, null);
            uint code = Convert.ToUInt32(outParams["ReturnValue"]);
            if (code != 0)
            {
                return OperationResult.Fail(DescribeReturnCode(code));
            }

            minimum = Convert.ToInt64(outParams["SizeMin"]);
            maximum = Convert.ToInt64(outParams["SizeMax"]);
            return OperationResult.Ok();
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"查询分区可用尺寸失败：{ex.Message}");
        }
    }

    /// <summary>格式化卷。</summary>
    public static OperationResult FormatVolume(
        char driveLetter,
        string fileSystem,
        string label,
        bool quickFormat,
        uint allocationUnitSize)
    {
        using ManagementObject? volume = FindVolumeByLetter(driveLetter, out OperationResult error);
        if (volume is null)
        {
            return error;
        }

        Dictionary<string, object> parameters = new()
        {
            ["FileSystem"] = fileSystem,
            ["FileSystemLabel"] = label,
            ["Full"] = !quickFormat,
            ["RunAsJob"] = false,
            ["Force"] = true,
        };

        if (allocationUnitSize > 0)
        {
            parameters["AllocationUnitSize"] = allocationUnitSize;
        }

        return Invoke(volume, "Format", parameters);
    }

    /// <summary>给分区分配盘符（WMI 实现；原生实现见 VolumeDevice.TryAssignDriveLetter）。</summary>
    public static OperationResult AddAccessPath(int diskNumber, int partitionNumber, string accessPath)
    {
        using ManagementObject? partition = FindPartition(diskNumber, partitionNumber, out OperationResult error);
        if (partition is null)
        {
            return error;
        }

        // 关键细节（实测于 Windows 11 26100）：
        // 指定了 AccessPath 时绝不能再传 AssignDriveLetter，
        // 传 false 会让系统返回"拒绝访问"(5)，不传该参数才会按 AccessPath 建立挂载点。
        return Invoke(partition, "AddAccessPath", new Dictionary<string, object> { ["AccessPath"] = accessPath });
    }

    /// <summary>移除分区的盘符或挂载点。</summary>
    public static OperationResult RemoveAccessPath(int diskNumber, int partitionNumber, string accessPath)
    {
        using ManagementObject? partition = FindPartition(diskNumber, partitionNumber, out OperationResult error);
        if (partition is null)
        {
            return error;
        }

        return Invoke(partition, "RemoveAccessPath", new Dictionary<string, object> { ["AccessPath"] = accessPath });
    }

    /// <summary>为分区设置活动 / 隐藏 / 只读等属性。</summary>
    public static OperationResult SetPartitionAttributes(
        int diskNumber,
        int partitionNumber,
        bool? isActive = null,
        bool? isHidden = null,
        bool? isReadOnly = null)
    {
        using ManagementObject? partition = FindPartition(diskNumber, partitionNumber, out OperationResult error);
        if (partition is null)
        {
            return error;
        }

        Dictionary<string, object> parameters = [];
        if (isActive.HasValue)
        {
            parameters["IsActive"] = isActive.Value;
        }

        if (isHidden.HasValue)
        {
            parameters["IsHidden"] = isHidden.Value;
        }

        if (isReadOnly.HasValue)
        {
            parameters["IsReadOnly"] = isReadOnly.Value;
        }

        return parameters.Count == 0 ? OperationResult.Ok() : Invoke(partition, "SetAttributes", parameters);
    }

    // ------------------------------------------------------------------
    // 查询辅助
    // ------------------------------------------------------------------

    /// <summary>按磁盘号取 MSFT_Disk 实例。</summary>
    private static ManagementObject? FindDisk(int diskNumber, out OperationResult error)
        => FindOne($"SELECT * FROM MSFT_Disk WHERE Number = {diskNumber}", $"找不到磁盘 {diskNumber}", out error);

    /// <summary>按磁盘号与分区号取 MSFT_Partition 实例。</summary>
    private static ManagementObject? FindPartition(int diskNumber, int partitionNumber, out OperationResult error)
        => FindOne(
            $"SELECT * FROM MSFT_Partition WHERE DiskNumber = {diskNumber} AND PartitionNumber = {partitionNumber}",
            $"找不到磁盘 {diskNumber} 上的分区 {partitionNumber}",
            out error);

    /// <summary>按盘符取 MSFT_Volume 实例。</summary>
    private static ManagementObject? FindVolumeByLetter(char driveLetter, out OperationResult error)
        => FindOne(
            $"SELECT * FROM MSFT_Volume WHERE DriveLetter = '{char.ToUpperInvariant(driveLetter)}'",
            $"找不到驱动器 {char.ToUpperInvariant(driveLetter)}: 对应的卷",
            out error);

    /// <summary>执行一条 WQL 查询并返回首个实例。</summary>
    private static ManagementObject? FindOne(string query, string notFoundMessage, out OperationResult error)
    {
        try
        {
            using ManagementObjectSearcher searcher = new(CreateScope(), new ObjectQuery(query));
            foreach (ManagementBaseObject item in searcher.Get())
            {
                error = OperationResult.Ok();
                return (ManagementObject)item;
            }

            error = OperationResult.Fail(notFoundMessage);
            return null;
        }
        catch (Exception ex)
        {
            error = OperationResult.Fail($"查询存储信息失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>调用 WMI 方法并统一处理返回值。</summary>
    private static OperationResult Invoke(ManagementObject instance, string method, Dictionary<string, object> parameters)
    {
        try
        {
            using ManagementBaseObject inParams = instance.GetMethodParameters(method);
            foreach (KeyValuePair<string, object> pair in parameters)
            {
                inParams[pair.Key] = pair.Value;
            }

            using ManagementBaseObject outParams = instance.InvokeMethod(method, inParams, null);
            uint code = Convert.ToUInt32(outParams["ReturnValue"]);
            if (code == 0)
            {
                return OperationResult.Ok();
            }

            string extended = ReadExtendedStatus(outParams);
            string message = DescribeReturnCode(code);
            return OperationResult.Fail(extended.Length > 0 ? $"{message}（{extended}）" : message);
        }
        catch (ManagementException ex)
        {
            return OperationResult.Fail($"调用 {method} 失败：{ex.Message}");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail($"调用 {method} 出现异常：{ex.Message}");
        }
    }

    /// <summary>尝试读取 MSFT_StorageExtendedStatus 里的详细信息。</summary>
    private static string ReadExtendedStatus(ManagementBaseObject outParams)
    {
        if (outParams["ExtendedStatus"] is not ManagementBaseObject extended)
        {
            return string.Empty;
        }

        foreach (string name in new[] { "Message", "Description", "ErrorMessage" })
        {
            if (extended[name] is string text && !string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return string.Empty;
    }

    /// <summary>把 WMI 返回码翻译成中文；高 16 位为 HRESULT 时取低 16 位复用统一翻译表。</summary>
    private static string DescribeReturnCode(uint code)
    {
        if ((code & 0xFFFF0000) != 0)
        {
            uint lowWord = code & 0x0000FFFF;
            if (lowWord is > 0 and < 0x4000)
            {
                return Win32Error.Describe((int)lowWord);
            }

            return $"系统返回错误码 0x{code:X8}";
        }

        return Win32Error.Describe((int)code);
    }
}
