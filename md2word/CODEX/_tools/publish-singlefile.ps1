# 发布"单文件版"：整个程序（.NET 运行时 + Windows App SDK + 资源）打包成一个 exe。
# 产物默认放到 dist\MarkdownToWord-SingleFile\，被替换掉的旧文件按规矩移入 BACKUP\DEL。
param(
    [string]$Configuration = "Release",
    [string]$OutputDir = ".\dist\MarkdownToWord-SingleFile"
)

$ErrorActionPreference = 'Stop'

dotnet publish ".\MarkdownToWord\MarkdownToWord.csproj" -c $Configuration -r win-x64 -p:SingleFilePublish=true -v m
if ($LASTEXITCODE -ne 0) { throw "publish 失败，退出码 $LASTEXITCODE" }

$publishDir = ".\MarkdownToWord\bin\$Configuration\net10.0-windows10.0.19041.0\win-x64\publish"
$exe = Join-Path $publishDir "MarkdownToWord.exe"
if (-not (Test-Path $exe)) { throw "没找到发布产物：$exe" }

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
$target = Join-Path $OutputDir "MarkdownToWord.exe"
if (Test-Path $target) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $bak = ".\BACKUP\DEL\singlefile-$stamp"
    New-Item -ItemType Directory -Path $bak -Force | Out-Null
    Move-Item -LiteralPath $target -Destination (Join-Path $bak "MarkdownToWord.exe") -Force
    Write-Host "旧单文件版 -> $bak"
}

Copy-Item -LiteralPath $exe -Destination $target -Force
$size = [Math]::Round((Get-Item $target).Length / 1MB, 1)
Write-Host ("单文件版已生成：" + (Resolve-Path $target).Path + "（" + $size + " MB）")
