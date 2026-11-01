using System.ComponentModel;
using System.Runtime.InteropServices;

namespace VhdxCopier;

internal static class NativeCopy
{
    public readonly record struct Progress(long TotalBytes, long BytesCopied);

    public readonly record struct Result(long TotalBytes, long BytesCopied);

    private enum CopyProgressResult : uint
    {
        Continue = 0,
        Cancel = 1,
        Stop = 2,
        Quiet = 3
    }

    private enum CopyProgressCallbackReason : uint
    {
        ChunkFinished = 0,
        StreamSwitch = 1
    }

    [Flags]
    private enum CopyFileFlags : uint
    {
        None = 0,
        FailIfExists = 1,
        Restartable = 2,
        AllowDecryptedDestination = 8,
        CopySymlink = 0x800,
        NoBuffering = 0x1000
    }

    private delegate CopyProgressResult CopyProgressRoutine(
        long totalFileSize,
        long totalBytesTransferred,
        long streamSize,
        long streamBytesTransferred,
        uint streamNumber,
        CopyProgressCallbackReason callbackReason,
        IntPtr sourceFile,
        IntPtr destinationFile,
        IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CopyFileExW(
        string existingFileName,
        string newFileName,
        CopyProgressRoutine progressRoutine,
        IntPtr data,
        ref int cancel,
        CopyFileFlags flags);

    public static Result Copy(string sourcePath, string destinationPath, CopyContext context)
    {
        var handle = GCHandle.Alloc(context);
        var cancel = context.CancellationToken.IsCancellationRequested ? 1 : 0;

        try
        {
            var succeeded = CopyFileExW(
                ToExtendedPath(sourcePath),
                ToExtendedPath(destinationPath),
                ProgressCallback,
                GCHandle.ToIntPtr(handle),
                ref cancel,
                CopyFileFlags.None);

            if (!succeeded)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == 1223 || context.CancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(context.CancellationToken);
                }

                throw new Win32Exception(error, $"复制失败：{new Win32Exception(error).Message}");
            }

            return new Result(context.TotalBytes, context.BytesCopied);
        }
        finally
        {
            handle.Free();
        }
    }

    private static CopyProgressResult ProgressCallback(
        long totalFileSize,
        long totalBytesTransferred,
        long streamSize,
        long streamBytesTransferred,
        uint streamNumber,
        CopyProgressCallbackReason callbackReason,
        IntPtr sourceFile,
        IntPtr destinationFile,
        IntPtr data)
    {
        var handle = GCHandle.FromIntPtr(data);
        if (handle.Target is not CopyContext context)
        {
            return CopyProgressResult.Stop;
        }

        if (totalFileSize > 0)
        {
            context.TotalBytes = totalFileSize;
        }
        context.BytesCopied = totalBytesTransferred;

        if (context.CancellationToken.IsCancellationRequested)
        {
            return CopyProgressResult.Cancel;
        }

        context.ReportProgress();
        return CopyProgressResult.Continue;
    }

    private static string ToExtendedPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return path;
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return @"\\?\UNC\" + path.Substring(2);
        }

        return @"\\?\" + path;
    }

    internal sealed class CopyContext
    {
        public CopyContext(
            CancellationToken cancellationToken,
            Action<Progress> onProgress)
        {
            CancellationToken = cancellationToken;
            OnProgress = onProgress;
        }

        public long TotalBytes { get; set; }

        public long BytesCopied { get; set; }

        public CancellationToken CancellationToken { get; }

        private Action<Progress> OnProgress { get; }

        public void ReportProgress()
        {
            OnProgress(new Progress(TotalBytes, BytesCopied));
        }
    }
}
