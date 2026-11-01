# Drive the clipboard option through UI Automation: open settings, expand the combo,
# select an item, and report whether the selection stuck (and whether settings.json changed).
param(
    [string]$Exe = "C:\Users\AZURE\Desktop\md2word\CODEX\dist\MarkdownToWord\MarkdownToWord.exe",
    [int]$SelectIndex = 0
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$clipCode = @'
using System;
using System.Runtime.InteropServices;

public static class ShellClip
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string format);

    [DllImport("user32.dll")]
    private static extern bool OpenClipboard(IntPtr hwndNewOwner);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalSize(IntPtr hMem);

    public static byte[] Read()
    {
        var format = RegisterClipboardFormat("HTML Format");
        if (!OpenClipboard(IntPtr.Zero)) return null;
        try
        {
            var handle = GetClipboardData(format);
            if (handle == IntPtr.Zero) return null;
            return new byte[(int)GlobalSize(handle)];
        }
        finally
        {
            CloseClipboard();
        }
    }
}
'@
Add-Type -TypeDefinition $clipCode -Language CSharp

$settingsName = [string]([char]0x8BBE + [char]0x7F6E)
$settingsPath = Join-Path $env:LOCALAPPDATA "MarkdownToWord\settings.json"
Write-Host ("settings.json before: " + (Get-Content -Raw $settingsPath))

function Get-Window {
    param($root, $processId)
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $processId)
    for ($i = 0; $i -lt 20; $i++) {
        $w = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
        if ($w) { return $w }
        Start-Sleep -Seconds 1
    }
    return $null
}

function Get-Descendants {
    param($el)
    return $el.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
}

$proc = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Seconds 8

try {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $win = Get-Window $root $proc.Id
    if (-not $win) { Write-Host "window not found"; exit 1 }

    $all = Get-Descendants $win
    $btn = $null
    for ($i = 0; $i -lt $all.Count; $i++) {
        $e = $all.Item($i)
        if ($e.Current.ControlType.ProgrammaticName -eq "ControlType.Button" -and $e.Current.Name -eq $settingsName) { $btn = $e }
    }
    if (-not $btn) { Write-Host "settings button not found"; exit 1 }
    $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Seconds 2

    $items = @()
    $all = Get-Descendants $win
    for ($i = 0; $i -lt $all.Count; $i++) {
        $e = $all.Item($i)
        if ($e.Current.ControlType.ProgrammaticName -eq "ControlType.RadioButton") { $items += $e }
    }
    Write-Host ("radio buttons found: " + $items.Count)
    foreach ($it in $items) {
        Write-Host ("  '" + $it.Current.Name + "' offscreen=" + $it.Current.IsOffscreen +
                    " rect=" + $it.Current.BoundingRectangle +
                    " selected=" + ($it.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected))
    }

    # screenshot just the settings panel (union of the radio rects, padded)
    if ($items.Count -gt 0) {
        Add-Type -AssemblyName System.Drawing
        $r0 = $items[0].Current.BoundingRectangle
        $left = [int]($r0.X - 170)
        $top = [int]($r0.Y - 120)
        $right = [int]($r0.X + $r0.Width + 40)
        $bottom = [int]($items[$items.Count - 1].Current.BoundingRectangle.Y +
                         $items[$items.Count - 1].Current.BoundingRectangle.Height + 30)
        $shot = New-Object System.Drawing.Bitmap ($right - $left), ($bottom - $top)
        $g = [System.Drawing.Graphics]::FromImage($shot)
        $g.CopyFromScreen($left, $top, 0, 0, (New-Object System.Drawing.Size ($right - $left), ($bottom - $top)))
        $g.Dispose()
        $shot.Save("C:\Users\AZURE\Desktop\md2word\CODEX\_output\ui-clipboard-option.png", [System.Drawing.Imaging.ImageFormat]::Png)
        $shot.Dispose()
        Write-Host "screenshot: _output\ui-clipboard-option.png"
    }

    if ($items.Count -gt $SelectIndex) {
        $target = $items[$SelectIndex]
        Write-Host ("selecting: " + $target.Current.Name)
        $target.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Start-Sleep -Seconds 2

        foreach ($it in $items) {
            Write-Host ("  after: '" + $it.Current.Name + "' selected=" +
                        ($it.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected))
        }
    }

    # run the toolbar copy action and see what lands on the clipboard
    $copyName = [string]([char]0x590D + [char]0x5236) + " Word"   # "复制 Word"
    $copyBtn = $null
    $all = Get-Descendants $win
    for ($i = 0; $i -lt $all.Count; $i++) {
        $e = $all.Item($i)
        if ($e.Current.ControlType.ProgrammaticName -eq "ControlType.Button" -and $e.Current.Name -eq $copyName) { $copyBtn = $e }
    }
    if ($copyBtn) {
        Write-Host "clicking the copy button"
        $copyBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Seconds 6
        $all = Get-Descendants $win
        for ($i = 0; $i -lt $all.Count; $i++) {
            $e = $all.Item($i)
            if ($e.Current.ControlType.ProgrammaticName -eq "ControlType.Text" -and $e.Current.Name -match "复制|失败|警告") {
                Write-Host ("infobar/text: " + $e.Current.Name)
            }
        }
        $htmlFormat = [ShellClip]::Read()
        if ($htmlFormat) { Write-Host ("clipboard now holds HTML Format: " + $htmlFormat.Length + " bytes") }
        else { Write-Host "clipboard does not hold HTML Format (document mode?)" }
    }
    else {
        Write-Host "copy button not found"
    }

    Write-Host ("settings.json after: " + (Get-Content -Raw $settingsPath))
}
finally {
    if (-not $proc.HasExited) {
        $proc.CloseMainWindow() | Out-Null
        Start-Sleep -Seconds 4
        if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
    }
}
