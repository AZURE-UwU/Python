# Inspect the settings flyout with UI Automation: is the new clipboard ComboBox there,
# is it inside the visible area, and does it expand when asked to?
param(
    [string]$Exe = "C:\Users\AZURE\Desktop\md2word\CODEX\dist\MarkdownToWord\MarkdownToWord.exe",
    [switch]$Expand
)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$proc = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Seconds 7

try {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
    $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
    if (-not $win) { Write-Host "window not found"; exit 1 }
    Write-Host ("window: " + $win.Current.Name)
    Write-Host ("window rect: " + $win.Current.BoundingRectangle)

    # note: enumerating with TrueCondition and filtering in PowerShell; property
    # conditions against this WinUI tree proved unreliable.
    $all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    Write-Host ("descendants: " + $all.Count)
    $btn = $null
    for ($i = 0; $i -lt $all.Count; $i++) {
        $e = $all.Item($i)
        if ($e.Current.ControlType.ProgrammaticName -eq "ControlType.Button" -and $e.Current.Name -like "*设置*") {
            $btn = $e
        }
    }
    if (-not $btn) { Write-Host "settings button not found"; exit 1 }
    $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Seconds 2
    Write-Host "settings flyout opened"
    Write-Host ("flyout window rect: " + $btn.Current.BoundingRectangle)

    $comboCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ComboBox)
    $combos = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $comboCond)
    Write-Host ("combo boxes: " + $combos.Count)
    for ($i = 0; $i -lt $combos.Count; $i++) {
        $c = $combos.Item($i)
        $r = $c.Current.BoundingRectangle
        Write-Host ("  [" + $i + "] name='" + $c.Current.Name + "' offscreen=" + $c.Current.IsOffscreen +
                    " enabled=" + $c.Current.IsEnabled + " rect=" + [int]$r.X + "," + [int]$r.Y + " " + [int]$r.Width + "x" + [int]$r.Height)
    }

    if ($Expand) {
        $target = $null
        for ($i = 0; $i -lt $combos.Count; $i++) {
            if ($combos.Item($i).Current.Name -match "复制") { $target = $combos.Item($i) }
        }
        if (-not $target) { Write-Host "clipboard combo not found by name; trying the last one"; $target = $combos.Item($combos.Count - 1) }
        Write-Host ("target: '" + $target.Current.Name + "' rect=" + $target.Current.BoundingRectangle)
        try {
            $pattern = $target.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
            Write-Host ("expand state before: " + $pattern.Current.ExpandCollapseState)
            $pattern.Expand()
            Start-Sleep -Milliseconds 900
            Write-Host ("expand state after: " + $pattern.Current.ExpandCollapseState)

            $listCond = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::ListItem)
            $items = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
                [System.Windows.Automation.TreeScope]::Descendants, $listCond)
            Write-Host ("list items visible now: " + $items.Count)
            for ($i = 0; $i -lt $items.Count; $i++) {
                $it = $items.Item($i)
                if ($it.Current.ProcessId -eq $proc.Id) {
                    Write-Host ("    item: '" + $it.Current.Name + "' offscreen=" + $it.Current.IsOffscreen)
                }
            }
        }
        catch {
            Write-Host ("expand failed: " + $_.Exception.Message)
        }
    }
}
finally {
    if (-not $proc.HasExited) {
        $proc.CloseMainWindow() | Out-Null
        Start-Sleep -Seconds 3
        if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
    }
}
