using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using MarkdownToWord.Core;
using MarkdownToWord.Core.Html;

namespace MarkdownToWord.Clipboard;

/// <summary>
/// 把"带格式的正文"放进剪贴板：HTML Format（CF_HTML）+ 一份纯文本兜底。
///
/// 与 <see cref="ShellFileClipboard"/>（放 docx 文件）不同，这里用的是 Win32 剪贴板静态数据：
/// 内容是一次性写进系统剪贴板的，进程退出后粘贴仍然有效，也不需要 OLE 消息循环。
/// Word 收到 HTML 后会把带格式的正文插到光标处，公式（OMML）会还原成 Word 原生公式。
/// </summary>
internal static class RichTextClipboard
{
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;
    private const string HtmlFormatName = "HTML Format";
    private const string StartMarker = "<!--StartFragment-->";
    private const string EndMarker = "<!--EndFragment-->";

    /// <summary>生成富文本并写入剪贴板，返回转换过程中的提示信息。</summary>
    public static List<string> Copy(string markdown, ConversionOptions options, string? baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(options);

        var html = HtmlClipboardBuilder.BuildHtml(markdown, options, baseDirectory, out var warnings);
        WriteToClipboard(html, BuildPlainText(markdown));
        return warnings;
    }

    /// <summary>把 HTML 包成剪贴板要的 CF_HTML 字节流（头部偏移都是 UTF-8 字节位置）。</summary>
    public static byte[] BuildCfHtml(string html)
    {
        const string headerTemplate =
            "Version:0.9\r\n" +
            "StartHTML:{0:D10}\r\n" +
            "EndHTML:{1:D10}\r\n" +
            "StartFragment:{2:D10}\r\n" +
            "EndFragment:{3:D10}\r\n" +
            "StartSelection:{2:D10}\r\n" +
            "EndSelection:{3:D10}\r\n";

        var payload = Encoding.UTF8.GetBytes(html);
        var headerSize = Encoding.UTF8.GetByteCount(string.Format(headerTemplate, 0, 0, 0, 0));

        var fragmentStart = IndexOf(payload, StartMarker);
        var fragmentEnd = IndexOf(payload, EndMarker);
        if (fragmentStart < 0 || fragmentEnd < 0)
        {
            fragmentStart = 0;
            fragmentEnd = payload.Length;
        }
        else
        {
            fragmentStart += Encoding.UTF8.GetByteCount(StartMarker);
        }

        var header = string.Format(
            headerTemplate,
            headerSize,
            headerSize + payload.Length,
            headerSize + fragmentStart,
            headerSize + fragmentEnd);

        var headerBytes = Encoding.UTF8.GetBytes(header);
        var result = new byte[headerBytes.Length + payload.Length];
        Buffer.BlockCopy(headerBytes, 0, result, 0, headerBytes.Length);
        Buffer.BlockCopy(payload, 0, result, headerBytes.Length, payload.Length);
        return result;
    }

    private static void WriteToClipboard(string html, string plainText)
    {
        var htmlFormat = RegisterClipboardFormat(HtmlFormatName);
        if (htmlFormat == 0)
        {
            throw new InvalidOperationException("系统没有注册 " + HtmlFormatName + " 剪贴板格式。");
        }

        if (!TryOpenClipboard())
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "剪贴板正被其他程序占用，稍后再试。");
        }

        try
        {
            EmptyClipboard();

            var htmlHandle = AllocateGlobal(BuildCfHtml(html));
            if (SetClipboardData(htmlFormat, htmlHandle) == IntPtr.Zero)
            {
                GlobalFree(htmlHandle);
                throw new Win32Exception(Marshal.GetLastWin32Error(), "写入 HTML 剪贴板失败。");
            }

            // 纯文本只是兜底：失败不影响 Word 的粘贴
            var textHandle = AllocateGlobal(Encoding.Unicode.GetBytes(plainText + "\0"));
            if (SetClipboardData(CfUnicodeText, textHandle) == IntPtr.Zero)
            {
                GlobalFree(textHandle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>剪贴板可能正被别的程序占用，稍微重试几次。</summary>
    private static bool TryOpenClipboard()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                return true;
            }

            Thread.Sleep(80);
        }

        return false;
    }

    private static string BuildPlainText(string markdown)
    {
        try
        {
            var plain = NormalizePlainText(MarkdownConverter.ToPlainText(markdown));
            if (!string.IsNullOrWhiteSpace(plain))
            {
                return plain;
            }
        }
        catch
        {
            // 纯文本提取失败就退回原始 Markdown
        }

        return markdown ?? string.Empty;
    }

    private static string NormalizePlainText(string plain)
    {
        var text = Regex.Replace(
            plain,
            @"<svg\b[\s\S]*?</svg>",
            string.Empty,
            RegexOptions.IgnoreCase);
        text = Regex.Replace(
            text,
            @"</?(?:p|div|span|b|strong|i|em|u|mark|del|ins|sup|sub|br|hr|ul|ol|li|dl|dt|dd|table|thead|tbody|tr|td|th|a|abbr|img|figure|figcaption|code|pre|script|style)(?:\s[^>]*)?/?>",
            match => Regex.IsMatch(match.Value, @"^<(?:br|hr)\b", RegexOptions.IgnoreCase) ? "\n" : string.Empty,
            RegexOptions.IgnoreCase);
        return WebUtility.HtmlDecode(text);
    }

    private static int IndexOf(byte[] haystack, string asciiNeedle)
    {
        var needle = Encoding.ASCII.GetBytes(asciiNeedle);
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var matched = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return i;
            }
        }

        return -1;
    }

    private static IntPtr AllocateGlobal(byte[] bytes)
    {
        var handle = GlobalAlloc(GmemMoveable, (UIntPtr)(uint)Math.Max(1, bytes.Length));
        if (handle == IntPtr.Zero)
        {
            throw new OutOfMemoryException("剪贴板内存分配失败。");
        }

        var target = GlobalLock(handle);
        if (target == IntPtr.Zero)
        {
            GlobalFree(handle);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "剪贴板内存锁定失败。");
        }

        try
        {
            Marshal.Copy(bytes, 0, target, bytes.Length);
        }
        finally
        {
            GlobalUnlock(handle);
        }

        return handle;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hwndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);
}
