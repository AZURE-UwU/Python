param(
    [string]$ExePath = "C:\Users\AZURE\Desktop\md2word\CODEX\dist\MarkdownToWord\MarkdownToWord.exe",
    [string]$SourcePath = "C:\Users\AZURE\Desktop\md2word\CODEX\dist\MarkdownToWord\Samples\syntax-tour.md"
)

Add-Type -AssemblyName System.Windows.Forms

$outFile = "C:\Users\AZURE\Desktop\md2word\CODEX\_output\clip-check.out.txt"
$errFile = "C:\Users\AZURE\Desktop\md2word\CODEX\_output\clip-check.err.txt"

# 先清空剪贴板，避免看到上一次的残留
[System.Windows.Forms.Clipboard]::Clear()

$process = Start-Process -FilePath $ExePath -ArgumentList @('--copy', $SourcePath) -NoNewWindow -Wait -PassThru `
    -RedirectStandardOutput $outFile -RedirectStandardError $errFile
Write-Host ("EXIT=" + $process.ExitCode)

Write-Host "--- 应用输出 ---"
Get-Content $outFile -Encoding UTF8 -ErrorAction SilentlyContinue
Get-Content $errFile -Encoding UTF8 -ErrorAction SilentlyContinue

Write-Host "--- 剪贴板：文件 ---"
$files = [System.Windows.Forms.Clipboard]::GetFileDropList()
if ($files.Count -gt 0) {
    foreach ($item in $files) { Write-Host ("  " + $item) }
} else {
    Write-Host "  (无)"
}

Write-Host "--- 剪贴板：文本 ---"
$text = [System.Windows.Forms.Clipboard]::GetText()
if ([string]::IsNullOrEmpty($text)) {
    Write-Host "  (无)"
} else {
    Write-Host ("  " + $text.Substring(0, [Math]::Min(60, $text.Length)).Replace("`r", " ").Replace("`n", " "))
}

Write-Host "--- 剪贴板：格式 ---"
$data = [System.Windows.Forms.Clipboard]::GetDataObject()
if ($data) { $data.GetFormats() | ForEach-Object { Write-Host ("  " + $_) } }
