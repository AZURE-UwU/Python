param(
    [string]$OutFile = "C:\Users\AZURE\Desktop\md2word\CODEX\_output\ui.png",
    [string]$ExePath = "C:\Users\AZURE\Desktop\md2word\CODEX\MarkdownToWord\bin\Debug\net10.0-windows10.0.19041.0\win-x64\MarkdownToWord.exe",
    [int]$WaitSeconds = 8
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$exe = $ExePath

Get-Process MarkdownToWord -ErrorAction SilentlyContinue | Stop-Process -Force
$process = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds $WaitSeconds

if ($process.HasExited) {
    Write-Host ("应用提前退出，ExitCode=" + $process.ExitCode)
    exit 1
}

$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bitmap = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
$graphics.Dispose()
$bitmap.Save($OutFile, [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()

Write-Host "已截图：$OutFile（屏幕 $($bounds.Width)x$($bounds.Height)）"
Write-Host ("进程仍在运行：pid " + $process.Id)
