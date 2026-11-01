# Read the "HTML Format" clipboard data straight from the Win32 clipboard and report
# what a consumer (Word) would see: CF_HTML header offsets, OMML formulas, images, text.
param(
    [string]$DumpTo = "C:\Users\AZURE\Desktop\md2word\CODEX\_output\clipboard-html.html"
)

$code = @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class HtmlClip
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hwndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalSize(IntPtr hMem);

    public static byte[] Read(uint format)
    {
        if (!OpenClipboard(IntPtr.Zero)) throw new Exception("OpenClipboard failed");
        try
        {
            var handle = GetClipboardData(format);
            if (handle == IntPtr.Zero) return null;
            var size = (int)GlobalSize(handle);
            var ptr = GlobalLock(handle);
            if (ptr == IntPtr.Zero) return null;
            try
            {
                var bytes = new byte[size];
                Marshal.Copy(ptr, bytes, 0, size);
                return bytes;
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    public static uint Html()
    {
        return RegisterClipboardFormat("HTML Format");
    }

    public static uint Text()
    {
        return 13;
    }
}
'@

Add-Type -TypeDefinition $code -Language CSharp

$bytes = [HtmlClip]::Read([HtmlClip]::Html())
if (-not $bytes) {
    Write-Host "no HTML Format on the clipboard (probably the docx-file mode)"
    exit 1
}

$all = [Text.Encoding]::UTF8.GetString($bytes)
$ascii = [Text.Encoding]::UTF8.GetBytes($all)
Write-Host ("HTML Format bytes: " + $bytes.Length)

$headerEnd = $all.IndexOf("<html")
$header = $all.Substring(0, $headerEnd)
Write-Host "--- header ---"
$header.TrimEnd().Split("`r`n") | ForEach-Object { Write-Host ("  " + $_) }

$startHtml = -1; $endHtml = -1; $startFrag = -1; $endFrag = -1
if ($header -match "StartHTML:(\d+)") { $startHtml = [int]$Matches[1] }
if ($header -match "EndHTML:(\d+)") { $endHtml = [int]$Matches[1] }
if ($header -match "StartFragment:(\d+)") { $startFrag = [int]$Matches[1] }
if ($header -match "EndFragment:(\d+)") { $endFrag = [int]$Matches[1] }

$seg = {
    param($from, $length)
    [Text.Encoding]::UTF8.GetString($ascii, $from, $length).Replace("`r", " ").Replace("`n", " ")
}

Write-Host "--- offset check (byte offsets into the utf-8 payload) ---"
Write-Host ("  StartHTML points at : " + (& $seg $startHtml 6))
Write-Host ("  StartFragment points at : " + (& $seg $startFrag 30))
Write-Host ("  20 bytes before EndFragment : " + (& $seg ($endFrag - 20) 20))
Write-Host ("  EndHTML equals payload length : " + ($endHtml -eq $ascii.Length) + " (" + $endHtml + " / " + $ascii.Length + ")")

$fragment = [Text.Encoding]::UTF8.GetString($ascii, $startFrag, $endFrag - $startFrag)
Set-Content -LiteralPath $DumpTo -Value $all -Encoding UTF8
Write-Host ("--- fragment (dumped to " + $DumpTo + ") ---")
Write-Host ("  chars : " + $fragment.Length)
Write-Host ("  <m:oMath> count : " + ([regex]::Matches($fragment, "<m:oMath")).Count)
Write-Host ("  <img> count : " + ([regex]::Matches($fragment, "<img")).Count)
Write-Host ("  file:// image src : " + ([regex]::Matches($fragment, 'src="file:///')).Count)
Write-Host ("  coloured code spans : " + ([regex]::Matches($fragment, '<span style="color:#')).Count)
Write-Host ("  <td> count : " + ([regex]::Matches($fragment, "<td")).Count)
Write-Host ("  <h1>-<h3> count : " + ([regex]::Matches($fragment, "<h[123]>")).Count)
Write-Host ("  head 200 chars : " + $fragment.Substring(0, [Math]::Min(200, $fragment.Length)).Replace("`r", " ").Replace("`n", " "))

$firstMath = $fragment.IndexOf("<m:oMath")
if ($firstMath -ge 0) {
    $len = [Math]::Min(300, $fragment.Length - $firstMath)
    Write-Host ("  first formula : " + $fragment.Substring($firstMath, $len).Replace("`r", " ").Replace("`n", " "))

    # the maths element must be well-formed xml on its own (Word parses it as OMML)
    try {
        $close = $fragment.IndexOf("</m:oMath>", $firstMath)
        $xml = $fragment.Substring($firstMath, $close - $firstMath + 10)
        [void][xml]$xml
        Write-Host "  first formula parses as xml : True"
    }
    catch {
        Write-Host ("  first formula xml error : " + $_.Exception.Message)
    }
}

$textBytes = [HtmlClip]::Read([HtmlClip]::Text())
if ($textBytes) {
    $t = [Text.Encoding]::Unicode.GetString($textBytes).TrimEnd([char]0)
    Write-Host ("--- plain text fallback : " + $t.Length + " chars ---")
}
else {
    Write-Host "--- no plain text fallback ---"
}
