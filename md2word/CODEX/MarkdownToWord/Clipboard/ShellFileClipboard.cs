using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace MarkdownToWord.Clipboard;

/// <summary>
/// 用"资源管理器里复制文件"的那套 shell 数据对象把 docx 放进剪贴板。
///
/// 为什么不继续用 WinRT 的 <c>DataPackage.SetStorageItems</c>：实测它只写出
/// <c>FileGroupDescriptorW</c>（带 FD_LINKUI 链接标志、没有 <c>FileContents</c> 数据流、
/// <c>FileNameW</c> 还是空串）。Word 拿到这种内容会把它当成"链接到某个文件的对象"，
/// 取内容时失败并弹出"Word 无法获取 … 链接的数据"。
/// 换成 shell 的数据对象后，剪贴板内容和在资源管理器里对一个文件按 Ctrl+C 完全一致
/// （CF_HDROP + FileGroupDescriptorW + FileContents + FileNameW 全都有），
/// Word 会走"插入文件"这条路，表格、图片和 Word 原生公式都能完整带过去。
///
/// 另一个坑：这里刻意**不调用 OleFlushClipboard**。实测 Flush 会把 FileContents（文件内容流）
/// 丢掉，只留下 FileGroupDescriptorW，Word 又会回到"取不到链接数据"的老路上；
/// 资源管理器自己复制文件时也不 Flush，而是让数据对象留在进程里按需渲染。
/// 代价是：剪贴板内容跟着本进程活着（跟资源管理器一样），程序退出后粘贴就取不到内容了。
/// </summary>
internal static class ShellFileClipboard
{
    /// <summary>取文件数据对象时要按这两个接口去问 shell。</summary>
    private static readonly Guid IidShellFolder = new("000214E6-0000-0000-C000-000000000046");
    private static readonly Guid IidDataObject = new("0000010E-0000-0000-C000-000000000046");

    /// <summary>OleInitialize 在"本线程已经初始化过 OLE"时的返回值（S_FALSE）。</summary>
    private const int SOk = 0;
    private const int SFalse = 1;
    private const int RpcEChangedMode = unchecked((int)0x80010106);

    private static readonly object OleLock = new();
    private static bool _oleInitialized;

    /// <summary>PeekMessage 的 PM_REMOVE。</summary>
    private const uint PmRemove = 0x0001;

    /// <summary>
    /// 把单个文件放进剪贴板（等价于在资源管理器里对这个文件按 Ctrl+C）。
    /// 文件必须留在磁盘上：粘贴时 Word 是照着这个路径去读的。
    /// </summary>
    public static void SetFile(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("剪贴板要引用的文件不存在：" + fullPath, fullPath);
        }

        EnsureOleInitialized();

        var dataObject = CreateFileDataObject(fullPath);

        var hr = OleSetClipboard(dataObject);
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }
    }

    /// <summary>
    /// 剪贴板内容是按需渲染的：别的程序来取数据时，OLE 会回调本进程的数据对象，
    /// 所以进程活着并且消息循环转着，粘贴才拿得到内容。
    /// 命令行模式用它把进程顶住一段时间，方便脚本或人工紧接着去粘贴。
    /// </summary>
    public static void KeepAlive(TimeSpan duration)
    {
        var deadline = Environment.TickCount64 + (long)duration.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            while (PeekMessage(out var message, IntPtr.Zero, 0, 0, PmRemove))
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }

            Thread.Sleep(15);
        }
    }

    /// <summary>
    /// 向 shell 要"这个文件的数据对象"——资源管理器复制文件时用的就是它。
    /// 拿到的对象里同时有 CF_HDROP、FileGroupDescriptorW、FileContents（文件内容流）
    /// 和指向完整路径的 FileNameW，Word 才认得出"这是一份要插进来的文档"。
    /// </summary>
    private static IDataObject CreateFileDataObject(string fullPath)
    {
        SHParseDisplayName(fullPath, IntPtr.Zero, out var pidl, 0, out _);
        try
        {
            SHBindToParent(pidl, IidShellFolder, out var folderObject, out var childPidl);
            var folder = (IShellFolder)folderObject;
            folder.GetUIObjectOf(IntPtr.Zero, 1, [childPidl], IidDataObject, IntPtr.Zero, out var dataObject);
            return (IDataObject)dataObject;
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidl);
        }
    }

    /// <summary>OLE 剪贴板 API 要求当前线程先做过 OleInitialize。</summary>
    private static void EnsureOleInitialized()
    {
        lock (OleLock)
        {
            if (_oleInitialized)
            {
                return;
            }

            var hr = OleInitialize(IntPtr.Zero);
            if (hr == RpcEChangedMode)
            {
                throw new InvalidOperationException("当前线程不是 STA 单元，无法使用 OLE 剪贴板。");
            }

            if (hr != SOk && hr != SFalse)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            _oleInitialized = true;
        }
    }

    [DllImport("ole32.dll")]
    private static extern int OleInitialize(IntPtr pvReserved);

    [DllImport("ole32.dll")]
    private static extern int OleSetClipboard([MarshalAs(UnmanagedType.Interface)] IDataObject pDataObj);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out NativeMessage message, IntPtr hwnd, uint filterMin, uint filterMax, uint remove);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref NativeMessage message);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHParseDisplayName(
        string pszName,
        IntPtr pbc,
        out IntPtr ppidl,
        uint sfgaoIn,
        out uint psfgaoOut);

    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SHBindToParent(
        IntPtr pidl,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv,
        out IntPtr ppidlLast);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pointX;
        public int pointY;
        public uint lPrivate;
    }

    /// <summary>shell 的文件夹接口，只为调用 GetUIObjectOf 取文件的数据对象。</summary>
    [ComImport]
    [Guid("000214E6-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        // 方法顺序必须和 shell 的 vtable 一致，前七个用不到也不能省
        void ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName,
            out uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
        void EnumObjects(IntPtr hwnd, int grfFlags, out IntPtr ppenumIDList);
        void BindToObject(IntPtr pidl, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
        void BindToStorage(IntPtr pidl, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
        int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        void CreateViewObject(IntPtr hwndOwner, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
        void GetAttributesOf(uint cidl, IntPtr apidl, ref uint rgfInOut);
        void GetUIObjectOf(IntPtr hwndOwner, uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid, IntPtr rgfReserved,
            [MarshalAs(UnmanagedType.Interface)] out object ppv);
        void GetDisplayNameOf(IntPtr pidl, uint uFlags, out IntPtr pName);
        void SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, out IntPtr ppidlOut);
    }
}
