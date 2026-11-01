/// @file    MainWindow.xaml.cs
/// @author  Bowen
/// @module  ADBDevTool / 主界面
///
/// 不变量：
/// 1. 刷新由 DispatcherQueueTimer 每 2.5 秒触发一次，_refreshGate 保证同一时刻只有一次刷新在跑。
/// 2. 设备列表内容只有在签名变化时才重建，避免正在选择时被刷新打断。
/// 3. 空提示与计数只随 _entries 一起更新：签名判定为「无变化」时列表行不动，提示也不许动，
///    否则会出现「提示说没有设备、列表却还有旧行」。

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ADBDevTool.Dialogs;
using ADBDevTool.Models;
using ADBDevTool.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;

namespace ADBDevTool;

public sealed partial class MainWindow : Window
{
    private const int MaxLogLines = 400;
    private const int ToggleHotkeyId = 1;

    /// <summary>拖动分隔条时的最小尺寸（DIP）。左列仍需能放下操作栏的一行按钮，否则会退化成一列按钮。</summary>
    private const double LeftColumnMinimumDip = 360;

    private const double FilePanelMinimumDip = 280;

    private const double DeviceRowMinimumDip = 160;

    private const double LogRowMinimumDip = 80;

    /// <summary>
    /// 窗口最小宽度（DIP）：面板按默认比例占 1/3 时，要保证左列 ≥ 360、面板 ≥ 280，
    /// 反推 (280×3)+58=898，取 900。
    /// </summary>
    private const double MinimumWindowWidthDip = 900;

    /// <summary>
    /// 窗口最小高度（DIP）：最窄左列下工具条与操作栏各折 2 行，加上设备列表 160 / 日志 80 与内外间距，
    /// 实测约 518，取 620 留余量。
    /// </summary>
    private const double MinimumWindowHeightDip = 620;


    /// <summary>scrcpy --print-fps 的输出形如 "INFO: 58 fps" 或 "INFO: 5 fps (+2 frames skipped)"。</summary>
    private static readonly Regex FpsPattern = new(@"(?<fps>\d+(?:\.\d+)?)\s*fps", RegexOptions.Compiled);

    private static readonly Regex SkippedFramesPattern = new(@"\+(?<count>\d+)\s*frames skipped", RegexOptions.Compiled);

    /// <summary>失效条目的徽标颜色与排序权重：压暗显示，并沉到列表末尾。</summary>
    private static readonly Color StaleBadgeColor = Color.FromArgb(255, 150, 150, 150);

    private const int StaleSortKey = 9;

    private readonly ObservableCollection<DeviceEntry> _entries = new();
    private readonly ObservableCollection<RemoteEntry> _remoteEntries = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly HashSet<string> _manualEndpoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherQueueTimer _timer;

    private AdbService? _adb;
    private AppSettings _settings = new();
    private Process? _scrcpyProcess;
    private GlobalHotkeyWindow? _hotkeyWindow;
    private string _remotePath = "/sdcard";
    private string? _remoteSerial;
    private bool _remoteBusy;
    private bool _stopDownloadProgress;
    /// <summary>列表内容签名；null 表示强制重建，不能用空串代替（空列表的签名就是空串）。</summary>
    private string? _signature;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();

        _settings = AppSettings.Load();
        ResizeWindowToDip(1320, 860);
        Log($"窗口初始化为 {AppWindow.Size.Width}x{AppWindow.Size.Height} 物理（缩放 {WindowScale:0.##}）。");
        UpdateWindowMinimumSize();
        ApplyWindowIcon();
        DeviceList.ItemsSource = _entries;
        RemoteList.ItemsSource = _remoteEntries;

        ColumnSplitter.UseCursor(InputSystemCursorShape.SizeWestEast);
        RowSplitter.UseCursor(InputSystemCursorShape.SizeNorthSouth);

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(2500);
        _timer.IsRepeating = true;
        _timer.Tick += async (_, _) => await RefreshAsync(manual: false);

        Closed += (_, _) =>
        {
            _timer.Stop();
            StopMirror(null);
            _hotkeyWindow?.Dispose();

            // 兜底：退出时再落一次盘，保证内存里生效的设置不会因为遗漏保存而丢失。
            _settings.Save();
        };

        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        var adbPath = AdbRunner.LocateAdb();
        if (adbPath is null)
        {
            SetStatus("未找到 adb.exe：请把 platform-tools 放到程序目录下。");
            Log("未找到 adb.exe。查找顺序：程序目录\\platform-tools\\adb.exe → 程序目录\\adb.exe → PATH → %LOCALAPPDATA%\\Android\\Sdk\\platform-tools。");
            SetButtonsEnabled(false);
            return;
        }

        Log($"adb: {adbPath}");
        _adb = new AdbService(new AdbRunner(adbPath));

        SetBusy(true, "正在启动 adb 服务…");
        try
        {
            var version = await _adb.GetVersionAsync();
            Log($"$ adb version → {version.Summary}");

            var start = await _adb.StartServerAsync();
            Log($"$ adb start-server → {start.Summary}");
        }
        catch (Exception ex)
        {
            Log($"启动 adb 服务失败：{ex.Message}");
        }
        finally
        {
            SetBusy(false, "就绪");
        }

        _timer.Start();
        await RefreshAsync(manual: false);
        Log($"初始化结束时窗口 {AppWindow.Size.Width}x{AppWindow.Size.Height} 物理。");
    }

    private async Task RefreshAsync(bool manual)
    {
        if (_adb is null)
        {
            return;
        }

        // 探活会把一次刷新拉长到秒级，手点「扫描局域网」如果直接跳过就会被静默丢弃，因此排队等前一次结束；
        // 自动刷新（2.5 秒周期）仍然跳过，不排队。
        if (manual)
        {
            await _refreshGate.WaitAsync();
        }
        else if (!_refreshGate.Wait(0))
        {
            return;
        }

        try
        {
            if (manual)
            {
                SetBusy(true, "正在扫描局域网…");
            }

            var devicesTask = _adb.ListDevicesAsync();
            var mdnsTask = _adb.ListMdnsServicesAsync();
            await Task.WhenAll(devicesTask, mdnsTask);

            var devices = AdbService.ParseDevices(devicesTask.Result.StandardOutput);
            var services = AdbService.ParseMdnsServices(mdnsTask.Result.StandardOutput);
            var deadEndpoints = await FindUnreachableEndpointsAsync(devices, services);
            Apply(devices, services, deadEndpoints);

            if (manual)
            {
                Log($"扫描完成：已连接 {devices.Count(d => d.State == AdbDeviceState.Connected)} 台，"
                    + $"mDNS 发现 {services.Count(s => s.Kind == MdnsServiceKind.Connect)} 个连接端口、"
                    + $"{services.Count(s => s.Kind == MdnsServiceKind.Pairing)} 个配对端口。");
                if (services.Count == 0)
                {
                    Log("提示：mDNS 没发现任何服务。确认手机与电脑在同一网段、手机「无线调试」已打开，且路由器未开启 AP 隔离。");
                }

                if (deadEndpoints.Count > 0)
                {
                    Log($"端口探活无响应：{string.Join("、", deadEndpoints)}。");
                }
            }
        }
        catch (Exception ex)
        {
            Log($"扫描异常：{ex.Message}");
        }
        finally
        {
            _refreshGate.Release();
            if (manual)
            {
                SetBusy(false, $"扫描完成，共 {_entries.Count} 项。");
            }
        }
    }

    private void Apply(
        IReadOnlyList<AdbDeviceInfo> devices,
        IReadOnlyList<MdnsService> services,
        IReadOnlySet<string> deadEndpoints)
    {
        var endpointByServiceName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var service in services.Where(service => service.Kind == MdnsServiceKind.Connect))
        {
            endpointByServiceName[service.Name] = service.Endpoint;
        }

        // 同一台手机可能有两个 serial：mDNS 自动连接时是 "<服务名>._adb-tls-connect._tcp"，
        // 手动 connect 后是 "IP:端口"。按 mDNS 服务名归并，列表里只保留一条。
        var aliasesBySerial = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var primaryBySerial = new Dictionary<string, AdbDeviceInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in devices)
        {
            var canonical = AdbService.CanonicalizeSerial(device.Serial, endpointByServiceName);
            if (!aliasesBySerial.TryGetValue(canonical, out var aliases))
            {
                aliases = new List<string>();
                aliasesBySerial[canonical] = aliases;
            }

            aliases.Add(device.Serial);

            // 优先保留 "IP:端口" 形态：它能直接作为 adb disconnect 参数，也便于复制。
            if (!primaryBySerial.TryGetValue(canonical, out var primary)
                || (!primary.Serial.Contains(':') && device.Serial.Contains(':')))
            {
                primaryBySerial[canonical] = device;
            }
        }

        var entries = new List<DeviceEntry>();

        foreach (var service in services.Where(s => s.Kind == MdnsServiceKind.Connect))
        {
            // 不变量：只有状态为 device 才建「已连接」条目；未授权 / 离线时设备 serial 同样等于 mDNS 端点，
            // 这里不建条目，留给下面的 primaryBySerial 循环按真实状态建。
            if (primaryBySerial.TryGetValue(service.Endpoint, out var connected))
            {
                if (connected.State == AdbDeviceState.Connected)
                {
                    entries.Add(ConnectedEntry(connected, AdbService.DisconnectTargetsFor(aliasesBySerial[service.Endpoint])));
                }

                continue;
            }

            var dead = deadEndpoints.Contains(service.Endpoint);
            entries.Add(new DeviceEntry
            {
                Endpoint = service.Endpoint,
                DisplayName = DescribeMdnsName(service.Name),
                Detail = dead ? $"{service.Endpoint} · 端口不通" : $"{service.Endpoint} · 无线调试，未连接",
                StateText = dead ? "失效" : "可连接",
                BadgeColor = dead ? StaleBadgeColor : Color.FromArgb(255, 21, 101, 192),
                CanConnect = !dead,
                IsPairingService = false,
                IsStale = dead,
                SortKey = dead ? StaleSortKey : 1,
            });
        }

        foreach (var service in services.Where(s => s.Kind == MdnsServiceKind.Pairing))
        {
            // 不变量：失效的配对端口 IsPairingService 为 false，配对对话框不预填该地址。
            var dead = deadEndpoints.Contains(service.Endpoint);
            entries.Add(new DeviceEntry
            {
                Endpoint = service.Endpoint,
                DisplayName = DescribeMdnsName(service.Name),
                Detail = dead ? $"{service.Endpoint} · 端口不通" : $"{service.Endpoint} · 配对端口，需 6 位配对码",
                StateText = dead ? "失效" : "待配对",
                BadgeColor = dead ? StaleBadgeColor : Color.FromArgb(255, 142, 36, 170),
                CanConnect = false,
                IsPairingService = !dead,
                IsStale = dead,
                SortKey = dead ? StaleSortKey : 2,
            });
        }

        foreach (var pair in primaryBySerial)
        {
            var device = pair.Value;
            if (entries.Any(entry => string.Equals(entry.Endpoint, device.Serial, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (device.State == AdbDeviceState.Connected)
            {
                entries.Add(ConnectedEntry(device, AdbService.DisconnectTargetsFor(aliasesBySerial[pair.Key])));
                continue;
            }

            var (stateText, sortKey) = device.State switch
            {
                AdbDeviceState.Unauthorized => ("待授权", 3),
                AdbDeviceState.Offline => ("离线", 4),
                _ => ("未知", 4),
            };

            // 无线地址的端口探不通时按「失效」显示：这条记录已经没人应答，点了也没用。
            var dead = device.IsWireless && deadEndpoints.Contains(device.Serial);
            entries.Add(new DeviceEntry
            {
                Endpoint = device.Serial,
                DisplayName = device.DisplayName,
                Detail = dead
                    ? $"{device.Serial} · 端口不通"
                    : device.IsWireless ? $"{device.Serial} · 无线调试" : $"{device.Serial} · USB",
                StateText = dead ? "失效" : stateText,
                BadgeColor = dead
                    ? StaleBadgeColor
                    : device.State == AdbDeviceState.Unauthorized
                        ? Color.FromArgb(255, 239, 108, 0)
                        : Color.FromArgb(255, 120, 120, 120),
                CanConnect = device.IsWireless && !dead,
                IsPairingService = false,
                IsStale = dead,
                SortKey = dead ? StaleSortKey : sortKey,
            });
        }

        foreach (var endpoint in _manualEndpoints)
        {
            if (entries.Any(entry => string.Equals(entry.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            entries.Add(new DeviceEntry
            {
                Endpoint = endpoint,
                DisplayName = "手动添加",
                Detail = deadEndpoints.Contains(endpoint) ? $"{endpoint} · 端口不通" : $"{endpoint} · 手动录入",
                StateText = deadEndpoints.Contains(endpoint) ? "失效" : "待连接",
                BadgeColor = deadEndpoints.Contains(endpoint) ? StaleBadgeColor : Color.FromArgb(255, 21, 101, 192),
                CanConnect = !deadEndpoints.Contains(endpoint),
                IsPairingService = false,
                IsStale = deadEndpoints.Contains(endpoint),
                SortKey = deadEndpoints.Contains(endpoint) ? StaleSortKey : 1,
            });
        }

        var ordered = entries
            .OrderBy(entry => entry.SortKey)
            .ThenBy(entry => entry.DisplayName, StringComparer.CurrentCulture)
            .ToList();

        var signature = string.Join("|", ordered.Select(entry => $"{entry.Endpoint}#{entry.StateText}#{entry.DisplayName}"));
        if (signature == _signature)
        {
            return;
        }

        _signature = signature;
        var previousEndpoint = (DeviceList.SelectedItem as DeviceEntry)?.Endpoint;

        _entries.Clear();
        foreach (var entry in ordered)
        {
            _entries.Add(entry);
        }

        CountText.Text = _entries.Count == 0 ? string.Empty : $"{_entries.Count} 项";
        EmptyHint.Visibility = _entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (previousEndpoint is not null)
        {
            RestoreSelection(previousEndpoint);

            // 重建集合后 ListView 会在下一个布局周期再处理一次选中状态，可能把刚设好的选中项清掉，
            // 因此等队列空一拍再补一次，否则连接/断开按钮会因为「未选择设备」而变灰。
            DispatcherQueue.TryEnqueue(() => RestoreSelection(previousEndpoint));
        }

        UpdateSelectionState();
    }

    private void RestoreSelection(string endpoint)
    {
        var target = _entries.FirstOrDefault(
            entry => string.Equals(entry.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase));
        if (target is not null && !ReferenceEquals(DeviceList.SelectedItem, target))
        {
            DeviceList.SelectedItem = target;
        }
    }

    /// <summary>
    /// 并发探活本轮出现的所有无线 IP:端口（mDNS 广播的连接 / 配对端口、adb 列表里尚未连上的无线地址、
    /// 手动录入的地址），返回端口连不上的那些，供列表置灰。mDNS 缓存与 adb 列表会残留已经失效的端口。
    /// 不变量：状态为 device 的已连接设备不参与探活，因此不会因一次探活失败而置灰。
    /// </summary>
    private async Task<IReadOnlySet<string>> FindUnreachableEndpointsAsync(
        IReadOnlyList<AdbDeviceInfo> devices,
        IReadOnlyList<MdnsService> services)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var service in services.Where(service => service.Kind != MdnsServiceKind.Other))
        {
            candidates.Add(service.Endpoint);
        }

        foreach (var device in devices.Where(device => device.State != AdbDeviceState.Connected))
        {
            if (device.IsWireless)
            {
                candidates.Add(device.Serial);
            }
        }

        foreach (var endpoint in _manualEndpoints)
        {
            candidates.Add(endpoint);
        }

        var results = await Task.WhenAll(candidates.Select(async endpoint =>
            (Endpoint: endpoint, Alive: await ProbeTcpAsync(endpoint) ?? true)));

        return results
            .Where(result => !result.Alive)
            .Select(result => result.Endpoint)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static DeviceEntry ConnectedEntry(AdbDeviceInfo device, IReadOnlyList<string> disconnectTargets) => new()
    {
        Endpoint = device.Serial,
        DisplayName = device.DisplayName,
        Detail = device.IsWireless ? $"{device.Serial} · 无线调试" : $"{device.Serial} · USB",
        StateText = "已连接",
        BadgeColor = Color.FromArgb(255, 46, 125, 50),
        CanConnect = false,
        IsPairingService = false,
        DisconnectTargets = disconnectTargets,
        SortKey = 0,
    };

    private static string DescribeMdnsName(string serviceName)
    {
        // mdns 服务名形如 adb-<设备标识>-<随机串>，取中间段作为可读名字。
        var parts = serviceName.Split('-', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $"无线调试设备 {parts[1]}" : serviceName;
    }

    private void OnScanClick(object sender, RoutedEventArgs e) => _ = RefreshAsync(manual: true);

    private async void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var updated = await SettingsDialog.ShowAsync(
            RootGrid.XamlRoot,
            WinRT.Interop.WindowNative.GetWindowHandle(this),
            _settings);
        if (updated is null)
        {
            return;
        }

        _settings = updated;
        var saved = _settings.Save();
        Log($"设置已更新：投屏关屏={FormatFlag(_settings.TurnScreenOffOnMirror)}，"
            + $"显示 scrcpy 命令行={FormatFlag(_settings.ShowScrcpyConsole)}"
            + $"，帧率上限={(_settings.VideoMaxFps > 0 ? _settings.VideoMaxFps + " 帧" : "不限")}"
            + $"，码率={_settings.VideoBitRateMbps} Mbps"
            + $"，键盘={_settings.KeyboardMode}"
            + (saved ? "。" : "（写入配置文件失败，仅本次运行有效）。"));
        SetStatus(saved ? "设置已保存。" : "设置已应用，但保存失败。");

        // 点击「设置」本身就会把前台切到本程序，对话框再抢一次焦点，保存后焦点就留在我们这边了，
        // 用户接着往手机打字会一个字都进不去。所以只要投屏会话还在，就无条件把焦点还给投屏窗口。
        if (_scrcpyProcess is not null)
        {
            var window = GetScrcpyWindow();
            Log(WindowActivator.RestoreAndActivate(window)
                ? "已把焦点还给投屏窗口，可以直接继续往手机打字。"
                : "想把焦点还给投屏窗口，但被系统前台锁拦下了，点一下投屏窗口即可继续打字。");
        }
    }

    private static string FormatFlag(bool value) => value ? "开" : "关";

    /// <summary>连接与断开连接合并成一个按钮：按选中设备当前是否已连接决定动作。</summary>
    private async void OnConnectToggleClick(object sender, RoutedEventArgs e)
    {
        if (_adb is null)
        {
            return;
        }

        if (DeviceList.SelectedItem is not DeviceEntry entry)
        {
            SetStatus("请先在列表中选择一台设备。");
            return;
        }

        if (entry.StateText == "已连接")
        {
            await DisconnectAsync(entry);
            return;
        }

        if (entry.IsPairingService)
        {
            SetStatus("这是配对端口，请点「无线调试配对」并输入手机上的 6 位配对码。");
            return;
        }

        await ConnectWirelessAsync(entry.Endpoint, entry);
    }

    /// <summary>
    /// 连接一个无线地址；地址已在 adb 列表里且状态为待授权 / 离线时，先 disconnect 再 connect。
    /// 外部契约（platform-tools 37.0.1 实测）：对列表里已有的地址，connect 只回
    /// "already connected to X" 且退出码为 0，不会重新握手。
    /// </summary>
    private async Task ConnectWirelessAsync(string endpoint, DeviceEntry? entry)
    {
        var known = await ReadDeviceStateAsync(endpoint);
        var stale = known is AdbDeviceState.Unauthorized or AdbDeviceState.Offline;

        IReadOnlyList<string> targets = entry is { DisconnectTargets.Count: > 0 }
            ? entry.DisconnectTargets
            : new[] { endpoint };

        var result = await RunCommandAsync(
            stale
                ? string.Join(" 然后 ", targets.Select(target => $"disconnect {target}")) + $" 然后 connect {endpoint}"
                : $"connect {endpoint}",
            async () =>
            {
                var last = new AdbResult(0, string.Empty, string.Empty, string.Empty);
                if (stale)
                {
                    foreach (var target in targets)
                    {
                        last = await _adb!.DisconnectAsync(target);
                        Log($"  disconnect {target} → {last.Summary}");
                    }
                }

                last = await _adb!.ConnectAsync(endpoint);
                return last;
            });

        await ReportConnectOutcomeAsync(entry, result);
    }

    private async Task DisconnectAsync(DeviceEntry entry)
    {
        if (_adb is null)
        {
            return;
        }

        if (!entry.CanDisconnect)
        {
            SetStatus(entry.Endpoint.Contains(':')
                ? "该设备当前没有以无线方式连接，无需断开。"
                : "USB 设备请直接拔线，adb disconnect 只对无线连接有效。");
            return;
        }

        var targets = entry.DisconnectTargets;
        await RunCommandAsync(
            string.Join(" 然后 ", targets.Select(target => $"disconnect {target}")),
            async () =>
            {
                var last = new AdbResult(0, string.Empty, string.Empty, string.Empty);
                foreach (var target in targets)
                {
                    last = await _adb.DisconnectAsync(target);
                    Log($"  disconnect {target} → {last.Summary}");
                }

                return last;
            });
    }

    /// <summary>SplitButton 的 Click 用 TypedEventHandler，签名与普通按钮不同。</summary>
    private async void OnPairClick(SplitButton sender, SplitButtonClickEventArgs args)
    {
        if (_adb is null)
        {
            return;
        }

        var preset = string.Empty;
        if (DeviceList.SelectedItem is DeviceEntry { IsPairingService: true } selected)
        {
            preset = selected.Endpoint;
        }
        else
        {
            var mdns = await _adb.ListMdnsServicesAsync();
            preset = AdbService.ParseMdnsServices(mdns.StandardOutput)
                .FirstOrDefault(service => service.Kind == MdnsServiceKind.Pairing)?.Endpoint ?? string.Empty;
        }

        var input = await InputDialog.ShowAsync(
            RootGrid.XamlRoot,
            "无线调试配对",
            "手机：设置 → 开发者选项 → 无线调试 → 使用配对码配对设备。把屏幕上的 IP:端口 和 6 位配对码填到这里。",
            "开始配对",
            ("配对地址 (IP:端口)", preset),
            ("配对码", string.Empty));

        if (input is null)
        {
            return;
        }

        if (!AdbService.TryNormalizeEndpoint(input[0], out var endpoint, out var error))
        {
            SetStatus(error);
            return;
        }

        var code = input[1].Trim();
        if (code.Length == 0 || !code.All(char.IsDigit))
        {
            SetStatus("配对码应为手机屏幕上显示的 6 位数字。");
            return;
        }

        Log($"$ adb pair {endpoint} ******");
        SetBusy(true, "正在配对…");

        AdbResult pairResult;
        try
        {
            pairResult = await _adb.PairAsync(endpoint, code);
            Log($"  {pairResult.Summary}");
        }
        catch (Exception ex)
        {
            Log($"配对异常：{ex.Message}");
            SetBusy(false, "配对失败。");
            return;
        }

        SetBusy(false, "配对结束");

        var succeeded = pairResult.StandardOutput.Contains("Successfully paired", StringComparison.OrdinalIgnoreCase);
        if (!succeeded)
        {
            SetStatus($"配对未成功：{pairResult.Summary}");
            return;
        }

        // 配对成功后，用同一 IP 的 connect 端口（与配对端口不同）建立连接。
        var host = endpoint[..endpoint.LastIndexOf(':')];
        await Task.Delay(500);
        var services = AdbService.ParseMdnsServices((await _adb.ListMdnsServicesAsync()).StandardOutput);
        var connectEndpoint = services
            .Where(service => service.Kind == MdnsServiceKind.Connect)
            .Select(service => service.Endpoint)
            .FirstOrDefault(candidate => candidate.StartsWith(host + ":", StringComparison.OrdinalIgnoreCase));

        if (connectEndpoint is null)
        {
            SetStatus("配对成功，但暂未发现连接端口，稍等几秒后点「扫描局域网」，再点「连接」。");
            await RefreshAsync(manual: false);
            return;
        }

        await ConnectWirelessAsync(connectEndpoint, null);
    }

    private async void OnManualClick(object sender, RoutedEventArgs e)
    {
        var input = await InputDialog.ShowAsync(
            RootGrid.XamlRoot,
            "手动添加设备",
            "输入手机无线调试页面上显示的 IP:端口（连接端口，不是配对端口）。",
            "添加",
            ("IP:端口", string.Empty));

        if (input is null)
        {
            return;
        }

        if (!AdbService.TryNormalizeEndpoint(input[0], out var endpoint, out var error))
        {
            SetStatus(error);
            return;
        }

        _manualEndpoints.Add(endpoint);
        SetStatus($"已加入 {endpoint}，正在连接…");
        if (_adb is not null)
        {
            await ConnectWirelessAsync(endpoint, null);
        }
    }

    private async void OnRestartAdbClick(object sender, RoutedEventArgs e)
    {
        if (_adb is null)
        {
            return;
        }

        SetBusy(true, "正在重启 adb…");
        var kill = await _adb.KillServerAsync();
        Log($"$ adb kill-server → {kill.Summary}");
        var start = await _adb.StartServerAsync();
        Log($"$ adb start-server → {start.Summary}");
        SetBusy(false, "adb 已重启。");

        _signature = null;
        await RefreshAsync(manual: false);
    }

    private void OnShellClick(object sender, RoutedEventArgs e)
    {
        if (_adb is null)
        {
            return;
        }

        if (DeviceList.SelectedItem is not DeviceEntry { StateText: "已连接" } entry)
        {
            SetStatus("请先连接并选择一台状态为「已连接」的设备。");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/k \"\"{_adb.AdbPath}\" -s {entry.Endpoint} shell\"",
                UseShellExecute = true,
            });
            Log($"已打开终端：adb -s {entry.Endpoint} shell");
        }
        catch (Exception ex)
        {
            SetStatus($"打开终端失败：{ex.Message}");
        }
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (DeviceList.SelectedItem is not DeviceEntry entry)
        {
            SetStatus("请先在列表中选择一台设备。");
            return;
        }

        try
        {
            var package = new DataPackage();
            package.SetText(entry.Endpoint);
            Clipboard.SetContent(package);
            SetStatus($"已复制：{entry.Endpoint}");
        }
        catch (Exception ex)
        {
            SetStatus($"复制失败：{ex.Message}");
        }
    }

    private void OnClearLogClick(object sender, RoutedEventArgs e) => LogBox.Text = string.Empty;

    /// <summary>投屏与关闭投屏合并成一个按钮：有会话时点它就是结束会话。</summary>
    private async void OnMirrorToggleClick(object sender, RoutedEventArgs e)
    {
        if (_scrcpyProcess is not null)
        {
            StopMirror("已关闭投屏。");
            return;
        }

        await StartMirrorAsync();
    }

    private async Task StartMirrorAsync()
    {
        if (_adb is null)
        {
            return;
        }

        if (DeviceList.SelectedItem is not DeviceEntry entry || entry.StateText != "已连接")
        {
            SetStatus("请先连接并选择一台状态为「已连接」的设备。");
            return;
        }

        var scrcpyPath = ScrcpyRunner.Locate();
        if (scrcpyPath is null)
        {
            Log("未找到 scrcpy.exe：把 scrcpy 发行包的文件放进程序目录下的 scrcpy\\ 文件夹即可。");
            SetStatus("未找到 scrcpy，投屏不可用。");
            return;
        }

        StopMirror(null);
        SetBusy(true, "正在启动投屏窗口…");

        try
        {
            var showConsole = _settings.ShowScrcpyConsole;
            _scrcpyProcess = ScrcpyRunner.Start(
                scrcpyPath,
                _adb.AdbPath,
                entry.Endpoint,
                $"投屏 {entry.DisplayName}",
                _settings.TurnScreenOffOnMirror,
                showConsole,
                _settings.VideoMaxFps,
                _settings.VideoBitRateMbps,
                _settings.VideoMaxSize,
                _settings.KeyboardMode,
                line => DispatcherQueue.TryEnqueue(() => OnScrcpyOutput(line)));
            _scrcpyProcess.Exited += OnScrcpyExited;
            Log($"$ scrcpy -s {entry.Endpoint}{(_settings.TurnScreenOffOnMirror ? " -S" : string.Empty)}"
                + $"{(_settings.VideoMaxFps > 0 ? $" --max-fps={_settings.VideoMaxFps}" : string.Empty)}"
                + $"{(_settings.VideoBitRateMbps > 0 ? $" --video-bit-rate={_settings.VideoBitRateMbps}M" : string.Empty)}"
                + $"{(_settings.VideoMaxSize > 0 ? $" --max-size={_settings.VideoMaxSize}" : string.Empty)}"
                + $"{(_settings.KeyboardMode != "auto" ? $" --keyboard={_settings.KeyboardMode}" : string.Empty)}");
            FpsText.Visibility = Visibility.Visible;
            FpsText.Text = showConsole ? "—" : "连接中";
            if (showConsole)
            {
                    Log("已开启 scrcpy 命令行窗口；界面上的实时帧率不可用。");
            }
            Log(showConsole
                ? $"已启动独立投屏窗口（{(_settings.TurnScreenOffOnMirror ? "手机屏幕已关闭" : "手机屏幕保持亮起")}），scrcpy 命令行窗口保持显示。"
                : $"已启动独立投屏窗口（{(_settings.TurnScreenOffOnMirror ? "手机屏幕已关闭" : "手机屏幕保持亮起")}），不显示 scrcpy 命令行窗口，其输出转入本日志。");
            SetStatus("投屏窗口已启动。");
            RegisterMirrorHotkeys();
        }
        catch (Exception ex)
        {
            Log($"投屏启动失败：{ex.Message}");
            SetStatus($"投屏启动失败：{ex.Message}");
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    /// <summary>scrcpy 的输出分流：帧率行更新界面上的实时帧率，其余照旧写进执行日志。</summary>
    private void OnScrcpyOutput(string line)
    {
        var match = FpsPattern.Match(line);
        if (match.Success)
        {
            var skipped = SkippedFramesPattern.Match(line);
            FpsText.Text = skipped.Success
                ? $"{match.Groups["fps"].Value} fps（丢 {skipped.Groups["count"].Value} 帧）"
                : $"{match.Groups["fps"].Value} fps";
            return;
        }

        Log($"[scrcpy] {line}");
    }

    private void OnScrcpyExited(object? sender, EventArgs e)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => OnScrcpyExited(sender, e));
            return;
        }

        // 进程退出事件经 DispatcherQueue 转发后才执行，期间可能已经启动了新会话，
        // 因此必须核对事件来源是不是当前会话，否则旧会话的退出会清掉新会话的状态。
        if (!ReferenceEquals(sender, _scrcpyProcess))
        {
            return;
        }

        var exitCode = sender is Process { HasExited: true } exited ? exited.ExitCode : -1;
        Log($"投屏已结束（scrcpy 退出码 {exitCode}）。");
        ClearMirrorState();
        SetStatus($"投屏已结束（退出码 {exitCode}）。");
    }

    private void StopMirror(string? status)
    {
        var process = _scrcpyProcess;
        ClearMirrorState();

        if (process is not null)
        {
            process.Exited -= OnScrcpyExited;

            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                Log($"结束投屏进程失败：{ex.Message}");
            }

            process.Dispose();
        }

        if (status is not null)
        {
            SetStatus(status);
        }
    }

    private void ClearMirrorState()
    {
        _scrcpyProcess = null;
        _hotkeyWindow?.Unregister(ToggleHotkeyId);
        FpsText.Visibility = Visibility.Collapsed;
        FpsText.Text = string.Empty;
        UpdateSelectionState();
    }

    /// <summary>投屏期间才注册全局热键，会话结束即注销，避免平时占着组合键。</summary>
    private void RegisterMirrorHotkeys()
    {
        try
        {
            _hotkeyWindow ??= new GlobalHotkeyWindow(OnGlobalHotkey);

            var toggle = HotkeySpec.TryParse(_settings.ToggleShortcut, out var toggleSpec)
                ? toggleSpec
                : HotkeySpec.None;

            if (_hotkeyWindow.TryRegister(ToggleHotkeyId, toggle, out var error))
            {
                if (!toggle.IsEmpty)
                {
                    Log($"热键已注册：{toggle} → 最小化投屏窗口 / 再次按下恢复并置前。");
                }
            }
            else
            {
                Log($"热键未生效：{error}。");
            }
        }
        catch (Exception ex)
        {
            Log($"全局热键不可用：{ex.Message}");
        }
    }

    private void OnGlobalHotkey(int id)
    {
        if (id != ToggleHotkeyId)
        {
            return;
        }

        var window = GetScrcpyWindow();
        if (window == IntPtr.Zero)
        {
            return;
        }

        if (WindowActivator.IsMinimized(window))
        {
            var activated = WindowActivator.RestoreAndActivate(window);
            Log(activated
                ? "热键：已恢复投屏窗口并置于前台。"
                : "热键：已恢复投屏窗口，但系统前台锁不允许它抢到前台（窗口已弹出在任务栏）。");
        }
        else
        {
            WindowActivator.Minimize(window);
            Log("热键：已最小化投屏窗口。");
        }
    }

    private IntPtr GetScrcpyWindow()
    {
        var process = _scrcpyProcess;
        if (process is null)
        {
            return IntPtr.Zero;
        }

        process.Refresh();
        return process.MainWindowHandle;
    }

    private async void OnDeviceDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (DeviceList.SelectedItem is DeviceEntry { CanConnect: true } entry)
        {
            await ConnectWirelessAsync(entry.Endpoint, entry);
        }
    }

    /// <summary>读 endpoint 在 adb devices 里的当前状态；设备不在列表或查询失败时返回 null。</summary>
    private async Task<AdbDeviceState?> ReadDeviceStateAsync(string endpoint)
    {
        if (_adb is null)
        {
            return null;
        }

        try
        {
            var devices = await _adb.ListDevicesAsync();
            return AdbService.ParseDevices(devices.StandardOutput)
                .FirstOrDefault(device => string.Equals(device.Serial, endpoint, StringComparison.OrdinalIgnoreCase))
                ?.State;
        }
        catch (Exception ex)
        {
            Log($"读取设备状态失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 连接结果只报 adb devices 里的真实状态与端口探活结果，不从 connect 的 stdout 文本推断含义。
    /// 外部契约（platform-tools 37.0.1 实测）：设备已在 server 列表里时 connect 回 "already connected to X"
    /// 且退出码为 0，此时设备可能是 device / unauthorized / offline 中的任意一种。
    /// </summary>
    private async Task ReportConnectOutcomeAsync(DeviceEntry? entry, AdbResult? result)
    {
        if (result is null)
        {
            return;
        }

        var endpoint = entry?.Endpoint ?? result.Arguments.Replace("connect ", string.Empty).Trim();

        switch (await ReadDeviceStateAsync(endpoint))
        {
            case AdbDeviceState.Connected:
                SetStatus($"已连接：{endpoint}");
                return;

            case AdbDeviceState.Unauthorized:
                Log($"设备状态：{endpoint} 待授权。");
                SetStatus($"连接结果：{endpoint} 待授权。");
                return;

            case AdbDeviceState.Offline:
                Log($"设备状态：{endpoint} 离线。");
                SetStatus($"连接结果：{endpoint} 离线。");
                return;

            case AdbDeviceState.Unknown:
                Log($"设备状态：{endpoint} 未知。");
                SetStatus($"连接结果：{endpoint} 状态未知。");
                return;
        }

        // 设备不在 adb 列表里：状态栏保留 RunCommandAsync 写下的 adb 原文，这里只补观测到的端口探活结果。
        Log($"设备状态：{endpoint} 不在 adb 列表中。");
        if (await ProbeTcpAsync(endpoint) is { } reachable)
        {
            Log($"端口探活：{endpoint} {(reachable ? "可建立 TCP 连接" : "无响应")}。");
        }
    }

    /// <summary>
    /// 探测 endpoint 的 TCP 端口能否建连：可建连 true，连不上 false。
    /// endpoint 不是 IP:端口 形态（USB 序号、mDNS 服务名）时返回 null，调用方据此跳过网络诊断。
    /// </summary>
    private static async Task<bool?> ProbeTcpAsync(string endpoint)
    {
        var separator = endpoint.LastIndexOf(':');
        if (separator <= 0 || separator == endpoint.Length - 1
            || !int.TryParse(endpoint[(separator + 1)..], out var port))
        {
            return null;
        }

        using var client = new TcpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
        try
        {
            await client.ConnectAsync(endpoint[..separator], port, timeout.Token);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void OnDeviceSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectionState();

    // ---------- 手机文件面板 ----------

    private void OnToggleFilePanelClick(object sender, RoutedEventArgs e)
        => ShowFilePanel(FilePanel.Visibility != Visibility.Visible);

    private void OnHideFilePanelClick(object sender, RoutedEventArgs e) => ShowFilePanel(false);

    private void ShowFilePanel(bool show)
    {
        FilePanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        if (!show)
        {
            FilePanelColumn.Width = new GridLength(0);
        }
        else
        {
            // 两列都用星值，窗口变窄时按比例一起收，不会把左列挤到操作栏溢出。
            LeftColumn.Width = new GridLength(2, GridUnitType.Star);
            FilePanelColumn.Width = new GridLength(1, GridUnitType.Star);
        }

        if (show)
        {
            _remoteSerial = CurrentRemoteSerial();
            _ = RefreshRemoteAsync();
        }

        Log(show ? "已展开手机文件面板。" : "已收起手机文件面板。");
        UpdateWindowMinimumSize();
    }

    private string? CurrentRemoteSerial()
        => DeviceList.SelectedItem is DeviceEntry { StateText: "已连接" } entry ? entry.Endpoint : null;

    /// <summary>AppWindow 的尺寸单位是物理像素，这里统一按窗口 DPI 折算成 DIP。</summary>
    private double WindowScale
    {
        get
        {
            var scale = WindowActivator.GetScale(WinRT.Interop.WindowNative.GetWindowHandle(this));
            return scale <= 0 ? 1.0 : scale;
        }
    }

    /// <summary>
    /// 把固定最小尺寸交给系统限制（拖动边框时被挡住），单位与 AppWindow 一致是物理像素。
    /// 最小尺寸刻意用固定值而不是按当前列比例反推——折行状态随宽度变化，动态下限会自我抬高（实测窗口被顶到 1305 DIP）。
    /// </summary>
    private void UpdateWindowMinimumSize()
    {
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            var scale = WindowScale;
            presenter.PreferredMinimumWidth = (int)Math.Round(MinimumWindowWidthDip * scale);
            presenter.PreferredMinimumHeight = (int)Math.Round(MinimumWindowHeightDip * scale);
        }
    }

    private void ResizeWindowToDip(double widthDip, double heightDip)
    {
        var scale = WindowScale;
        AppWindow.Resize(new SizeInt32(
            (int)Math.Round(widthDip * scale),
            (int)Math.Round(heightDip * scale)));
    }

    /// <summary>
    /// 非打包的 WinUI 窗口自身不带图标（实测窗口大小图标句柄都为 0），这里从 exe 资源里取出来显式设上，
    /// 免得任务栏与标题栏的行为依赖系统的回落规则。
    /// </summary>
    private void ApplyWindowIcon()
    {
        var window = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var exePath = Environment.ProcessPath;
        if (window == IntPtr.Zero || string.IsNullOrEmpty(exePath))
        {
            return;
        }

        var large = new IntPtr[1];
        var small = new IntPtr[1];
        if (ExtractIconEx(exePath, 0, large, small, 1) == 0)
        {
            return;
        }

        if (large[0] != IntPtr.Zero)
        {
            SendMessage(window, WmSetIcon, IconBig, large[0]);
        }

        if (small[0] != IntPtr.Zero)
        {
            SendMessage(window, WmSetIcon, IconSmall, small[0]);
        }
    }

    private const uint WmSetIcon = 0x0080;
    private static readonly IntPtr IconBig = new(1);
    private static readonly IntPtr IconSmall = IntPtr.Zero;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, uint count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    private async Task RefreshRemoteAsync()
    {
        if (_adb is null || _remoteBusy)
        {
            return;
        }

        if (CurrentRemoteSerial() is not { } serial)
        {
            _remoteEntries.Clear();
            RemoteHint.Text = "请先在左侧连接并选中一台设备。";
            SetRemoteControlsEnabled(false);
            return;
        }

        var result = await _adb.ListRemoteDirectoryAsync(serial, _remotePath);
        Log($"$ adb shell ls -la {AdbService.ShellQuote(_remotePath)}");

        if (result.ExitCode != 0)
        {
            _remoteEntries.Clear();
            Log($"  {result.Summary}");
            RemoteHint.Text = $"无法读取 {_remotePath}：{result.Summary}";
            SetRemoteControlsEnabled(true);
            return;
        }

        var entries = AdbService.ParseLsLong(result.StandardOutput, _remotePath)
            .OrderBy(entry => entry.IsDirectory ? 0 : 1)
            .ThenBy(entry => entry.Name, StringComparer.CurrentCulture)
            .ToList();

        _remoteEntries.Clear();
        foreach (var entry in entries)
        {
            _remoteEntries.Add(entry);
        }

        RemotePathBox.Text = _remotePath;
        RemoteHint.Text = $"{_remotePath}，{entries.Count} 项。右键条目可下载、重命名、删除；把文件从资源管理器拖进本面板即可上传。";
        SetRemoteControlsEnabled(true);
    }

    private async Task NavigateRemoteAsync(string path)
    {
        _remotePath = NormalizeRemotePath(path);
        await RefreshRemoteAsync();
    }

    private static string NormalizeRemotePath(string? path)
    {
        var value = (path ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return "/sdcard";
        }

        if (!value.StartsWith('/'))
        {
            value = "/" + value;
        }

        while (value.Length > 1 && value.EndsWith('/'))
        {
            value = value[..^1];
        }

        return value;
    }

    private static string ParentOf(string path)
    {
        if (path.Length <= 1)
        {
            return "/";
        }

        var index = path.LastIndexOf('/');
        return index <= 0 ? "/" : path[..index];
    }

    private static string JoinRemote(string parent, string name)
        => parent == "/" ? "/" + name : parent + "/" + name;

    private void OnRemoteRefreshClick(object sender, RoutedEventArgs e) => _ = RefreshRemoteAsync();

    private void OnRemoteUpClick(object sender, RoutedEventArgs e) => _ = NavigateRemoteAsync(ParentOf(_remotePath));

    private void OnRemotePathKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        _ = NavigateRemoteAsync(RemotePathBox.Text);
    }

    private async void OnRemoteDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (RemoteList.SelectedItem is not RemoteEntry entry)
        {
            return;
        }

        if (entry.IsDirectory)
        {
            await NavigateRemoteAsync(entry.FullPath);
            return;
        }

        await DownloadRemoteAsync(entry);
    }

    private void OnFilePanelDragOver(object sender, DragEventArgs e)
    {
        if (_remoteBusy || CurrentRemoteSerial() is null || !e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = $"上传到 {_remotePath}";
        e.DragUIOverride.IsCaptionVisible = true;
    }

    private async void OnFilePanelDrop(object sender, DragEventArgs e)
    {
        if (_adb is null || _remoteBusy || CurrentRemoteSerial() is not { } serial)
        {
            return;
        }

        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var items = await e.DataView.GetStorageItemsAsync();
        var paths = items.OfType<StorageFile>().Select(file => file.Path)
            .Concat(items.OfType<StorageFolder>().Select(folder => folder.Path))
            .Where(path => !string.IsNullOrEmpty(path))
            .ToList();

        foreach (var path in paths)
        {
            await PushPathAsync(serial, path);
        }
    }

    /// <summary>把本地文件或目录推到面板当前目录；同名直接覆盖，目录递归上传。</summary>
    private async Task PushPathAsync(string serial, string localPath)
    {
        if (_adb is null)
        {
            return;
        }

        var name = Path.GetFileName(localPath.TrimEnd('\\', '/'));
        SetRemoteBusy(true, $"正在上传 {name} …");
        Log($"$ adb push {AdbService.WindowsQuote(localPath)} {AdbService.WindowsQuote(_remotePath)}");

        try
        {
            var result = await _adb.PushFileAsync(serial, localPath, _remotePath);
            Log($"  {result.Summary}");
            RemoteHint.Text = result.ExitCode == 0
                ? $"已上传 {name}。"
                : $"上传 {name} 失败：{result.Summary}";
        }
        finally
        {
            SetRemoteBusy(false, null);
        }

        await RefreshRemoteAsync();
    }

    private async Task DownloadRemoteAsync(RemoteEntry entry)
    {
        if (_adb is null || _remoteBusy)
        {
            return;
        }

        if (CurrentRemoteSerial() is not { } serial)
        {
            RemoteHint.Text = "请先在左侧连接并选中一台设备。";
            return;
        }

        if (entry.IsDirectory)
        {
            RemoteHint.Text = "目录暂不支持整体下载，请进入目录后选择文件。";
            return;
        }

        var targetDirectory = _settings.ResolveDownloadDirectory();
        Directory.CreateDirectory(targetDirectory);
        var localPath = Path.Combine(targetDirectory, entry.Name);

        SetRemoteBusy(true, $"正在下载 {entry.Name} …");
        Log($"$ adb pull {AdbService.WindowsQuote(entry.FullPath)} {AdbService.WindowsQuote(localPath)}");

        _stopDownloadProgress = false;
        var progressTask = TrackDownloadProgressAsync(localPath, entry.Size, entry.Name);

        try
        {
            var result = await _adb.PullFileAsync(serial, entry.FullPath, localPath);
            Log($"  {result.Summary}");
            RemoteHint.Text = result.ExitCode == 0
                ? $"已下载到 {localPath}"
                : $"下载失败：{result.Summary}";
        }
        finally
        {
            _stopDownloadProgress = true;
            await progressTask;
            SetRemoteBusy(false, null);
        }
    }

    /// <summary>adb pull 的输出没有百分比，改为轮询落地文件大小换算进度；轮询在后台线程，结果经队列回 UI。</summary>
    private async Task TrackDownloadProgressAsync(string localPath, long totalBytes, string name)
    {
        while (!_stopDownloadProgress)
        {
            try
            {
                var info = new FileInfo(localPath);
                var received = info.Exists ? info.Length : 0;
                var percent = totalBytes > 0 ? Math.Min(100.0, received * 100.0 / totalBytes) : 0;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!_remoteBusy)
                    {
                        return;
                    }

                    TransferBar.IsIndeterminate = false;
                    TransferBar.Value = percent;
                    RemoteHint.Text = $"正在下载 {name} … {percent:0}%（{received / 1048576.0:0.#} / {totalBytes / 1048576.0:0.#} MB）";
                });
            }
            catch (IOException)
            {
                // 文件正在写入或被占用，下一拍再读。
            }

            await Task.Delay(300);
        }
    }

    private async void OnRemoteDownloadClick(object sender, RoutedEventArgs e)
    {
        if (RemoteList.SelectedItem is RemoteEntry entry)
        {
            await DownloadRemoteAsync(entry);
            return;
        }

        RemoteHint.Text = "请先选中一个条目。";
    }

    private async void OnRemoteRenameClick(object sender, RoutedEventArgs e)
    {
        if (_adb is null || _remoteBusy || CurrentRemoteSerial() is not { } serial)
        {
            RemoteHint.Text = "请先在左侧连接并选中一台设备。";
            return;
        }

        if (RemoteList.SelectedItem is not RemoteEntry entry)
        {
            RemoteHint.Text = "请先选中一个条目。";
            return;
        }

        var input = await InputDialog.ShowAsync(
            RootGrid.XamlRoot, "重命名", $"把 {entry.FullPath} 改名为：", "重命名", ("新名称", entry.Name));
        if (input is null)
        {
            return;
        }

        var newName = input[0].Trim();
        if (newName.Length == 0 || newName.Contains('/'))
        {
            RemoteHint.Text = "名称不能为空，也不能包含斜杠。";
            return;
        }

        var newPath = JoinRemote(ParentOf(entry.FullPath), newName);
        Log($"$ adb shell mv {AdbService.ShellQuote(entry.FullPath)} {AdbService.ShellQuote(newPath)}");
        var result = await _adb.RenameRemoteAsync(serial, entry.FullPath, newPath);
        Log($"  {result.Summary}");
        RemoteHint.Text = result.ExitCode == 0 ? $"已重命名为 {newName}。" : $"重命名失败：{result.Summary}";
        await RefreshRemoteAsync();
    }

    private async void OnRemoteDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_adb is null || _remoteBusy || CurrentRemoteSerial() is not { } serial)
        {
            RemoteHint.Text = "请先在左侧连接并选中一台设备。";
            return;
        }

        if (RemoteList.SelectedItem is not RemoteEntry entry)
        {
            RemoteHint.Text = "请先选中一个条目。";
            return;
        }

        var confirm = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "删除确认",
            Content = $"确定删除 {entry.FullPath} 吗？该操作不可恢复。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        Log($"$ adb shell rm -rf {AdbService.ShellQuote(entry.FullPath)}");
        var result = await _adb.DeleteRemoteAsync(serial, entry.FullPath);
        Log($"  {result.Summary}");
        RemoteHint.Text = result.ExitCode == 0 ? $"已删除 {entry.Name}。" : $"删除失败：{result.Summary}";
        await RefreshRemoteAsync();
    }

    private async void OnRemoteNewFolderClick(object sender, RoutedEventArgs e)
    {
        if (_adb is null || _remoteBusy || CurrentRemoteSerial() is not { } serial)
        {
            RemoteHint.Text = "请先在左侧连接并选中一台设备。";
            return;
        }

        var input = await InputDialog.ShowAsync(
            RootGrid.XamlRoot, "新建文件夹", $"在 {_remotePath} 下新建文件夹：", "创建", ("文件夹名", "新建文件夹"));
        if (input is null)
        {
            return;
        }

        var name = input[0].Trim();
        if (name.Length == 0 || name.Contains('/'))
        {
            RemoteHint.Text = "名称不能为空，也不能包含斜杠。";
            return;
        }

        var path = JoinRemote(_remotePath, name);
        Log($"$ adb shell mkdir -p {AdbService.ShellQuote(path)}");
        var result = await _adb.CreateRemoteDirectoryAsync(serial, path);
        Log($"  {result.Summary}");
        RemoteHint.Text = result.ExitCode == 0 ? $"已创建 {name}。" : $"创建失败：{result.Summary}";
        await RefreshRemoteAsync();
    }

    private async void OnRemoteUploadClick(object sender, RoutedEventArgs e)
    {
        if (_adb is null || _remoteBusy || CurrentRemoteSerial() is not { } serial)
        {
            RemoteHint.Text = "请先在左侧连接并选中一台设备。";
            return;
        }

        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

        var files = await picker.PickMultipleFilesAsync();
        foreach (var file in files)
        {
            await PushPathAsync(serial, file.Path);
        }
    }

    private void SetRemoteBusy(bool busy, string? status)
    {
        _remoteBusy = busy;
        TransferBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy)
        {
            TransferBar.IsIndeterminate = true;
            TransferBar.Value = 0;
        }

        if (status is not null)
        {
            RemoteHint.Text = status;
        }

        SetRemoteControlsEnabled(!busy && CurrentRemoteSerial() is not null);
    }

    // ---------- 分隔条拖动 ----------

    private bool _columnDragging;
    private bool _rowDragging;
    private double _dragOrigin;
    private double _dragLeftStars;
    private double _dragPanelStars;
    private double _dragDeviceStars;
    private double _dragLogStars;

    private void OnColumnSplitterPressed(object sender, PointerRoutedEventArgs e)
    {
        if (FilePanel.Visibility != Visibility.Visible)
        {
            return;
        }

        _columnDragging = ColumnSplitter.CapturePointer(e.Pointer);
        _dragOrigin = e.GetCurrentPoint(RootGrid).Position.X;
        _dragLeftStars = LeftColumn.Width.Value;
        _dragPanelStars = FilePanelColumn.Width.Value;
        e.Handled = true;
    }

    private void OnColumnSplitterMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_columnDragging)
        {
            return;
        }

        var space = LeftColumn.ActualWidth + FilePanelColumn.ActualWidth;
        var total = _dragLeftStars + _dragPanelStars;
        if (space <= 0 || total <= 0)
        {
            return;
        }

        // 星值是相对量，先按「两列当前实际占的 DIP」算出每 DIP 对应多少星值，拖动才跟手。
        var perDip = total / space;
        var delta = (e.GetCurrentPoint(RootGrid).Position.X - _dragOrigin) * perDip;
        var left = _dragLeftStars + delta;
        var panel = _dragPanelStars - delta;

        var minLeft = LeftColumnMinimumDip * perDip;
        var minPanel = FilePanelMinimumDip * perDip;
        if (left < minLeft)
        {
            panel -= minLeft - left;
            left = minLeft;
        }

        if (panel < minPanel)
        {
            left -= minPanel - panel;
            panel = minPanel;
        }

        if (left < minLeft || panel < minPanel)
        {
            return;
        }

        LeftColumn.Width = new GridLength(left, GridUnitType.Star);
        FilePanelColumn.Width = new GridLength(panel, GridUnitType.Star);
        e.Handled = true;
    }

    private void OnColumnSplitterReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_columnDragging)
        {
            return;
        }

        _columnDragging = false;
        ColumnSplitter.ReleasePointerCapture(e.Pointer);
        UpdateWindowMinimumSize();
        e.Handled = true;
    }

    private void OnRowSplitterPressed(object sender, PointerRoutedEventArgs e)
    {
        _rowDragging = RowSplitter.CapturePointer(e.Pointer);
        _dragOrigin = e.GetCurrentPoint(RootGrid).Position.Y;
        _dragDeviceStars = DeviceRow.Height.Value;
        _dragLogStars = LogRow.Height.Value;
        e.Handled = true;
    }

    private void OnRowSplitterMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_rowDragging)
        {
            return;
        }

        var space = DeviceRow.ActualHeight + LogRow.ActualHeight;
        var total = _dragDeviceStars + _dragLogStars;
        if (space <= 0 || total <= 0)
        {
            return;
        }

        var perDip = total / space;
        var delta = (e.GetCurrentPoint(RootGrid).Position.Y - _dragOrigin) * perDip;
        var device = _dragDeviceStars + delta;
        var log = _dragLogStars - delta;

        var minDevice = DeviceRowMinimumDip * perDip;
        var minLog = LogRowMinimumDip * perDip;
        if (device < minDevice)
        {
            log -= minDevice - device;
            device = minDevice;
        }

        if (log < minLog)
        {
            device -= minLog - log;
            log = minLog;
        }

        if (device < minDevice || log < minLog)
        {
            return;
        }

        DeviceRow.Height = new GridLength(device, GridUnitType.Star);
        LogRow.Height = new GridLength(log, GridUnitType.Star);
        e.Handled = true;
    }

    private void OnRowSplitterReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_rowDragging)
        {
            return;
        }

        _rowDragging = false;
        RowSplitter.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void SetRemoteControlsEnabled(bool enabled)
    {
        RemoteList.IsEnabled = enabled;
        RemotePathBox.IsEnabled = enabled;
        RemoteUpButton.IsEnabled = enabled;
        RemoteRefreshButton.IsEnabled = enabled;
        RemoteUploadButton.IsEnabled = enabled;
        RemoteNewFolderButton.IsEnabled = enabled;
    }

    /// <summary>面板可见且选中设备发生变化时才重新列目录，避免每次刷新按钮状态都触发 adb 调用。</summary>
    private void SyncRemotePanel()
    {
        var serial = CurrentRemoteSerial();
        SetRemoteControlsEnabled(!_remoteBusy && serial is not null && FilePanel.Visibility == Visibility.Visible);

        if (FilePanel.Visibility != Visibility.Visible || serial == _remoteSerial)
        {
            return;
        }

        _remoteSerial = serial;
        _ = RefreshRemoteAsync();
    }

    private void UpdateSelectionState()
    {
        var selected = DeviceList.SelectedItem as DeviceEntry;
        var connectedSelection = selected is { StateText: "已连接" };
        var mirroring = _scrcpyProcess is not null;

        SelectionText.Text = selected is null
            ? "未选择设备"
            : $"已选择：{selected.DisplayName}（{selected.Endpoint}）· {selected.StateText}";
        ToolTipService.SetToolTip(SelectionText, SelectionText.Text);

        // 已连接设备对应「断开连接」，未连接设备对应「连接」；两者都不满足时置灰。
        ConnectButton.Content = connectedSelection ? "断开连接" : "连接";
        ConnectButton.IsEnabled = selected is not null
            && (connectedSelection || selected.CanConnect)
            && !_busy;

        ShellButton.IsEnabled = connectedSelection && !_busy;

        // 会话存在时按钮始终是「关闭投屏」，并且不依赖当前是否选中设备。
        MirrorButton.Content = mirroring ? "关闭投屏" : "投屏";
        MirrorButton.IsEnabled = mirroring ? !_busy : connectedSelection && !_busy;

        SyncRemotePanel();
    }

    private async Task<AdbResult?> RunCommandAsync(string displayArguments, Func<Task<AdbResult>> action)
    {
        if (_adb is null)
        {
            return null;
        }

        SetBusy(true, $"正在执行：adb {displayArguments}");
        Log($"$ adb {displayArguments}");

        AdbResult? result = null;
        try
        {
            result = await action();
            Log($"  {result.Summary}");
            SetStatus($"adb {displayArguments} → {result.Summary}");
        }
        catch (Exception ex)
        {
            Log($"  异常：{ex.Message}");
            SetStatus($"adb {displayArguments} 失败：{ex.Message}");
        }
        finally
        {
            SetBusy(false, null);
        }

        await RefreshAsync(manual: false);
        return result;
    }

    private void SetBusy(bool busy, string? status)
    {
        _busy = busy;
        BusyRing.IsActive = busy;

        ScanButton.IsEnabled = !busy;
        PairButton.IsEnabled = !busy;
        ManualMenuItem.IsEnabled = !busy;
        RestartAdbButton.IsEnabled = !busy;

        if (status is not null)
        {
            SetStatus(status);
        }

        UpdateSelectionState();
    }

    private void SetButtonsEnabled(bool enabled)
    {
        ScanButton.IsEnabled = enabled;
        PairButton.IsEnabled = enabled;
        ManualMenuItem.IsEnabled = enabled;
        RestartAdbButton.IsEnabled = enabled;
    }

    private void SetStatus(string text)
    {
        StatusText.Text = text;
        ToolTipService.SetToolTip(StatusText, text);
    }

    private void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        // 不变量：WinUI 的 TextBox 会把换行统一成 \r（实测 ValuePattern 中 LF 计数为 0），
        // 因此按 \r 与 \n 一起切分，否则行数截断失效、日志会无限增长。
        var lines = LogBox.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Append(line)
            .ToArray();

        if (lines.Length > MaxLogLines)
        {
            lines = lines[^MaxLogLines..];
        }

        LogBox.Text = string.Join(Environment.NewLine, lines);
        LogBox.SelectionStart = LogBox.Text.Length;
    }
}
