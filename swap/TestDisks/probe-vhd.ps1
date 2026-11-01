# VHDX 创建能力探测脚本（仅诊断用，运行在提权会话中）
$log = "C:\Users\AZURE\Documents\ChatGPT\swap\TestDisks\probe.txt"
$vhdx = "C:\Users\AZURE\Documents\ChatGPT\swap\TestDisks\dm-selftest.vhdx"
$scriptPath = "C:\Users\AZURE\Documents\ChatGPT\swap\TestDisks\create-vdisk.txt"

function W([string]$text) { $text | Out-File -FilePath $log -Append -Encoding utf8 }

"=== probe start $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===" | Out-File -FilePath $log -Encoding utf8
W ("admin = " + ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))

$vds = Get-Service vds -ErrorAction SilentlyContinue
W ("vds status = " + $vds.Status + " / startType = " + $vds.StartType)

try { Start-Service vds -ErrorAction Stop; W "Start-Service vds: OK" } catch { W ("Start-Service vds failed: " + $_.Exception.Message) }

$vds = Get-Service vds -ErrorAction SilentlyContinue
W ("vds status after start attempt = " + $vds.Status)

"create vdisk file=`"$vhdx`" maximum=2048 type=expandable" | Set-Content -Path $scriptPath -Encoding Unicode
$out = & diskpart /s $scriptPath 2>&1 | Out-String
W ("diskpart exit code = " + $LASTEXITCODE)
W "diskpart output:"
W $out
W ("file exists after diskpart = " + (Test-Path $vhdx))

if (-not (Test-Path $vhdx)) {
    try {
        New-VHD -Path $vhdx -SizeBytes 2GB -Dynamic -ErrorAction Stop | Out-Null
        W "New-VHD: OK"
    } catch {
        W ("New-VHD failed: " + $_.Exception.Message)
    }
    W ("file exists after New-VHD = " + (Test-Path $vhdx))
}

if (Test-Path $vhdx) {
    W ("vhd file size = " + (Get-Item $vhdx).Length)
}

W "=== probe end ==="
