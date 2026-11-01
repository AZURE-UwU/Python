# Open the settings flyout with UI Automation and screenshot just the app window area,
# so we can see the actual layout of the clipboard option.
param(
    [string]$Exe = "C:\Users\AZURE\Desktop\md2word\CODEX\dist\MarkdownToWord\MarkdownToWord.exe",
    [string]$OutFile = "C:\Users\AZURE\Desktop\md2word\CODEX\_output\ui-settings.png"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing

$signature = @'
using System;
using System.Runtime.InteropServices;
public static class Shot
{
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
'@
Add-Type -TypeDefinition $signature -ErrorAction SilentlyContinue
[Shot]::SetProcessDPIAware() | Out-Null

$settingsName = [string]([char]0x8BBE + [char]0x7F6E)   # "设置"
$proc = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Seconds 8

try {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
    $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
    for ($attempt = 0; -not $win -and $attempt -lt 20; $attempt++) {
        Start-Sleep -Seconds 1
        $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
    }
    if (-not $win) { Write-Host "window not found"; exit 1 }

    $all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $btn = $null
    $clipCombo = $null
    for ($i = 0; $i -lt $all.Count; $i++) {
        $e = $all.Item($i)
        $type = $e.Current.ControlType.ProgrammaticName
        if ($type -eq "ControlType.Button" -and $e.Current.Name -eq $settingsName) { $btn = $e }
        if ($e.Current.AutomationId -eq "ClipboardModeBox") { $clipCombo = $e }
    }

    if (-not $btn) { Write-Host "settings button not found"; exit 1 }
    $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Seconds 2

    if (-not $clipCombo) {
        $all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        for ($i = 0; $i -lt $all.Count; $i++) {
            if ($all.Item($i).Current.AutomationId -eq "ClipboardModeBox") { $clipCombo = $all.Item($i) }
        }
    }

    if ($clipCombo) {
        $r = $clipCombo.Current.BoundingRectangle
        Write-Host ("ClipboardModeBox: offscreen=" + $clipCombo.Current.IsOffscreen + " enabled=" + $clipCombo.Current.IsEnabled +
                    " rect=" + [int]$r.X + "," + [int]$r.Y + " " + [int]$r.Width + "x" + [int]$r.Height)
    }
    else {
        Write-Host "ClipboardModeBox not found in the tree"
    }

    $wr = $win.Current.BoundingRectangle
    $left = [int]$wr.X
    $top = [int]$wr.Y
    $width = [int]$wr.Width
    $height = [int]$wr.Height
    if ($clipCombo) {
        $r = $clipCombo.Current.BoundingRectangle
        if ($r.X -lt $left) { $width += $left - [int]$r.X; $left = [int]$r.X }
        if ($r.Y -lt $top) { $height += $top - [int]$r.Y; $top = [int]$r.Y }
        $right = [Math]::Max($left + $width, [int]($r.X + $r.Width))
        $bottom = [Math]::Max($top + $height, [int]($r.Y + $r.Height))
        $width = $right - $left
        $height = $bottom - $top
    }

    $bitmap = New-Object System.Drawing.Bitmap $width, $height
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($left, $top, 0, 0, (New-Object System.Drawing.Size $width, $height))
    $graphics.Dispose()
    $bitmap.Save($OutFile, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    Write-Host ("screenshot: " + $OutFile + " (" + $width + "x" + $height + ")")
}
finally {
    if (-not $proc.HasExited) {
        $proc.CloseMainWindow() | Out-Null
        Start-Sleep -Seconds 3
        if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
    }
}
