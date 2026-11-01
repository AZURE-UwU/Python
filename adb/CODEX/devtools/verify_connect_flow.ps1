# @file    verify_connect_flow.ps1
# @author  Bowen
# @module  ADBDevTool / 连接流程自检
#
# 契约：
# 1. 只改造 -AppDir 指定的程序目录：把 platform-tools\adb.exe 改名成 adb.real.exe，放入假 adb；
#    脚本结束（含抛异常）时必须还原。
# 2. -State 决定假 adb 的初始状态：unauth（在列表里未授权、重连仍不授权）、
#    unauthfix（在列表里未授权、摘掉后能连上）、none（不在列表里）。
# 3. 动作固定为：选中含 15037 的列表项 → 点「连接」→ 输出列表项状态、状态栏文案与执行日志尾部。
#    假 adb 报的地址是 127.0.0.1:15037，本脚本在 15037 上监听，保证探活判定为「可建连」。

param(
    [Parameter(Mandatory = $true)][string]$AppDir,
    [Parameter(Mandatory = $true)][ValidateSet('unauth', 'unauthfix', 'none')][string]$State,
    [string]$FakeAdb = 'C:\Users\AZURE\Desktop\Project\小工具\adb\CODEX\devtools\FakeAdb\publish\adb.exe',
    [int]$LivePort = 15037,
    [int]$WaitSeconds = 8
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

function Get-Window([int]$processId) {
    $AE::RootElement.FindFirst(
        $scope::Children,
        (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $processId)))
}

function Get-NamedElement($window, [string]$label) {
    $window.FindFirst(
        $scope::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $label)))
}

function Select-ListItemContaining($window, [string]$needle) {
    foreach ($e in $window.FindAll($scope::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($e.Current.ControlType -ne $CT::ListItem) { continue }
        $text = ''
        foreach ($child in $e.FindAll($scope::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
            $text += $child.Current.Name + ' '
        }
        if ($text -like "*$needle*") {
            $e.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
            return $true
        }
    }
    return $false
}

function Write-LogBox($window) {
    $all = $window.FindAll($scope::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($e in $all) {
        if ($e.Current.AutomationId -ne 'LogBox') { continue }
        $value = $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
        Write-Output '--- 执行日志 ---'
        Write-Output ($value -replace "`r", "`n")
        return
    }
}

Get-Process ADBDevTool -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $LivePort)
$listener.Start()

try {
    Move-Item -LiteralPath $adbPath -Destination $adbBackup -Force
    Copy-Item -LiteralPath $FakeAdb -Destination $adbPath -Force
    Set-Content -LiteralPath $statePath -Value $State -NoNewline
    # unauth 场景固定让手机侧拒绝握手：disconnect 清掉记录后，重新握手仍然不授权。
    $phonePath = Join-Path $platformTools 'fake-phone.txt'
    Set-Content -LiteralPath $phonePath -Value $(if ($State -eq 'unauth') { 'refuse' } else { 'allow' }) -NoNewline

    $proc = Start-Process -FilePath $exePath -PassThru
    Start-Sleep -Seconds 6
    $win = Get-Window $proc.Id
    if (-not $win) { throw '找不到程序主窗口' }

    $labels = @()
    foreach ($e in $win.FindAll($scope::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($e.Current.ControlType -eq $CT::Text -and $e.Current.IsOffscreen -eq $false) { $labels += $e.Current.Name }
    }
    Write-Output "=== 场景 $State：点击前列表项状态 ==="
    Write-Output (($labels | Where-Object { $_ -in '已连接', '待授权', '离线', '可连接', '待配对', '待连接' }) -join ' / ')

    if (-not (Select-ListItemContaining $win "$LivePort")) { throw "列表里没有含 $LivePort 的条目" }
    Start-Sleep -Milliseconds 600
    (Get-NamedElement $win '连接').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Seconds $WaitSeconds

    Write-Output "=== 场景 $State：点击后状态栏 ==="
    foreach ($e in $win.FindAll($scope::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($e.Current.ControlType -ne $CT::Text) { continue }
        $n = $e.Current.Name
        if ($n -like '*连接*' -or $n -like '*已选择*' -or $n -like '*未选择*') { Write-Output $n }
    }

    Write-LogBox $win
    Write-Output "假 adb 结束状态：$((Get-Content -LiteralPath $statePath -Raw).Trim())"
}
finally {
    Get-Process ADBDevTool -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    $listener.Stop()
    if (Test-Path -LiteralPath $adbBackup) {
        Move-Item -LiteralPath $adbBackup -Destination $adbPath -Force
        Write-Output '已还原真实 adb.exe。'
    }
}
