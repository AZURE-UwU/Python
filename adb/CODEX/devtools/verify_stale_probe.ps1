# @file    verify_stale_probe.ps1
# @author  Bowen
# @module  ADBDevTool / 死端口置灰自检
#
# 契约：
# 1. 只改造 -AppDir 指定的程序目录：把 platform-tools\adb.exe 改名成 adb.real.exe，放入假 adb；
#    脚本结束（含抛异常）时必须还原。
# 2. 假 adb 以 state=probe 广播两个端口：127.0.0.1:15037（本脚本监听，可建连）与 127.0.0.1:9（不通）。
# 3. 判据：15037 行状态为「可连接」，9 行状态为「失效」，选中失效行时连接按钮不可用。

param(
    [Parameter(Mandatory = $true)][string]$AppDir,
    [string]$FakeAdb = 'C:\Users\AZURE\Desktop\Project\小工具\adb\CODEX\devtools\FakeAdb\publish\adb.exe',
    [int]$LivePort = 15037
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
$exePath = Join-Path $AppDir 'ADBDevTool.exe'

function Get-Rows($window) {
    $rows = @()
    foreach ($e in $window.FindAll($scope::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($e.Current.ControlType -ne $CT::ListItem) { continue }
        $text = @()
        foreach ($child in $e.FindAll($scope::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
            $text += $child.Current.Name
        }
        $rows += , [pscustomobject]@{ Element = $e; Text = ($text -join ' | ') }
    }
    $rows
}

$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $LivePort)
$listener.Start()

Get-Process ADBDevTool -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

try {
    Move-Item -LiteralPath $adbPath -Destination $adbBackup -Force
    Copy-Item -LiteralPath $FakeAdb -Destination $adbPath -Force
    Set-Content -LiteralPath (Join-Path $platformTools 'fake-state.txt') -Value 'probe' -NoNewline
    Set-Content -LiteralPath (Join-Path $platformTools 'fake-phone.txt') -Value 'allow' -NoNewline

    $proc = Start-Process -FilePath $exePath -PassThru
    Start-Sleep -Seconds 9
    $win = $AE::RootElement.FindFirst(
        $scope::Children,
        (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $proc.Id)))
    if (-not $win) { throw '找不到程序主窗口' }

    Write-Output '=== 列表行（名称 | 明细 | 状态）==='
    $rows = Get-Rows $win
    foreach ($row in $rows) { Write-Output $row.Text }

    $dead = $rows | Where-Object { $_.Text -like "*:$LivePort*" -eq $false -and $_.Text -like '*127.0.0.1*' }
    if (-not $dead) { throw '没有找到失效行' }
    $dead[0].Element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 800

    $connectButton = $win.FindFirst(
        $scope::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, '连接')))
    Write-Output "选中失效行后：连接按钮 IsEnabled = $($connectButton.Current.IsEnabled)"

    $scanButton = $win.FindFirst(
        $scope::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, '扫描局域网')))
    $scanButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Seconds 6

    foreach ($e in $win.FindAll($scope::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($e.Current.AutomationId -ne 'LogBox') { continue }
        $log = $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
        Write-Output '=== 执行日志 ==='
        Write-Output ($log -replace "`r", "`n")
        break
    }
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
