using System.Net.Http;

namespace MarkdownToWord.Core.Docx;

internal enum ImageKind
{
    Png,
    Jpeg,
    Gif,
    Bmp,
    Tiff,
    WebP,
    Unknown,
}

internal sealed record ResolvedImage(byte[] Data, ImageKind Kind, int PixelWidth, int PixelHeight, string Source);

/// <summary>
/// 图片解析：支持本地相对/绝对路径、file:// 、data:base64 以及（可选的）http(s) 远程地址。
/// 解析失败不会抛异常，只是返回 null，由调用方降级为占位文本。
/// </summary>
internal sealed class ImageResolver : IDisposable
{
    private const int MaxRemoteBytes = 20 * 1024 * 1024;

    private readonly ConversionOptions _opt;
    private readonly string? _baseDirectory;
    private readonly HttpClient? _http;
    private readonly Dictionary<string, ResolvedImage?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ImageResolver(ConversionOptions options, string? baseDirectory)
    {
        _opt = options;
        _baseDirectory = baseDirectory;

        if (options.AllowRemoteImages)
        {
            var handler = new HttpClientHandler { AllowAutoRedirect = true };
            _http = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(Math.Clamp(options.RemoteImageTimeoutSeconds, 3, 60)),
            };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) MarkdownToWord/1.0");
        }
    }

    public List<string> Warnings { get; } = [];

    public ResolvedImage? Resolve(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var key = url.Trim();
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        ResolvedImage? result;
        try
        {
            result = Load(key);
        }
        catch (Exception ex)
        {
            Warnings.Add($"图片加载失败：{key}（{ex.GetType().Name}）");
            result = null;
        }

        _cache[key] = result;
        return result;
    }

    private ResolvedImage? Load(string url)
    {
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return LoadDataUri(url);
        }

        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return LoadRemote(url);
        }

        return LoadFile(url);
    }

    private ResolvedImage? LoadDataUri(string url)
    {
        var comma = url.IndexOf(',');
        if (comma < 0)
        {
            return null;
        }

        var meta = url[5..comma];
        var payload = url[(comma + 1)..];
        if (!meta.Contains("base64", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(payload);
            return BuildImage(bytes, url);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private ResolvedImage? LoadRemote(string url)
    {
        if (_http is null)
        {
            Warnings.Add($"已跳过远程图片（当前设置禁止联网）：{url}");
            return null;
        }

        try
        {
            using var response = _http.GetAsync(url, HttpCompletionOption.ResponseContentRead).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                Warnings.Add($"远程图片返回 { (int)response.StatusCode }：{url}");
                return null;
            }

            var bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            if (bytes.Length == 0 || bytes.Length > MaxRemoteBytes)
            {
                Warnings.Add($"远程图片过大或为空：{url}");
                return null;
            }

            return BuildImage(bytes, url);
        }
        catch (Exception ex)
        {
            Warnings.Add($"远程图片下载失败：{url}（{ex.GetType().Name}）");
            return null;
        }
    }

    private ResolvedImage? LoadFile(string url)
    {
        string path;
        if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return null;
            }

            path = uri.LocalPath;
        }
        else
        {
            path = Uri.UnescapeDataString(url);
        }

        path = path.Replace('/', Path.DirectorySeparatorChar);

        if (!Path.IsPathRooted(path))
        {
            var root = _baseDirectory ?? Directory.GetCurrentDirectory();
            path = Path.GetFullPath(Path.Combine(root, path));
        }

        if (!File.Exists(path))
        {
            Warnings.Add($"找不到本地图片：{path}");
            return null;
        }

        var bytes = File.ReadAllBytes(path);
        return BuildImage(bytes, path);
    }

    private static ResolvedImage? BuildImage(byte[] bytes, string source)
    {
        var kind = DetectKind(bytes);
        if (kind == ImageKind.Unknown)
        {
            return null;
        }

        var (width, height) = ReadDimensions(bytes, kind);
        return new ResolvedImage(bytes, kind, width, height, source);
    }

    private static ImageKind DetectKind(byte[] data)
    {
        if (data.Length >= 8 &&
            data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
        {
            return ImageKind.Png;
        }

        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return ImageKind.Jpeg;
        }

        if (data.Length >= 6 && data[0] == 'G' && data[1] == 'I' && data[2] == 'F')
        {
            return ImageKind.Gif;
        }

        if (data.Length >= 2 && data[0] == 'B' && data[1] == 'M')
        {
            return ImageKind.Bmp;
        }

        if (data.Length >= 4 &&
            ((data[0] == 0x49 && data[1] == 0x49 && data[2] == 0x2A) ||
             (data[0] == 0x4D && data[1] == 0x4D && data[2] == 0x00)))
        {
            return ImageKind.Tiff;
        }

        if (data.Length >= 12 && data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F' &&
            data[8] == 'W' && data[9] == 'E' && data[10] == 'B' && data[11] == 'P')
        {
            return ImageKind.WebP;
        }

        return ImageKind.Unknown;
    }

    private static (int Width, int Height) ReadDimensions(byte[] data, ImageKind kind)
    {
        try
        {
            switch (kind)
            {
                case ImageKind.Png when data.Length >= 24:
                    return (ReadBigEndianInt32(data, 16), ReadBigEndianInt32(data, 20));

                case ImageKind.Gif when data.Length >= 10:
                    return (data[6] | (data[7] << 8), data[8] | (data[9] << 8));

                case ImageKind.Bmp when data.Length >= 26:
                    return (ReadLittleEndianInt32(data, 18), ReadLittleEndianInt32(data, 22));

                case ImageKind.Jpeg:
                    return ReadJpegDimensions(data);

                case ImageKind.WebP:
                    return ReadWebpDimensions(data);

                default:
                    return (0, 0);
            }
        }
        catch
        {
            return (0, 0);
        }
    }

    private static (int Width, int Height) ReadJpegDimensions(byte[] data)
    {
        var i = 2;
        while (i + 9 < data.Length)
        {
            if (data[i] != 0xFF)
            {
                i++;
                continue;
            }

            var marker = data[i + 1];
            if (marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF)
            {
                var height = (data[i + 5] << 8) | data[i + 6];
                var width = (data[i + 7] << 8) | data[i + 8];
                return (width, height);
            }

            var length = (data[i + 2] << 8) | data[i + 3];
            if (length <= 0)
            {
                break;
            }

            i += 2 + length;
        }

        return (0, 0);
    }

    private static (int Width, int Height) ReadWebpDimensions(byte[] data)
    {
        if (data.Length < 30)
        {
            return (0, 0);
        }

        var format = System.Text.Encoding.ASCII.GetString(data, 12, 4);
        switch (format)
        {
            case "VP8X":
                return (
                    1 + (data[24] | (data[25] << 8) | (data[26] << 16)),
                    1 + (data[27] | (data[28] << 8) | (data[29] << 16)));
            case "VP8 ":
                return (
                    (data[26] | (data[27] << 8)) & 0x3FFF,
                    (data[28] | (data[29] << 8)) & 0x3FFF);
            case "VP8L":
                var bits = data[21] | (data[22] << 8) | (data[23] << 16) | (data[24] << 24);
                return ((bits & 0x3FFF) + 1, ((bits >> 14) & 0x3FFF) + 1);
            default:
                return (0, 0);
        }
    }

    private static int ReadBigEndianInt32(byte[] data, int offset)
    {
        return (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
    }

    private static int ReadLittleEndianInt32(byte[] data, int offset)
    {
        return data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24);
    }

    public void Dispose()
    {
        _http?.Dispose();
    }
}
