using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DiskManager.Interop;

/// <summary>从系统卷管理器读到的原始卷信息。</summary>
internal sealed class RawVolume
{
    /// <summary>卷 GUID 路径，形如 \\?\Volume{guid}\。</summary>
    public string VolumeName { get; set; } = string.Empty;

    /// <summary>挂载点列表：盘符（C:\）或文件夹挂载点。</summary>
    public List<string> MountPoints { get; } = [];

    public string DriveLetters { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string FileSystem { get; set; } = string.Empty;
    public uint SerialNumber { get; set; }
    public long Size { get; set; }
    public long FreeSpace { get; set; }
    public bool IsNtfs { get; set; }

    /// <summary>卷是否可正常读取（脱机磁盘上的卷会读取失败）。</summary>
    public bool IsAccessible { get; set; }

    /// <summary>IOCTL 读到的磁盘区间，用于判断卷落在哪些磁盘上。</summary>
    public List<DiskExtent> Extents { get; } = [];
}

/// <summary>卷枚举与卷级 IOCTL 封装。</summary>
internal static class VolumeDevice
{
    /// <summary>枚举系统已知的全部卷（含无盘符的隐藏分区）。</summary>
    public static List<RawVolume> Enumerate()
    {
        List<RawVolume> volumes = [];
        StringBuilder nameBuffer = new(260);

        IntPtr findHandle = NativeMethods.FindFirstVolumeW(nameBuffer, nameBuffer.Capacity);
        if (findHandle == new IntPtr(-1))
        {
            return volumes;
        }

        try
        {
            do
            {
                string volumeName = nameBuffer.ToString();
                if (string.IsNullOrWhiteSpace(volumeName))
                {
                    continue;
                }

                RawVolume volume = new() { VolumeName = volumeName };
                ReadMountPoints(volume);
                ReadFileSystemInfo(volume);
                ReadExtents(volume);
                volumes.Add(volume);
            }
            while (NativeMethods.FindNextVolumeW(findHandle, nameBuffer, nameBuffer.Capacity));
        }
        finally
        {
            NativeMethods.FindVolumeClose(findHandle);
        }

        return volumes;
    }

    /// <summary>读取卷的挂载点（盘符 / NTFS 文件夹挂载点）。</summary>
    private static void ReadMountPoints(RawVolume volume)
    {
        NativeMethods.GetVolumePathNamesForVolumeNameW(volume.VolumeName, [], 0, out int required);
        if (required <= 0)
        {
            return;
        }

        char[] buffer = new char[required];
        if (!NativeMethods.GetVolumePathNamesForVolumeNameW(volume.VolumeName, buffer, buffer.Length, out int written))
        {
            return;
        }

        // 返回的是双空字符结尾的 MULTI_SZ，逐段拆开。
        int start = 0;
        for (int i = 0; i < written && i < buffer.Length; i++)
        {
            if (buffer[i] != '\0')
            {
                continue;
            }

            if (i > start)
            {
                string path = new(buffer, start, i - start);
                volume.MountPoints.Add(path);

                if (path.Length >= 2 && path[1] == ':')
                {
                    volume.DriveLetters += path[0];
                }
            }

            start = i + 1;
        }
    }

    /// <summary>读取卷标、文件系统与容量。</summary>
    private static void ReadFileSystemInfo(RawVolume volume)
    {
        StringBuilder label = new(128);
        StringBuilder fileSystem = new(32);
        bool ok = NativeMethods.GetVolumeInformationW(
            volume.VolumeName,
            label,
            label.Capacity,
            out uint serial,
            out _,
            out _,
            fileSystem,
            fileSystem.Capacity);

        if (ok)
        {
            volume.Label = label.ToString();
            volume.FileSystem = fileSystem.ToString();
            volume.SerialNumber = serial;
            volume.IsNtfs = string.Equals(volume.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase);
        }

        if (NativeMethods.GetDiskFreeSpaceExW(volume.VolumeName, out _, out ulong total, out ulong free))
        {
            volume.Size = (long)total;
            volume.FreeSpace = (long)free;
            volume.IsAccessible = true;
            return;
        }

        // 已脱机或被系统独占的卷拿不到可用空间，退回 IOCTL 读卷长度。
        using SafeFileHandle handle = OpenVolume(volume.VolumeName, writeAccess: false);
        if (!handle.IsInvalid &&
            DiskIo.TryIoctl(handle, IoCtl.DiskGetLengthInfo, out GetLengthInformation length, out _, out _))
        {
            volume.Size = length.Length;
        }
    }

    /// <summary>读取卷由哪些磁盘区间组成（用于"磁盘"列与磁盘图）。</summary>
    private static void ReadExtents(RawVolume volume)
    {
        using SafeFileHandle handle = OpenVolume(volume.VolumeName, writeAccess: false);
        if (handle.IsInvalid)
        {
            return;
        }

        /*
         * VOLUME_DISK_EXTENTS 的原生布局：
         *     DWORD       NumberOfDiskExtents;   // 偏移 0
         *     DISK_EXTENT Extents[];             // 偏移 8，不是 4！
         *
         * DISK_EXTENT 里含 LARGE_INTEGER，按 8 字节对齐，
         * 所以数组起始位置前面有 4 字节填充。按偏移 4 解析会把
         * 磁盘号读成 0、偏移整体错位（实测就是被这个坑住过一次）。
         */
        const int ExtentsOffset = 8;
        const int ExtentSize = 24;

        int bufferSize = 1024;
        while (bufferSize <= 64 * 1024)
        {
            byte[] buffer = new byte[bufferSize];
            if (DiskIo.TryIoctl(handle, IoCtl.VolumeGetDiskExtents, null, buffer, out int returned, out int code))
            {
                if (returned < ExtentsOffset)
                {
                    return;
                }

                uint count = BitConverter.ToUInt32(buffer, 0);
                int available = (returned - ExtentsOffset) / ExtentSize;
                int take = (int)Math.Min(count, (uint)available);

                for (int i = 0; i < take; i++)
                {
                    volume.Extents.Add(MemoryMarshal.Read<DiskExtent>(
                        buffer.AsSpan(ExtentsOffset + (i * ExtentSize), ExtentSize)));
                }

                return;
            }

            if (code is Win32Error.ErrorInsufficientBuffer or Win32Error.ErrorMoreData)
            {
                bufferSize = Math.Max(bufferSize * 2, returned + Marshal.SizeOf<DiskExtent>());
                continue;
            }

            return; // 脱机卷、RAW 卷等读不到区间属于正常情况
        }
    }

    /// <summary>打开卷句柄；volumeName 末尾的反斜杠必须去掉才能用于 CreateFile。</summary>
    public static SafeFileHandle OpenVolume(string volumeName, bool writeAccess)
    {
        string path = volumeName.TrimEnd('\\');
        uint access = Win32Access.GenericRead | (writeAccess ? Win32Access.GenericWrite : 0);

        SafeFileHandle handle = NativeMethods.CreateFileW(
            path,
            access,
            Win32Access.FileShareRead | Win32Access.FileShareWrite,
            IntPtr.Zero,
            Win32Access.OpenExisting,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid && writeAccess)
        {
            // 写权限失败时退回只读重试，至少保证信息能读出来。
            handle.Dispose();
            handle = NativeMethods.CreateFileW(
                path,
                Win32Access.GenericRead,
                Win32Access.FileShareRead | Win32Access.FileShareWrite,
                IntPtr.Zero,
                Win32Access.OpenExisting,
                0,
                IntPtr.Zero);
        }

        return handle;
    }

    /// <summary>读取 NTFS 卷的簇、MFT 等细节，供"属性"对话框展示。</summary>
    public static bool TryGetNtfsData(string volumeName, out NtfsVolumeData data)
    {
        data = default;
        using SafeFileHandle handle = OpenVolume(volumeName, writeAccess: false);
        if (handle.IsInvalid)
        {
            return false;
        }

        return DiskIo.TryIoctl(handle, IoCtl.FsCtlGetNtfsVolumeData, out data, out _, out _);
    }

    /// <summary>检查某个盘符当前是否已被占用。</summary>
    public static bool IsDriveLetterUsed(char letter)
    {
        uint mask = NativeMethods.GetLogicalDrives();
        int index = char.ToUpperInvariant(letter) - 'A';
        if (index is < 0 or > 25)
        {
            return true;
        }

        return (mask & (1u << index)) != 0;
    }

    /// <summary>取当前所有已占用盘符。</summary>
    public static HashSet<char> GetUsedDriveLetters()
    {
        HashSet<char> used = [];
        uint mask = NativeMethods.GetLogicalDrives();
        for (int i = 0; i < 26; i++)
        {
            if ((mask & (1u << i)) != 0)
            {
                used.Add((char)('A' + i));
            }
        }

        return used;
    }

    /// <summary>把盘符分配给指定卷（等价于磁盘管理里的"更改驱动器号和路径"）。</summary>
    public static bool TryAssignDriveLetter(char letter, string volumeName, out string error)
    {
        string mountPoint = $"{char.ToUpperInvariant(letter)}:\\";
        if (NativeMethods.SetVolumeMountPointW(mountPoint, volumeName))
        {
            error = string.Empty;
            return true;
        }

        error = $"分配驱动器号 {mountPoint} 失败：{Win32Error.LastMessage()}";
        return false;
    }

    /// <summary>删除挂载点（盘符或文件夹挂载点）。</summary>
    public static bool TryRemoveMountPoint(string mountPoint, out string error)
    {
        if (NativeMethods.DeleteVolumeMountPointW(mountPoint))
        {
            error = string.Empty;
            return true;
        }

        error = $"删除挂载点 {mountPoint} 失败：{Win32Error.LastMessage()}";
        return false;
    }

    /// <summary>取得卷所在磁盘号列表。</summary>
    public static IEnumerable<int> GetDiskNumbers(RawVolume volume)
        => volume.Extents.Select(e => (int)e.DiskNumber).Distinct();

    /// <summary>
    /// 把某块磁盘上的所有卷强制脱机（卸载文件系统）。
    /// 用途：弹出设备前释放占用——系统会因为"卷正在使用"否决弹出请求，
    /// 把卷脱机后再弹，成功率明显提高（等价于磁盘管理里的"脱机卷"）。
    /// </summary>
    public static int TakeVolumesOffline(int diskNumber, out int failed)
    {
        using IDisposable scope = DiskManager.Services.OperationLog.Scope($"TakeVolumesOffline 磁盘 {diskNumber}");

        int succeeded = 0;
        failed = 0;

        foreach (RawVolume volume in Enumerate())
        {
            if (!volume.Extents.Any(e => (int)e.DiskNumber == diskNumber))
            {
                continue;
            }

            using SafeFileHandle handle = OpenVolume(volume.VolumeName, writeAccess: true);
            if (handle.IsInvalid)
            {
                failed++;
                continue;
            }

            if (DiskIo.TryIoctl(handle, IoCtl.VolumeOffline, out _))
            {
                succeeded++;
            }
            else
            {
                failed++;
            }
        }

        return succeeded;
    }
}
