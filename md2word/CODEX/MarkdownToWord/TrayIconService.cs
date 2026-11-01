using System.Runtime.InteropServices;

namespace MarkdownToWord;

/// <summary>Win32 系统托盘图标；消息回调由主窗口的隐藏顶层窗口分发。</summary>
internal sealed class TrayIconService : IDisposable
{
    private const uint NimAdd = 0;
    private const uint NimDelete = 2;
    private const uint NifMessage = 0x0001;
    private const uint NifIcon = 0x0002;
    private const uint NifTip = 0x0004;
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x0010;
    private const uint LrDefaultSize = 0x0040;
    private const uint MfString = 0;
    private const uint MfSeparator = 0x0800;
    private const uint TpmReturnCmd = 0x0100;
    private const uint TpmRightButton = 0x0002;
    private const int WmLeftButtonDoubleClick = 0x0203;
    private const int WmRightButtonUp = 0x0205;
    private const int WmContextMenu = 0x007B;
    private const int WmNull = 0;
    private const uint TrayCallbackMessage = 0x8000 + 37;
    private const uint TrayIconId = 1;
    private const int MenuOpen = 1;
    private const int MenuExit = 2;

    private readonly IntPtr _owner;
    private readonly Action _open;
    private readonly Action _exit;
    private readonly IntPtr _icon;
    private readonly string _toolTip;
    private bool _disposed;

    public TrayIconService(IntPtr owner, string iconPath, string toolTip, Action open, Action exit)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(exit);

        _owner = owner;
        _open = open;
        _exit = exit;
        _toolTip = toolTip;
        _icon = LoadImage(
            IntPtr.Zero,
            iconPath,
            ImageIcon,
            0,
            0,
            LrLoadFromFile | LrDefaultSize);
        if (_icon == IntPtr.Zero)
        {
            throw new InvalidOperationException("无法加载托盘图标。");
        }

        var data = CreateData();
        if (!ShellNotifyIcon(NimAdd, ref data))
        {
            DestroyIcon(_icon);
            throw new InvalidOperationException("无法创建托盘图标。");
        }
    }

    public bool HandleMessage(uint message, IntPtr lParam)
    {
        if (message != TrayCallbackMessage)
        {
            return false;
        }

        var notification = lParam.ToInt64() & 0xFFFF;
        if (notification == WmLeftButtonDoubleClick)
        {
            _open();
            return true;
        }

        if (notification is WmRightButtonUp or WmContextMenu)
        {
            ShowMenu();
        }

        return true;
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            AppendMenu(menu, MfString, MenuOpen, "打开");
            AppendMenu(menu, MfSeparator, 0, null);
            AppendMenu(menu, MfString, MenuExit, "退出");

            GetCursorPos(out var point);
            SetForegroundWindow(_owner);
            var command = TrackPopupMenu(
                menu,
                TpmReturnCmd | TpmRightButton,
                point.X,
                point.Y,
                0,
                _owner,
                IntPtr.Zero);
            PostMessage(_owner, WmNull, IntPtr.Zero, IntPtr.Zero);

            if (command == MenuOpen)
            {
                _open();
            }
            else if (command == MenuExit)
            {
                _exit();
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private NotifyIconData CreateData()
    {
        return new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(),
            Window = _owner,
            Id = TrayIconId,
            Flags = NifMessage | NifIcon | NifTip,
            CallbackMessage = TrayCallbackMessage,
            Icon = _icon,
            Tip = _toolTip,
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var data = CreateData();
        ShellNotifyIcon(NimDelete, ref data);
        DestroyIcon(_icon);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr Icon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;

        public uint State;
        public uint StateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;

        public uint TimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;

        public uint InfoFlags;
        public Guid ItemGuid;
        public IntPtr BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(
        IntPtr instance,
        string name,
        uint type,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, uint itemId, string? text);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(
        IntPtr menu,
        uint flags,
        int x,
        int y,
        int reserved,
        IntPtr window,
        IntPtr rectangle);
}
