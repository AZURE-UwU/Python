$ErrorActionPreference = "Continue"
$log = "C:\Users\AZURE\Documents\ChatGPT\swap\TestDisks\probe4.txt"
$vhdx = "C:\Users\AZURE\Documents\ChatGPT\swap\TestDisks\dm-selftest.vhdx"

function W($t) { $t | Out-File -FilePath $log -Append -Encoding utf8 }
"=== probe4 $(Get-Date -Format 'HH:mm:ss') ===" | Out-File -FilePath $log -Encoding utf8
W ("admin=" + ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))

# 挂载测试盘
try { Mount-VHD -Path $vhdx -NoDriveLetter -ErrorAction Stop; W "mounted ok" } catch { W ("mount failed: " + $_.Exception.Message) }
Start-Sleep -Seconds 3

$disks = Get-Disk
foreach ($d in $disks) {
    W ("disk {0}: name={1} bus={2} size={3} style={4} off={5} ro={6} op={7}" -f $d.Number, $d.FriendlyName, $d.BusType, $d.Size, $d.PartitionStyle, $d.IsOffline, $d.IsReadOnly, $d.OperationalStatus)
}

$test = $disks | Where-Object { $_.BusType -eq 'File Backed Virtual' -and $_.Size -lt 100GB } | Select-Object -First 1
if (-not $test) { W "no test disk found"; exit 1 }
W ("test disk number = " + $test.Number)

# WMI 视角
$cim = Get-CimInstance -Namespace root/Microsoft/Windows/Storage -ClassName MSFT_Disk -Filter "Number=$($test.Number)"
W ("wmi: style={0} off={1} ro={2} partitions={3} largestFree={4} op={5}" -f $cim.PartitionStyle, $cim.IsOffline, $cim.IsReadOnly, $cim.NumberOfPartitions, $cim.LargestFreeExtent, $cim.OperationalStatus)

# 用 PowerShell 官方命令创建分区
try {
    $p = New-Partition -DiskNumber $test.Number -Size 512MB -ErrorAction Stop
    W ("New-Partition OK: partition#=" + $p.PartitionNumber + " offset=" + $p.Offset + " size=" + $p.Size)
} catch {
    W ("New-Partition FAILED: " + $_.Exception.Message)
}

# 直接看分区表
$parts = Get-Partition -DiskNumber $test.Number -ErrorAction SilentlyContinue
if ($parts) { foreach ($p in $parts) { W ("partition {0}: offset={1} size={2} type={3}" -f $p.PartitionNumber, $p.Offset, $p.Size, $p.Type) } } else { W "no partitions listed" }

# 卸载
try { Dismount-VHD -Path $vhdx -ErrorAction Stop; W "dismounted ok" } catch { W ("dismount failed: " + $_.Exception.Message) }
W "=== end ==="
