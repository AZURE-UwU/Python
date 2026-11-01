# @file    uia_probe.ps1
# @author  Bowen
# @module  ADBDevTool / 无界面自检
#
# 契约：
# 1. 只操作进程名为 -ProcessName 的顶层窗口，不触碰其他进程的界面。
# 2. -Mode click 要求按钮名在窗口内唯一命中，命中数不等于 1 时不点击并退出码 2。
# 3. -Mode dump 只读，输出文本控件与列表项的可见性及列表项数量。

param(
    [ValidateSet('dump', 'click', 'values', 'select', 'expand', 'invoke', 'setvalue')][string]$Mode = 'dump',
    [string]$Name = '',
    [string]$ValueText = '',
    [string]$ProcessName = 'ADBDevTool'
)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$AE = [System.Windows.Automation.AutomationElement]
$CT = [System.Windows.Automation.ControlType]
$scope = [System.Windows.Automation.TreeScope]

$proc = Get-Process -Name $ProcessName -ErrorAction Stop | Select-Object -First 1
$win = $AE::RootElement.FindFirst(
    $scope::Children,
    (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $proc.Id)))
if (-not $win) { throw "找不到 $ProcessName 的顶层窗口" }

if ($Mode -eq 'click') {
    $nameCond = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $Name)
    $typeCond = New-Object System.Windows.Automation.PropertyCondition(
        $AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
    $andCond = New-Object System.Windows.Automation.AndCondition($nameCond, $typeCond)
    $hits = $win.FindAll($scope::Descendants, $andCond)
    if ($hits.Count -ne 1) {
        Write-Output "命中 $($hits.Count) 个名为「$Name」的按钮，按契约不点击。"
        exit 2
    }
    $hits[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Write-Output "已点击「$Name」。"
    exit 0
}

if ($Mode -eq 'values') {
    $all = $win.FindAll($scope::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($e in $all) {
        if (-not $e.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$null)) { continue }
        $vp = $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $value = $vp.Current.Value
        if ([string]::IsNullOrWhiteSpace($value)) { continue }
        Write-Output "----- $($e.Current.AutomationId) [$($e.Current.ControlType.ProgrammaticName)] -----"
        Write-Output $value
    }
    exit 0
}

if ($Mode -eq 'select') {
    $hit = $null
    foreach ($e in $win.FindAll($scope::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($e.Current.ControlType -ne $CT::ListItem) { continue }
        $text = ''
        foreach ($child in $e.FindAll($scope::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
            $text += $child.Current.Name + ' '
        }
        if ($text -like "*$Name*") { $hit = $e; break }
    }
    if (-not $hit) { throw "找不到包含「$Name」的列表项" }
    $hit.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Write-Output "已选中包含「$Name」的列表项。"
    exit 0
}

if ($Mode -eq 'expand') {
    $e = $win.FindFirst(
        $scope::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $Name)))
    if (-not $e) { throw "找不到「$Name」" }
    $e.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Write-Output "已展开「$Name」。"
    exit 0
}

if ($Mode -eq 'invoke') {
    $e = $win.FindFirst(
        $scope::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $Name)))
    if (-not $e) { throw "找不到「$Name」" }
    $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Write-Output "已触发「$Name」。"
    exit 0
}

if ($Mode -eq 'setvalue') {
    # 不传 -Name 时跳过 LogBox（执行日志本身也是 Edit），否则会往日志框里写字。
    $all = $win.FindAll($scope::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $target = $null
    foreach ($e in $all) {
        if ($e.Current.ControlType -ne $CT::Edit) { continue }
        $id = $e.Current.AutomationId
        if ($Name.Length -gt 0) {
            if ($id -ne $Name) { continue }
        }
        elseif ($id -eq 'LogBox') { continue }
        $target = $e
        break
    }
    if (-not $target) { throw "找不到输入框（AutomationId=$Name）" }
    $target.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($ValueText)
    Write-Output "已向输入框写入「$ValueText」。"
    exit 0
}

$wanted = @(
    [System.Windows.Automation.ControlType]::Text,
    [System.Windows.Automation.ControlType]::ListItem,
    [System.Windows.Automation.ControlType]::List,
    [System.Windows.Automation.ControlType]::Button
)
$all = $win.FindAll($scope::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
foreach ($e in $all) {
    $ct = $e.Current.ControlType
    if ($wanted -notcontains $ct) { continue }
    $label = $ct.ProgrammaticName -replace '^ControlType\.', ''
    $off = if ($e.Current.IsOffscreen) { 'offscreen' } else { 'onscreen' }
    Write-Output ("{0,-10} {1,-9} {2}" -f $label, $off, $e.Current.Name)
}
