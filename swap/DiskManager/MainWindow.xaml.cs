using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DiskManager.Controls;
using DiskManager.Dialogs;
using DiskManager.Interop;
using DiskManager.Models;
using DiskManager.Services;

namespace DiskManager;

/// <summary>
/// 主窗口：上半区卷列表 + 下半区磁盘图，右键菜单与系统磁盘管理保持一致。
/// 所有写操作前都会确认，操作结束后自动刷新。
/// </summary>
public partial class MainWindow : Window
{
    private readonly ObservableCollection<VolumeItem> _volumes = [];
    private readonly ObservableCollection<DiskItem> _disks = [];

    /// <summary>右键点中的对象，供上下文菜单使用。</summary>
    private VolumeItem? _contextVolume;
    private DiskItem? _contextDisk;
    private PartitionItem? _contextPartition;

    private bool _refreshing;

    /// <summary>系统硬件变化消息（插拔设备、挂载/分离 VHD 等）。</summary>
    private const int WmDeviceChange = 0x0219;

    private HwndSource? _windowSource;
    private DispatcherTimer? _refreshTimer;

    public MainWindow()
    {
        InitializeComponent();

        VolumeGrid.ItemsSource = _volumes;

        DiskMap.PartitionSelected += OnMapPartitionSelected;
        DiskMap.ContextMenuRequested += OnMapContextMenuRequested;

        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += OnLoaded;
    }

    // ------------------------------------------------------------------
    // 硬件变化自动刷新
    // ------------------------------------------------------------------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 挂消息钩子：插拔设备 / 挂载分离 VHD 都会自动刷新，不用手动按 F5
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
            // 一个设备移除会连发多条消息，防抖后只刷新一次
            ScheduleRefresh(700, "检测到硬件变化，正在刷新…");
        }

        return IntPtr.Zero;
    }

    /// <summary>延迟刷新（重置上一次等待，多个硬件事件只刷新一次）。</summary>
    private void ScheduleRefresh(int delayMs, string? statusText = null)
    {
        if (_refreshTimer is null)
        {
            _refreshTimer = new DispatcherTimer();
            _refreshTimer.Tick += (_, _) =>
            {
                _refreshTimer!.Stop();

                // 上一次刷新还没结束就先让路，稍后再刷（避免这条事件被丢掉）
                if (_refreshing)
                {
                    ScheduleRefresh(800);
                    return;
                }

                _ = RefreshAsync();
            };
        }

        if (statusText is not null && !_refreshing)
        {
            StatusText.Text = statusText;
        }

        _refreshTimer.Stop();
        _refreshTimer.Interval = TimeSpan.FromMilliseconds(delayMs);
        _refreshTimer.Start();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ElevationBanner.Visibility = Elevation.IsElevated ? Visibility.Collapsed : Visibility.Visible;
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
    // 刷新
    // ------------------------------------------------------------------

    /// <summary>后台枚举磁盘与卷，回到界面线程整体替换。</summary>
    private async Task RefreshAsync()
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        StatusText.Text = "正在刷新磁盘信息…";
        VolumeItem? previousVolume = VolumeGrid.SelectedItem as VolumeItem;

        try
        {
            InventorySnapshot snapshot = await Task.Run(DiskInventory.Capture);

            _disks.Clear();
            foreach (DiskItem disk in snapshot.Disks)
            {
                _disks.Add(disk);
            }

            _volumes.Clear();
            foreach (VolumeItem volume in snapshot.Volumes)
            {
                _volumes.Add(volume);
            }

            DiskMap.SetDisks(_disks);
            RestoreSelection(previousVolume);
            BuildOperationMenus();

            string warning = snapshot.Warnings.Count > 0 ? $"　⚠ {snapshot.Warnings[0]}" : string.Empty;
            StatusText.Text = $"磁盘 {_disks.Count} 个，卷 {_volumes.Count} 个{warning}";
            ConnectionText.Text = Elevation.IsElevated ? "管理员权限" : "普通权限（只读）";
        }
        catch (Exception ex)
        {
            StatusText.Text = "刷新失败";
            MessageBox.Show(this, $"刷新磁盘信息失败：{ex.Message}", "磁盘管理器", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>刷新后尽量保持原来选中的卷。</summary>
    private void RestoreSelection(VolumeItem? previous)
    {
        if (previous is null)
        {
            return;
        }

        VolumeItem? match = _volumes.FirstOrDefault(v => v.VolumeName == previous.VolumeName);
        if (match is not null)
        {
            VolumeGrid.SelectedItem = match;
            VolumeGrid.ScrollIntoView(match);
        }
    }

    /// <summary>重建"操作"菜单（菜单内容随当前选中对象变化）。</summary>
    private void BuildOperationMenus()
    {
        AllVolumeTasksMenu.Items.Clear();
        foreach (UIElement item in BuildVolumeMenuItems(VolumeGrid.SelectedItem as VolumeItem))
        {
            AllVolumeTasksMenu.Items.Add(item);
        }

        AllDiskTasksMenu.Items.Clear();
        DiskItem? disk = GetSelectedDisk();
        AllDiskTasksMenu.Items.Add(BuildMenuItem("刷新", (_, _) => _ = RefreshAsync()));

        if (disk is null)
        {
            MenuItem placeholder = BuildMenuItem("（请先在下方选择磁盘）", null);
            placeholder.IsEnabled = false;
            AllDiskTasksMenu.Items.Add(placeholder);
            return;
        }

        foreach (UIElement item in BuildDiskMenuItems(disk, null))
        {
            AllDiskTasksMenu.Items.Add(item);
        }
    }

    // ------------------------------------------------------------------
    // 选中联动
    // ------------------------------------------------------------------

    private void OnVolumeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VolumeGrid.SelectedItem is VolumeItem volume)
        {
            PartitionItem? partition = volume.Partitions.FirstOrDefault();
            DiskMap.SelectPartition(partition, partition?.DiskNumber ?? volume.DiskNumbers.FirstOrDefault());
        }
        else
        {
            DiskMap.SelectPartition(null, null);
        }

        BuildOperationMenus();
    }

    private void OnMapPartitionSelected(object? sender, PartitionItem? partition)
    {
        if (partition?.Volume is { } volume)
        {
            VolumeGrid.SelectedItem = volume;
            VolumeGrid.ScrollIntoView(volume);
        }
        else
        {
            VolumeGrid.SelectedItem = null;
        }
    }

    /// <summary>取当前在下方选中的磁盘。</summary>
    private DiskItem? GetSelectedDisk()
    {
        if (_contextDisk is not null)
        {
            return _contextDisk;
        }

        if (VolumeGrid.SelectedItem is VolumeItem volume)
        {
            int number = volume.DiskNumbers.FirstOrDefault(-1);
            return _disks.FirstOrDefault(d => d.Number == number);
        }

        return null;
    }

    // ------------------------------------------------------------------
    // 右键菜单
    // ------------------------------------------------------------------

    private void OnVolumeGridRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 右击时先选中该行，保证菜单作用于用户点的对象。
        _contextDisk = null;
        _contextPartition = null;

        DependencyObject? source = e.OriginalSource as DependencyObject;
        while (source is not null and not DataGridRow)
        {
            source = VisualTreeHelper.GetParent(source);
        }

        if (source is DataGridRow row && row.Item is VolumeItem volume)
        {
            VolumeGrid.SelectedItem = volume;
        }
    }

    private void OnVolumeGridContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        _contextVolume = VolumeGrid.SelectedItem as VolumeItem;
        _contextDisk = null;
        _contextPartition = null;

        if (_contextVolume is null)
        {
            return;
        }

        ContextMenu menu = new();
        foreach (UIElement item in BuildVolumeMenuItems(_contextVolume))
        {
            menu.Items.Add(item);
        }

        VolumeGrid.ContextMenu = menu;
    }

    private void OnMapContextMenuRequested(object? sender, MapContextEventArgs e)
    {
        _contextDisk = e.Disk;
        _contextPartition = e.Partition;
        _contextVolume = e.Partition?.Volume;

        ContextMenu menu = new();
        if (_contextVolume is not null)
        {
            foreach (UIElement item in BuildVolumeMenuItems(_contextVolume))
            {
                menu.Items.Add(item);
            }
        }
        else
        {
            foreach (UIElement item in BuildDiskMenuItems(e.Disk, e.Partition))
            {
                menu.Items.Add(item);
            }
        }

        menu.PlacementTarget = DiskMap;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    /// <summary>卷相关的菜单项。</summary>
    private IEnumerable<UIElement> BuildVolumeMenuItems(VolumeItem? volume)
    {
        if (volume is null)
        {
            MenuItem placeholder = BuildMenuItem("（请先选择卷）", null);
            placeholder.IsEnabled = false;
            yield return placeholder;
            yield break;
        }

        DiskItem? disk = _disks.FirstOrDefault(d => d.Number == volume.DiskNumbers.FirstOrDefault(-1));

        yield return BuildMenuItem("打开(_O)", (_, _) => OpenInExplorer(volume), canExecute: volume.MountPoints.Count > 0);
        yield return BuildMenuItem("更改驱动器号和路径(_C)…", (_, _) => _ = ChangeDriveLetterAsync(volume), canExecute: Elevation.IsElevated);
        yield return BuildMenuItem("格式化(_F)…", (_, _) => _ = FormatVolumeAsync(volume), canExecute: Elevation.IsElevated && volume.MountPoints.Count > 0);
        yield return BuildMenuItem("扩展卷(_X)…", (_, _) => _ = ExtendVolumeAsync(volume, disk), canExecute: Elevation.IsElevated && disk is not null);
        yield return BuildMenuItem("压缩卷(_P)…", (_, _) => _ = ShrinkVolumeAsync(volume), canExecute: Elevation.IsElevated);
        yield return new Separator();
        yield return BuildMenuItem("删除卷(_D)…", (_, _) => _ = DeleteVolumeAsync(volume), canExecute: Elevation.IsElevated);
        yield return new Separator();
        yield return BuildMenuItem("属性(_R)", (_, _) => ShowVolumeProperties(volume));
    }

    /// <summary>磁盘相关的菜单项。</summary>
    private IEnumerable<UIElement> BuildDiskMenuItems(DiskItem disk, PartitionItem? partition)
    {
        if (!disk.IsInitialized)
        {
            yield return BuildMenuItem("初始化磁盘(_I)…", (_, _) => _ = InitializeDiskAsync(disk), canExecute: Elevation.IsElevated);
        }
        else
        {
            yield return BuildMenuItem("新建简单卷(_N)…", (_, _) => _ = CreateVolumeAsync(disk), canExecute: Elevation.IsElevated);
        }

        if (partition is not null)
        {
            yield return BuildMenuItem("格式化(_F)…", (_, _) => _ = FormatVolumeAsync(partition.Volume!), canExecute: Elevation.IsElevated && partition.Volume is not null);
            yield return BuildMenuItem("删除卷(_D)…", (_, _) => _ = DeleteVolumeAsync(partition.Volume!), canExecute: Elevation.IsElevated && partition.Volume is not null);
        }

        yield return new Separator();

        yield return BuildMenuItem(
            disk.IsOffline ? "联机(_O)" : "脱机(_O)",
            (_, _) => _ = ToggleOfflineAsync(disk),
            canExecute: Elevation.IsElevated && (disk.IsOffline || disk.CanOffline));

        yield return BuildMenuItem(
            disk.IsReadOnly ? "清除只读标记" : "标记为只读",
            (_, _) => _ = ToggleReadOnlyAsync(disk),
            canExecute: Elevation.IsElevated);

        yield return BuildMenuItem(
            disk.EjectMenuText,
            (_, _) => _ = RemoveDiskAsync(disk),
            canExecute: Elevation.IsElevated && disk.CanEject);

        if (disk.IsInitialized)
        {
            string target = disk.Style == PartitionStyle.Gpt ? "MBR" : "GPT";
            yield return BuildMenuItem($"转换为 {target} 磁盘(_M)…", (_, _) => _ = ConvertStyleAsync(disk), canExecute: Elevation.IsElevated);
        }

        yield return BuildMenuItem("清除磁盘(_L)…", (_, _) => _ = ClearDiskAsync(disk), canExecute: Elevation.IsElevated && disk.IsInitialized);
        yield return new Separator();
        yield return BuildMenuItem("属性(_R)", (_, _) => PropertiesDialog.ShowDisk(this, disk));
    }

    /// <summary>创建菜单项的小工具。</summary>
    private static MenuItem BuildMenuItem(string header, RoutedEventHandler? click, bool canExecute = true)
    {
        MenuItem item = new() { Header = header, IsEnabled = canExecute };
        if (click is not null)
        {
            item.Click += click;
        }

        return item;
    }

    // ------------------------------------------------------------------
    // 操作：磁盘
    // ------------------------------------------------------------------

    /// <summary>脱机 / 联机，本程序的核心功能。</summary>
    private async Task ToggleOfflineAsync(DiskItem disk)
    {
        bool toOffline = !disk.IsOffline;

        if (toOffline)
        {
            bool confirmed = ConfirmDialog.Ask(
                this,
                "脱机磁盘",
                $"确定要使磁盘 {disk.Number}（{disk.FriendlyName}）脱机吗？",
                "脱机后该磁盘上的所有卷都会从文件资源管理器中消失，直到重新联机为止。",
                requireAcknowledge: true,
                confirmText: "脱机");

            if (!confirmed)
            {
                return;
            }
        }

        BlockResolutionOptions? options = null;
        if (toOffline)
        {
            options = await PrepareBlockOptionsAsync(disk, "脱机");
            if (options is null)
            {
                StatusText.Text = "已取消脱机";
                return;
            }
        }

        await RunOperationAsync(
            toOffline ? $"正在使磁盘 {disk.Number} 脱机…" : $"正在使磁盘 {disk.Number} 联机…",
            () => DiskOperations.SetOffline(disk.Number, toOffline, persist: true, options));
    }

    private async Task ToggleReadOnlyAsync(DiskItem disk)
    {
        bool toReadOnly = !disk.IsReadOnly;
        if (toReadOnly)
        {
            bool confirmed = ConfirmDialog.Ask(
                this,
                "标记为只读",
                $"确定要把磁盘 {disk.Number} 标记为只读吗？",
                "标记后该磁盘的所有写入操作都会被系统拒绝。",
                confirmText: "确定");

            if (!confirmed)
            {
                return;
            }
        }

        await RunOperationAsync(
            toReadOnly ? "正在设置只读标记…" : "正在清除只读标记…",
            () => DiskOperations.SetReadOnly(disk.Number, toReadOnly));
    }

    private async Task InitializeDiskAsync(DiskItem disk)
    {
        InitializeDiskDialog dialog = new(this, disk);
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        bool confirmed = ConfirmDialog.Ask(
            this,
            "初始化磁盘",
            $"确定要初始化磁盘 {disk.Number} 吗？",
            "初始化会写入新的分区表，磁盘上原有的分区信息将被清除，数据将无法恢复。",
            requireAcknowledge: true,
            confirmText: "确定");

        if (!confirmed)
        {
            return;
        }

        PartitionStyle style = dialog.SelectedStyle;
        await RunOperationAsync($"正在初始化磁盘 {disk.Number}…", () => DiskOperations.Initialize(disk.Number, style));
    }

    private async Task ConvertStyleAsync(DiskItem disk)
    {
        PartitionStyle target = disk.Style == PartitionStyle.Gpt ? PartitionStyle.Mbr : PartitionStyle.Gpt;
        string targetName = target == PartitionStyle.Gpt ? "GPT" : "MBR";

        bool confirmed = ConfirmDialog.Ask(
            this,
            $"转换为 {targetName} 磁盘",
            $"确定要把磁盘 {disk.Number} 转换为 {targetName} 分区表吗？",
            "转换分区表需要磁盘上没有已挂载的卷，否则会失败；操作有可能导致分区无法访问。",
            requireAcknowledge: true,
            confirmText: "转换");

        if (!confirmed)
        {
            return;
        }

        await RunOperationAsync($"正在把磁盘 {disk.Number} 转换为 {targetName}…", () => DiskOperations.ConvertStyle(disk.Number, target));
    }

    private async Task ClearDiskAsync(DiskItem disk)
    {
        bool confirmed = ConfirmDialog.Ask(
            this,
            "清除磁盘",
            $"确定要清除磁盘 {disk.Number} 上的全部分区吗？",
            "此操作会删除该磁盘上的所有分区与卷，数据无法恢复。",
            requireAcknowledge: true,
            confirmText: "清除磁盘");

        if (!confirmed)
        {
            return;
        }

        await RunOperationAsync($"正在清除磁盘 {disk.Number}…", () => DiskOperations.ClearDisk(disk.Number, zeroOut: false));
    }

    /// <summary>
    /// 移除磁盘：虚拟磁盘走"分离 VHD"，其他设备走"弹出"。
    /// 资源管理器里对 VHD 显示的"弹出"其实就是分离，这里保持一致。
    /// </summary>
    private async Task RemoveDiskAsync(DiskItem disk)
    {
        string action = disk.EjectActionName;

        bool confirmed = ConfirmDialog.Ask(
            this,
            disk.IsVirtualDisk ? "分离虚拟磁盘" : "弹出设备",
            $"确定要{action}磁盘 {disk.Number}（{disk.FriendlyName}）吗？",
            disk.IsVirtualDisk
                ? "系统会先卸载该虚拟磁盘上的所有卷、刷新写缓存，然后把它从系统中分离；" +
                  "以后可以再用磁盘管理把对应的 VHD/VHDX 文件挂载回来。"
                : "系统会先卸载该磁盘上的所有卷、刷新写缓存，然后把设备从系统中移除；" +
                  "移除后需要重新插拔（或重新挂载）才能再次使用。",
            confirmText: action);

        if (!confirmed)
        {
            return;
        }

        BlockResolutionOptions? options = await PrepareBlockOptionsAsync(disk, action);
        if (options is null)
        {
            StatusText.Text = $"已取消{action}";
            return;
        }

        await RunOperationAsync(
            $"正在{action}磁盘 {disk.Number}…",
            () => DiskOperations.RemoveDisk(disk, options));
    }

    /// <summary>
    /// 操作前的预检查：在界面线程上把"要不要先分离挡路的虚拟磁盘"问清楚，
    /// 并把宿主文件路径准备好。服务层只接收结果，不再回调界面——
    /// 后台线程回调界面需要"投递 + 阻塞等待"，界面正在处理对话框时会互相等待造成死锁（表现为整个程序卡死）。
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
                $"检查磁盘状态超时（15 秒）：这块磁盘可能没有响应。\n\n日志：{OperationLog.LogPath}",
                "磁盘管理器",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return null;
        }

        if (block.Kind != OfflineGuard.BlockKind.InUseVirtualDisk)
        {
            return new BlockResolutionOptions { Report = message => StatusText.Text = message };
        }

        (List<string>? holders, _) = await RunWithTimeoutAsync(
            () => FileLockInspector.GetLockingProcesses([block.Detail]),
            TimeSpan.FromSeconds(5));

        string holderText = holders is { Count: > 0 }
            ? $"\n\n{FileLockInspector.DescribeHolders(holders)}"
            : string.Empty;

        /*
         * 三选一：是=先分离（推荐）；否=强制卸载（与 Windows 磁盘管理相同）；取消=放弃。
         * 强制模式不检查文件占用、直接强制卸载卷，正在使用这些文件的程序会出错。
         */
        MessageBoxResult answer = MessageBox.Show(
            this,
            $"磁盘 {disk.Number}（{disk.FriendlyName}）上有正在使用的虚拟机/镜像文件：\n\n{block.Detail}{holderText}\n\n" +
            $"【是】先分离这个虚拟磁盘，然后{action}磁盘 {disk.Number}（推荐）\n" +
            $"【否】强制卸载卷后{action}（与 Windows 磁盘管理相同，程序可能出错、虚拟机可能损坏）\n" +
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
                Report = message => StatusText.Text = message,
            };
        }

        string? file = block.Detail;
        if (string.IsNullOrWhiteSpace(file) || !System.IO.File.Exists(file))
        {
            file = PickVirtualDiskFile();
        }

        return string.IsNullOrWhiteSpace(file)
            ? null
            : new BlockResolutionOptions
            {
                DetachBlockingVirtualDiskConfirmed = true,
                PreResolvedVirtualDiskFile = file,
                Report = message => StatusText.Text = message,
            };
    }

    /// <summary>带超时地执行后台工作。</summary>
    private static async Task<(T? Value, bool Completed)> RunWithTimeoutAsync<T>(Func<T> work, TimeSpan timeout)
    {
        Task<T> task = Task.Run(work);
        Task finished = await Task.WhenAny(task, Task.Delay(timeout));
        return finished == task ? (await task, true) : (default, false);
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
    // 操作：卷
    // ------------------------------------------------------------------

    private async Task CreateVolumeAsync(DiskItem disk)
    {
        long maximum = GetLargestFreeExtent(disk);
        if (maximum <= 0)
        {
            MessageBox.Show(this, "该磁盘上没有可用的未分配空间。", "新建简单卷", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        NewVolumeDialog dialog = new(this, disk, maximum);
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        // 对话框的返回值先在界面线程取出来，后台线程只使用这些纯数据。
        long volumeSize = dialog.RequestedSize;
        char? driveLetter = dialog.DriveLetter;
        string fileSystem = dialog.FileSystem;
        string label = dialog.VolumeLabel;
        bool quickFormat = dialog.QuickFormat;

        await RunOperationAsync(
            "正在新建简单卷…",
            () => VolumeOperations.CreateSimpleVolume(
                disk,
                volumeSize,
                offset: null,
                driveLetter,
                fileSystem,
                label,
                quickFormat,
                allocationUnitSize: 0));
    }

    private async Task DeleteVolumeAsync(VolumeItem volume)
    {
        bool confirmed = ConfirmDialog.Ask(
            this,
            "删除卷",
            $"确定要删除卷 {volume.DisplayName} 吗？",
            "删除该卷会同时删除卷上的所有数据，且无法恢复。",
            requireAcknowledge: true,
            confirmText: "删除卷");

        if (!confirmed)
        {
            return;
        }

        await RunOperationAsync("正在删除卷…", () => VolumeOperations.DeleteVolume(volume, forceRemoveMountPoints: true));
    }

    private async Task FormatVolumeAsync(VolumeItem volume)
    {
        string[] units = ["默认", "512 字节", "1024 字节", "2048 字节", "4096 字节", "8192 字节", "16384 字节", "32768 字节", "65536 字节"];
        FormatVolumeDialog dialog = new(this, volume, units);
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        bool confirmed = ConfirmDialog.Ask(
            this,
            "格式化",
            $"确定要格式化卷 {volume.DisplayName} 吗？",
            "格式化会清除该卷上的所有数据，且无法撤销。",
            requireAcknowledge: true,
            confirmText: "格式化");

        if (!confirmed)
        {
            return;
        }

        string fileSystem = dialog.FileSystem;
        string label = dialog.VolumeLabel;
        bool quickFormat = dialog.QuickFormat;
        uint allocationUnitSize = dialog.AllocationUnitSize;

        await RunOperationAsync(
            "正在格式化…",
            () => VolumeOperations.FormatVolume(volume, fileSystem, label, quickFormat, allocationUnitSize));
    }

    private async Task ChangeDriveLetterAsync(VolumeItem volume)
    {
        ChangeDriveLetterDialog dialog = new(this, volume);
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        ChangeLetterMode mode = dialog.Mode;
        char? newLetter = dialog.DriveLetter;
        string folderPath = dialog.FolderPath;

        await RunOperationAsync(
            "正在更改驱动器号…",
            () => VolumeOperations.ChangeDriveLetter(volume, mode, newLetter, folderPath));
    }

    private async Task ExtendVolumeAsync(VolumeItem volume, DiskItem? disk)
    {
        if (disk is null)
        {
            MessageBox.Show(this, "找不到该卷所在的磁盘，请刷新后重试。", "扩展卷", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        long maximum = GetMaximumVolumeSize(volume, disk);
        if (maximum <= volume.Size)
        {
            MessageBox.Show(
                this,
                "该卷后面没有紧邻的未分配空间，无法扩展。\n\n提示：扩展卷只能使用紧跟在分区后面的未分配空间。",
                "扩展卷",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        ExtendVolumeDialog dialog = new(this, volume, maximum);
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        long newSize = dialog.NewSize;
        await RunOperationAsync("正在扩展卷…", () => VolumeOperations.ExtendVolume(volume, newSize));
    }

    private async Task ShrinkVolumeAsync(VolumeItem volume)
    {
        PartitionItem? partition = volume.Partitions.FirstOrDefault();
        if (partition is null)
        {
            MessageBox.Show(this, "找不到该卷对应的分区信息，请刷新后重试。", "压缩卷", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        (long minimum, OperationResult supported) = await Task.Run(() =>
        {
            OperationResult result = StorageOperations.GetSupportedSize(partition.DiskNumber, partition.Number, out long min, out _);
            return (min, result);
        });

        if (!supported.Success)
        {
            MessageBox.Show(this, $"无法读取可压缩空间：{supported.Message}", "压缩卷", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ShrinkVolumeDialog dialog = new(this, volume, minimum);
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        long targetSize = dialog.NewSize;
        await RunOperationAsync("正在压缩卷…", () => VolumeOperations.ShrinkVolume(volume, targetSize));
    }

    private void ShowVolumeProperties(VolumeItem volume)
    {
        NtfsVolumeData? ntfs = null;
        if (volume.IsAccessible && string.Equals(volume.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase))
        {
            if (VolumeDevice.TryGetNtfsData(volume.VolumeName, out NtfsVolumeData data))
            {
                ntfs = data;
            }
        }

        PropertiesDialog.ShowVolume(this, volume, ntfs);
    }

    // ------------------------------------------------------------------
    // 辅助
    // ------------------------------------------------------------------

    /// <summary>磁盘上最大的未分配区间。</summary>
    private static long GetLargestFreeExtent(DiskItem disk)
    {
        long largest = 0;
        long cursor = 0;

        foreach (PartitionItem partition in disk.Partitions.OrderBy(p => p.Offset))
        {
            if (partition.Offset > cursor)
            {
                largest = Math.Max(largest, partition.Offset - cursor);
            }

            cursor = Math.Max(cursor, partition.Offset + partition.Length);
        }

        if (disk.Size > cursor)
        {
            largest = Math.Max(largest, disk.Size - cursor);
        }

        return largest;
    }

    /// <summary>卷可扩展到的最大尺寸：当前大小 + 紧随其后的未分配空间。</summary>
    private static long GetMaximumVolumeSize(VolumeItem volume, DiskItem disk)
    {
        PartitionItem? partition = volume.Partitions.FirstOrDefault();
        if (partition is null)
        {
            return volume.Size;
        }

        long end = partition.Offset + partition.Length;
        long next = disk.Partitions
            .Where(p => p.Offset >= end)
            .Select(p => p.Offset)
            .DefaultIfEmpty(disk.Size)
            .Min();

        return partition.Length + (next - end);
    }

    /// <summary>统一的"执行操作 → 提示结果 → 刷新"流程。</summary>
    private async Task RunOperationAsync(string busyText, Func<OperationResult> operation)
    {
        StatusText.Text = busyText;
        IsEnabled = false;
        OperationResult result;

        try
        {
            result = await Task.Run(operation);
        }
        catch (Exception ex)
        {
            result = OperationResult.Fail(ex.Message);
        }
        finally
        {
            IsEnabled = true;
        }

        if (!result.Success)
        {
            MessageBox.Show(this, result.Message, "磁盘管理器", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // 先刷新一次，再补一次延迟刷新：
        // 弹出/分离这类操作是异步完成的，立刻刷新可能还会看到刚移除的磁盘。
        await RefreshAsync();
        ScheduleRefresh(1500);
    }

    private void OpenInExplorer(VolumeItem volume)
    {
        string? target = volume.MountPoints.FirstOrDefault(m => m.Length >= 2 && m[1] == ':');
        if (target is null)
        {
            return;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = target,
            UseShellExecute = true,
        });
    }

    // ------------------------------------------------------------------
    // 菜单栏事件
    // ------------------------------------------------------------------

    private void OnRefreshClick(object sender, RoutedEventArgs e) => _ = RefreshAsync();

    private void OnExitClick(object sender, RoutedEventArgs e) => Close();

    private void OnToggleTopClick(object sender, RoutedEventArgs e)
    {
        bool show = ViewTopMenu.IsChecked;
        TopRow.Height = show ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        TopRow.MinHeight = show ? 120 : 0;
        VolumeGrid.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnToggleBottomClick(object sender, RoutedEventArgs e)
    {
        bool show = ViewBottomMenu.IsChecked;
        BottomRow.Height = show ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        BottomRow.MinHeight = show ? 120 : 0;
        DiskMap.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRestartElevatedClick(object sender, RoutedEventArgs e)
    {
        if (Elevation.TryRestartElevated(string.Empty, out string error))
        {
            Close();
            return;
        }

        MessageBox.Show(this, error, "磁盘管理器", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            this,
            "磁盘管理器 1.0\n\n" +
            "【版本状态】本 WPF 版已停止更新，主线是 WinUI 3 版（DiskManagerWinUI.exe），\n" +
            "功能相同、外观更现代。本版保留是为了在没有 Windows App Runtime 的机器上也能用。\n\n" +
            "· 枚举：原生磁盘 IOCTL（IOCTL_DISK_GET_DRIVE_LAYOUT_EX 等）\n" +
            "· 脱机/联机：IOCTL_DISK_SET_DISK_ATTRIBUTES\n" +
            "· 分区与格式化：Windows 存储管理接口（Storage WMI）\n\n" +
            "所有写操作都需要管理员权限，执行前请确认磁盘上数据已备份。",
            "关于磁盘管理器",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }
}
