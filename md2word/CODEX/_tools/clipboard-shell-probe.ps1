# Probe: build the same shell data object Explorer uses when copying a file,
# dump the clipboard formats it offers, check whether FileContents can be read,
# and optionally put it on the real clipboard (OleSetClipboard + OleFlushClipboard).
param(
    [string]$Path = "C:\Users\AZURE\AppData\Local\MarkdownToWord\Clipboard\syntax-tour.docx",
    [string]$Text = "probe plain text",
    [switch]$PutOnClipboard,
    [switch]$InspectClipboard,
    [switch]$RawSequence
)

$code = @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

public static class ShellClipProbe
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SHBindToParent(
        IntPtr pidl,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv,
        out IntPtr ppidlLast);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string format);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClipboardFormatName(uint format, StringBuilder lpszFormatName, int cchMaxCount);

    [DllImport("ole32.dll")]
    private static extern int OleSetClipboard([MarshalAs(UnmanagedType.Interface)] IDataObject pDataObj);

    [DllImport("ole32.dll")]
    private static extern int OleFlushClipboard();

    [DllImport("ole32.dll")]
    private static extern int OleGetClipboard([MarshalAs(UnmanagedType.Interface)] out IDataObject ppDataObj);

    [DllImport("ole32.dll")]
    private static extern int OleInitialize(IntPtr pvReserved);

    [DllImport("ole32.dll")]
    private static extern int OleSetClipboardRaw(IntPtr pDataObj);

    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SHCreateDataObject(IntPtr pidlFolder, uint cidl, IntPtr[] apidl,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);

    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref STGMEDIUM medium);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalSize(IntPtr hMem);

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
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

    public static IDataObject CreateForFile(string path)
    {
        IntPtr pidl;
        uint attrs;
        SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out attrs);
        try
        {
            object folderObj;
            IntPtr child;
            SHBindToParent(pidl, typeof(IShellFolder).GUID, out folderObj, out child);
            var folder = (IShellFolder)folderObj;
            object dataObj;
            folder.GetUIObjectOf(IntPtr.Zero, 1, new[] { child }, typeof(IDataObject).GUID, IntPtr.Zero, out dataObj);
            return (IDataObject)dataObj;
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidl);
        }
    }

    public static string FormatName(short cf)
    {
        switch (cf)
        {
            case 1: return "CF_TEXT";
            case 2: return "CF_BITMAP";
            case 8: return "CF_DIB";
            case 13: return "CF_UNICODETEXT";
            case 15: return "CF_HDROP";
            case 17: return "CF_DIBV5";
            default: break;
        }

        var sb = new StringBuilder(256);
        if ((ushort)cf >= 0xC000 && GetClipboardFormatName((ushort)cf, sb, sb.Capacity) > 0)
        {
            return sb.ToString();
        }

        return "#" + cf;
    }

    public static string DumpFormats(IDataObject doo, string title)
    {
        var sb = new StringBuilder();
        sb.AppendLine(title);
        var e = doo.EnumFormatEtc(DATADIR.DATADIR_GET);
        if (e == null)
        {
            sb.AppendLine("  (EnumFormatEtc returned null)");
            return sb.ToString();
        }

        var buf = new FORMATETC[1];
        var fetched = new int[1];
        while (e.Next(1, buf, fetched) == 0)
        {
            var fe = buf[0];
            sb.AppendFormat("  {0,-26} cf={1,-6} tymed={2,-4} lindex={3}\r\n",
                FormatName(fe.cfFormat), fe.cfFormat, (int)fe.tymed, fe.lindex);
        }

        return sb.ToString();
    }

    private static byte[] ReadMedium(STGMEDIUM st)
    {
        if ((st.tymed & TYMED.TYMED_ISTREAM) != 0 && st.unionmember != IntPtr.Zero)
        {
            var stream = (IStream)Marshal.GetObjectForIUnknown(st.unionmember);
            var ms = new MemoryStream();
            var chunk = new byte[8192];
            var readPtr = Marshal.AllocHGlobal(4);
            try
            {
                int read;
                do
                {
                    stream.Read(chunk, chunk.Length, readPtr);
                    read = Marshal.ReadInt32(readPtr);
                    if (read > 0) ms.Write(chunk, 0, read);
                }
                while (read > 0);
            }
            finally
            {
                Marshal.FreeHGlobal(readPtr);
            }

            return ms.ToArray();
        }

        if ((st.tymed & TYMED.TYMED_HGLOBAL) != 0 && st.unionmember != IntPtr.Zero)
        {
            var size = (int)GlobalSize(st.unionmember);
            var bytes = new byte[size];
            Marshal.Copy(st.unionmember, bytes, 0, size);
            return bytes;
        }

        return null;
    }

    public static string ProbeFileContents(IDataObject doo, int lindex, int expectedSize)
    {
        var fe = new FORMATETC
        {
            cfFormat = (short)RegisterClipboardFormat("FileContents"),
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex = lindex,
            tymed = TYMED.TYMED_ISTREAM | TYMED.TYMED_HGLOBAL
        };

        var st = new STGMEDIUM();
        try
        {
            doo.GetData(ref fe, out st);
            var bytes = ReadMedium(st);
            var same = bytes != null && bytes.Length == expectedSize;
            return string.Format("  FileContents(lindex={0}): tymed={1} bytes={2} (file={3}, {4})",
                lindex, (int)st.tymed, bytes == null ? -1 : bytes.Length, expectedSize,
                same ? "MATCH" : "MISMATCH");
        }
        catch (Exception ex)
        {
            return string.Format("  FileContents(lindex={0}): FAILED - {1}", lindex, ex.Message);
        }
        finally
        {
            ReleaseStgMedium(ref st);
        }
    }

    public static string ProbeFormat(IDataObject doo, string format, int lindex)
    {
        var fe = new FORMATETC
        {
            cfFormat = (short)RegisterClipboardFormat(format),
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex = lindex,
            tymed = TYMED.TYMED_HGLOBAL
        };

        var st = new STGMEDIUM();
        try
        {
            doo.GetData(ref fe, out st);
            var bytes = ReadMedium(st);
            var extra = string.Empty;
            if (bytes != null && bytes.Length >= 2)
            {
                extra = "  value='" + Encoding.Unicode.GetString(bytes, 0, Math.Min(bytes.Length, 120)).TrimEnd('\0') + "'";
            }

            return string.Format("  {0}(lindex={1}) = {2} bytes{3}", format, lindex, bytes == null ? -1 : bytes.Length, extra);
        }
        catch (Exception ex)
        {
            return string.Format("  {0}(lindex={1}): FAILED - {2}", format, lindex, ex.Message);
        }
        finally
        {
            ReleaseStgMedium(ref st);
        }
    }

    /// <summary>把一个格式的各种 (tymed, lindex) 组合都试一遍，看消费者到底能拿到什么。</summary>
    public static string ProbeCombinations(IDataObject doo, string format, int expectedSize)
    {
        var sb = new StringBuilder();
        var cf = (short)RegisterClipboardFormat(format);
        var tyMeds = new[]
        {
            TYMED.TYMED_HGLOBAL,
            TYMED.TYMED_ISTREAM,
            TYMED.TYMED_ISTORAGE,
            TYMED.TYMED_ISTREAM | TYMED.TYMED_HGLOBAL,
            TYMED.TYMED_ISTORAGE | TYMED.TYMED_ISTREAM | TYMED.TYMED_HGLOBAL
        };

        foreach (var lindex in new[] { -1, 0 })
        {
            foreach (var tymed in tyMeds)
            {
                var fe = new FORMATETC { cfFormat = cf, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = lindex, tymed = tymed };
                var st = new STGMEDIUM();
                try
                {
                    doo.GetData(ref fe, out st);
                    var bytes = ReadMedium(st);
                    sb.AppendLine(string.Format("    {0} lindex={1} tymed={2}: OK medium={3} bytes={4} ({5})",
                        format, lindex, (int)tymed, (int)st.tymed, bytes == null ? -1 : bytes.Length,
                        bytes != null && bytes.Length == expectedSize ? "MATCH" : "no"));
                }
                catch (Exception ex)
                {
                    sb.AppendLine(string.Format("    {0} lindex={1} tymed={2}: {3}", format, lindex, (int)tymed, ex.Message));
                }
                finally
                {
                    ReleaseStgMedium(ref st);
                }
            }
        }

        return sb.ToString();
    }

    public static string TrySetText(IDataObject doo, string text)
    {
        var fe = new FORMATETC
        {
            cfFormat = 13,
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            tymed = TYMED.TYMED_HGLOBAL
        };

        var h = Marshal.StringToHGlobalUni(text);
        var st = new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = h, pUnkForRelease = null };
        var hr = 0;
        try
        {
            doo.SetData(ref fe, ref st, false);
        }
        catch (Exception ex)
        {
            Marshal.FreeHGlobal(h);
            return "  SetData(UnicodeText): THREW - " + ex.Message;
        }

        if (hr != 0)
        {
            Marshal.FreeHGlobal(h);
        }

        return string.Format("  SetData(UnicodeText) hr=0x{0:X8}", hr);
    }

    public static string SetOnClipboard(IDataObject doo)
    {
        var hr = OleSetClipboard(doo);
        if (hr != 0) return string.Format("OleSetClipboard hr=0x{0:X8}", hr);
        var hf = OleFlushClipboard();
        return string.Format("OleSetClipboard OK, OleFlushClipboard hr=0x{0:X8}", hf);
    }

    public static string ProbeCurrentClipboard(int expectedSize)
    {
        IDataObject doo;
        var hr = OleGetClipboard(out doo);
        if (hr != 0 || doo == null) return string.Format("OleGetClipboard hr=0x{0:X8}", hr);

        var sb = new StringBuilder();
        sb.AppendLine(DumpFormats(doo, "OLE clipboard formats now:"));
        sb.Append(ProbeCombinations(doo, "FileContents", expectedSize));
        sb.Append(ProbeCombinations(doo, "FileGroupDescriptorW", -1));
        sb.AppendLine(ProbeFileContents(doo, 0, expectedSize));
        sb.AppendLine(ProbeFormat(doo, "FileGroupDescriptorW", -1));
        sb.AppendLine(ProbeFormat(doo, "FileNameW", -1));
        try
        {
            var fe = new FORMATETC { cfFormat = 13, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL };
            STGMEDIUM st;
            doo.GetData(ref fe, out st);
            var bytes = ReadMedium(st);
            ReleaseStgMedium(ref st);
            var s = bytes == null ? "(null)" : Encoding.Unicode.GetString(bytes).TrimEnd('\0');
            sb.AppendLine("  CF_UNICODETEXT = " + (s.Length > 40 ? s.Substring(0, 40) + "..." : s));
        }
        catch (Exception ex)
        {
            sb.AppendLine("  CF_UNICODETEXT: FAILED - " + ex.Message);
        }

        return sb.ToString();
    }

    public static string RunProbe(string path, string text, int expectedSize)
    {
        var doo = CreateForFile(path);
        var sb = new StringBuilder();
        sb.Append(DumpFormats(doo, "shell data object formats (what Explorer uses when copying a file):"));
        sb.AppendLine(ProbeFileContents(doo, 0, expectedSize));
        sb.AppendLine(ProbeFormat(doo, "FileGroupDescriptorW", -1));
        sb.AppendLine(ProbeFormat(doo, "FileNameW", -1));
        sb.AppendLine(TrySetText(doo, text));
        return sb.ToString();
    }

    public static string RunClipboardProbe(string path, string text, int expectedSize)
    {
        var doo = CreateForFile(path);
        var sb = new StringBuilder();
        sb.AppendLine(TrySetText(doo, text));
        sb.AppendLine(SetOnClipboard(doo));
        sb.AppendLine();
        sb.Append(ProbeCurrentClipboard(expectedSize));
        return sb.ToString();
    }

    private static IntPtr FindLastItemId(IntPtr pidl)
    {
        var current = pidl;
        while (true)
        {
            var size = Marshal.ReadInt16(current);
            if (size <= 0) return IntPtr.Zero;
            var next = IntPtr.Add(current, size);
            if (Marshal.ReadInt16(next) <= 0) return current;
            current = next;
        }
    }

    public static string RunRawSequence(string path, int expectedSize)
    {
        var sb = new StringBuilder();
        sb.AppendLine("OleInitialize hr=0x" + OleInitialize(IntPtr.Zero).ToString("X8"));

        var dir = Path.GetDirectoryName(path);
        IntPtr pidlDir, pidlFile;
        uint attrs;
        SHParseDisplayName(dir, IntPtr.Zero, out pidlDir, 0, out attrs);
        SHParseDisplayName(path, IntPtr.Zero, out pidlFile, 0, out attrs);
        var child = FindLastItemId(pidlFile);
        sb.AppendLine("pidlDir=" + pidlDir + " pidlFile=" + pidlFile + " child=" + child);

        IntPtr pdo;
        SHCreateDataObject(pidlDir, 1, new[] { child }, typeof(IDataObject).GUID, out pdo);
        sb.AppendLine("dataObject ptr=" + pdo);

        var hr = OleSetClipboardRaw(pdo);
        sb.AppendLine("OleSetClipboard hr=0x" + hr.ToString("X8"));
        var hf = OleFlushClipboard();
        sb.AppendLine("OleFlushClipboard hr=0x" + hf.ToString("X8"));
        sb.Append(ProbeCurrentClipboard(expectedSize));

        Marshal.Release(pdo);
        Marshal.FreeCoTaskMem(pidlDir);
        Marshal.FreeCoTaskMem(pidlFile);
        return sb.ToString();
    }
}
'@

Add-Type -TypeDefinition $code -Language CSharp

Write-Host ("target file: " + $Path)
$size = (Get-Item -LiteralPath $Path).Length
Write-Host ("size on disk: " + $size + " bytes")
Write-Host ""

if ($InspectClipboard) {
    Write-Host ([ShellClipProbe]::ProbeCurrentClipboard($size))
    return
}

if ($RawSequence) {
    Write-Host ([ShellClipProbe]::RunRawSequence($Path, $size))
}
else {
    Write-Host ([ShellClipProbe]::RunProbe($Path, $Text, $size))
}

if ($PutOnClipboard) {
    Write-Host ([ShellClipProbe]::RunClipboardProbe($Path, $Text, $size))
}
