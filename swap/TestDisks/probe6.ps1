$ErrorActionPreference = "Stop"
$log = "C:\Users\AZURE\Documents\ChatGPT\swap\TestDisks\probe6.txt"
$vhdx = "C:\Users\AZURE\Documents\ChatGPT\swap\TestDisks\dm-selftest.vhdx"
function W($t) { Add-Content -Path $log -Value $t -Encoding UTF8 }
Set-Content -Path $log -Value ("=== probe6 " + (Get-Date -Format 'HH:mm:ss') + " ===") -Encoding UTF8
Mount-VHD -Path $vhdx -NoDriveLetter
Start-Sleep -Seconds 3
$test = Get-Disk | Where-Object { $_.BusType -eq 'File Backed Virtual' -and $_.Size -lt 100GB } | Select-Object -First 1
W ("test disk = " + $test.Number + " style=" + $test.PartitionStyle)
$p = New-Partition -DiskNumber $test.Number -Size 256MB
W ("partition created #" + $p.PartitionNumber + " offset=" + $p.Offset + " size=" + $p.Size)
Start-Sleep -Seconds 2
try { Add-PartitionAccessPath -DiskNumber $test.Number -PartitionNumber $p.PartitionNumber -AccessPath 'Y:\'; W "letter before format: OK" } catch { W ("letter before format FAILED: " + $_.Exception.Message) }
try { Format-Volume -Partition $p -FileSystem NTFS -NewFileSystemLabel PROBE -Confirm:$false | Out-Null; W "format via -Partition: OK" } catch { W ("format via -Partition FAILED: " + $_.Exception.Message) }
Start-Sleep -Seconds 2
try { Add-PartitionAccessPath -DiskNumber $test.Number -PartitionNumber $p.PartitionNumber -AccessPath 'Y:\'; W "letter after format: OK" } catch { W ("letter after format FAILED: " + $_.Exception.Message) }
W ("volumes: " + ((Get-Volume | ForEach-Object { $_.DriveLetter.ToString() + "|" + $_.FileSystem + "|" + $_.FileSystemLabel }) -join " ; "))
Dismount-VHD -Path $vhdx
W "dismounted"
