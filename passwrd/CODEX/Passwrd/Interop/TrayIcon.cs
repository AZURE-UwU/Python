using System.Runtime.InteropServices;

namespace Passwrd.Interop;

internal sealed class TrayIcon : IDisposable
{
    private const uint TrayIconId = 1;
    private const uint CallbackMessage = NativeMethods.WmApp + 20;
    private const uint MenuOpen = 1001;
    private const uint MenuToggleHotkeys = 1002;
    private const uint MenuExit = 1003;

    private readonly nint _windowHandle;
    private readonly Action _openSettings;
    private readonly Action _toggleHotkeys;
    private readonly Action _exit;
    private readonly Func<bool> _areHotkeysEnabled;
    private nint _iconHandle;
    private bool _ownsIconHandle;
    private bool _isAdded;
    private bool _disposed;

    public TrayIcon(
        nint windowHandle,
        Action openSettings,
        Action toggleHotkeys,
        Action exit,
        Func<bool> areHotkeysEnabled)
    {
        _windowHandle = windowHandle;
        _openSettings = openSettings;
        _toggleHotkeys = toggleHotkeys;
        _exit = exit;
        _areHotkeysEnabled = areHotkeysEnabled;
    }

    public bool Add()
    {
        string iconPath = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Passwrd.ico");
        _iconHandle = NativeMethods.LoadImage(
            nint.Zero,
            iconPath,
            NativeMethods.ImageIcon,
            0,
            0,
            NativeMethods.LrLoadFromFile | NativeMethods.LrDefaultSize);
        _ownsIconHandle = _iconHandle != nint.Zero;

        if (_iconHandle == nint.Zero)
        {
            _iconHandle = NativeMethods.LoadIcon(nint.Zero, NativeMethods.IdiApplication);
        }

        NativeMethods.NotifyIconData data = new()
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.NotifyIconData>(),
            WindowHandle = _windowHandle,
            Id = TrayIconId,
            Flags = NativeMethods.NifMessage | NativeMethods.NifIcon | NativeMethods.NifTip,
            CallbackMessage = CallbackMessage,
            IconHandle = _iconHandle,
            Tip = "Passwrd - 全局快捷输入",
            Info = string.Empty,
            InfoTitle = string.Empty,
        };

        _isAdded = NativeMethods.Shell_NotifyIcon(NativeMethods.NimAdd, ref data);
        return _isAdded;
    }

    public bool TryHandleMessage(uint message, nint lParam)
    {
        if (message != CallbackMessage)
        {
            return false;
        }

        int mouseMessage = (int)(lParam.ToInt64() & 0xFFFF);
        if (mouseMessage == NativeMethods.WmLButtonDblClk)
        {
            _openSettings();
        }
        else if (mouseMessage == NativeMethods.WmRButtonUp)
        {
            ShowContextMenu();
        }

        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_isAdded)
        {
            NativeMethods.NotifyIconData data = new()
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.NotifyIconData>(),
                WindowHandle = _windowHandle,
                Id = TrayIconId,
                Info = string.Empty,
                Tip = string.Empty,
                InfoTitle = string.Empty,
            };

            NativeMethods.Shell_NotifyIcon(NativeMethods.NimDelete, ref data);
            _isAdded = false;
        }

        if (_ownsIconHandle && _iconHandle != nint.Zero)
        {
            NativeMethods.DestroyIcon(_iconHandle);
            _iconHandle = nint.Zero;
            _ownsIconHandle = false;
        }

        _disposed = true;
    }

    private void ShowContextMenu()
    {
        nint menuHandle = NativeMethods.CreatePopupMenu();
        if (menuHandle == nint.Zero)
        {
            return;
        }

        try
        {
            NativeMethods.AppendMenu(
                menuHandle,
                NativeMethods.MfString,
                MenuOpen,
                "打开设置");

            uint toggleFlags = NativeMethods.MfString;
            if (_areHotkeysEnabled())
            {
                toggleFlags |= NativeMethods.MfChecked;
            }

            NativeMethods.AppendMenu(
                menuHandle,
                toggleFlags,
                MenuToggleHotkeys,
                "启用全局快捷键");
            NativeMethods.AppendMenu(menuHandle, NativeMethods.MfSeparator, 0, null);
            NativeMethods.AppendMenu(
                menuHandle,
                NativeMethods.MfString,
                MenuExit,
                "退出 Passwrd");

            uint command;
            if (NativeMethods.GetCursorPos(out NativeMethods.Point point))
            {
                NativeMethods.SetForegroundWindow(_windowHandle);
                command = NativeMethods.TrackPopupMenuEx(
                    menuHandle,
                    NativeMethods.TpmReturnCmd | NativeMethods.TpmRightButton,
                    point.X,
                    point.Y,
                    _windowHandle,
                    nint.Zero);
                NativeMethods.PostMessage(_windowHandle, NativeMethods.WmNull, nint.Zero, nint.Zero);
            }
            else
            {
                command = 0;
            }

            switch (command)
            {
                case MenuOpen:
                    _openSettings();
                    break;
                case MenuToggleHotkeys:
                    _toggleHotkeys();
                    break;
                case MenuExit:
                    _exit();
                    break;
            }
        }
        finally
        {
            NativeMethods.DestroyMenu(menuHandle);
        }
    }
}
