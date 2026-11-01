$ErrorActionPreference = "Continue"
$log = "C:\Users\AZURE\Documents\ChatGPT\swap\TestDisks\probe3.txt"
Remove-Item $log -ErrorAction SilentlyContinue

"line1 admin=" + ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) | Out-File -FilePath $log -Encoding utf8
"line2 vds=" + (Get-Service vds).Status | Out-File -FilePath $log -Append -Encoding utf8

$vhdx = "C:\Users\AZURE\Documents\ChatGPT\swap\TestDisks\dm-selftest.vhdx"
$attachScript = "C:\Users\AZURE\Documents\ChatGPT\swap\TestDisks\attach-vdisk.txt"
@("select vdisk file=`"$vhdx`"", "attach vdisk") | Set-Content -Path $attachScript -Encoding Unicode

$result = & diskpart /s $attachScript 2>&1 | Out-String
"line3 diskpart exit=$LASTEXITCODE" | Out-File -FilePath $log -Append -Encoding utf8
"line4 output:" | Out-File -FilePath $log -Append -Encoding utf8
$result | Out-File -FilePath $log -Append -Encoding utf8
Start-Sleep -Seconds 3
"line5 disks:" | Out-File -FilePath $log -Append -Encoding utf8
(Get-Disk | Select-Object Number, FriendlyName, BusType, Size, OperationalStatus | Format-Table | Out-String) | Out-File -FilePath $log -Append -Encoding utf8
