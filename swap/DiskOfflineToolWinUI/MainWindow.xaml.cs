using System.Collections.ObjectModel;
using DiskManager.Models;
using DiskManager.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Devices.Enumeration;
using Windows.Graphics;

namespace DiskOfflineToolWinUI;

/// <summary>
/// 磁盘脱机工具（WinUI 3 版）。
/// 界面用 Fluent 风格重做，业务逻辑仍走与 WPF 版完全相同的 DiskListService。
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>磁盘设备接口类 GUID，用于监听设备插拔。</summary>
    private const string DiskInterfaceClassGuid = "{53F56307-B6BF-11D0-94F2-00A0C91EFB8B}";

    private readonly ObservableCollection<DiskItem> _disks = [];
    private DeviceWatcher? _deviceWatcher;
    private DispatcherTimerLike? _refreshTimer;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();

        // 现代化外观：Mica 背景 + 合适的初始尺寸
        SystemBackdrop = new MicaBackdrop();
        if (Content is FrameworkElement root)
        {
            // 等布局完成再定尺寸：AppWindow.Resize 用的是物理像素，
            // 高 DPI 下需要乘上缩放比例，否则窗口会明显偏小。
            root.Loaded += (_, _) => ResizeToLogical(1020, 660);
        }

        DiskList.ItemsSource = _disks;
        UpdatePermissionState();

        StartDeviceWatcher();
        _ = RefreshAsync();
    }

    // ------------------------------------------------------------------
    // 硬件变化自动刷新（WinRT DeviceWatcher，比手写 WM_DEVICECHANGE 更省事）
    // ------------------------------------------------------------------

    private void StartDeviceWatcher()
    {
        try
        {
            _deviceWatcher = DeviceInformation.CreateWatcher(
                DiskInterfaceClassGuid,
                null,
                DeviceInformationKind.DeviceInterface);

            _deviceWatcher.Added += (_, _) => ScheduleRefresh(700, "检测到硬件变化，正在刷新…");
            _deviceWatcher.Removed += (_, _) => ScheduleRefresh(700, "检测到硬件变化，正在刷新…");
            _deviceWatcher.Start();
        }
        catch (Exception ex)
        {
            // 监听失败不影响主功能：操作后仍会主动刷新
            StatusText.Text = $"硬件变化监听未启用（{ex.Message}）";
        }
    }

    /// <summary>延迟刷新（重置上一次等待，多个事件只刷新一次）。</summary>
    private void ScheduleRefresh(int delayMs, string? statusText = null)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (statusText is not null && !_busy)
            {
                StatusText.Text = statusText;
            }

            _refreshTimer ??= new DispatcherTimerLike(TimeSpan.FromMilliseconds(delayMs), () =>
            {
                _refreshTimer?.Stop();

                // 正在执行操作就先让路，稍后再刷，避免把这次变化丢掉
                if (_busy)
                {
                    ScheduleRefresh(800);
                    return;
                }

                _ = RefreshAsync();
            });

            _refreshTimer.Restart(TimeSpan.FromMilliseconds(delayMs));
        });
    }

    // ------------------------------------------------------------------
    // 列表
    // ------------------------------------------------------------------

    /// <summary>后台读取磁盘列表，回到界面线程整体替换。</summary>
    private async Task RefreshAsync()
    {
        if (_busy)
        {
            return;
        }

        int? previousNumber = (DiskList.SelectedItem as DiskItem)?.Number;
        SetBusy(true);
        StatusText.Text = "正在读取磁盘列表…";

        try
        {
            (IReadOnlyList<DiskItem> disks, IReadOnlyList<string> warnings) = await Task.Run(() =>
            {
                IReadOnlyList<DiskItem> captured = DiskListService.Capture(out IReadOnlyList<string> capturedWarnings);
                return (captured, capturedWarnings);
            });

            _disks.Clear();
            foreach (DiskItem disk in disks)
            {
                _disks.Add(disk);
            }

            if (previousNumber.HasValue)
            {
                DiskItem? match = _disks.FirstOrDefault(d => d.Number == previousNumber.Value);
                if (match is not null)
                {
                    DiskList.SelectedItem = match;
                }
            }

            string warning = warnings.Count > 0 ? $"　⚠ {warnings[0]}" : string.Empty;
            StatusText.Text = $"共 {_disks.Count} 块磁盘{warning}";

            ResetHorizontalScroll();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"读取磁盘列表失败：{ex.Message}";
        }
        finally
        {
            SetBusy(false);
            UpdateCommandState();
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateCommandState();

    /// <summary>
    /// 把列表横向位置归零。
    /// 窗口尺寸变化后 ScrollViewer 会保留原来的偏移，窄窗口下就会"一上来停在中间"，
    /// 看不到"磁盘 / 设备名称"两列；这里在布局前后各复位一次。
    /// </summary>
    private void ResetHorizontalScroll()
    {
        void Reset() => FindScrollViewer(DiskList)?.ChangeView(0, null, null, true);

        DispatcherQueue.TryEnqueue(Reset);

        Microsoft.UI.Dispatching.DispatcherQueueTimer timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(400);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => Reset();
        timer.Start();
    }

    /// <summary>在可视树里找 ListView 内部的 ScrollViewer。</summary>
    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer)
        {
            return viewer;
        }

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            ScrollViewer? found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>根据选中项与磁盘状态刷新按钮可用性与文案。</summary>
    private void UpdateCommandState()
    {
        DiskItem? disk = DiskList.SelectedItem as DiskItem;
        bool elevated = Elevation.IsElevated;
        bool idle = !_busy;

        /*
         * 按钮不再因为"安全闸门"而置灰——按用户要求：**只警告、不硬拦**。
         * 现在只按"能不能做"决定可用性（已脱机就只能联机），风险交给点击后的警告框说明。
         */
        OfflineButton.IsEnabled = idle && elevated && disk is not null && !disk.IsOffline;
        OnlineButton.IsEnabled = idle && elevated && disk is not null && disk.IsOffline;
        RemoveButton.IsEnabled = idle && elevated && disk is not null;

        RemoveButtonText.Text = disk?.EjectActionName ?? "弹出";

        if (disk is null)
        {
            SelectionText.Text = "未选择磁盘";
            return;
        }

        string offlineHint = disk.IsOffline
            ? "（已脱机，可联机）"
            : disk.OfflineBlockReason.Length > 0
                ? $"（注意：{disk.OfflineBlockReason}）"
                : "（可脱机）";

        string removeHint = disk.EjectBlockReason.Length > 0
            ? $"　注意：{disk.EjectBlockReason}"
            : $"　可{disk.EjectActionName}";

        SelectionText.Text = $"{disk.NumberText}　{disk.FriendlyName}　{disk.StatusText} {offlineHint}{removeHint}";
    }

    private void UpdatePermissionState()
    {
        bool elevated = Elevation.IsElevated;
        ElevationBar.IsOpen = !elevated;
        PermissionText.Text = elevated ? "管理员权限" : "普通权限（只读）";
    }

    /// <summary>按逻辑像素设置窗口大小（自动换算高 DPI 缩放）。</summary>
    private void ResizeToLogical(int width, int height)
    {
        double scale = Content?.XamlRoot?.RasterizationScale ?? 1.0;
        AppWindow.Resize(new SizeInt32((int)(width * scale), (int)(height * scale)));
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        RefreshButton.IsEnabled = !busy;

        if (busy)
        {
            OfflineButton.IsEnabled = false;
            OnlineButton.IsEnabled = false;
            RemoveButton.IsEnabled = false;
        }
    }

    // ------------------------------------------------------------------
    // 脱机 / 联机 / 移除
    // ------------------------------------------------------------------

    private async void OnOfflineClick(object sender, RoutedEventArgs e)
    {
        if (DiskList.SelectedItem is not DiskItem disk || _busy || !CheckElevation())
        {
            return;
        }

        bool confirmed = await ConfirmAsync(
            "脱机磁盘",
            $"确定要使磁盘 {disk.Number}（{disk.FriendlyName}）脱机吗？",
            "脱机后该磁盘上的所有卷都会从文件资源管理器中消失，直到重新联机为止。",
            "脱机");

        if (!confirmed)
        {
            return;
        }

        await RunToggleAsync(disk.Number, offline: true);
    }

    private async void OnOnlineClick(object sender, RoutedEventArgs e)
    {
        if (DiskList.SelectedItem is not DiskItem disk || _busy || !CheckElevation())
        {
            return;
        }

        await RunToggleAsync(disk.Number, offline: false);
    }

    private async void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (DiskList.SelectedItem is not DiskItem disk || _busy || !CheckElevation())
        {
            return;
        }

        await RemoveDiskAsync(disk);
    }

    private async void OnListDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (DiskList.SelectedItem is not DiskItem disk || _busy || !CheckElevation())
        {
            return;
        }

        // 双击 = 脱机 / 联机 切换（本工具最常用的动作）；移除请用「弹出」按钮
        await RunToggleAsync(disk.Number, offline: !disk.IsOffline);
    }

    private async Task RunToggleAsync(int diskNumber, bool offline)
    {
        DiskItem? target = _disks.FirstOrDefault(d => d.Number == diskNumber);
        BlockResolutionOptions? options = null;

        if (target is not null && offline)
        {
            options = await PrepareBlockOptionsAsync(target, "脱机");
            if (options is null)
            {
                StatusText.Text = "已取消脱机";
                return;
            }
        }

        SetBusy(true);
        StatusText.Text = offline ? $"正在使磁盘 {diskNumber} 脱机…" : $"正在使磁盘 {diskNumber} 联机…";

        (OperationResult? result, bool completed) = await RunWithTimeoutAsync(
            () => DiskListService.SetOffline(diskNumber, offline, options),
            TimeSpan.FromSeconds(40));

        if (!completed || result is null)
        {
            result = OperationResult.Fail(
                "操作超时（40 秒）：这块磁盘可能没有响应，或者上面的文件被占用。\n\n" +
                "已停止等待，后台请求仍在继续——如果它最终成功，列表会自动更新。\n\n" +
                $"日志：{OperationLog.LogPath}");
        }

        SetBusy(false);

        if (!result.Success)
        {
            await ShowMessageAsync("操作未完成", result.Message);
        }
        else
        {
            StatusText.Text = $"磁盘 {diskNumber} 已{(offline ? "脱机" : "联机")}";
        }

        await RefreshAsync();
        ScheduleRefresh(1200);
    }

    private async Task RemoveDiskAsync(DiskItem disk)
    {
        string action = disk.EjectActionName;

        bool confirmed = await ConfirmAsync(
            disk.IsVirtualDisk ? "分离虚拟磁盘" : "弹出设备",
            $"确定要{action}磁盘 {disk.Number}（{disk.FriendlyName}）吗？",
            disk.IsVirtualDisk
                ? "系统会先卸载该虚拟磁盘上的卷、刷新写缓存，然后把它从系统中分离；以后可以再挂载回来。"
                : "系统会先卸载该磁盘上的卷、刷新写缓存，然后把设备从系统中移除；移除后需要重新插拔才能使用。",
            action);

        if (!confirmed)
        {
            return;
        }

        SetBusy(true);
        StatusText.Text = $"正在{action}磁盘 {disk.Number}…";

        BlockResolutionOptions? options = await PrepareBlockOptionsAsync(disk, action);
        if (options is null)
        {
            SetBusy(false);
            StatusText.Text = $"已取消{action}";
            return;
        }

        (OperationResult? result, bool completed) = await RunWithTimeoutAsync(
            () => DiskListService.Remove(disk, options),
            TimeSpan.FromSeconds(40));

        if (!completed || result is null)
        {
            result = OperationResult.Fail(
                $"操作超时（40 秒）：{action}请求仍在进行中，这块磁盘可能没有响应，或者上面的文件被占用。\n\n" +
                "已停止等待，后台请求仍在继续——如果它最终成功，设备会自行消失。\n\n" +
                $"日志：{OperationLog.LogPath}");
        }

        SetBusy(false);

        if (!result.Success)
        {
            await ShowMessageAsync($"{action}未完成", result.Message);
        }
        else
        {
            StatusText.Text = result.Message;
        }

        // 设备移除是异步的：立刻刷一次，再补一次延迟刷新
        await RefreshAsync();
        ScheduleRefresh(1500);
    }

    /// <summary>
    /// 操作前的预检查（全部在界面线程上 await）。
    /// 服务层不再回调界面——后台线程回调界面要"投递 + 阻塞等待"，
    /// 界面正在处理对话框时会互相等待形成死锁（表现就是整个程序卡死）。
    /// </summary>
    private async Task<BlockResolutionOptions?> PrepareBlockOptionsAsync(DiskItem disk, string action)
    {
        (OfflineGuard.Block? block, bool evaluated) = await RunWithTimeoutAsync(
            () => OfflineGuard.Evaluate(disk),
            TimeSpan.FromSeconds(15));

        if (!evaluated || block is null)
        {
            await ShowMessageAsync(
                "检查超时",
                "检查磁盘状态超时（15 秒）：这块磁盘可能没有响应。\n\n" +
                $"日志：{OperationLog.LogPath}");
            return null;
        }

        // 没有风险 → 直接执行
        if (!block.IsBlocked)
        {
            return new BlockResolutionOptions { Report = message => StatusText.Text = message };
        }

        /*
         * 有风险：**只警告、不硬拦**。
         *   继续     → 明确知道后果，照样执行（写入日志备查）
         *   先分离   → 仅"在用的虚拟机文件"提供：先把虚拟磁盘分离，再执行（推荐）
         *   取消     → 放弃
         */
        (string title, string warning) = DescribeBlock(block);

        (List<string>? holders, _) = block.Kind == OfflineGuard.BlockKind.InUseVirtualDisk
            ? await RunWithTimeoutAsync(
                () => FileLockInspector.GetLockingProcesses([block.Detail]),
                TimeSpan.FromSeconds(5))
            : (null, true);

        string holderText = holders is { Count: > 0 }
            ? $"\n\n{FileLockInspector.DescribeHolders(holders)}"
            : string.Empty;

        bool canDetach = block.Kind == OfflineGuard.BlockKind.InUseVirtualDisk;

        ContentDialog dialog = new()
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = $"{warning}{holderText}\n\n" +
                       $"【继续执行】按现在的状态直接{action}（后果自负）\n" +
                       (canDetach ? "【先分离并继续】先把那个虚拟磁盘从系统分离，再执行（推荐）\n" : string.Empty) +
                       "【取消】放弃",
            },
            PrimaryButtonText = "继续执行",
            SecondaryButtonText = canDetach ? "先分离并继续" : null,
            CloseButtonText = "取消",
            DefaultButton = canDetach ? ContentDialogButton.Secondary : ContentDialogButton.Close,
        };

        ContentDialogResult choice = await dialog.ShowAsync();
        if (choice == ContentDialogResult.None)
        {
            return null;
        }

        if (choice == ContentDialogResult.Primary)
        {
            OperationLog.Write($"用户确认忽略安全警告（{action}）：{block.Reason}");
            return new BlockResolutionOptions
            {
                IgnoreSafetyWarning = true,
                Report = message => StatusText.Text = message,
            };
        }

        string? file = block.Detail;
        if (string.IsNullOrWhiteSpace(file) || !System.IO.File.Exists(file))
        {
            file = await PickVirtualDiskFileAsync();
        }

        if (string.IsNullOrWhiteSpace(file))
        {
            await ShowMessageAsync("已取消", "没有指定虚拟磁盘的宿主文件。");
            return null;
        }

        return new BlockResolutionOptions
        {
            DetachBlockingVirtualDiskConfirmed = true,
            PreResolvedVirtualDiskFile = file,
            Report = message => StatusText.Text = message,
        };
    }

    /// <summary>带超时地执行后台工作。</summary>
    /// <summary>
    /// 把闸门的拦截原因翻译成"警告文案"（标题 + 后果说明）。
    /// 设计上是**只警告不拦**：后果写清楚，用户确认后照样执行。
    /// </summary>
    private static (string Title, string Warning) DescribeBlock(OfflineGuard.Block block)
        => block.Kind switch
        {
            OfflineGuard.BlockKind.SystemPartition => (
                "⚠ 该磁盘含系统分区",
                "这块盘上有 Windows 的启动文件。脱机或移除可能导致系统无法启动。\n" +
                "（系统自带的磁盘管理同样不会拦你，但请确认你清楚后果。）"),
            OfflineGuard.BlockKind.BootVolume => (
                "⚠ 该磁盘含启动卷",
                "Windows 目录就在这块盘上。执行后系统会**立即崩溃（蓝屏 0x22）**。"),
            OfflineGuard.BlockKind.PageFile => (
                "⚠ 该磁盘上有分页文件",
                "系统正在用这块盘做虚拟内存。执行后系统会崩溃或严重卡顿。"),
            OfflineGuard.BlockKind.ProtectedVolume => (
                "⚠ 该磁盘上有系统正在使用的卷",
                "系统或本程序正在使用这块盘上的卷。执行后相关程序会立即出错。"),
            OfflineGuard.BlockKind.InUseVirtualDisk => (
                "⚠ 该磁盘上有正在使用的虚拟机文件",
                $"文件：{block.Detail}\n" +
                (block.VirtualDiskNumber >= 0 ? $"它正提供为磁盘 {block.VirtualDiskNumber}。\n" : string.Empty) +
                "直接继续会让使用它的虚拟机出错，甚至损坏虚拟磁盘。"),
            _ => ("⚠ 注意", block.Reason),
        };

    /// <summary>带超时地执行后台工作。</summary>
    private static async Task<(T? Value, bool Completed)> RunWithTimeoutAsync<T>(Func<T> work, TimeSpan timeout)
    {
        Task<T> task = Task.Run(work);
        Task finished = await Task.WhenAny(task, Task.Delay(timeout));
        return finished == task ? (await task, true) : (default, false);
    }

    /// <summary>让用户指定虚拟磁盘的宿主文件（在界面线程上 await，不阻塞）。</summary>
    private async Task<string?> PickVirtualDiskFileAsync()
    {
        Windows.Storage.Pickers.FileOpenPicker picker = new();
        picker.FileTypeFilter.Add(".vhdx");
        picker.FileTypeFilter.Add(".vhd");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

        Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    // ------------------------------------------------------------------
    // 对话框辅助
    // ------------------------------------------------------------------

    private bool CheckElevation()
    {
        if (Elevation.IsElevated)
        {
            return true;
        }

        _ = ShowMessageAsync("需要管理员权限", "请以管理员身份重新启动后再执行该操作。");
        return false;
    }

    private async Task ShowMessageAsync(string title, string message)
        => await ShowDialogAsync(title, message, "知道了", null);

    private async Task<bool> ConfirmAsync(string title, string message, string detail, string confirmText)
        => await ShowDialogAsync(title, $"{message}\n\n{detail}", confirmText, "取消");

    /// <summary>在界面线程上同步显示确认框（供后台线程里调用的回调使用）。</summary>
    private bool ConfirmDialogDirect(string title, string message, string detail, string confirmText)
    {
        Task<bool> task = ConfirmAsync(title, message, detail, confirmText);
        task.Wait(TimeSpan.FromMinutes(5));
        return task.IsCompletedSuccessfully && task.Result;
    }

    private async Task<bool> ShowDialogAsync(string title, string message, string primaryText, string? closeText)
    {
        ContentDialog dialog = new()
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primaryText,
            CloseButtonText = closeText ?? string.Empty,
            DefaultButton = closeText is null ? ContentDialogButton.Primary : ContentDialogButton.Close,
        };

        ContentDialogResult result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    /// <summary>从后台线程安全地在界面线程上取值（阻塞等待，仅供回调使用）。</summary>
    private T InvokeOnUi<T>(Func<T> action)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            return action();
        }

        T result = default!;
        using ManualResetEventSlim gate = new(false);

        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                result = action();
            }
            finally
            {
                gate.Set();
            }
        });

        gate.Wait(TimeSpan.FromMinutes(5));
        return result;
    }

    private void InvokeOnUi(Action action) => InvokeOnUi(() => { action(); return true; });

    // ------------------------------------------------------------------
    // 命令
    // ------------------------------------------------------------------

    private void OnRefreshClick(object sender, RoutedEventArgs e) => _ = RefreshAsync();

    private void OnRestartElevatedClick(object sender, RoutedEventArgs e)
    {
        if (Elevation.TryRestartElevated(string.Empty, out string error))
        {
            Close();
            return;
        }

        _ = ShowMessageAsync("提权启动失败", error);
    }
}

/// <summary>
/// 轻量定时器封装：WinUI 的 DispatcherQueueTimer 需要显式创建与管理，
/// 这里包一层，便于"重置计时"的防抖写法。
/// </summary>
internal sealed class DispatcherTimerLike
{
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;
    private readonly Action _callback;

    public DispatcherTimerLike(TimeSpan interval, Action callback)
    {
        _callback = callback;
        _timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = interval;
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => _callback();
    }

    public void Restart(TimeSpan interval)
    {
        _timer.Interval = interval;
        _timer.Stop();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();
}
