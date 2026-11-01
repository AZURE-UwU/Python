/// @file    WindowActivator.cs
/// @author  Bowen
/// @module  ADBDevTool / 窗口恢复与前台抢夺
///
/// 外部契约（可核验指针，来源 Win32 SetForegroundWindow / AttachThreadInput 文档）：
/// 1. SetForegroundWindow 受前台锁限制：只有前台进程、收到最后一个输入事件的进程等才有资格调用，
///    否则调用会失败或只闪任务栏。实测：裸调用被挡掉。
/// 2. AttachThreadInput 把当前线程挂到前台窗口所属线程的输入队列后，SetForegroundWindow 可以生效。
///    实测对经典 Win32 前台程序有效；对 UWP 一类前台窗口（foreground 属于 ApplicationFrameHost）可能仍然失败。
/// 3. SwitchToThisWindow 未见于公开文档，但长期存在于 user32.dll 且被大量托盘工具使用，作为最后兜底。

using System;
using System.Runtime.InteropServices;

namespace ADBDevTool.Services;

internal static class WindowActivator
{
    private const int SwRestore = 9;
    private const int SwMinimize = 6;

    public static bool IsMinimized(IntPtr hwnd) => hwnd != IntPtr.Zero && IsIconic(hwnd);

    /// <summary>窗口的 DIP 到物理像素倍率；取不到窗口或 DPI 时按 1.0 处理。</summary>
    public static double GetScale(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return 1.0;
        }

        var dpi = GetDpiForWindow(hwnd);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    public static void Minimize(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero)
        {
            ShowWindow(hwnd, SwMinimize);
        }
    }

    /// <summary>把窗口恢复出来并尽量抢到前台；返回值表示最终是否真的成为前台窗口。</summary>
    public static bool RestoreAndActivate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        ShowWindow(hwnd, SwRestore);
        if (IsForeground(hwnd))
        {
            return true;
        }

        SetForegroundWindow(hwnd);
        if (IsForeground(hwnd))
        {
            return true;
        }

        AttachToForegroundThread(hwnd);
        if (IsForeground(hwnd))
        {
            return true;
        }

        // 临时处置：前两招都被前台锁挡下时才用未公开接口兜底。
        // 失效条件：user32.dll 不再导出 SwitchToThisWindow。移除条件：Windows 提供公开的等价激活接口。
        SwitchToThisWindow(hwnd, true);
        return IsForeground(hwnd);
    }

    private static void AttachToForegroundThread(IntPtr hwnd)
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return;
        }

        var foregroundThread = GetWindowThreadProcessId(foreground, IntPtr.Zero);
        var currentThread = GetCurrentThreadId();
        if (foregroundThread == 0 || foregroundThread == currentThread)
        {
            SetForegroundWindow(hwnd);
            return;
        }

        AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            SetForegroundWindow(hwnd);
        }
        finally
        {
            AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    public static bool IsForeground(IntPtr hwnd) => hwnd != IntPtr.Zero && GetForegroundWindow() == hwnd;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attachThread, uint attachToThread, bool attach);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr hWnd, bool altTab);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
