# @file    repro_restart_adb.ps1
# @author  Bowen
# @module  ADBDevTool / 重启 adb 后空提示自检
#
# 契约：
# 1. -FakeAdb 非空时只改造 -AppDir 指定的程序目录：把 platform-tools\adb.exe 改名为 adb.real.exe，
#    再放入假 adb；脚本结束（含抛异常）时必须还原。-FakeAdb 传空串则用真实 adb 跑。
# 2. 采样判据：「还没有发现设备」提示可见 且 列表项数大于 0，记一次违规。
# 3. 假 adb 初始状态 device，kill-server 后变 none，模拟 adb 重启后设备掉线、mDNS 清空。

param(
    [Parameter(Mandatory = $true)][string]$AppDir,
    [int]$SampleSeconds = 8,
    [string]$FakeAdb = 'C:\Users\AZURE\Desktop\Project\小工具\adb\CODEX\devtools\FakeAdb\publish\adb.exe'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$AE = [System.Windows.Automation.AutomationElement]
$CT = [System.Windows.Automation.ControlType]
$scope = [System.Windows.Automation.TreeScope]
$platformTools = Join-Path $AppDir 'platform-tools'
$adbPath = Join-Path $platformTools 'adb.exe'
$adbBackup = Join-Path $platformTools 'adb.real.exe'
$statePath = Join-Path $platformTools 'fake-state.txt'
$exePath = Join-Path $AppDir 'ADBDevTool.exe'

function Get-Snapshot([System.Windows.Automation.AutomationElement]$window) {
    $all = $window.FindAll($scope::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $items = 0
    $hint = $false
    foreach ($e in $all) {
        $ct = $e.Current.ControlType
        if ($ct -eq $CT::ListItem) {
            $items++
        }
        elseif ($ct -eq $CT::Text) {
            if ($e.Current.Name -like '还没有发现设备*') { $hint = $true }
        }
    }
    [pscustomobject]@{ Items = $items; Hint = $hint }
}

function Get-Button($window, [string]$label) {
    $nameCond = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $label)
    $typeCond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Button)
    $hits = $window.FindAll($scope::Descendants, (New-Object System.Windows.Automation.AndCondition($nameCond, $typeCond)))
    if ($hits.Count -ne 1) { throw "按钮「$label」命中 $($hits.Count) 个，按契约不点击" }
    $hits[0]
}

Get-Process ADBDevTool -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

try {
    $useFake = $FakeAdb.Length -gt 0
    if ($useFake) {
        Move-Item -LiteralPath $adbPath -Destination $adbBackup -Force
        Copy-Item -LiteralPath $FakeAdb -Destination $adbPath -Force
        Set-Content -LiteralPath $statePath -Value 'device' -NoNewline
    }

    $proc = Start-Process -FilePath $exePath -PassThru
    Start-Sleep -Seconds 6

    $win = $AE::RootElement.FindFirst(
        $scope::Children,
        (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $proc.Id)))
    if (-not $win) { throw '找不到程序主窗口' }

    $before = Get-Snapshot $win
    Write-Output "重启前：列表项 $($before.Items) 项，空提示可见 $($before.Hint)"
    if ($before.Items -lt 1) { throw '假 adb 未让列表出现条目，夹具无效' }

    (Get-Button $win '重启 adb').GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Write-Output '已点击「重启 adb」，开始采样…'

    $deadline = (Get-Date).AddSeconds($SampleSeconds)
    $violations = 0
    $last = $null
    while ((Get-Date) -lt $deadline) {
        $snap = Get-Snapshot $win
        $last = $snap
        if ($snap.Hint -eq $true -and $snap.Items -gt 0) {
            $violations++
            Write-Output "  违规：空提示可见，列表仍有 $($snap.Items) 项"
        }
        Start-Sleep -Milliseconds 200
    }

    Write-Output "采样结束：列表项 $($last.Items) 项，空提示可见 $($last.Hint)，违规 $violations 次"
    if ($useFake) {
        Write-Output "假 adb 状态：$((Get-Content -LiteralPath $statePath -Raw).Trim())"
    }
}
finally {
    Get-Process ADBDevTool -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    if (Test-Path -LiteralPath $adbBackup) {
        Move-Item -LiteralPath $adbBackup -Destination $adbPath -Force
        Write-Output '已还原真实 adb.exe。'
    }
}
