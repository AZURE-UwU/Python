using System.Runtime.InteropServices;

namespace Passwrd.Interop;

internal static class NativeMethods
{
    public const int WmHotkey = 0x0312;
    public const int WmApp = 0x8000;
    public const int WmNull = 0x0000;

    public const int SwHide = 0;
    public const int SwShowNormal = 1;
    public const int SwRestore = 9;

    public const uint ModNoRepeat = 0x4000;

    public const int NifMessage = 0x00000001;
    public const int NifIcon = 0x00000002;
    public const int NifTip = 0x00000004;
    public const int NimAdd = 0x00000000;
    public const int NimDelete = 0x00000002;

    public const int WmLButtonDblClk = 0x0203;
    public const int WmRButtonUp = 0x0205;

    public const int MfString = 0x00000000;
    public const int MfChecked = 0x00000008;
    public const int MfSeparator = 0x00000800;
    public const int TpmRightButton = 0x0002;
    public const int TpmReturnCmd = 0x0100;

    public const int IdiApplication = 32512;
    public const uint ImageIcon = 1;
    public const uint LrLoadFromFile = 0x00000010;
    public const uint LrDefaultSize = 0x00000040;

    public const int VkShift = 0x10;
    public const int VkControl = 0x11;
    public const int VkMenu = 0x12;
    public const int VkLWin = 0x5B;
    public const int VkRWin = 0x5C;

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NotifyIconData
    {
        public uint Size;
        public nint WindowHandle;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint IconHandle;

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
        public nint BalloonIconHandle;
    }

    public delegate nint SubclassProc(
        nint windowHandle,
        uint message,
        nint wParam,
        nint lParam,
        nuint subclassId,
        nint referenceData);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(nint windowHandle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(nint windowHandle, int id);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(nint windowHandle, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(nint windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(nint windowHandle, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    public static extern nint LoadIcon(nint instanceHandle, nint iconName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint LoadImage(
        nint instanceHandle,
        string imageName,
        uint imageType,
        int width,
        int height,
        uint loadFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(nint iconHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out Point point);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowSubclass(
        nint windowHandle,
        SubclassProc subclassProc,
        nuint subclassId,
        nint referenceData);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RemoveWindowSubclass(
        nint windowHandle,
        SubclassProc subclassProc,
        nuint subclassId);

    [DllImport("comctl32.dll")]
    public static extern nint DefSubclassProc(
        nint windowHandle,
        uint message,
        nint wParam,
        nint lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Shell_NotifyIcon(
        uint message,
        ref NotifyIconData data);

    [DllImport("user32.dll")]
    public static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AppendMenu(
        nint menuHandle,
        uint flags,
        nuint itemId,
        string? itemText);

    [DllImport("user32.dll")]
    public static extern uint TrackPopupMenuEx(
        nint menuHandle,
        uint flags,
        int x,
        int y,
        nint windowHandle,
        nint parameters);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyMenu(nint menuHandle);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(
        uint inputCount,
        Input[] inputs,
        int inputSize);

    [StructLayout(LayoutKind.Sequential)]
    public struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeyboardInput Keyboard;

        [FieldOffset(0)]
        public HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HardwareInput
    {
        public uint Message;
        public ushort ParameterLow;
        public ushort ParameterHigh;
    }
}
