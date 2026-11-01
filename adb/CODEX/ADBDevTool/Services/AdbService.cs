/// @file    AdbService.cs
/// @author  Bowen
/// @module  ADBDevTool / adb 命令与输出解析
///
/// 不变量：
/// 1. `adb devices` 的 serial 形如 "192.168.1.20:37787"（无线）或不含冒号的 USB 序号；
///    只有含冒号的 serial 才能作为 `adb disconnect` 的参数。
/// 2. `adb mdns services` 每行三列：服务名 / 服务类型 / IP:端口；
///    `_adb-tls-pairing._tcp` 是配对端口，`_adb-tls-connect._tcp` 是连接端口，两者端口不同。
/// 3. `adb shell ls -la`（toybox）每行形如 "-rw-rw---- 1 u0_a123 media_rw 12345 2026-05-01 12:34 name"，
///    文件名可含空格，因此按固定字段前缀 + 剩余全部为名字来解析。
/// 4. `adb push/pull` 的输出重定向后只有一行汇总，没有可解析的百分比进度（实测 30 MB 文件）。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ADBDevTool.Models;

namespace ADBDevTool.Services;

internal enum MdnsServiceKind
{
    Pairing,
    Connect,
    Other,
}

internal sealed record MdnsService(string Name, MdnsServiceKind Kind, string Endpoint);

internal enum AdbDeviceState
{
    Connected,
    Unauthorized,
    Offline,
    Unknown,
}

internal sealed record AdbDeviceInfo(string Serial, AdbDeviceState State, string Model, string Product)
{
    public bool IsWireless => Serial.Contains(':', StringComparison.Ordinal)
        || Serial.EndsWith(AdbService.MdnsSerialSuffix, StringComparison.OrdinalIgnoreCase);

    public string DisplayName => string.IsNullOrWhiteSpace(Model)
        ? (IsWireless ? "未命名设备" : "USB 设备")
        : Model.Replace('_', ' ');

    public string StateText => State switch
    {
        AdbDeviceState.Connected => "已连接",
        AdbDeviceState.Unauthorized => "待授权",
        AdbDeviceState.Offline => "离线",
        _ => "未知",
    };
}

internal sealed class AdbService
{
    private static readonly Regex LsLongPattern = new(
        @"^(?<perms>[dl\-][rwxsStT\-]{9}[.+@]?)\s+(?<links>\d+)\s+(?<owner>\S+)\s+(?<group>\S+)\s+(?<size>\d+)\s+(?<date>\d{4}-\d{2}-\d{2})\s+(?<time>\d{1,2}:\d{2})\s+(?<name>.+)$",
        RegexOptions.Compiled);

    /// <summary>退化解析用：定位时间字段，其后的全部内容视为文件名。</summary>
    private static readonly Regex LsLegacyNamePattern = new(
        @"\d{1,2}:\d{2}(?::\d{2})?\s+(?<name>.+)$",
        RegexOptions.Compiled);

    /// <summary>adb 以 mDNS 服务名作为 serial 时的后缀（例如 adb-3D4E5F6G-1a2B3c._adb-tls-connect._tcp）。</summary>
    public const string MdnsSerialSuffix = "._adb-tls-connect._tcp";

    private readonly AdbRunner _runner;

    public AdbService(AdbRunner runner)
    {
        _runner = runner;
    }

    public string AdbPath => _runner.AdbPath;

    public Task<AdbResult> GetVersionAsync(CancellationToken ct = default)
        => _runner.RunAsync("version", TimeSpan.FromSeconds(10), ct);

    public Task<AdbResult> StartServerAsync(CancellationToken ct = default)
        => _runner.RunAsync("start-server", TimeSpan.FromSeconds(30), ct);

    public Task<AdbResult> KillServerAsync(CancellationToken ct = default)
        => _runner.RunAsync("kill-server", TimeSpan.FromSeconds(15), ct);

    public Task<AdbResult> ListDevicesAsync(CancellationToken ct = default)
        => _runner.RunAsync("devices -l", TimeSpan.FromSeconds(10), ct);

    public Task<AdbResult> ListMdnsServicesAsync(CancellationToken ct = default)
        => _runner.RunAsync("mdns services", TimeSpan.FromSeconds(10), ct);

    public Task<AdbResult> ConnectAsync(string endpoint, CancellationToken ct = default)
        => _runner.RunAsync($"connect {endpoint}", TimeSpan.FromSeconds(20), ct);

    public Task<AdbResult> DisconnectAsync(string endpoint, CancellationToken ct = default)
        => _runner.RunAsync($"disconnect {endpoint}", TimeSpan.FromSeconds(15), ct);

    public Task<AdbResult> PairAsync(string endpoint, string pairingCode, CancellationToken ct = default)
        => _runner.RunAsync($"pair {endpoint} {pairingCode}", TimeSpan.FromSeconds(60), ct);

    public Task<AdbResult> ShellAsync(string serial, string command, CancellationToken ct = default)
        => _runner.RunAsync($"-s {serial} shell {command}", TimeSpan.FromSeconds(30), ct);

    public Task<AdbResult> ListRemoteDirectoryAsync(string serial, string path, CancellationToken ct = default)
    {
        // 目录参数必须带尾斜杠：设备上 /sdcard 是指向 /storage/self/primary 的符号链接，
        // 不带斜杠时 toybox ls 只列链接自身（实测只回一行），带斜杠才列目录内容。
        var target = path.EndsWith('/') ? path : path + "/";
        return _runner.RunAsync($"-s {serial} shell ls -la {ShellQuote(target)}", TimeSpan.FromSeconds(20), ct);
    }

    public Task<AdbResult> PushFileAsync(string serial, string localPath, string remotePath, CancellationToken ct = default)
        => _runner.RunAsync(
            $"-s {serial} push {WindowsQuote(localPath)} {WindowsQuote(remotePath)}",
            TimeSpan.FromMinutes(60),
            ct);

    public Task<AdbResult> PullFileAsync(string serial, string remotePath, string localPath, CancellationToken ct = default)
        => _runner.RunAsync(
            $"-s {serial} pull {WindowsQuote(remotePath)} {WindowsQuote(localPath)}",
            TimeSpan.FromMinutes(60),
            ct);

    public Task<AdbResult> DeleteRemoteAsync(string serial, string path, CancellationToken ct = default)
        => _runner.RunAsync($"-s {serial} shell rm -rf {ShellQuote(path)}", TimeSpan.FromSeconds(30), ct);

    public Task<AdbResult> RenameRemoteAsync(string serial, string oldPath, string newPath, CancellationToken ct = default)
        => _runner.RunAsync($"-s {serial} shell mv {ShellQuote(oldPath)} {ShellQuote(newPath)}", TimeSpan.FromSeconds(30), ct);

    public Task<AdbResult> CreateRemoteDirectoryAsync(string serial, string path, CancellationToken ct = default)
        => _runner.RunAsync($"-s {serial} shell mkdir -p {ShellQuote(path)}", TimeSpan.FromSeconds(30), ct);

    /// <summary>按 POSIX 规则给参数加单引号，内部的单引号写成 '\''。</summary>
    public static string ShellQuote(string value)
        => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary>
    /// 给不经过设备 shell 的参数（adb push/pull 的两端路径）加双引号。
    /// 实测：给 pull 的远端路径加 shell 单引号时 adb 会收到字面单引号，报 failed to stat remote object。
    /// </summary>
    public static string WindowsQuote(string value) => "\"" + value + "\"";

    /// <summary>解析 `adb shell ls -la` 的输出；无法解析的行被忽略，"." 与 ".." 跳过。</summary>
    public static IReadOnlyList<RemoteEntry> ParseLsLong(string output, string parentPath)
    {
        var result = new List<RemoteEntry>();
        var prefix = parentPath.EndsWith('/') || parentPath.Length == 0 ? parentPath : parentPath + "/";

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r', ' ', '\t');
            if (line.Length == 0 || line.StartsWith("total ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string permissions;
            string name;
            string modified;
            long size;

            var match = LsLongPattern.Match(line);
            if (match.Success)
            {
                permissions = match.Groups["perms"].Value;
                name = match.Groups["name"].Value;
                modified = $"{match.Groups["date"].Value} {match.Groups["time"].Value}";
                size = long.TryParse(match.Groups["size"].Value, out var parsed) ? parsed : 0;
            }
            else
            {
                // 退化路径：非 toybox 的日期格式（例如 "May  1 12:34"）会让固定列数对不上，
                // 因此只假定「权限 链接数 属主 属组 大小」这前 5 列，文件名取时间字段之后的全部内容。
                var fields = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                var nameMatch = LsLegacyNamePattern.Match(line);
                if (fields.Length < 5 || !nameMatch.Success)
                {
                    continue;
                }

                permissions = fields[0];
                name = nameMatch.Groups["name"].Value.Trim();
                modified = string.Empty;
                size = long.TryParse(fields[4], out var parsed) ? parsed : 0;
            }

            if (name is "." or "..")
            {
                continue;
            }

            var isLink = permissions.StartsWith('l');
            if (isLink)
            {
                var arrow = name.IndexOf(" -> ", StringComparison.Ordinal);
                if (arrow > 0)
                {
                    name = name[..arrow];
                }
            }

            result.Add(new RemoteEntry
            {
                Name = name,
                FullPath = prefix + name,
                IsDirectory = permissions.StartsWith('d'),
                IsLink = isLink,
                Size = size,
                Modified = modified,
            });
        }

        return result;
    }

    /// <summary>解析 `adb mdns services`；无法解析的行被忽略。</summary>
    public static IReadOnlyList<MdnsService> ParseMdnsServices(string output)
    {
        var result = new List<MdnsService>();
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim('\r', ' ', '\t');
            if (line.Length == 0 || line.StartsWith("List of", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var columns = SplitColumns(line);
            if (columns.Length < 3)
            {
                continue;
            }

            var kind = columns[1] switch
            {
                var t when t.Contains("pairing", StringComparison.OrdinalIgnoreCase) => MdnsServiceKind.Pairing,
                var t when t.Contains("connect", StringComparison.OrdinalIgnoreCase) => MdnsServiceKind.Connect,
                _ => MdnsServiceKind.Other,
            };

            if (kind == MdnsServiceKind.Other)
            {
                continue;
            }

            result.Add(new MdnsService(columns[0], kind, columns[2]));
        }

        return result;
    }

    /// <summary>列分隔优先按制表符（platform-tools 37.0.1 的实际格式），不足三列时退化为按空白切分。</summary>
    private static string[] SplitColumns(string line)
    {
        var byTab = line.Split('\t', StringSplitOptions.RemoveEmptyEntries)
            .Select(column => column.Trim())
            .Where(column => column.Length > 0)
            .ToArray();
        if (byTab.Length >= 3)
        {
            return byTab;
        }

        return line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>解析 `adb devices -l`。</summary>
    public static IReadOnlyList<AdbDeviceInfo> ParseDevices(string output)
    {
        var result = new List<AdbDeviceInfo>();
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim('\r', ' ', '\t');
            if (line.Length == 0 || line.StartsWith("List of", StringComparison.OrdinalIgnoreCase) || line.StartsWith('*'))
            {
                continue;
            }

            var columns = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length < 2)
            {
                continue;
            }

            var state = columns[1] switch
            {
                "device" => AdbDeviceState.Connected,
                "unauthorized" => AdbDeviceState.Unauthorized,
                "offline" => AdbDeviceState.Offline,
                _ => AdbDeviceState.Unknown,
            };

            var model = string.Empty;
            var product = string.Empty;
            foreach (var attribute in columns.Skip(2))
            {
                var separator = attribute.IndexOf(':');
                if (separator <= 0)
                {
                    continue;
                }

                var key = attribute[..separator];
                var value = attribute[(separator + 1)..];
                if (key == "model")
                {
                    model = value;
                }
                else if (key == "product")
                {
                    product = value;
                }
            }

            result.Add(new AdbDeviceInfo(columns[0], state, model, product));
        }

        return result;
    }

    /// <summary>校验用户输入的 IP:端口，端口范围 1-65535。</summary>
    public static bool TryNormalizeEndpoint(string? input, out string endpoint, out string error)
    {
        endpoint = string.Empty;
        error = string.Empty;
        var text = (input ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            error = "请输入 IP:端口。";
            return false;
        }

        var separator = text.LastIndexOf(':');
        if (separator <= 0 || separator == text.Length - 1)
        {
            error = "格式应为 IP:端口，例如 192.168.1.20:37787。";
            return false;
        }

        var host = text[..separator].Trim();
        var portText = text[(separator + 1)..].Trim();
        if (!ushort.TryParse(portText, out var port) || port == 0)
        {
            error = "端口必须是 1-65535 之间的整数。";
            return false;
        }

        endpoint = $"{host}:{port}";
        return true;
    }

    /// <summary>把 mDNS 服务名形态的 serial 还原成它的 IP:端口；找不到对应服务时保持原样。</summary>
    public static string CanonicalizeSerial(string serial, IReadOnlyDictionary<string, string> endpointByServiceName)
    {
        if (!serial.EndsWith(MdnsSerialSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return serial;
        }

        var serviceName = serial[..^MdnsSerialSuffix.Length];
        return endpointByServiceName.TryGetValue(serviceName, out var endpoint) ? endpoint : serial;
    }

    /// <summary>只有 IP:端口 与 mDNS 服务名两种形态能被 adb disconnect 接受，USB 序号不在其中。</summary>
    public static IReadOnlyList<string> DisconnectTargetsFor(IEnumerable<string> aliases)
        => aliases
            .Where(alias => alias.Contains(':')
                || alias.EndsWith(MdnsSerialSuffix, StringComparison.OrdinalIgnoreCase))
            .ToList();
}
