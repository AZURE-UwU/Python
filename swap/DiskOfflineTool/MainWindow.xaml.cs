using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using DiskManager.Models;
using DiskManager.Services;

namespace DiskOfflineTool;

/// <summary>
/// 主窗口：只做一件事——列出所有磁盘，对选中的磁盘执行脱机 / 联机。
/// 写操作全部通过 DiskListService 走主程序已验证的底层实现。
/// </summary>
public partial class MainWindow : Window
{
    private readonly ObservableCollection<DiskItem> _disks = [];
    private bool _busy;

    /// <summary>系统硬件变化消息（插拔设备、挂载/分离 VHD 等）。</summary>
    private const int WmDeviceChange = 0x0219;

    private HwndSource? _windowSource;
    private DispatcherTimer? _refreshTimer;

    public MainWindow()
    {
        InitializeComponent();
        DiskGrid.ItemsSource = _disks;

        Loaded += OnLoaded;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    // ------------------------------------------------------------------
    // 硬件变化自动刷新
    // ------------------------------------------------------------------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 挂一个消息钩子：任何硬件插拔都会通知我们，自动刷新磁盘列表
        _windowSource = PresentationSource.FromVisual(this) as HwndSource;
        _windowSource?.AddHook(OnWindowMessage);
    }

    protected override void OnClosed(EventArgs e)
    {
        _windowSource?.RemoveHook(OnWindowMessage);
        _refreshTimer?.Stop();
        base.OnClosed(e);
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmDeviceChange)
        {
            // 硬件事件会成串到达（一个设备移除会连发好几条），防抖后再刷新
            ScheduleRefresh(700, "检测到硬件变化，正在刷新…");
        }

        return IntPtr.Zero;
    }

    /// <summary>延迟刷新（会重置上一次的等待，多个事件只刷新一次）。</summary>
    private void ScheduleRefresh(int delayMs, string? statusText = null)
    {
        if (_refreshTimer is null)
        {
            _refreshTimer = new DispatcherTimer();
            _refreshTimer.Tick += (_, _) =>
            {
                _refreshTimer!.Stop();

                // 正在执行操作就先让路，稍后再刷（避免这条事件被丢掉）
                if (_busy)
                {
                    ScheduleRefresh(800);
                    return;
                }

                _ = RefreshAsync();
            };
        }

        if (statusText is not null && !_busy)
        {
            StatusText.Text = statusText;
        }

        _refreshTimer.Stop();
        _refreshTimer.Interval = TimeSpan.FromMilliseconds(delayMs);
        _refreshTimer.Start();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        bool elevated = Elevation.IsElevated;
        ElevationBanner.Visibility = elevated ? Visibility.Collapsed : Visibility.Visible;
        PermissionText.Text = elevated ? "管理员权限" : "普通权限（只读）";

        await RefreshAsync();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            _ = RefreshAsync();
            e.Handled = true;
        }
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

        int? previousNumber = (DiskGrid.SelectedItem as DiskItem)?.Number;
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

            // 尽量保持原来选中的磁盘
            if (previousNumber.HasValue)
            {
                DiskItem? match = _disks.FirstOrDefault(d => d.Number == previousNumber.Value);
                if (match is not null)
                {
                    DiskGrid.SelectedItem = match;
                }
            }

            string warning = warnings.Count > 0 ? $"　⚠ {warnings[0]}" : string.Empty;
            StatusText.Text = $"共 {_disks.Count} 块磁盘{warning}";
        }
        catch (Exception ex)
        {
            StatusText.Text = "读取磁盘列表失败";
            MessageBox.Show(this, $"读取磁盘列表失败：{ex.Message}", "磁盘脱机工具", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
            UpdateButtons();
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    /// <summary>
    /// 截图演示用：选中第一块"不允许脱机"的磁盘，便于一眼看到界面是否已置灰并给出原因。
    /// </summary>
    internal void SelectFirstProtectedDisk()
    {
        // 优先选一块能"分离 VHD"的虚拟磁盘（展示按钮会变成"分离"），
        // 其次是可弹出的设备，最后是被保护的磁盘。
        DiskItem? target = _disks.FirstOrDefault(d => d.CanEject && d.IsVirtualDisk)
            ?? _disks.FirstOrDefault(d => d.CanEject)
            ?? _disks.FirstOrDefault(d => d.OfflineBlockReason.Length > 0)
            ?? _disks.FirstOrDefault();
        if (target is not null)
        {
            DiskGrid.SelectedItem = target;
            DiskGrid.ScrollIntoView(target);
        }
    }

    /// <summary>根据选中项与磁盘状态刷新按钮可用性。</summary>
    private void UpdateButtons()
    {
        DiskItem? disk = DiskGrid.SelectedItem as DiskItem;
        bool elevated = Elevation.IsElevated;

        bool canOffline = elevated && disk is not null && !disk.IsOffline && DiskListService.CanOffline(disk) && disk.Size > 0;
        bool canOnline = elevated && disk is not null && disk.IsOffline;
        bool canEject = elevated && disk is not null && disk.CanEject;

        OfflineButton.IsEnabled = canOffline;
        OnlineButton.IsEnabled = canOnline;
        EjectButton.IsEnabled = canEject;
        OfflineMenuItem.IsEnabled = canOffline;
        OnlineMenuItem.IsEnabled = canOnline;
        EjectMenuItem.IsEnabled = canEject;

        // 虚拟磁盘走"分离 VHD"，按钮和菜单文案跟着磁盘类型变化
        EjectButton.Content = disk?.EjectActionName ?? "弹出";
        EjectButton.ToolTip = disk?.IsVirtualDisk == true
            ? "分离该虚拟磁盘（等价于磁盘管理里的「分离 VHD」）"
            : "安全移除设备（相当于任务栏的「安全删除硬件」），仅对可移动设备有效";
        EjectMenuItem.Header = disk?.IsVirtualDisk == true ? "分离 VHD(_E)" : "弹出(_E)";

        if (disk is null)
        {
            SelectionText.Text = "未选择磁盘";
            return;
        }

        string hint = disk.IsOffline
            ? "（已脱机，可执行联机）"
            : DiskListService.CanOffline(disk)
                ? "（可执行脱机）"
                : $"（不允许脱机：{disk.OfflineBlockReason}）";

        string ejectHint = disk.CanEject
            ? $"　可{disk.EjectActionName}"
            : $"　不支持{disk.EjectActionName}";

        SelectionText.Text = $"已选择：磁盘 {disk.Number}　{disk.FriendlyName}　{disk.StatusText} {hint}{ejectHint}";
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        RefreshButton.IsEnabled = !busy;

        if (busy)
        {
            OfflineButton.IsEnabled = false;
            OnlineButton.IsEnabled = false;
            EjectButton.IsEnabled = false;
        }
    }

    // ------------------------------------------------------------------
    // 脱机 / 联机
    // ------------------------------------------------------------------

    private async void OnOfflineClick(object sender, RoutedEventArgs e)
    {
        if (DiskGrid.SelectedItem is not DiskItem disk || _busy)
        {
            return;
        }

        if (!Elevation.IsElevated)
        {
            MessageBox.Show(this, "需要以管理员身份运行才能脱机磁盘。", "磁盘脱机工具", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!DiskListService.CanOffline(disk))
        {
            MessageBox.Show(
                this,
                $"磁盘 {disk.Number} 包含系统卷或启动卷，Windows 不允许将其脱机。",
                "无法脱机",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        MessageBoxResult answer = MessageBox.Show(
            this,
            $"确定要使磁盘 {disk.Number}（{disk.FriendlyName}）脱机吗？\n\n" +
            "脱机后该磁盘上的所有卷都会从文件资源管理器中消失，直到重新联机为止。",
            "脱机磁盘",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        await RunToggleAsync(disk.Number, offline: true);
    }

    private async void OnOnlineClick(object sender, RoutedEventArgs e)
    {
        if (DiskGrid.SelectedItem is not DiskItem disk || _busy)
        {
            return;
        }

        if (!Elevation.IsElevated)
        {
            MessageBox.Show(this, "需要以管理员身份运行才能联机磁盘。", "磁盘脱机工具", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await RunToggleAsync(disk.Number, offline: false);
    }

    /// <summary>
    /// 弹出（安全移除）选中的磁盘。
    /// 只对支持安全移除的设备有效：内部固定磁盘会被直接拒绝，不会冒险拔掉。
    /// </summary>
    private async void OnEjectClick(object sender, RoutedEventArgs e)
    {
        if (DiskGrid.SelectedItem is not DiskItem disk || _busy)
        {
            return;
        }

        if (!Elevation.IsElevated)
        {
            MessageBox.Show(this, "需要以管理员身份运行才能弹出设备。", "磁盘脱机工具", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!disk.CanEject)
        {
            MessageBox.Show(
                this,
                $"磁盘 {disk.Number} 无法{disk.EjectActionName}：\n\n{disk.EjectBlockReason}",
                $"无法{disk.EjectActionName}",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        bool isVirtual = disk.IsVirtualDisk;
        MessageBoxResult answer = MessageBox.Show(
            this,
            isVirtual
                ? $"确定要分离虚拟磁盘 {disk.Number}（{disk.FriendlyName}）吗？\n\n" +
                  "系统会先卸载该虚拟磁盘上的卷、刷新写缓存，然后把它从系统中分离；" +
                  "以后可以再用磁盘管理把对应的 VHD/VHDX 文件挂载回来。"
                : $"确定要弹出磁盘 {disk.Number}（{disk.FriendlyName}）吗？\n\n" +
                  "系统会先卸载该磁盘上的卷、刷新写缓存，然后把设备从系统中移除；" +
                  "移除后需要重新插拔（或重新挂载）才能再次使用。",
            isVirtual ? "分离虚拟磁盘" : "弹出设备",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        await RunRemoveAsync(disk);
    }

    /// <summary>双击列表行：按当前状态执行相反的操作。</summary>
    private void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DiskGrid.SelectedItem is not DiskItem disk || _busy)
        {
            return;
        }

        if (disk.IsOffline)
        {
            OnOnlineClick(sender, e);
            return;
        }

        OnOfflineClick(sender, e);
    }

    /// <summary>执行动作并刷新列表。只把磁盘号传给后台线程，绝不跨线程碰界面对象。</summary>
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

        OperationResult result = await RunOperationWithFeedbackAsync(
            offline ? $"正在使磁盘 {diskNumber} 脱机…" : $"正在使磁盘 {diskNumber} 联机…",
            () => DiskListService.SetOffline(diskNumber, offline, options),
            TimeSpan.FromSeconds(40));

        SetBusy(false);

        if (!result.Success)
        {
            MessageBox.Show(
                this,
                $"{(offline ? "脱机" : "联机")}磁盘 {diskNumber} 失败：\n\n{result.Message}",
                "磁盘脱机工具",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        else
        {
            StatusText.Text = $"磁盘 {diskNumber} 已{(offline ? "脱机" : "联机")}";
        }

        // 立刻刷新一次，再补一次延迟刷新：
        // 设备移除是异步完成的，太早刷新会把已经弹出的磁盘又列出来。
        await RefreshAsync();
        ScheduleRefresh(1200);
    }

    /// <summary>执行移除（弹出或分离）并刷新列表。</summary>
    private async Task RunRemoveAsync(DiskItem disk)
    {
        string action = disk.IsVirtualDisk ? "分离" : "弹出";

        BlockResolutionOptions? options = await PrepareBlockOptionsAsync(disk, action);
        if (options is null)
        {
            StatusText.Text = $"已取消{action}";
            return;
        }

        OperationResult result = await RunOperationWithFeedbackAsync(
            disk.IsVirtualDisk ? $"正在分离虚拟磁盘 {disk.Number}…" : $"正在弹出磁盘 {disk.Number}…",
            () => DiskListService.Remove(disk, options),
            TimeSpan.FromSeconds(40));

        SetBusy(false);

        if (!result.Success)
        {
            MessageBox.Show(this, result.Message, "磁盘脱机工具", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else
        {
            StatusText.Text = result.Message;
        }

        await RefreshAsync();
        ScheduleRefresh(1500);
    }

    /// <summary>
    /// 操作前的预检查（全部在界面线程上 await，绝不让后台线程回调界面——那会死锁）。
    ///
    /// 如果这块磁盘上有"正在使用的虚拟磁盘文件"：
    ///   1) 查出是谁占着（Restart Manager，带超时）；
    ///   2) 问用户要不要先分离它；
    ///   3) 顺便把宿主文件路径准备好（自动找不到就让用户手动指定）。
    /// 返回 null 表示用户放弃或评估超时，调用方直接结束。
    /// </summary>
    private async Task<BlockResolutionOptions?> PrepareBlockOptionsAsync(DiskItem disk, string action)
    {
        (OfflineGuard.Block? block, bool evaluated) = await RunWithTimeoutAsync(
            () => OfflineGuard.Evaluate(disk),
            TimeSpan.FromSeconds(15));

        if (!evaluated || block is null)
        {
            MessageBox.Show(
                this,
                $"检查磁盘状态超时（15 秒）：这块磁盘可能没有响应，或者上面的文件被占用。\n\n" +
                $"日志：{OperationLog.LogPath}",
                "磁盘脱机工具",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return null;
        }

        // 没有挡路的虚拟磁盘文件 → 直接返回普通选项
        if (block.Kind != OfflineGuard.BlockKind.InUseVirtualDisk)
        {
            return new BlockResolutionOptions { Report = text => StatusText.Text = text };
        }

        (List<string>? holders, _) = await RunWithTimeoutAsync(
            () => FileLockInspector.GetLockingProcesses([block.Detail]),
            TimeSpan.FromSeconds(5));

        string holderText = holders is { Count: > 0 }
            ? $"\n\n{FileLockInspector.DescribeHolders(holders)}"
            : string.Empty;

        /*
         * 三选一：
         *   是   → 先分离那个虚拟磁盘，再继续（推荐，除占用外无副作用）
         *   否   → 强制卸载卷（与 Windows 磁盘管理相同的做法：不检查占用、强制卸载，
         *          正在使用这些文件的程序会收到句柄失效错误，虚拟机可能损坏）
         *   取消 → 放弃
         */
        MessageBoxResult answer = MessageBox.Show(
            this,
            $"磁盘 {disk.Number}（{disk.FriendlyName}）上有正在使用的虚拟机/镜像文件：\n\n" +
            $"{block.Detail}{holderText}\n\n" +
            $"【是】先分离这个虚拟磁盘，然后{action}磁盘 {disk.Number}（推荐）\n" +
            $"【否】强制卸载卷后{action}（与 Windows 磁盘管理相同：不检查文件占用，" +
            "正在使用这些文件的程序会出错，虚拟机可能损坏）\n" +
            "【取消】放弃",
            "检测到文件正在被占用",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Yes);

        if (answer == MessageBoxResult.Cancel)
        {
            return null;
        }

        if (answer == MessageBoxResult.No)
        {
            return new BlockResolutionOptions
            {
                ForceDismount = true,
                Report = text => StatusText.Text = text,
            };
        }

        // 把宿主文件路径准备好（自动扫描失败就让用户手动指定）
        string? file = block.Detail;
        if (string.IsNullOrWhiteSpace(file) || !System.IO.File.Exists(file))
        {
            file = PickVirtualDiskFile();
        }

        if (string.IsNullOrWhiteSpace(file))
        {
            MessageBox.Show(
                this,
                "没有指定虚拟磁盘的宿主文件，已取消。",
                "磁盘脱机工具",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return null;
        }

        return new BlockResolutionOptions
        {
            DetachBlockingVirtualDiskConfirmed = true,
            PreResolvedVirtualDiskFile = file,
            Report = text => StatusText.Text = text,
        };
    }

    /// <summary>带超时地执行后台工作；超时返回 (false)。</summary>
    private static async Task<(T? Value, bool Completed)> RunWithTimeoutAsync<T>(Func<T> work, TimeSpan timeout)
    {
        Task<T> task = Task.Run(work);
        Task finished = await Task.WhenAny(task, Task.Delay(timeout));
        return finished == task ? (await task, true) : (default, false);
    }

    /// <summary>
    /// 带超时 + 进度反馈地执行后台操作。
    /// 每秒刷新一次状态栏（"已等待 N 秒…"），这样即使系统调用迟迟不返回，
    /// 用户也能看出程序还活着，而不是又"卡死"了。
    /// </summary>
    private async Task<OperationResult> RunOperationWithFeedbackAsync(
        string busyText,
        Func<OperationResult> operation,
        TimeSpan timeout)
    {
        SetBusy(true);
        StatusText.Text = busyText;

        Task<OperationResult> task = Task.Run(operation);
        int elapsedSeconds = 0;
        int timeoutSeconds = (int)timeout.TotalSeconds;

        while (!task.IsCompleted && elapsedSeconds < timeoutSeconds)
        {
            await Task.Delay(1000);
            elapsedSeconds++;

            if (!task.IsCompleted)
            {
                StatusText.Text = $"{busyText}（已等待 {elapsedSeconds} 秒…）";
            }
        }

        if (task.IsCompleted)
        {
            return await task;
        }

        return OperationResult.Fail(
            $"操作超时（{timeoutSeconds} 秒）：这块磁盘可能没有响应，或者上面的文件被占用。\n\n" +
            "已停止等待，后台请求仍在继续——如果它最终成功，设备会自行消失。\n\n" +
            $"日志：{OperationLog.LogPath}");
    }

    /// <summary>让用户指定虚拟磁盘的宿主文件（仅在自动查找失败时调用）。</summary>
    private string? PickVirtualDiskFile()
    {
        return Dispatcher.Invoke(() =>
        {
            Microsoft.Win32.OpenFileDialog dialog = new()
            {
                Title = "请指定该虚拟磁盘对应的 VHD / VHDX 文件",
                Filter = "虚拟磁盘文件 (*.vhdx;*.vhd)|*.vhdx;*.vhd|所有文件 (*.*)|*.*",
                CheckFileExists = true,
            };

            return dialog.ShowDialog(this) == true ? dialog.FileName : null;
        });
    }

    // ------------------------------------------------------------------
    // 菜单
    // ------------------------------------------------------------------

    private void OnRefreshClick(object sender, RoutedEventArgs e) => _ = RefreshAsync();

    private void OnExitClick(object sender, RoutedEventArgs e) => Close();

    private void OnRestartElevatedClick(object sender, RoutedEventArgs e)
    {
        if (Elevation.TryRestartElevated(string.Empty, out string error))
        {
            Close();
            return;
        }

        MessageBox.Show(this, error, "磁盘脱机工具", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            this,
            "磁盘脱机工具 1.0\n\n" +
            "【版本状态】本 WPF 版已停止更新，主线是 WinUI 3 版（DiskOfflineToolWinUI.exe），\n" +
            "功能相同、外观更现代。本版保留是为了在没有 Windows App Runtime 的机器上也能用。\n\n" +
            "· 列出全部物理磁盘\n" +
            "· 对选中磁盘执行脱机 / 联机（IOCTL_DISK_SET_DISK_ATTRIBUTES，失败时退回系统存储接口）\n" +
            "· 需要管理员权限；系统盘与启动盘不允许脱机\n\n" +
            "脱机只是让系统暂时不使用该磁盘，数据不会被删除，重新联机即可恢复。",
            "关于",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }
}
