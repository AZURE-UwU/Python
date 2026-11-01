using System.Diagnostics;
using System.Security.Principal;

namespace DiskManager.Services;

/// <summary>管理员权限检测与自我提权重启。</summary>
internal static class Elevation
{
    /// <summary>当前进程是否以管理员身份运行。</summary>
    public static bool IsElevated
    {
        get
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            WindowsPrincipal principal = new(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>
    /// 以管理员身份重新启动自己（会弹出 UAC 确认框）。
    /// 仅用于调试版本或用户拒绝提权后的补救。
    /// </summary>
    public static bool TryRestartElevated(string arguments, out string error)
    {
        try
        {
            string? path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path))
            {
                error = "无法定位当前程序路径。";
                return false;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
            });

            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = $"提权启动失败：{ex.Message}";
            return false;
        }
    }
}
