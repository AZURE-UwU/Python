Add-Type -AssemblyName System.Windows.Forms

Write-Host "--- 剪贴板中的文件 ---"
$files = [System.Windows.Forms.Clipboard]::GetFileDropList()
if ($files.Count -gt 0) {
    foreach ($item in $files) { Write-Host ("  " + $item) }
} else {
    Write-Host "  (无)"
}

Write-Host "--- 剪贴板中的文本（前 100 字）---"
$text = [System.Windows.Forms.Clipboard]::GetText()
if ([string]::IsNullOrEmpty($text)) {
    Write-Host "  (无)"
} else {
    Write-Host ("  " + $text.Substring(0, [Math]::Min(100, $text.Length)).Replace("`r", " ").Replace("`n", " "))
}

Write-Host "--- 剪贴板可用格式 ---"
$data = [System.Windows.Forms.Clipboard]::GetDataObject()
if ($data) { $data.GetFormats() | ForEach-Object { Write-Host ("  " + $_) } }
