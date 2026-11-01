using System.Runtime.InteropServices;

namespace VhdxCopier;

internal static class FilePickerInterop
{
    private const int CancelledHResult = unchecked((int)0x800704C7);

    public static string? PickVhdx(IntPtr owner)
    {
        var dialog = (IFileOpenDialog)new FileOpenDialog();
        try
        {
            dialog.GetOptions(out var options);
            dialog.SetOptions(
                options |
                FILEOPENDIALOGOPTIONS.FOS_FORCEFILESYSTEM |
                FILEOPENDIALOGOPTIONS.FOS_FILEMUSTEXIST |
                FILEOPENDIALOGOPTIONS.FOS_PATHMUSTEXIST);
            dialog.SetTitle("选择 VHDX 文件");

            var filters = new[]
            {
                new COMDLG_FILTERSPEC
                {
                    pszName = "VHDX 文件",
                    pszSpec = "*.vhdx"
                },
                new COMDLG_FILTERSPEC
                {
                    pszName = "所有文件",
                    pszSpec = "*.*"
                }
            };

            dialog.SetFileTypes((uint)filters.Length, filters);
            dialog.SetFileTypeIndex(1);
            return ShowAndGetPath(dialog, owner);
        }
        finally
        {
            Marshal.ReleaseComObject(dialog);
        }
    }

    public static string? PickFolder(IntPtr owner)
    {
        var dialog = (IFileOpenDialog)new FileOpenDialog();
        try
        {
            dialog.GetOptions(out var options);
            dialog.SetOptions(
                options |
                FILEOPENDIALOGOPTIONS.FOS_PICKFOLDERS |
                FILEOPENDIALOGOPTIONS.FOS_FORCEFILESYSTEM |
                FILEOPENDIALOGOPTIONS.FOS_PATHMUSTEXIST);
            dialog.SetTitle("选择目标文件夹");
            return ShowAndGetPath(dialog, owner);
        }
        finally
        {
            Marshal.ReleaseComObject(dialog);
        }
    }

    private static string? ShowAndGetPath(IFileOpenDialog dialog, IntPtr owner)
    {
        var result = dialog.Show(owner);
        if (result == CancelledHResult)
        {
            return null;
        }

        Marshal.ThrowExceptionForHR(result);

        dialog.GetResult(out var item);
        try
        {
            item.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out var path);
            return path;
        }
        finally
        {
            Marshal.ReleaseComObject(item);
        }
    }

    private enum SIGDN : uint
    {
        SIGDN_FILESYSPATH = 0x80058000
    }

    [Flags]
    private enum FILEOPENDIALOGOPTIONS : uint
    {
        FOS_OVERWRITEPROMPT = 0x2,
        FOS_STRICTFILETYPES = 0x4,
        FOS_NOCHANGEDIR = 0x8,
        FOS_PICKFOLDERS = 0x20,
        FOS_FORCEFILESYSTEM = 0x40,
        FOS_ALLNONSTORAGEITEMS = 0x80,
        FOS_NOVALIDATE = 0x100,
        FOS_ALLOWMULTISELECT = 0x200,
        FOS_PATHMUSTEXIST = 0x800,
        FOS_FILEMUSTEXIST = 0x1000,
        FOS_CREATEPROMPT = 0x2000,
        FOS_SHAREAWARE = 0x4000,
        FOS_NOREADONLYRETURN = 0x8000,
        FOS_NOTESTFILECREATE = 0x10000,
        FOS_HIDEMRUPLACES = 0x20000,
        FOS_HIDEPINNEDPLACES = 0x40000,
        FOS_NODEREFERENCELINKS = 0x100000,
        FOS_DONTADDTORECENT = 0x2000000,
        FOS_FORCESHOWHIDDEN = 0x10000000,
        FOS_DEFAULTNOMINIMODE = 0x20000000,
        FOS_FORCEPREVIEWPANEON = 0x40000000,
        FOS_SUPPORTSTREAMABLEITEMS = 0x80000000
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct COMDLG_FILTERSPEC
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pszName;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string pszSpec;
    }

    [ComImport]
    [Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    private class FileOpenDialog
    {
    }

    [ComImport]
    [Guid("D57C7288-D4AD-4768-BE02-9D969532D960")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig]
        int Show(IntPtr hwndOwner);

        void SetFileTypes(
            uint cFileTypes,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)]
            COMDLG_FILTERSPEC[] rgFilterSpec);

        void SetFileTypeIndex(uint iFileType);

        void GetFileTypeIndex(out uint piFileType);

        void Advise(IntPtr pfde, out uint pdwCookie);

        void Unadvise(uint dwCookie);

        void SetOptions(FILEOPENDIALOGOPTIONS fos);

        void GetOptions(out FILEOPENDIALOGOPTIONS pfos);

        void SetDefaultFolder(IShellItem psi);

        void SetFolder(IShellItem psi);

        void GetFolder(out IShellItem ppsi);

        void GetCurrentSelection(out IShellItem ppsi);

        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);

        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);

        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);

        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);

        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);

        void GetResult(out IShellItem ppsi);

        void AddPlace(IShellItem psi, int fdap);

        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);

        void Close(int hr);

        void SetClientGuid(ref Guid guid);

        void ClearClientData();

        void SetFilter(IntPtr pFilter);

        void GetResults(out IntPtr ppenum);

        void GetSelectedItems(out IntPtr ppsai);
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);

        void GetParent(out IShellItem ppsi);

        void GetDisplayName(
            SIGDN sigdnName,
            [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);

        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);

        void Compare(IShellItem psi, uint hint, out int piOrder);
    }
}
