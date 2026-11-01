using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DiskManager.Services;

/// <summary>
/// 查"这个文件被哪些进程占用"。
///
/// 用 Windows 的 Restart Manager（rstrtmgr.dll）——安装程序判断"文件被占用无法覆盖"用的就是它。
/// 典型场景：Hyper-V 关闭虚拟机后，VHDX 仍被 vmwp.exe 持有，
/// 这时脱机/弹出都会被系统拒绝，工具需要明确告诉用户是谁占着。
/// </summary>
internal static class FileLockInspector
{
    /// <summary>
    /// 返回占用指定文件的进程（形如 "vmwp (1234)"）。
    /// 查不到时返回空列表——不代表没被占用，只代表 Restart Manager 看不出来。
    /// </summary>
    public static List<string> GetLockingProcesses(IEnumerable<string> filePaths, int maxFiles = 8)
    {
        List<string> result = [];
        string[] files = filePaths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).Take(maxFiles).ToArray();
        if (files.Length == 0)
        {
            return result;
        }

        StringBuilder sessionKey = new(256 + 1);
        int status = RmStartSession(out uint session, 0, sessionKey);
        if (status != 0)
        {
            return result;
        }

        try
        {
            status = RmRegisterResources(session, (uint)files.Length, files, 0, null, 0, null);
            if (status != 0)
            {
                return result;
            }

            uint needed = 0;
            uint count = 0;
            uint reasons = 0;

            // 第一次调用拿数量，第二次拿内容（ERROR_MORE_DATA = 234）
            status = RmGetList(session, out needed, ref count, null, ref reasons);
            if (needed == 0)
            {
                return result;
            }

            RmProcessInfo[] infos = new RmProcessInfo[needed];
            count = needed;
            status = RmGetList(session, out needed, ref count, infos, ref reasons);
            if (status != 0 && status != 234)
            {
                return result;
            }

            for (int i = 0; i < count && i < infos.Length; i++)
            {
                int pid = infos[i].Process.ProcessId;
                string name = infos[i].AppName;

                if (string.IsNullOrWhiteSpace(name) && pid > 0)
                {
                    try
                    {
                        using Process process = Process.GetProcessById(pid);
                        name = process.ProcessName;
                    }
                    catch
                    {
                        name = "未知进程";
                    }
                }

                string entry = pid > 0 ? $"{name} (PID {pid})" : name;
                if (!string.IsNullOrWhiteSpace(entry) && !result.Contains(entry, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(entry);
                }
            }
        }
        finally
        {
            RmEndSession(session);
        }

        return result;
    }

    /// <summary>把占用进程列表拼成一句可读的提示。</summary>
    public static string DescribeHolders(IEnumerable<string> holders)
    {
        List<string> list = [.. holders];
        return list.Count == 0
            ? string.Empty
            : $"当前占用它的进程：{string.Join("、", list)}。";
    }

    // ------------------------------------------------------------------
    // Restart Manager P/Invoke
    // ------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int ProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string AppName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string ServiceShortName;

        public int ApplicationType;
        public uint AppStatus;
        public uint TsSessionId;

        [MarshalAs(UnmanagedType.Bool)]
        public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, StringBuilder strSessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint pSessionHandle,
        uint nFiles,
        string[] rgsFilenames,
        uint nApplications,
        [In] RmUniqueProcess[]? rgApplications,
        uint nServices,
        string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(
        uint dwSessionHandle,
        out uint pnProcInfoNeeded,
        ref uint pnProcInfo,
        [In, Out] RmProcessInfo[]? rgAffectedApps,
        ref uint lpdwRebootReasons);
}
