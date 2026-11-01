param([Parameter(Mandatory = $true)][string]$Latex)

$exe = "C:\Users\AZURE\Desktop\md2word\CODEX\MarkdownToWord\bin\Debug\net10.0-windows10.0.19041.0\win-x64\MarkdownToWord.exe"
$outFile = "C:\Users\AZURE\Desktop\md2word\CODEX\_output\math.out.txt"
$errFile = "C:\Users\AZURE\Desktop\md2word\CODEX\_output\math.err.txt"

$quoted = '"' + ($Latex -replace '"', '\"') + '"'
Start-Process -FilePath $exe -ArgumentList @('--math-probe', $quoted) -NoNewWindow -Wait `
    -RedirectStandardOutput $outFile -RedirectStandardError $errFile | Out-Null

$raw = Get-Content $outFile -Raw -Encoding UTF8
($raw -replace '><', ">`n<")
if ((Get-Item $errFile).Length -gt 0) {
    Write-Host "--- stderr ---"
    Get-Content $errFile
}
