param(
    [Parameter(Mandatory = $true)][string]$DocxPath,
    [string]$PartName = "word/document.xml",
    [int]$ShowChars = 6000
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$target = Join-Path "C:\Users\AZURE\Desktop\md2word\CODEX\_output" "extract-$stamp"
New-Item -ItemType Directory -Force -Path $target | Out-Null

[System.IO.Compression.ZipFile]::ExtractToDirectory($DocxPath, $target)

Write-Host "解包目录：$target"
Write-Host "--- 包内文件 ---"
Get-ChildItem $target -Recurse -File | ForEach-Object { $_.FullName.Replace($target, '') }

$partPath = Join-Path $target ($PartName -replace '/', '\')
if (-not (Test-Path $partPath)) {
    Write-Host "缺少部件：$PartName"
    exit 1
}

$raw = Get-Content $partPath -Raw -Encoding UTF8
Write-Host ("--- {0} 长度 {1} ---" -f $PartName, $raw.Length)

$pretty = $raw
$pretty = $pretty -replace '><', ">`n<"
$lines = $pretty -split "`n"

Write-Host "--- 元素出现次数（前 40） ---"
$counts = @{}
foreach ($line in $lines) {
    if ($line -match '^<([a-zA-Z0-9]+):([a-zA-Z0-9]+)') {
        $key = $Matches[1] + ':' + $Matches[2]
        if ($counts.ContainsKey($key)) { $counts[$key]++ } else { $counts[$key] = 1 }
    }
}
$counts.GetEnumerator() | Sort-Object -Property Value -Descending | Select-Object -First 40 |
    ForEach-Object { Write-Host ("  {0} = {1}" -f $_.Key, $_.Value) }

Write-Host "--- 公式（m:）内容，最多 120 行 ---"
$mathLines = $lines | Where-Object { $_ -match '<m:' }
Write-Host ("匹配行数：{0}" -f $mathLines.Count)
$mathLines | Select-Object -First 120 | ForEach-Object { Write-Host $_ }
