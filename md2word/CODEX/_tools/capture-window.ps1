param(
    [string]$OutFile = "C:\Users\AZURE\Desktop\md2word\CODEX\_output\ui.png",
    [string]$ProcessName = "MarkdownToWord",
    [int]$WaitSeconds = 8
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$signature = @'
using System;
using System.Runtime.InteropServices;

public static class Win32Capture
{
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOSIZE = 0x0001;
}
'@

Add-Type -TypeDefinition $signature -ErrorAction SilentlyContinue
[Win32Capture]::SetProcessDPIAware() | Out-Null

$exe = "C:\Users\AZURE\Desktop\md2word\CODEX\MarkdownToWord\bin\Debug\net10.0-windows10.0.19041.0\win-x64\MarkdownToWord.exe"
Get-Process $ProcessName -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

$process = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds $WaitSeconds

$process.Refresh()
if ($process.HasExited) {
    Write-Host ("应用提前退出，ExitCode=" + $process.ExitCode)
    exit 1
}

$handle = $process.MainWindowHandle
if ($handle -eq [IntPtr]::Zero) {
    Write-Host "未找到主窗口句柄"
    exit 2
}

[Win32Capture]::ShowWindow($handle, 9) | Out-Null   # SW_RESTORE
[Win32Capture]::SetWindowPos($handle, [Win32Capture]::HWND_TOPMOST, 0, 0, 0, 0,
    ([Win32Capture]::SWP_NOMOVE -bor [Win32Capture]::SWP_NOSIZE)) | Out-Null
[Win32Capture]::SetForegroundWindow($handle) | Out-Null
Start-Sleep -Seconds 2

$rect = New-Object Win32Capture+RECT
[Win32Capture]::GetWindowRect($handle, [ref]$rect) | Out-Null
$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top

$bitmap = New-Object System.Drawing.Bitmap $width, $height
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size $width, $height))
$graphics.Dispose()
$bitmap.Save($OutFile, [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()

Write-Host "已截图：$OutFile（窗口 ${width}x${height}）"

[Win32Capture]::SetWindowPos($handle, [Win32Capture]::HWND_NOTOPMOST, 0, 0, 0, 0,
    ([Win32Capture]::SWP_NOMOVE -bor [Win32Capture]::SWP_NOSIZE)) | Out-Null
