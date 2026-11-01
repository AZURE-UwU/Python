using DiskManager.Interop;
using DiskManager.Models;
using DiskManager.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DiskManagerWinUI.Dialogs;

/// <summary>"新建简单卷"的输入结果。</summary>
internal sealed record NewVolumeOptions(long Size, char? DriveLetter, string FileSystem, string Label, bool QuickFormat);

/// <summary>"格式化"的输入结果。</summary>
internal sealed record FormatOptions(string FileSystem, string Label, bool QuickFormat, uint AllocationUnitSize);

/// <summary>"更改驱动器号和路径"的输入结果。</summary>
internal sealed record LetterOptions(ChangeLetterMode Mode, char? DriveLetter, string FolderPath);

/// <summary>
/// 所有对话框（ContentDialog 版）。
/// 与 WPF 版规则一致：校验在"确定"时执行，不通过就阻止关闭并给出提示。
/// </summary>
internal static class DiskDialogs
{
    private static readonly string[] FileSystems = ["NTFS", "exFAT", "FAT32", "ReFS"];
    private static readonly string[] AllocationUnits =
        ["默认", "512 字节", "1024 字节", "2048 字节", "4096 字节", "8192 字节", "16384 字节", "32768 字节", "65536 字节"];

    // ------------------------------------------------------------------
    // 基础对话框
    // ------------------------------------------------------------------

    public static async Task ShowMessageAsync(XamlRoot root, string title, string message)
    {
        ContentDialog dialog = new()
        {
            XamlRoot = root,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "确定",
        };

        await dialog.ShowAsync();
    }

    /// <summary>确认框；requireAcknowledge 为真时必须勾选"我已确认"才能继续。</summary>
    public static async Task<bool> ConfirmAsync(
        XamlRoot root,
        string title,
        string message,
        string detail,
        string confirmText,
        bool requireAcknowledge = false)
    {
        StackPanel panel = new() { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });

        if (!string.IsNullOrEmpty(detail))
        {
            panel.Children.Add(new TextBlock
            {
                Text = detail,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 0xA0, 0x30, 0x10)),
            });
        }

        CheckBox? acknowledge = null;
        if (requireAcknowledge)
        {
            acknowledge = new CheckBox { Content = "我已确认上述后果，并了解此操作无法撤销" };
            panel.Children.Add(acknowledge);
        }

        ContentDialog dialog = new()
        {
            XamlRoot = root,
            Title = title,
            Content = panel,
            PrimaryButtonText = confirmText,
            CloseButtonText = "取消",
            DefaultButton = requireAcknowledge ? ContentDialogButton.Close : ContentDialogButton.Primary,
        };

        if (acknowledge is not null)
        {
            dialog.PrimaryButtonClick += (_, e) =>
            {
                if (acknowledge.IsChecked != true)
                {
                    e.Cancel = true;
                    ShowError(panel, "请先勾选确认项。");
                }
            };
        }

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    // ------------------------------------------------------------------
    // 初始化磁盘
    // ------------------------------------------------------------------

    public static async Task<PartitionStyle?> InitializeDiskAsync(XamlRoot root, DiskItem disk)
    {
        RadioButton gpt = new() { Content = "GPT（GUID 分区表）", IsChecked = true };
        RadioButton mbr = new() { Content = "MBR（主引导记录）" };

        StackPanel panel = new() { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = $"磁盘 {disk.Number}（{disk.CapacityText}）尚未初始化。",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(gpt);
        panel.Children.Add(mbr);
        panel.Children.Add(new TextBlock
        {
            Text = "GPT 支持 2 TB 以上容量、最多 128 个分区，是现代系统的推荐选择；MBR 兼容性更好但最多 4 个主分区且容量上限 2 TB。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = SecondaryBrush(),
            FontSize = 12,
        });

        ContentDialog dialog = new()
        {
            XamlRoot = root,
            Title = "初始化磁盘",
            Content = panel,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary
            ? gpt.IsChecked == true ? PartitionStyle.Gpt : PartitionStyle.Mbr
            : null;
    }

    // ------------------------------------------------------------------
    // 新建简单卷
    // ------------------------------------------------------------------

    public static async Task<NewVolumeOptions?> NewVolumeAsync(XamlRoot root, DiskItem disk, long maximumBytes)
    {
        TextBox sizeBox = new() { Text = SizeFormatter.FormatMegabytes(maximumBytes) };
        ComboBox letterBox = new();
        letterBox.Items.Add("不分配驱动器号或驱动器路径");
        foreach (char letter in AvailableLetters(string.Empty))
        {
            letterBox.Items.Add($"{letter}:");
        }

        letterBox.SelectedIndex = letterBox.Items.Count > 1 ? 1 : 0;

        ComboBox fileSystemBox = new();
        foreach (string fs in FileSystems)
        {
            fileSystemBox.Items.Add(fs);
        }

        fileSystemBox.SelectedIndex = 0;

        TextBox labelBox = new() { Text = "新加卷" };
        CheckBox quickFormat = new() { Content = "执行快速格式化", IsChecked = true };

        StackPanel panel = new() { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = $"将在磁盘 {disk.Number} 上创建新的简单卷，最大可用空间约 {SizeFormatter.Format(maximumBytes)}。",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(Field("卷大小(MB)：", sizeBox));
        panel.Children.Add(Field("驱动器号：", letterBox));
        panel.Children.Add(Field("文件系统：", fileSystemBox));
        panel.Children.Add(Field("卷标：", labelBox));
        panel.Children.Add(quickFormat);
        panel.Children.Add(new TextBlock
        {
            Text = "格式化会清除该分区上的全部数据，请确认后再继续。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = SecondaryBrush(),
            FontSize = 12,
        });

        NewVolumeOptions? result = null;
        ContentDialog dialog = new()
        {
            XamlRoot = root,
            Title = "新建简单卷",
            Content = panel,
            PrimaryButtonText = "下一步",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        dialog.PrimaryButtonClick += (_, e) =>
        {
            if (!long.TryParse(sizeBox.Text.Trim(), out long megabytes) || megabytes <= 0)
            {
                e.Cancel = true;
                ShowError(panel, "卷大小必须是大于 0 的整数（单位 MB）。");
                return;
            }

            long bytes = SizeFormatter.FromMegabytes(megabytes);
            if (bytes > maximumBytes)
            {
                e.Cancel = true;
                ShowError(panel, $"卷大小超过了最大可用空间（{SizeFormatter.Format(maximumBytes)}）。");
                return;
            }

            string fileSystem = fileSystemBox.SelectedItem as string ?? "NTFS";
            if (fileSystem == "FAT32" && bytes > 32L * 1024 * 1024 * 1024)
            {
                e.Cancel = true;
                ShowError(panel, "FAT32 卷最大支持 32 GB，请改用 NTFS 或 exFAT。");
                return;
            }

            if (fileSystem == "ReFS" && bytes < 2L * 1024 * 1024 * 1024)
            {
                e.Cancel = true;
                ShowError(panel, "ReFS 卷最小为 2 GB，且仅专业工作站版支持。");
                return;
            }

            string selection = letterBox.SelectedItem as string ?? string.Empty;
            char? letter = selection.Length >= 2 && selection[1] == ':' ? selection[0] : null;
            result = new NewVolumeOptions(bytes, letter, fileSystem, labelBox.Text.Trim(), quickFormat.IsChecked == true);
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary ? result : null;
    }

    // ------------------------------------------------------------------
    // 格式化
    // ------------------------------------------------------------------

    public static async Task<FormatOptions?> FormatVolumeAsync(XamlRoot root, VolumeItem volume)
    {
        TextBox labelBox = new() { Text = volume.Label };

        ComboBox fileSystemBox = new();
        foreach (string fs in FileSystems)
        {
            fileSystemBox.Items.Add(fs);
        }

        fileSystemBox.SelectedItem = volume.FileSystem.Length > 0 ? volume.FileSystem : "NTFS";

        ComboBox unitBox = new();
        foreach (string unit in AllocationUnits)
        {
            unitBox.Items.Add(unit);
        }

        unitBox.SelectedIndex = 0;
        CheckBox quickFormat = new() { Content = "执行快速格式化", IsChecked = true };

        StackPanel panel = new() { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = $"卷 {volume.DisplayName}（{volume.CapacityText}）",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(Field("卷标：", labelBox));
        panel.Children.Add(Field("文件系统：", fileSystemBox));
        panel.Children.Add(Field("分配单元大小：", unitBox));
        panel.Children.Add(quickFormat);

        FormatOptions? result = null;
        ContentDialog dialog = new()
        {
            XamlRoot = root,
            Title = "格式化",
            Content = panel,
            PrimaryButtonText = "开始",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        dialog.PrimaryButtonClick += (_, e) =>
        {
            string unit = unitBox.SelectedItem as string ?? "默认";
            uint allocationUnit = unit == "默认" ? 0 : uint.Parse(unit.Split(' ')[0]);

            result = new FormatOptions(
                fileSystemBox.SelectedItem as string ?? "NTFS",
                labelBox.Text.Trim(),
                quickFormat.IsChecked == true,
                allocationUnit);
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary ? result : null;
    }

    // ------------------------------------------------------------------
    // 更改驱动器号和路径
    // ------------------------------------------------------------------

    public static async Task<LetterOptions?> ChangeDriveLetterAsync(XamlRoot root, VolumeItem volume)
    {
        RadioButton assignRadio = new() { Content = "分配以下驱动器号：", IsChecked = true };
        RadioButton removeRadio = new() { Content = "删除驱动器号（该卷将不再占用盘符）" };
        RadioButton folderRadio = new() { Content = "装入以下空白 NTFS 文件夹中：" };

        ComboBox letterBox = new();
        foreach (char letter in AvailableLetters(volume.LetterText))
        {
            letterBox.Items.Add($"{letter}:");
        }

        if (letterBox.Items.Count > 0)
        {
            letterBox.SelectedIndex = 0;
        }

        TextBox folderBox = new() { Text = @"C:\Mount\卷" };

        StackPanel panel = new() { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = $"卷 {volume.DisplayName}", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(assignRadio);
        panel.Children.Add(Field("驱动器号：", letterBox));
        panel.Children.Add(removeRadio);
        panel.Children.Add(folderRadio);
        panel.Children.Add(Field("文件夹：", folderBox));
        panel.Children.Add(new TextBlock
        {
            Text = "目标文件夹必须存在且为空，所在卷必须是 NTFS。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = SecondaryBrush(),
            FontSize = 12,
        });

        LetterOptions? result = null;
        ContentDialog dialog = new()
        {
            XamlRoot = root,
            Title = "更改驱动器号和路径",
            Content = panel,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        dialog.PrimaryButtonClick += (_, e) =>
        {
            if (removeRadio.IsChecked == true)
            {
                result = new LetterOptions(ChangeLetterMode.Remove, null, string.Empty);
                return;
            }

            if (folderRadio.IsChecked == true)
            {
                string path = folderBox.Text.Trim();
                if (path.Length < 3 || path[1] != ':' || !path.Contains('\\'))
                {
                    e.Cancel = true;
                    ShowError(panel, @"请输入完整的文件夹路径，例如 C:\Mount\卷。");
                    return;
                }

                result = new LetterOptions(ChangeLetterMode.MountFolder, null, path);
                return;
            }

            string selection = letterBox.SelectedItem as string ?? string.Empty;
            if (selection.Length < 2)
            {
                e.Cancel = true;
                ShowError(panel, "请选择一个驱动器号。");
                return;
            }

            result = new LetterOptions(ChangeLetterMode.Assign, selection[0], string.Empty);
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary ? result : null;
    }

    // ------------------------------------------------------------------
    // 扩展 / 压缩
    // ------------------------------------------------------------------

    public static async Task<long?> ExtendVolumeAsync(XamlRoot root, VolumeItem volume, long maximumSize)
    {
        long shrinkable = Math.Max(0, maximumSize - volume.Size);
        TextBox sizeBox = new() { Text = SizeFormatter.FormatMegabytes(shrinkable) };

        StackPanel panel = new() { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = $"当前大小：{SizeFormatter.Format(volume.Size)}　　最大可扩展至：{SizeFormatter.Format(maximumSize)}",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(Field("选择要扩展的空间量(MB)：", sizeBox));

        long? result = null;
        ContentDialog dialog = new()
        {
            XamlRoot = root,
            Title = "扩展卷",
            Content = panel,
            PrimaryButtonText = "扩展",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        dialog.PrimaryButtonClick += (_, e) =>
        {
            if (!long.TryParse(sizeBox.Text.Trim(), out long megabytes) || megabytes <= 0)
            {
                e.Cancel = true;
                ShowError(panel, "请输入大于 0 的整数（单位 MB）。");
                return;
            }

            long newSize = volume.Size + SizeFormatter.FromMegabytes(megabytes);
            if (newSize > maximumSize)
            {
                e.Cancel = true;
                ShowError(panel, $"扩展后的大小会超过可用的最大空间（{SizeFormatter.Format(maximumSize)}）。");
                return;
            }

            result = newSize;
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary ? result : null;
    }

    public static async Task<long?> ShrinkVolumeAsync(XamlRoot root, VolumeItem volume, long minimumSize)
    {
        long shrinkable = Math.Max(0, volume.Size - minimumSize);
        TextBox sizeBox = new() { Text = SizeFormatter.FormatMegabytes(shrinkable / 2) };

        StackPanel panel = new() { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = $"压缩前总大小：{SizeFormatter.Format(volume.Size)}　　可用压缩空间：{SizeFormatter.Format(shrinkable)}",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(Field("输入要压缩的空间量(MB)：", sizeBox));

        long? result = null;
        ContentDialog dialog = new()
        {
            XamlRoot = root,
            Title = "压缩卷",
            Content = panel,
            PrimaryButtonText = "压缩",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        dialog.PrimaryButtonClick += (_, e) =>
        {
            if (!long.TryParse(sizeBox.Text.Trim(), out long megabytes) || megabytes <= 0)
            {
                e.Cancel = true;
                ShowError(panel, "请输入大于 0 的整数（单位 MB）。");
                return;
            }

            long newSize = volume.Size - SizeFormatter.FromMegabytes(megabytes);
            if (newSize < minimumSize)
            {
                e.Cancel = true;
                ShowError(panel, $"最多只能压缩到 {SizeFormatter.Format(minimumSize)}（受不可移动文件限制）。");
                return;
            }

            result = newSize;
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary ? result : null;
    }

    // ------------------------------------------------------------------
    // 属性
    // ------------------------------------------------------------------

    public static async Task ShowVolumePropertiesAsync(XamlRoot root, VolumeItem volume, NtfsVolumeData? ntfs)
    {
        List<(string Name, string Value)> general =
        [
            ("卷标", volume.Label.Length > 0 ? volume.Label : "（无）"),
            ("类型", volume.TypeName),
            ("文件系统", volume.FileSystemText.Length > 0 ? volume.FileSystemText : "（未知）"),
            ("状态", volume.StatusText),
            ("容量", volume.CapacityText),
            ("可用空间", volume.FreeSpaceText.Length > 0 ? $"{volume.FreeSpaceText}（{volume.FreePercentText}）" : "（不可用）"),
            ("驱动器号", volume.LetterText.Length > 0 ? volume.LetterText : "（未分配）"),
            ("磁盘", volume.DiskText.Length > 0 ? volume.DiskText : "（未知）"),
        ];

        List<(string Name, string Value)> details = [("卷设备路径", volume.VolumeName)];
        details.AddRange(volume.MountPoints.Select(p => ("挂载点", p)));

        if (ntfs is { } data)
        {
            details.Add(("卷序列号", $"{data.VolumeSerialNumber:X16}"));
            details.Add(("每扇区字节数", data.BytesPerSector.ToString()));
            details.Add(("每簇字节数", data.BytesPerCluster.ToString()));
            details.Add(("总簇数", data.TotalClusters.ToString()));
            details.Add(("可用簇数", data.FreeClusters.ToString()));
        }

        await ShowPropertyDialogAsync(root, "卷属性", $"卷 {volume.DisplayName}", general, details);
    }

    public static async Task ShowDiskPropertiesAsync(XamlRoot root, DiskItem disk)
    {
        List<(string Name, string Value)> general =
        [
            ("磁盘号", disk.NumberText),
            ("设备名称", disk.FriendlyName.Length > 0 ? disk.FriendlyName : "（未知）"),
            ("容量", disk.CapacityText),
            ("状态", disk.StatusText),
            ("分区样式", disk.IsInitialized ? disk.StyleText : "未初始化"),
            ("总线类型", disk.BusTypeName.Length > 0 ? disk.BusTypeName : "（未知）"),
            ("分区数量", disk.Partitions.Count.ToString()),
            ("只读", disk.IsReadOnly ? "是" : "否"),
            ("脱机限制", disk.OfflineBlockReason.Length > 0 ? disk.OfflineBlockReason : "无（允许脱机）"),
            ("移除方式", disk.CanEject ? disk.EjectActionName : disk.EjectBlockReason),
        ];

        List<(string Name, string Value)> details =
        [
            ("每扇区字节数", disk.BytesPerSector.ToString()),
            ("磁盘签名", disk.Style == PartitionStyle.Mbr ? $"0x{disk.MbrSignature:X8}" : "（不适用）"),
            ("磁盘 GUID", disk.Style == PartitionStyle.Gpt ? disk.GptDiskId.ToString() : "（不适用）"),
            ("序列号", disk.Serial.Length > 0 ? disk.Serial : "（未知）"),
        ];

        foreach (PartitionItem partition in disk.Partitions)
        {
            details.Add(($"分区 {partition.Number}",
                $"{partition.TypeName}　偏移 {SizeFormatter.Format(partition.Offset)}　大小 {SizeFormatter.Format(partition.Length)}"));
        }

        await ShowPropertyDialogAsync(root, "磁盘属性", $"{disk.NumberText}　{disk.FriendlyName}", general, details);
    }

    private static async Task ShowPropertyDialogAsync(
        XamlRoot root,
        string title,
        string header,
        IEnumerable<(string Name, string Value)> general,
        IEnumerable<(string Name, string Value)> details)
    {
        StackPanel body = new() { Spacing = 8 };
        body.Children.Add(new TextBlock { Text = header, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        body.Children.Add(BuildSection("常规", general));
        body.Children.Add(BuildSection("详细信息", details));

        ContentDialog dialog = new()
        {
            XamlRoot = root,
            Title = title,
            Content = new ScrollViewer { Content = body, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            CloseButtonText = "确定",
        };

        await dialog.ShowAsync();
    }

    private static UIElement BuildSection(string title, IEnumerable<(string Name, string Value)> rows)
    {
        StackPanel section = new() { Spacing = 2 };
        section.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 6, 0, 2),
        });

        foreach ((string name, string value) in rows)
        {
            Grid row = new() { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            row.Children.Add(new TextBlock
            {
                Text = name,
                FontSize = 12,
                Foreground = SecondaryBrush(),
            });

            TextBlock valueText = new()
            {
                Text = value,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            };

            Grid.SetColumn(valueText, 1);
            row.Children.Add(valueText);
            section.Children.Add(row);
        }

        return section;
    }

    // ------------------------------------------------------------------
    // 小工具
    // ------------------------------------------------------------------

    private static SolidColorBrush SecondaryBrush() => new(Color.FromArgb(255, 0x77, 0x77, 0x77));

    /// <summary>"标签 + 控件"的一行。</summary>
    private static UIElement Field(string label, FrameworkElement control)
    {
        Grid row = new() { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        row.Children.Add(new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13,
        });

        Grid.SetColumn(control, 1);
        row.Children.Add(control);
        return row;
    }

    /// <summary>把校验错误追加到面板里（只保留一条，重复点击不会堆叠）。</summary>
    private static void ShowError(Panel panel, string message)
    {
        TextBlock error = new()
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 0xC4, 0x2B, 0x1C)),
            FontSize = 12,
        };

        panel.Children.Add(error);
    }

    /// <summary>取当前空闲盘符（优先靠后，避免占用常用盘符）。</summary>
    private static IEnumerable<char> AvailableLetters(string current)
    {
        HashSet<char> used = VolumeDevice.GetUsedDriveLetters();
        foreach (char letter in current.Where(char.IsLetter))
        {
            used.Remove(letter);
        }

        for (char letter = 'Y'; letter >= 'D'; letter--)
        {
            if (!used.Contains(letter))
            {
                yield return letter;
            }
        }
    }
}
