using System.Runtime.InteropServices;

namespace Passwrd.Services;

internal static class CrashLogger
{
    private static readonly object SyncRoot = new();

    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Passwrd",
        "crash.log");

    public static void Write(string source, Exception? exception)
    {
        try
        {
            string? directory = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string message =
                $"[{DateTimeOffset.Now:O}] {source}{Environment.NewLine}" +
                FormatException(exception) +
                Environment.NewLine +
                Environment.NewLine;

            lock (SyncRoot)
            {
                File.AppendAllText(LogPath, message);
            }
        }
        catch
        {
        }
    }

    private static string FormatException(Exception? exception)
    {
        if (exception is null)
        {
            return "未知异常";
        }

        string position = string.Empty;
        object? line = exception.GetType().GetProperty("LineNumber")?.GetValue(exception);
        object? column = exception.GetType().GetProperty("LinePosition")?.GetValue(exception);
        if (line is not null || column is not null)
        {
            position = $"位置：{line}:{column}{Environment.NewLine}";
        }

        string detail =
            $"类型：{exception.GetType().FullName}{Environment.NewLine}" +
            $"HRESULT：0x{exception.HResult:X8}{Environment.NewLine}" +
            position +
            exception +
            Environment.NewLine;

        string? restrictedError = TryGetRestrictedError();
        if (!string.IsNullOrWhiteSpace(restrictedError))
        {
            detail += $"WinRT 错误信息：{restrictedError}{Environment.NewLine}";
        }

        if (exception.InnerException is not null)
        {
            detail += $"内部异常：{FormatException(exception.InnerException)}";
        }

        return detail;
    }

    private static string? TryGetRestrictedError()
    {
        IRestrictedErrorInfo? errorInfo = null;

        try
        {
            int result = GetRestrictedErrorInfo(out errorInfo);
            if (result < 0 || errorInfo is null)
            {
                return null;
            }

            errorInfo.GetErrorDetails(
                out string description,
                out int errorCode,
                out string restrictedDescription,
                out string capabilitySid);
            errorInfo.GetReference(out string reference);

            return string.Join(
                Environment.NewLine,
                new[]
                {
                    description,
                    restrictedDescription,
                    reference,
                    capabilitySid,
                    $"错误码：0x{errorCode:X8}",
                }.Where(value => !string.IsNullOrWhiteSpace(value)));
        }
        catch
        {
            return null;
        }
        finally
        {
            if (errorInfo is not null && Marshal.IsComObject(errorInfo))
            {
                Marshal.ReleaseComObject(errorInfo);
            }
        }
    }

    [DllImport("combase.dll")]
    private static extern int GetRestrictedErrorInfo(out IRestrictedErrorInfo? errorInfo);

    [ComImport]
    [Guid("DF0B3D60-548F-101B-8E65-08002B2BD119")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IRestrictedErrorInfo
    {
        void GetErrorDetails(
            [MarshalAs(UnmanagedType.BStr)] out string description,
            out int error,
            [MarshalAs(UnmanagedType.BStr)] out string restrictedDescription,
            [MarshalAs(UnmanagedType.BStr)] out string capabilitySid);

        void GetReference([MarshalAs(UnmanagedType.BStr)] out string reference);
    }
}
