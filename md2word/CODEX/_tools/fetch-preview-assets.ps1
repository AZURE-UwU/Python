$ErrorActionPreference = 'Stop'

$proxy = 'http://127.0.0.1:7897'
$work = 'C:\Users\AZURE\Desktop\md2word\CODEX\_output\assets-download'
$target = 'C:\Users\AZURE\Desktop\md2word\CODEX\MarkdownToWord\Assets\preview'

New-Item -ItemType Directory -Force -Path $work, $target | Out-Null

function Get-Tarball([string]$url, [string]$file) {
    $dest = Join-Path $work $file
    if (Test-Path $dest) { Write-Host "已存在：$file"; return $dest }
    Write-Host "下载 $url"
    & curl.exe -sS -x $proxy -L --max-time 180 -o $dest $url
    if (-not (Test-Path $dest) -or (Get-Item $dest).Length -lt 1000) { throw "下载失败：$url" }
    return $dest
}

$katexVersion = '0.16.11'
$hljsVersion = '11.10.0'

$katexTar = Get-Tarball "https://registry.npmjs.org/katex/-/katex-$katexVersion.tgz" "katex.tgz"
$hljsTar = Get-Tarball "https://registry.npmjs.org/@highlightjs/cdn-assets/-/cdn-assets-$hljsVersion.tgz" "hljs.tgz"

$katexDir = Join-Path $work 'katex'
$hljsDir = Join-Path $work 'hljs'
New-Item -ItemType Directory -Force -Path $katexDir, $hljsDir | Out-Null

& tar.exe -xzf $katexTar -C $katexDir
& tar.exe -xzf $hljsTar -C $hljsDir

Write-Host "--- katex dist ---"
Get-ChildItem (Join-Path $katexDir 'package\dist') | Select-Object -First 12 -ExpandProperty Name
Write-Host "--- hljs ---"
Get-ChildItem $hljsDir -Recurse -Depth 2 -Filter '*.min.js' | Select-Object -First 5 -ExpandProperty FullName
