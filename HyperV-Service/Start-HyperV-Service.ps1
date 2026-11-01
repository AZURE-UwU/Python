# Start-HyperV-Service.ps1
# Purpose: make sure the Hyper-V Virtual Machine Management service (vmms) is running.
# Use it after you stopped vmms to eject the VM storage disk.
# Run it by double-clicking Start-HyperV-Service.cmd (it handles UAC elevation).
# ASCII-only on purpose: Windows PowerShell reads .ps1 as ANSI when there is no BOM.

$logDir  = Split-Path -Parent $PSCommandPath
$logFile = Join-Path $logDir 'Start-HyperV-Service.log'

function Write-Both([string]$text) {
    Write-Host $text
    Add-Content -Path $script:logFile -Value ((Get-Date).ToString('yyyy-MM-dd HH:mm:ss') + '  ' + $text) -Encoding UTF8
}

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'Administrator rights required - requesting elevation (click Yes on the UAC prompt)...'
    Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File', $PSCommandPath)
    Start-Sleep -Seconds 1
    return
}

try {
    Write-Both ('vmms status before : ' + (Get-Service vmms).Status)
    Set-Service -Name vmms -StartupType Automatic -ErrorAction Stop
    if ((Get-Service vmms).Status -ne 'Running') {
        Start-Service -Name vmms -ErrorAction Stop
    }
    Start-Sleep -Seconds 2
    $svc = Get-Service vmms
    Write-Both ('vmms status after  : ' + $svc.Status + '   StartType=' + $svc.StartType)
    Write-Both 'Done. Hyper-V Manager should be able to connect to AZURE now.'
} catch {
    Write-Both ('FAILED: ' + $_.Exception.Message)
}
Start-Sleep -Seconds 4
