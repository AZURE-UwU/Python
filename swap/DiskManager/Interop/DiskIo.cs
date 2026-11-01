using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskManager.Interop;

/// <summary>
/// DeviceIoControl 的统一封装。
/// 所有调用失败都只返回错误码，由上层决定是弹窗、重试还是降级，避免异常穿透界面线程。
/// </summary>
internal static class DiskIo
{
    /// <summary>无输入/输出缓冲的 IOCTL。</summary>
    public static bool TryIoctl(SafeFileHandle handle, uint code, out int error)
        => TryIoctl(handle, code, null, null, out _, out error);

    /// <summary>通用 IOCTL 调用，输入/输出均按字节缓冲传递。</summary>
    public static unsafe bool TryIoctl(
        SafeFileHandle handle,
        uint code,
        byte[]? input,
        byte[]? output,
        out int bytesReturned,
        out int error)
    {
        if (handle.IsInvalid)
        {
            bytesReturned = 0;
            error = Win32Error.ErrorInvalidFunction;
            return false;
        }

        fixed (byte* pIn = input)
        fixed (byte* pOut = output)
        {
            bool ok = NativeMethods.DeviceIoControl(
                handle,
                code,
                (IntPtr)pIn,
                input?.Length ?? 0,
                (IntPtr)pOut,
                output?.Length ?? 0,
                out bytesReturned,
                IntPtr.Zero);

            error = ok ? 0 : Marshal.GetLastWin32Error();
            return ok;
        }
    }

    /// <summary>结构体入参 + 结构体出参的 IOCTL，内部自动处理内存布局。</summary>
    public static unsafe bool TryIoctl<TIn, TOut>(
        SafeFileHandle handle,
        uint code,
        ref TIn input,
        out TOut output,
        out int bytesReturned,
        out int error)
        where TIn : unmanaged
        where TOut : unmanaged
    {
        fixed (TIn* pIn = &input)
        {
            TOut local = default;
            bool ok = NativeMethods.DeviceIoControl(
                handle,
                code,
                (IntPtr)pIn,
                sizeof(TIn),
                (IntPtr)(&local),
                sizeof(TOut),
                out bytesReturned,
                IntPtr.Zero);

            output = local;
            error = ok ? 0 : Marshal.GetLastWin32Error();
            return ok;
        }
    }

    /// <summary>无入参、结构体出参的 IOCTL（如查询几何信息、设备编号）。</summary>
    public static unsafe bool TryIoctl<TOut>(
        SafeFileHandle handle,
        uint code,
        out TOut output,
        out int bytesReturned,
        out int error)
        where TOut : unmanaged
    {
        if (handle.IsInvalid)
        {
            output = default;
            bytesReturned = 0;
            error = Win32Error.ErrorInvalidFunction;
            return false;
        }

        TOut local = default;
        bool ok = NativeMethods.DeviceIoControl(
            handle,
            code,
            IntPtr.Zero,
            0,
            (IntPtr)(&local),
            sizeof(TOut),
            out bytesReturned,
            IntPtr.Zero);

        output = local;
        error = ok ? 0 : Marshal.GetLastWin32Error();
        return ok;
    }

    /// <summary>把结构体转成字节数组，供可变长 IOCTL 输入使用。</summary>
    public static byte[] ToBytes<T>(ref T value) where T : unmanaged
    {
        int size = Marshal.SizeOf<T>();
        byte[] buffer = new byte[size];
        unsafe
        {
            fixed (byte* p = buffer)
            fixed (T* pv = &value)
            {
                Buffer.MemoryCopy(pv, p, size, size);
            }
        }
        return buffer;
    }
}
