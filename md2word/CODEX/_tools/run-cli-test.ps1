param(
    [string]$SourcePath = "C:\Users\AZURE\Desktop\md2word\CODEX\MarkdownToWord\Samples\syntax-tour.md",
    [string]$OutputPath = "C:\Users\AZURE\Desktop\md2word\CODEX\_output\syntax-tour.docx",
    [string[]]$Extra = @()
)

$exe = "C:\Users\AZURE\Desktop\md2word\CODEX\MarkdownToWord\bin\Debug\net10.0-windows10.0.19041.0\win-x64\MarkdownToWord.exe"
$outFile = "C:\Users\AZURE\Desktop\md2word\CODEX\_output\cli.out.txt"
$errFile = "C:\Users\AZURE\Desktop\md2word\CODEX\_output\cli.err.txt"
New-Item -ItemType Directory -Force -Path (Split-Path $OutputPath) | Out-Null

$arguments = @('--convert', $SourcePath, $OutputPath, '--validate') + $Extra
$process = Start-Process -FilePath $exe -ArgumentList $arguments -NoNewWindow -Wait -PassThru `
    -RedirectStandardOutput $outFile -RedirectStandardError $errFile

Write-Host ("EXIT=" + $process.ExitCode)
Write-Host "--- stdout ---"
Get-Content $outFile -ErrorAction SilentlyContinue
Write-Host "--- stderr ---"
Get-Content $errFile -ErrorAction SilentlyContinue
