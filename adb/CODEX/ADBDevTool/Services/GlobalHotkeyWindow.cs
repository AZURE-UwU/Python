/// @file    GlobalHotkeyWindow.cs
/// @author  Bowen
/// @module  ADBDevTool / 全局热键
///
/// 设计约束：
/// 1. 用独立的 message-only 窗口（父窗口 HWND_MESSAGE）承载 RegisterHotKey，不去子类化 WinUI 主窗口的过程函数，
///    避免干扰 XAML 自己的消息处理；该窗口建在 UI 线程上，消息走同一个消息循环。
/// 2. RegisterHotKey 失败时错误码 1409 表示组合键已被其它程序占用。

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ADBDevTool.Services;

internal sealed class GlobalHotkeyWindow : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int ModNoRepeat = 0x4000;
    private const int HwndMessage = -3;
    private const int ErrorHotkeyAlreadyRegistered = 1409;
    private const string ClassName = "ADBDevToolHotkeyWindow";

    private static readonly WndProcDelegate StaticWindowProc = WindowProc;
    private static GlobalHotkeyWindow? _current;

    private readonly Action<int> _onHotkey;
    private IntPtr _hwnd;

    public GlobalHotkeyWindow(Action<int> onHotkey)
    {
        _onHotkey = onHotkey;

        var module = GetModuleHandle(null);
        var windowClass = new WndClassEx
        {
            cbSize = Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(StaticWindowProc),
            hInstance = module,
            lpszClassName = ClassName,
        };
        RegisterClassEx(ref windowClass);

        _hwnd = CreateWindowEx(
            0,
            ClassName,
            "ADBDevToolHotkey",
            0,
            0,
            0,
            0,
            0,
            new IntPtr(HwndMessage),
            IntPtr.Zero,
            module,
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException($"创建热键消息窗口失败（错误码 {Marshal.GetLastWin32Error()}）。");
        }

        _current = this;
    }

    public bool TryRegister(int id, HotkeySpec spec, out string error)
    {
        error = string.Empty;
        Unregister(id);

        if (spec.IsEmpty)
        {
            return true;
        }

        if (RegisterHotKey(_hwnd, id, (uint)(spec.Modifiers | ModNoRepeat), (uint)spec.KeyCode))
        {
            return true;
        }

        var code = Marshal.GetLastWin32Error();
        error = code == ErrorHotkeyAlreadyRegistered
            ? $"{spec} 已被其它程序占用"
            : $"注册 {spec} 失败（错误码 {code}）";
        return false;
    }

    public void Unregister(int id)
    {
        if (_hwnd != IntPtr.Zero)
        {
            UnregisterHotKey(_hwnd, id);
        }
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero)
        {
            UnregisterHotKey(_hwnd, 1);
            UnregisterHotKey(_hwnd, 2);
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        if (ReferenceEquals(_current, this))
        {
            _current = null;
        }
    }

    private static IntPtr WindowProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmHotkey)
        {
            _current?._onHotkey(wParam.ToInt32());
            return IntPtr.Zero;
        }

        return DefWindowProc(hWnd, message, wParam, lParam);
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WndClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int exStyle,
        string className,
        string windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
