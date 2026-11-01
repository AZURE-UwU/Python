using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DiskManager.Interop;
using DiskManager.Models;

namespace DiskManager.Dialogs;

/// <summary>"初始化磁盘"对话框。</summary>
internal sealed class InitializeDiskDialog : DialogBase
{
    private readonly RadioButton _mbrRadio;
    private readonly RadioButton _gptRadio;

    public InitializeDiskDialog(Window owner, DiskItem disk)
        : base("初始化磁盘", $"磁盘 {disk.Number}（{disk.CapacityText}）尚未初始化。", 470)
    {
        Owner = owner;

        StackPanel panel = new();
        _gptRadio = new RadioButton { Content = "GPT（GUID 分区表）", IsChecked = true };
        _mbrRadio = new RadioButton { Content = "MBR（主引导记录）" };
        panel.Children.Add(_gptRadio);
        panel.Children.Add(_mbrRadio);
        AddFullWidth(panel, 0);

        AddFullWidthNotice(
            "GPT 支持 2 TB 以上容量、最多 128 个分区，是现代系统的推荐选择；" +
            "MBR 兼容性更好但最多 4 个主分区且容量上限 2 TB。");

        BuildDefaultButtons("确定");
    }

    /// <summary>用户选择的分区表类型（确定时拍的快照，后台线程读它安全）。</summary>
    public PartitionStyle SelectedStyle { get; private set; } = PartitionStyle.Gpt;

    protected override bool Validate()
    {
        SelectedStyle = _gptRadio.IsChecked == true ? PartitionStyle.Gpt : PartitionStyle.Mbr;
        return true;
    }
}

/// <summary>"属性"对话框：卷与磁盘信息只读展示。</summary>
internal sealed class PropertiesDialog : DialogBase
{
    private PropertiesDialog(string title, string header, IEnumerable<(string Name, string Value)> general, IEnumerable<(string Name, string Value)> details, Window? owner)
        : base(title, header, 520)
    {
        if (owner is not null)
        {
            Owner = owner;
        }

        TabControl tabs = new() { Height = 320, Margin = new Thickness(0, 8, 0, 0) };
        tabs.Items.Add(BuildTab("常规", general));
        tabs.Items.Add(BuildTab("详细信息", details));
        AddFullWidth(tabs, 4);

        AddButton("确定", isDefault: true, isCancel: true, onClick: () => Close());
    }

    /// <summary>展示卷属性。</summary>
    public static void ShowVolume(Window owner, VolumeItem volume, NtfsVolumeData? ntfs)
    {
        List<(string, string)> general =
        [
            ("卷标", volume.Label.Length > 0 ? volume.Label : "（无）"),
            ("类型", volume.TypeName),
            ("文件系统", volume.FileSystemText.Length > 0 ? volume.FileSystemText : "（未知）"),
            ("状态", volume.StatusText),
            ("容量", volume.CapacityText),
            ("可用空间", volume.FreeSpaceText.Length > 0 ? $"{volume.FreeSpaceText}（{volume.FreePercentText}）" : "（不可用）"),
            ("驱动器号", volume.LetterText.Length > 0 ? volume.LetterText : "（未分配）"),
            ("磁盘", volume.DiskText.Length > 0 ? volume.DiskText : "（未知）"),
            ("容错", volume.FaultToleranceText),
            ("开销", volume.OverheadText),
        ];

        List<(string, string)> details = [("卷设备路径", volume.VolumeName)];
        details.AddRange(volume.MountPoints.Select(p => ("挂载点", p)));

        if (ntfs is { } data)
        {
            details.Add(("卷序列号", $"{data.VolumeSerialNumber:X16}"));
            details.Add(("每扇区字节数", data.BytesPerSector.ToString()));
            details.Add(("每簇字节数", data.BytesPerCluster.ToString()));
            details.Add(("总簇数", data.TotalClusters.ToString()));
            details.Add(("可用簇数", data.FreeClusters.ToString()));
            details.Add(("MFT 起始 LCN", data.MftStartLcn.ToString()));
        }

        PropertiesDialog dialog = new("卷属性", $"卷 {volume.DisplayName}", general, details, owner);
        dialog.ShowDialog();
    }

    /// <summary>展示磁盘属性。</summary>
    public static void ShowDisk(Window owner, DiskItem disk)
    {
        List<(string, string)> general =
        [
            ("磁盘号", disk.NumberText),
            ("设备名称", disk.FriendlyName.Length > 0 ? disk.FriendlyName : "（未知）"),
            ("容量", disk.CapacityText),
            ("状态", disk.StatusText),
            ("分区样式", disk.IsInitialized ? disk.StyleText : "未初始化"),
            ("总线类型", disk.BusTypeName.Length > 0 ? disk.BusTypeName : "（未知）"),
            ("分区数量", disk.Partitions.Count.ToString()),
            ("只读", disk.IsReadOnly ? "是" : "否"),
            ("可移动介质", disk.IsRemovable ? "是" : "否"),
            ("系统/启动磁盘", disk.IsSystemDisk || disk.IsBootDisk ? "是" : "否"),
            ("脱机限制", disk.OfflineBlockReason.Length > 0 ? disk.OfflineBlockReason : "无（允许脱机）"),
            ("弹出", disk.CanEject ? "支持（可安全移除）" : disk.EjectBlockReason),
        ];

        List<(string, string)> details =
        [
            ("每扇区字节数", disk.BytesPerSector.ToString()),
            ("磁盘签名", disk.Style == PartitionStyle.Mbr ? $"0x{disk.MbrSignature:X8}" : "（不适用）"),
            ("磁盘 GUID", disk.Style == PartitionStyle.Gpt ? disk.GptDiskId.ToString() : "（不适用）"),
            ("序列号", disk.Serial.Length > 0 ? disk.Serial : "（未知）"),
        ];

        foreach (PartitionItem partition in disk.Partitions)
        {
            details.Add(($"分区 {partition.Number}", $"{partition.TypeName}　偏移 {SizeFormatter.Format(partition.Offset)}　大小 {SizeFormatter.Format(partition.Length)}"));
        }

        PropertiesDialog dialog = new("磁盘属性", $"{disk.NumberText}　{disk.FriendlyName}", general, details, owner);
        dialog.ShowDialog();
    }

    /// <summary>构造一个只读信息页。</summary>
    private static TabItem BuildTab(string title, IEnumerable<(string Name, string Value)> rows)
    {
        Grid grid = new() { Margin = new Thickness(14) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        int index = 0;
        foreach ((string name, string value) in rows)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock caption = new()
            {
                Text = name,
                Margin = new Thickness(0, 3, 16, 3),
                Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            };

            TextBlock content = new()
            {
                Text = value,
                Margin = new Thickness(0, 3, 0, 3),
                TextWrapping = TextWrapping.Wrap,
            };

            Grid.SetRow(caption, index);
            Grid.SetColumn(caption, 0);
            Grid.SetRow(content, index);
            Grid.SetColumn(content, 1);
            grid.Children.Add(caption);
            grid.Children.Add(content);
            index++;
        }

        ScrollViewer scroll = new() { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        return new TabItem { Header = title, Content = scroll };
    }
}
