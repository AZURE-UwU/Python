# Open the generated HTML in Word (the same importer Word uses for clipboard HTML),
# report how many native equations Word recognised, and optionally try a real paste.
# Never quits Word: the user may have their own session open.
param(
    [Parameter(Mandatory = $true)][string]$HtmlPath,
    [string]$PdfPath = "",
    [switch]$Paste
)

$ErrorActionPreference = 'Stop'
$word = $null
$doc = $null
$pasteDoc = $null
$oldAlerts = $null

try {
    $word = New-Object -ComObject Word.Application
    $oldAlerts = $word.DisplayAlerts
    $word.DisplayAlerts = 0
    Write-Host ("Word version: " + $word.Version)
    Write-Host ("open documents before: " + $word.Documents.Count)

    $doc = $word.Documents.Open($HtmlPath, $false, $true)
    Write-Host ("html opened: paragraphs=" + $doc.Paragraphs.Count + " tables=" + $doc.Tables.Count + " inlineShapes=" + $doc.InlineShapes.Count)
    Write-Host ("OMaths (native equations) = " + $doc.OMaths.Count)

    for ($i = 1; $i -le [Math]::Min(5, $doc.OMaths.Count); $i++) {
        $text = $doc.OMaths.Item($i).Range.Text
        Write-Host ("  eq " + $i + " : " + $text)
    }

    if ($PdfPath -ne "") {
        $doc.ExportAsFixedFormat($PdfPath, 17)
        Write-Host ("pdf: " + $PdfPath)
    }

    if ($Paste) {
        $pasteDoc = $word.Documents.Add()
        $pasteDoc.Activate()
        $word.Selection.Paste()
        Write-Host ("pasted: paragraphs=" + $pasteDoc.Paragraphs.Count + " tables=" + $pasteDoc.Tables.Count + " inlineShapes=" + $pasteDoc.InlineShapes.Count)
        Write-Host ("pasted OMaths (native equations) = " + $pasteDoc.OMaths.Count)
        $openedText = $doc.Content.Text
        $pastedText = $pasteDoc.Content.Text
        Write-Host ("opened text length = " + $openedText.Length + " ; pasted text length = " + $pastedText.Length)
        Write-Host ("pasted text: " + ($pastedText -replace "[\r\n]+", " "))
        for ($i = 1; $i -le [Math]::Min(5, $pasteDoc.OMaths.Count); $i++) {
            Write-Host ("  pasted eq " + $i + " : " + $pasteDoc.OMaths.Item($i).Range.Text)
        }
    }
}
finally {
    if ($pasteDoc) { try { $pasteDoc.Close(0) } catch { } }
    if ($doc) { try { $doc.Close(0) } catch { } }
    if ($word) {
        try { if ($oldAlerts -ne $null) { $word.DisplayAlerts = $oldAlerts } } catch { }
    }
}
