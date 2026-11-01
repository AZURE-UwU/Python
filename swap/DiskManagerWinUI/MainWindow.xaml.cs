using System.Collections.ObjectModel;
using DiskManager.Interop;
using DiskManager.Models;
using DiskManager.Services;
using DiskManagerWinUI.Controls;
using DiskManagerWinUI.Dialogs;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI.Core;

namespace DiskManagerWinUI;

/// <summary>
/// 磁盘管理器（WinUI 3 版）：上半区卷列表 + 下半区磁盘图，右键菜单与操作流程
/// 与 WPF 版完全一致，磁盘逻辑共用同一份源码。
/// </summary>
public sealed partial class MainWindow : Window
{
    private const string DiskInterfaceClassGuid = "{53F56307-B6BF-11D0-94F2-00A0C91EFB8B}";

    private readonly ObservableCollection<VolumeItem> _volumes = [];
    private readonly ObservableCollection<DiskItem> _disks = [];

    private DeviceWatcher? _deviceWatcher;
    private DispatcherQueueTimer? _refreshTimer;
    private bool _busy;
    private bool _refreshing;
    private bool _demoMode;

    /// <summary>右键上下文：当前操作对象。</summary>
    private VolumeItem? _contextVolume;
    private DiskItem? _contextDisk;

    /// <summary>拖动分隔条时的状态。</summary>
    private bool _draggingSplitter;
    private double _splitterStartY;
    private GridLength _topRowStartHeight = new(1, GridUnitType.Star);

    public MainWindow()
    {
        InitializeComponent();

        SystemBackdrop = new MicaBackdrop();
        if (Content is FrameworkElement root)
        {
            root.Loaded += (_, _) => ResizeToLogical(1180, 760);
        }

        VolumeList.ItemsSource = _volumes;

        DiskMap.PartitionSelected += OnMapPartitionSelected;
        DiskMap.ContextMenuRequested += OnMapContextMenuRequested;

        _demoMode = App.StartupArguments.Any(a => string.Equals(a, "--demo", StringComparison.OrdinalIgnoreCase));

        UpdatePermissionState();
        StartDeviceWatcher();
        _ = RefreshAsync();
    }

    /// <summary>按逻辑像素设置窗口大小（自动换算高 DPI 缩放）。</summary>
    private void ResizeToLogical(int width, int height)
    {
        double scale = Content?.XamlRoot?.RasterizationScale ?? 1.0;
        AppWindow.Resize(new SizeInt32((int)(width * scale), (int)(height * scale)));
    }

    // ------------------------------------------------------------------
    // 硬件变化自动刷新
    // ------------------------------------------------------------------

    private void StartDeviceWatcher()
    {
        if (_demoMode)
        {
            return;
        }

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
        catch
        {
            // 监听失败不影响主功能：操作后仍会主动刷新
        }
    }

    private void ScheduleRefresh(int delayMs, string? statusText = null)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (statusText is not null && !_busy && !_refreshing)
            {
                StatusText.Text = statusText;
            }

            _refreshTimer ??= CreateRefreshTimer();
            _refreshTimer.Interval = TimeSpan.FromMilliseconds(delayMs);
            _refreshTimer.Stop();
            _refreshTimer.Start();
        });
    }

    private DispatcherQueueTimer CreateRefreshTimer()
    {
        DispatcherQueueTimer timer = DispatcherQueue.CreateTimer();
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();

            // 正在忙就先让路，稍后再刷，避免把这次变化丢掉
            if (_busy || _refreshing)
            {
                ScheduleRefresh(800);
                return;
            }

            _ = RefreshAsync();
        };

        return timer;
    }

    // ------------------------------------------------------------------
    // 刷新
    // ------------------------------------------------------------------

    private async Task RefreshAsync()
    {
        if (_refreshing || _busy)
        {
            return;
        }

        _refreshing = true;
        RefreshButton.IsEnabled = false;
        StatusText.Text = "正在刷新磁盘信息…";
        string? previousVolumePath = (VolumeList.SelectedItem as VolumeItem)?.VolumeName;

        try
        {
            (IReadOnlyList<DiskItem> disks, IReadOnlyList<VolumeItem> volumes, IReadOnlyList<string> warnings) =
                await Task.Run(() =>
                {
                    if (_demoMode)
                    {
                        (List<DiskItem> demoDisks, List<VolumeItem> demoVolumes) = DemoData.Create();
                        return ((IReadOnlyList<DiskItem>)demoDisks, (IReadOnlyList<VolumeItem>)demoVolumes, (IReadOnlyList<string>)[]);
                    }

                    InventorySnapshot snapshot = DiskInventory.Capture();
                    return ((IReadOnlyList<DiskItem>)snapshot.Disks, (IReadOnlyList<VolumeItem>)snapshot.Volumes, (IReadOnlyList<string>)snapshot.Warnings);
                });

            _disks.Clear();
            foreach (DiskItem disk in disks)
            {
                _disks.Add(disk);
            }

            _volumes.Clear();
            foreach (VolumeItem volume in volumes)
            {
                _volumes.Add(volume);
            }

            DiskMap.SetDisks(_disks);

            if (previousVolumePath is not null)
            {
                VolumeItem? match = _volumes.FirstOrDefault(v => v.VolumeName == previousVolumePath);
                if (match is not null)
                {
                    VolumeList.SelectedItem = match;
                }
            }

            string warning = warnings.Count > 0 ? $"　⚠ {warnings[0]}" : string.Empty;
            StatusText.Text = $"磁盘 {_disks.Count} 个，卷 {_volumes.Count} 个{warning}"
                + (_demoMode ? "　（演示数据）" : string.Empty);

            UpdateOperationMenus();
            ResetHorizontalScroll();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"刷新失败：{ex.Message}";
        }
        finally
        {
            _refreshing = false;
            RefreshButton.IsEnabled = true;
        }
    }

    /// <summary>让卷列表横向位置归零（窄窗口下避免一上来停在中间）。</summary>
    private void ResetHorizontalScroll()
    {
        void Reset() => FindScrollViewer(VolumeList)?.ChangeView(0, null, null, true);

        DispatcherQueue.TryEnqueue(Reset);

        DispatcherQueueTimer timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(400);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => Reset();
        timer.Start();
    }

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

    private void UpdatePermissionState()
    {
        bool elevated = Elevation.IsElevated;
        ElevationBar.IsOpen = !elevated;
        PermissionText.Text = elevated ? "管理员权限" : "普通权限（只读）";
    }

    // ------------------------------------------------------------------
    // 选择联动
    // ------------------------------------------------------------------

    private void OnVolumeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VolumeList.SelectedItem is VolumeItem volume)
        {
            PartitionItem? partition = volume.Partitions.FirstOrDefault();
            DiskMap.SelectPartition(partition, partition?.DiskNumber ?? volume.DiskNumbers.FirstOrDefault());
            SelectionText.Text = $"{volume.DisplayName}　{volume.TypeName}　{volume.CapacityText}　{volume.StatusText}";
        }
        else
        {
            DiskMap.SelectPartition(null, null);
            SelectionText.Text = "未选择卷";
        }

        UpdateOperationMenus();
    }

    private void OnMapPartitionSelected(object? sender, PartitionItem? partition)
    {
        if (partition?.Volume is { } volume)
        {
            VolumeList.SelectedItem = volume;
        }
        else
        {
            VolumeList.SelectedItem = null;
        }
    }

    // ------------------------------------------------------------------
    // 菜单
    // ------------------------------------------------------------------

    private void UpdateOperationMenus()
    {
        AllVolumeTasksMenu.Items.Clear();
        foreach (MenuFlyoutItemBase item in BuildVolumeMenuItems(VolumeList.SelectedItem as VolumeItem))
        {
            AllVolumeTasksMenu.Items.Add(item);
        }

        AllDiskTasksMenu.Items.Clear();
        DiskItem? disk = GetSelectedDisk();

        if (disk is null)
        {
            AllDiskTasksMenu.Items.Add(new MenuFlyoutItem { Text = "（请先在下方选择磁盘）", IsEnabled = false });
        }
        else
        {
            foreach (MenuFlyoutItemBase item in BuildDiskMenuItems(disk, null))
            {
                AllDiskTasksMenu.Items.Add(item);
            }
        }
    }

    private DiskItem? GetSelectedDisk()
    {
        if (_contextDisk is not null)
        {
            return _contextDisk;
        }

        if (VolumeList.SelectedItem is VolumeItem volume)
        {
            int number = volume.DiskNumbers.FirstOrDefault(-1);
            return _disks.FirstOrDefault(d => d.Number == number);
        }

        return null;
    }

    private IEnumerable<MenuFlyoutItemBase> BuildVolumeMenuItems(VolumeItem? volume)
    {
        bool elevated = Elevation.IsElevated;

        if (volume is null)
        {
            yield return new MenuFlyoutItem { Text = "（请先选择卷）", IsEnabled = false };
            yield break;
        }

        DiskItem? disk = _disks.FirstOrDefault(d => d.Number == volume.DiskNumbers.FirstOrDefault(-1));

        yield return CreateItem("打开", () => OpenInExplorer(volume), volume.MountPoints.Count > 0);
        yield return CreateItem("更改驱动器号和路径…", async () => await ChangeDriveLetterAsync(volume), elevated);
        yield return CreateItem("格式化…", async () => await FormatVolumeAsync(volume), elevated && volume.MountPoints.Count > 0);
        yield return CreateItem("扩展卷…", async () => await ExtendVolumeAsync(volume, disk), elevated && disk is not null);
        yield return CreateItem("压缩卷…", async () => await ShrinkVolumeAsync(volume), elevated);
        yield return new MenuFlyoutSeparator();
        yield return CreateItem("删除卷…", async () => await DeleteVolumeAsync(volume), elevated);
        yield return new MenuFlyoutSeparator();
        yield return CreateItem("属性", async () => await ShowVolumePropertiesAsync(volume), true);
    }

    private IEnumerable<MenuFlyoutItemBase> BuildDiskMenuItems(DiskItem disk, PartitionItem? partition)
    {
        bool elevated = Elevation.IsElevated;

        if (!disk.IsInitialized)
        {
            yield return CreateItem("初始化磁盘…", async () => await InitializeDiskAsync(disk), elevated);
        }
        else
        {
            yield return CreateItem("新建简单卷…", async () => await CreateVolumeAsync(disk), elevated);
        }

        if (partition?.Volume is { } partitionVolume)
        {
            yield return CreateItem("格式化…", async () => await FormatVolumeAsync(partitionVolume), elevated);
            yield return CreateItem("删除卷…", async () => await DeleteVolumeAsync(partitionVolume), elevated);
        }

        yield return new MenuFlyoutSeparator();
        yield return CreateItem(
            disk.IsOffline ? "联机" : "脱机",
            async () => await ToggleOfflineAsync(disk),
            elevated); // 只警告不拦：可用性不再受安全闸门影响

        yield return CreateItem(
            disk.IsReadOnly ? "清除只读标记" : "标记为只读",
            async () => await ToggleReadOnlyAsync(disk),
            elevated);

        yield return CreateItem(
            disk.EjectMenuText.Replace("(_E)…", "…"),
            async () => await RemoveDiskAsync(disk),
            elevated);

        if (disk.IsInitialized)
        {
            string target = disk.Style == PartitionStyle.Gpt ? "MBR" : "GPT";
            yield return CreateItem($"转换为 {target} 磁盘…", async () => await ConvertStyleAsync(disk), elevated);
        }

        yield return CreateItem("清除磁盘…", async () => await ClearDiskAsync(disk), elevated && disk.IsInitialized);
        yield return new MenuFlyoutSeparator();
        yield return CreateItem("属性", async () => await ShowDiskPropertiesAsync(disk), true);
    }

    private static MenuFlyoutItem CreateItem(string text, Func<Task> action, bool enabled)
    {
        MenuFlyoutItem item = new() { Text = text, IsEnabled = enabled };
        item.Click += async (_, _) => await action();
        return item;
    }

    private static MenuFlyoutItem CreateItem(string text, Action action, bool enabled)
    {
        MenuFlyoutItem item = new() { Text = text, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    // ------------------------------------------------------------------
    // 右键菜单
    // ------------------------------------------------------------------

    private void OnVolumeListRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        // 命中的行：右键先选中，再按对象类型弹菜单
        VolumeItem? target = (e.OriginalSource as FrameworkElement)?.DataContext as VolumeItem
            ?? VolumeList.SelectedItem as VolumeItem;

        if (target is null)
        {
            return;
        }

        VolumeList.SelectedItem = target;
        _contextVolume = target;
        _contextDisk = null;

        MenuFlyout flyout = new();
        foreach (MenuFlyoutItemBase item in BuildVolumeMenuItems(target))
        {
            flyout.Items.Add(item);
        }

        flyout.ShowAt(VolumeList, new FlyoutShowOptions { Position = e.GetPosition(VolumeList) });
        e.Handled = true;
    }

    private void OnMapContextMenuRequested(object? sender, DiskMapContextEventArgs e)
    {
        _contextDisk = e.Disk;
        _contextVolume = e.Partition?.Volume;

        MenuFlyout flyout = new();

        if (_contextVolume is { } volume)
        {
            foreach (MenuFlyoutItemBase item in BuildVolumeMenuItems(volume))
            {
                flyout.Items.Add(item);
            }
        }
        else
        {
            foreach (MenuFlyoutItemBase item in BuildDiskMenuItems(e.Disk, e.Partition))
            {
                flyout.Items.Add(item);
            }
        }

        flyout.ShowAt(DiskMap, new FlyoutShowOptions { Position = e.Position });
    }

    // ------------------------------------------------------------------
    // 各类操作
    // ------------------------------------------------------------------

    private async Task RunOperationAsync(string busyText, Func<OperationResult> operation)
    {
        StatusText.Text = busyText;
        _busy = true;
        RefreshButton.IsEnabled = false;

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
            _busy = false;
            RefreshButton.IsEnabled = true;
        }

        if (!result.Success)
        {
            await DiskDialogs.ShowMessageAsync(Content.XamlRoot, "磁盘管理器", result.Message);
        }

        await RefreshAsync();
        ScheduleRefresh(1500);
    }

    private async Task ToggleOfflineAsync(DiskItem disk)
    {
        bool toOffline = !disk.IsOffline;

        if (toOffline)
        {
            bool confirmed = await DiskDialogs.ConfirmAsync(
                Content.XamlRoot,
                "脱机磁盘",
                $"确定要使磁盘 {disk.Number}（{disk.FriendlyName}）脱机吗？",
                "脱机后该磁盘上的所有卷都会从文件资源管理器中消失，直到重新联机为止。",
                "脱机",
                requireAcknowledge: true);

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
            bool confirmed = await DiskDialogs.ConfirmAsync(
                Content.XamlRoot,
                "标记为只读",
                $"确定要把磁盘 {disk.Number} 标记为只读吗？",
                "标记后该磁盘的所有写入操作都会被系统拒绝。",
                "确定");

            if (!confirmed)
            {
                return;
            }
        }

        await RunOperationAsync(
            toReadOnly ? "正在设置只读标记…" : "正在清除只读标记…",
            () => DiskOperations.SetReadOnly(disk.Number, toReadOnly));
    }

    private async Task RemoveDiskAsync(DiskItem disk)
    {
        string action = disk.EjectActionName;

        if (!disk.CanEject)
        {
            await DiskDialogs.ShowMessageAsync(Content.XamlRoot, $"无法{action}", $"磁盘 {disk.Number} {disk.EjectBlockReason}。");
            return;
        }

        bool confirmed = await DiskDialogs.ConfirmAsync(
            Content.XamlRoot,
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

        BlockResolutionOptions? options = await PrepareBlockOptionsAsync(disk, action);
        if (options is null)
        {
            StatusText.Text = $"已取消{action}";
            return;
        }

        await RunOperationAsync($"正在{action}磁盘 {disk.Number}…", () => DiskOperations.RemoveDisk(disk, options));
    }

    private async Task InitializeDiskAsync(DiskItem disk)
    {
        PartitionStyle? style = await DiskDialogs.InitializeDiskAsync(Content.XamlRoot, disk);
        if (style is null)
        {
            return;
        }

        bool confirmed = await DiskDialogs.ConfirmAsync(
            Content.XamlRoot,
            "初始化磁盘",
            $"确定要初始化磁盘 {disk.Number} 吗？",
            "初始化会写入新的分区表，磁盘上原有的分区信息将被清除，数据将无法恢复。",
            "确定",
            requireAcknowledge: true);

        if (!confirmed)
        {
            return;
        }

        PartitionStyle selected = style.Value;
        await RunOperationAsync($"正在初始化磁盘 {disk.Number}…", () => DiskOperations.Initialize(disk.Number, selected));
    }

    private async Task ConvertStyleAsync(DiskItem disk)
    {
        PartitionStyle target = disk.Style == PartitionStyle.Gpt ? PartitionStyle.Mbr : PartitionStyle.Gpt;
        string targetName = target == PartitionStyle.Gpt ? "GPT" : "MBR";

        bool confirmed = await DiskDialogs.ConfirmAsync(
            Content.XamlRoot,
            $"转换为 {targetName} 磁盘",
            $"确定要把磁盘 {disk.Number} 转换为 {targetName} 分区表吗？",
            "转换分区表需要磁盘上没有已挂载的卷，否则会失败；操作有可能导致分区无法访问。",
            "转换",
            requireAcknowledge: true);

        if (!confirmed)
        {
            return;
        }

        await RunOperationAsync($"正在把磁盘 {disk.Number} 转换为 {targetName}…", () => DiskOperations.ConvertStyle(disk.Number, target));
    }

    private async Task ClearDiskAsync(DiskItem disk)
    {
        bool confirmed = await DiskDialogs.ConfirmAsync(
            Content.XamlRoot,
            "清除磁盘",
            $"确定要清除磁盘 {disk.Number} 上的全部分区吗？",
            "此操作会删除该磁盘上的所有分区与卷，数据无法恢复。",
            "清除磁盘",
            requireAcknowledge: true);

        if (!confirmed)
        {
            return;
        }

        await RunOperationAsync($"正在清除磁盘 {disk.Number}…", () => DiskOperations.ClearDisk(disk.Number, zeroOut: false));
    }

    private async Task CreateVolumeAsync(DiskItem disk)
    {
        long maximum = GetLargestFreeExtent(disk);
        if (maximum <= 0)
        {
            await DiskDialogs.ShowMessageAsync(Content.XamlRoot, "新建简单卷", "该磁盘上没有可用的未分配空间。");
            return;
        }

        NewVolumeOptions? options = await DiskDialogs.NewVolumeAsync(Content.XamlRoot, disk, maximum);
        if (options is null)
        {
            return;
        }

        await RunOperationAsync(
            "正在新建简单卷…",
            () => VolumeOperations.CreateSimpleVolume(
                disk,
                options.Size,
                offset: null,
                options.DriveLetter,
                options.FileSystem,
                options.Label,
                options.QuickFormat,
                allocationUnitSize: 0));
    }

    private async Task DeleteVolumeAsync(VolumeItem volume)
    {
        bool confirmed = await DiskDialogs.ConfirmAsync(
            Content.XamlRoot,
            "删除卷",
            $"确定要删除卷 {volume.DisplayName} 吗？",
            "删除该卷会同时删除卷上的所有数据，且无法恢复。",
            "删除卷",
            requireAcknowledge: true);

        if (!confirmed)
        {
            return;
        }

        await RunOperationAsync("正在删除卷…", () => VolumeOperations.DeleteVolume(volume, forceRemoveMountPoints: true));
    }

    private async Task FormatVolumeAsync(VolumeItem volume)
    {
        FormatOptions? options = await DiskDialogs.FormatVolumeAsync(Content.XamlRoot, volume);
        if (options is null)
        {
            return;
        }

        bool confirmed = await DiskDialogs.ConfirmAsync(
            Content.XamlRoot,
            "格式化",
            $"确定要格式化卷 {volume.DisplayName} 吗？",
            "格式化会清除该卷上的所有数据，且无法撤销。",
            "格式化",
            requireAcknowledge: true);

        if (!confirmed)
        {
            return;
        }

        await RunOperationAsync(
            "正在格式化…",
            () => VolumeOperations.FormatVolume(volume, options.FileSystem, options.Label, options.QuickFormat, options.AllocationUnitSize));
    }

    private async Task ChangeDriveLetterAsync(VolumeItem volume)
    {
        LetterOptions? options = await DiskDialogs.ChangeDriveLetterAsync(Content.XamlRoot, volume);
        if (options is null)
        {
            return;
        }

        await RunOperationAsync(
            "正在更改驱动器号…",
            () => VolumeOperations.ChangeDriveLetter(volume, options.Mode, options.DriveLetter, options.FolderPath));
    }

    private async Task ExtendVolumeAsync(VolumeItem volume, DiskItem? disk)
    {
        if (disk is null)
        {
            await DiskDialogs.ShowMessageAsync(Content.XamlRoot, "扩展卷", "找不到该卷所在的磁盘，请刷新后重试。");
            return;
        }

        long maximum = GetMaximumVolumeSize(volume, disk);
        if (maximum <= volume.Size)
        {
            await DiskDialogs.ShowMessageAsync(
                Content.XamlRoot,
                "扩展卷",
                "该卷后面没有紧邻的未分配空间，无法扩展。\n\n提示：扩展卷只能使用紧跟在分区后面的未分配空间。");
            return;
        }

        long? newSize = await DiskDialogs.ExtendVolumeAsync(Content.XamlRoot, volume, maximum);
        if (newSize is null)
        {
            return;
        }

        await RunOperationAsync("正在扩展卷…", () => VolumeOperations.ExtendVolume(volume, newSize.Value));
    }

    private async Task ShrinkVolumeAsync(VolumeItem volume)
    {
        PartitionItem? partition = volume.Partitions.FirstOrDefault();
        if (partition is null)
        {
            await DiskDialogs.ShowMessageAsync(Content.XamlRoot, "压缩卷", "找不到该卷对应的分区信息，请刷新后重试。");
            return;
        }

        (long minimum, OperationResult supported) = await Task.Run(() =>
        {
            OperationResult result = StorageOperations.GetSupportedSize(partition.DiskNumber, partition.Number, out long min, out _);
            return (min, result);
        });

        if (!supported.Success)
        {
            await DiskDialogs.ShowMessageAsync(Content.XamlRoot, "压缩卷", $"无法读取可压缩空间：{supported.Message}");
            return;
        }

        long? newSize = await DiskDialogs.ShrinkVolumeAsync(Content.XamlRoot, volume, minimum);
        if (newSize is null)
        {
            return;
        }

        await RunOperationAsync("正在压缩卷…", () => VolumeOperations.ShrinkVolume(volume, newSize.Value));
    }

    private async Task ShowVolumePropertiesAsync(VolumeItem volume)
    {
        NtfsVolumeData? ntfs = null;
        if (volume.IsAccessible && string.Equals(volume.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase)
            && VolumeDevice.TryGetNtfsData(volume.VolumeName, out NtfsVolumeData data))
        {
            ntfs = data;
        }

        await DiskDialogs.ShowVolumePropertiesAsync(Content.XamlRoot, volume, ntfs);
    }

    private Task ShowDiskPropertiesAsync(DiskItem disk) => DiskDialogs.ShowDiskPropertiesAsync(Content.XamlRoot, disk);

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

    /// <summary>
    /// 操作前的预检查（与另外三个程序一致）：在界面线程上把"要不要先分离挡路的虚拟磁盘"问清楚，
    /// 并把宿主文件路径准备好。服务层不再回调界面——后台回调界面会造成死锁（整个程序卡死）。
    /// </summary>
    private async Task<BlockResolutionOptions?> PrepareBlockOptionsAsync(DiskItem disk, string action)
    {
        (OfflineGuard.Block? block, bool evaluated) = await RunWithTimeoutAsync(
            () => OfflineGuard.Evaluate(disk),
            TimeSpan.FromSeconds(15));

        if (!evaluated || block is null)
        {
            await DiskDialogs.ShowMessageAsync(
                Content.XamlRoot,
                "检查超时",
                $"检查磁盘状态超时（15 秒）：这块磁盘可能没有响应。\n\n日志：{OperationLog.LogPath}");
            return null;
        }

        if (!block.IsBlocked)
        {
            return new BlockResolutionOptions { Report = message => StatusText.Text = message };
        }

        // 有风险：只警告、不硬拦（用户确认后照样执行，并写入日志备查）
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
            await DiskDialogs.ShowMessageAsync(Content.XamlRoot, "已取消", "没有指定虚拟磁盘的宿主文件。");
            return null;
        }

        return new BlockResolutionOptions
        {
            DetachBlockingVirtualDiskConfirmed = true,
            PreResolvedVirtualDiskFile = file,
            Report = message => StatusText.Text = message,
        };
    }

    /// <summary>把闸门的拦截原因翻译成"警告文案"（只警告不拦，把后果写清楚）。</summary>
    private static (string Title, string Warning) DescribeBlock(OfflineGuard.Block block)
        => block.Kind switch
        {
            OfflineGuard.BlockKind.SystemPartition => (
                "⚠ 该磁盘含系统分区",
                "这块盘上有 Windows 的启动文件。脱机或移除可能导致系统无法启动。"),
            OfflineGuard.BlockKind.BootVolume => (
                "⚠ 该磁盘含启动卷",
                "Windows 目录就在这块盘上。执行后系统会立即崩溃（蓝屏 0x22）。"),
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

    /// <summary>从后台线程安全地在界面线程上取值。</summary>
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
    // 尺寸计算辅助
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

    // ------------------------------------------------------------------
    // 分隔条拖动
    // ------------------------------------------------------------------

    private void OnSplitterPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _draggingSplitter = true;
        _splitterStartY = e.GetCurrentPoint(RootGrid).Position.Y;
        _topRowStartHeight = RootGrid.RowDefinitions[3].Height;

        Splitter.CapturePointer(e.Pointer);
    }

    private void OnSplitterPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingSplitter)
        {
            return;
        }

        double y = e.GetCurrentPoint(RootGrid).Position.Y;
        double delta = y - _splitterStartY;
        double available = Math.Max(240, RootGrid.ActualHeight - 40);

        double topHeight = _topRowStartHeight.IsStar
            ? Math.Clamp(RootGrid.RowDefinitions[3].ActualHeight + delta, 120, available - 120)
            : Math.Clamp(_topRowStartHeight.Value + delta, 120, available - 120);

        RootGrid.RowDefinitions[3].Height = new GridLength(topHeight, GridUnitType.Pixel);
        RootGrid.RowDefinitions[5].Height = new GridLength(Math.Max(120, available - topHeight), GridUnitType.Star);
    }

    private void OnSplitterPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _draggingSplitter = false;
        Splitter.ReleasePointerCapture(e.Pointer);
    }

    // ------------------------------------------------------------------
    // 菜单命令
    // ------------------------------------------------------------------

    private void OnRefreshClick(object sender, RoutedEventArgs e) => _ = RefreshAsync();

    private void OnExitClick(object sender, RoutedEventArgs e) => Close();

    private void OnViewToggleClick(object sender, RoutedEventArgs e)
    {
        bool showTop = ViewTopToggle.IsChecked;
        bool showBottom = ViewBottomToggle.IsChecked;

        RootGrid.RowDefinitions[3].Height = showTop ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        RootGrid.RowDefinitions[3].MinHeight = showTop ? 120 : 0;
        VolumeList.Visibility = showTop ? Visibility.Visible : Visibility.Collapsed;

        RootGrid.RowDefinitions[5].Height = showBottom ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        RootGrid.RowDefinitions[5].MinHeight = showBottom ? 120 : 0;
        DiskMap.Visibility = showBottom ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRestartElevatedClick(object sender, RoutedEventArgs e)
    {
        if (Elevation.TryRestartElevated(string.Empty, out string error))
        {
            Close();
            return;
        }

        _ = DiskDialogs.ShowMessageAsync(Content.XamlRoot, "磁盘管理器", error);
    }

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        _ = DiskDialogs.ShowMessageAsync(
            Content.XamlRoot,
            "关于磁盘管理器",
            "磁盘管理器 1.0（WinUI 3 版）\n\n" +
            "· 枚举：原生磁盘 IOCTL（IOCTL_DISK_GET_DRIVE_LAYOUT_EX 等）\n" +
            "· 脱机/联机：IOCTL_DISK_SET_DISK_ATTRIBUTES\n" +
            "· 分区与格式化：Windows 存储管理接口（Storage WMI）\n" +
            "· 虚拟磁盘：virtdisk.dll 分离接口\n\n" +
            "所有写操作都需要管理员权限，执行前请确认数据已备份。");
    }
}
