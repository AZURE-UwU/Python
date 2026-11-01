using DiskManager.Models;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace DiskManagerWinUI.Controls;

/// <summary>磁盘图上的右键请求。</summary>
public sealed class DiskMapContextEventArgs : EventArgs
{
    public required DiskItem Disk { get; init; }

    /// <summary>命中的分区；点在未分配区域时为 null（此时应显示磁盘菜单）。</summary>
    public PartitionItem? Partition { get; init; }

    /// <summary>相对窗口的位置，用于弹出菜单。</summary>
    public Point Position { get; init; }
}

/// <summary>
/// 磁盘图形视图（WinUI 版）。
/// 与 WPF 版视觉一致：左侧磁盘信息栏，右侧按容量比例绘制分区与未分配空间。
/// </summary>
public sealed partial class DiskMapView : UserControl
{
    private const double RowHeight = 74;
    private const double HeaderWidth = 208;

    /// <summary>磁盘图最小可读宽度：窗口更窄时出现横向滚动，而不是把分区压扁。</summary>
    private const double MinimumMapWidth = 520;

    private readonly List<DiskRow> _rows = [];
    private PartitionItem? _selectedPartition;
    private int? _selectedDiskNumber;

    public DiskMapView()
    {
        InitializeComponent();
    }

    /// <summary>用户点击了某个分区（null 表示点了未分配区域）。</summary>
    public event EventHandler<PartitionItem?>? PartitionSelected;

    /// <summary>用户在磁盘图上点了右键。</summary>
    public event EventHandler<DiskMapContextEventArgs>? ContextMenuRequested;

    /// <summary>用新的磁盘列表重建磁盘图。</summary>
    public void SetDisks(IReadOnlyList<DiskItem> disks)
    {
        Rows.Children.Clear();
        _rows.Clear();

        foreach (DiskItem disk in disks)
        {
            DiskRow row = new(
                disk,
                partition => RaiseSelection(disk, partition),
                (partition, position) => RaiseContext(disk, partition, position));

            _rows.Add(row);
            Rows.Children.Add(row.Container);
        }

        ApplySelection();
    }

    /// <summary>由上方卷列表反选磁盘图。</summary>
    public void SelectPartition(PartitionItem? partition, int? diskNumber)
    {
        _selectedPartition = partition;
        _selectedDiskNumber = diskNumber;
        ApplySelection();
    }

    private void ApplySelection()
    {
        foreach (DiskRow row in _rows)
        {
            row.ApplySelection(_selectedPartition, _selectedDiskNumber == row.Disk.Number);
        }
    }

    private void RaiseSelection(DiskItem disk, PartitionItem? partition)
    {
        _selectedDiskNumber = disk.Number;
        _selectedPartition = partition;
        ApplySelection();
        PartitionSelected?.Invoke(this, partition);
    }

    private void RaiseContext(DiskItem disk, PartitionItem? partition, Point position)
    {
        if (partition is not null)
        {
            _selectedDiskNumber = disk.Number;
            _selectedPartition = partition;
            ApplySelection();
        }

        ContextMenuRequested?.Invoke(this, new DiskMapContextEventArgs
        {
            Disk = disk,
            Partition = partition,
            Position = position,
        });
    }

    /// <summary>一块磁盘对应的一行。</summary>
    private sealed class DiskRow
    {
        private readonly Dictionary<PartitionItem, Border> _blocks = [];
        private readonly Border _mapBorder;
        private readonly Canvas _canvas;

        public DiskRow(DiskItem disk, Action<PartitionItem?> onSelected, Action<PartitionItem?, Point> onContext)
        {
            Disk = disk;
            _onSelected = onSelected;
            _onContext = onContext;

            Container = new Grid
            {
                Height = RowHeight,
                MinWidth = HeaderWidth + MinimumMapWidth,
                ColumnSpacing = 6,
            };

            Container.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(HeaderWidth) });
            Container.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Container.Children.Add(BuildHeader(disk));

            _canvas = new Canvas();
            _mapBorder = new Border
            {
                Margin = new Thickness(0, 6, 6, 6),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 0xD0, 0xD7, 0xDE)),
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Color.FromArgb(255, 0xFF, 0xFF, 0xFF)),
                Child = _canvas,
            };

            _mapBorder.RightTapped += (_, e) => onContext(null, e.GetPosition(Container));

            Grid.SetColumn(_mapBorder, 1);
            Container.Children.Add(_mapBorder);

            _canvas.SizeChanged += (_, _) => Layout();
            _mapBorder.SizeChanged += (_, _) => Layout();
            Layout();
        }

        private readonly Action<PartitionItem?> _onSelected;
        private readonly Action<PartitionItem?, Point> _onContext;

        public DiskItem Disk { get; }

        public Grid Container { get; }

        /// <summary>按容量比例重排分区块。</summary>
        public void Layout()
        {
            _canvas.Children.Clear();
            _blocks.Clear();

            double width = _mapBorder.ActualWidth - 2;
            double height = _mapBorder.ActualHeight - 2;
            if (width <= 20 || height <= 10)
            {
                return;
            }

            if (Disk.Size <= 0 || Disk.Partitions.Count == 0)
            {
                _canvas.Children.Add(BuildBlock(
                    0,
                    width,
                    height,
                    Disk.Size <= 0 ? "没有介质" : "未初始化",
                    null,
                    null,
                    plain: true));
                return;
            }

            double pixelsPerByte = width / Disk.Size;
            long cursor = 0;

            foreach (PartitionItem partition in Disk.Partitions.OrderBy(p => p.Offset))
            {
                if (partition.Offset > cursor)
                {
                    AddUnallocated(cursor, partition.Offset - cursor, pixelsPerByte, height);
                }

                double left = partition.Offset * pixelsPerByte;
                double blockWidth = Math.Max(2, partition.Length * pixelsPerByte);
                AddPartition(partition, left, blockWidth, height);

                cursor = Math.Max(cursor, partition.Offset + partition.Length);
            }

            if (cursor < Disk.Size)
            {
                AddUnallocated(cursor, Disk.Size - cursor, pixelsPerByte, height);
            }
        }

        /// <summary>更新选中态与高亮。</summary>
        public void ApplySelection(PartitionItem? selected, bool diskHighlighted)
        {
            foreach ((PartitionItem partition, Border border) in _blocks)
            {
                bool isSelected = ReferenceEquals(partition, selected);
                border.BorderBrush = new SolidColorBrush(isSelected
                    ? Color.FromArgb(255, 0x1E, 0x6F, 0xD9)
                    : Color.FromArgb(255, 0x87, 0xA6, 0xC4));
                border.BorderThickness = new Thickness(isSelected ? 2 : 1);
            }

            _mapBorder.BorderBrush = new SolidColorBrush(diskHighlighted
                ? Color.FromArgb(255, 0x33, 0x99, 0xFF)
                : Color.FromArgb(255, 0xD0, 0xD7, 0xDE));
            _mapBorder.BorderThickness = new Thickness(diskHighlighted ? 2 : 1);
        }

        private void AddUnallocated(long offset, long length, double pixelsPerByte, double height)
        {
            double left = offset * pixelsPerByte;
            double blockWidth = length * pixelsPerByte;
            if (blockWidth < 0.5)
            {
                return;
            }

            string? text = blockWidth > 90
                ? $"未分配\n{SizeFormatter.Format(length)}"
                : blockWidth > 50 ? "未分配" : null;

            Border border = BuildBlock(left, blockWidth, height, text, null, null, plain: true);
            border.Tapped += (_, _) => _onSelected(null);
            border.RightTapped += (_, e) => _onContext(null, e.GetPosition(Container));
            _canvas.Children.Add(border);
        }

        private void AddPartition(PartitionItem partition, double left, double width, double height)
        {
            bool systemPartition = partition.IsEfiSystem || partition.IsMicrosoftReserved || partition.IsRecovery;

            string? title = width > 26 ? partition.MapTitle : null;
            string? second = width > 70
                ? partition.Volume is { FileSystem.Length: > 0 } volume
                    ? $"{volume.CapacityText} {volume.FileSystem}"
                    : partition.Volume?.CapacityText ?? SizeFormatter.Format(partition.Length)
                : null;

            Border border = BuildBlock(left, width, height, title, second, systemPartition ? null : VolumeGradient(), plain: systemPartition);

            border.Tapped += (_, _) => _onSelected(partition);
            border.RightTapped += (_, e) => _onContext(partition, e.GetPosition(Container));

            _canvas.Children.Add(border);
            _blocks[partition] = border;
        }

        private static LinearGradientBrush VolumeGradient() => new()
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop { Color = Color.FromArgb(255, 0xEA, 0xF3, 0xFD), Offset = 0 },
                new GradientStop { Color = Color.FromArgb(255, 0xD3, 0xE5, 0xF8), Offset = 1 },
            },
        };

        /// <summary>生成一个分区块（可选主/次两行文字）。</summary>
        private Border BuildBlock(
            double left,
            double width,
            double height,
            string? title,
            string? second,
            Brush? background,
            bool plain)
        {
            Border border = new()
            {
                Width = Math.Max(1, width),
                Height = height,
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 0x87, 0xA6, 0xC4)),
                CornerRadius = new CornerRadius(3),
                Background = background ?? new SolidColorBrush(Color.FromArgb(255, 0xF2, 0xF2, 0xF2)),
            };

            if (!string.IsNullOrEmpty(title))
            {
                StackPanel text = new()
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Padding = new Thickness(2),
                };

                text.Children.Add(new TextBlock
                {
                    Text = title,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 0x20, 0x20, 0x20)),
                });

                if (!string.IsNullOrEmpty(second))
                {
                    text.Children.Add(new TextBlock
                    {
                        Text = second,
                        FontSize = 11,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Foreground = new SolidColorBrush(Color.FromArgb(255, 0x60, 0x60, 0x60)),
                    });
                }

                border.Child = text;
            }

            if (plain)
            {
                border.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 0xB4, 0xB4, 0xB4));
            }

            Canvas.SetLeft(border, left);
            Canvas.SetTop(border, 0);
            return border;
        }

        /// <summary>左侧磁盘信息栏。</summary>
        private static UIElement BuildHeader(DiskItem disk)
        {
            StackPanel panel = new() { Padding = new Thickness(12, 8, 8, 8), Spacing = 1 };

            panel.Children.Add(new TextBlock
            {
                Text = disk.NumberText,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
            });

            panel.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(disk.FriendlyName) ? "未知设备" : disk.FriendlyName,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 0x60, 0x60, 0x60)),
            });

            panel.Children.Add(new TextBlock
            {
                Text = disk.IsInitialized ? $"基本  {disk.CapacityText}" : disk.CapacityText,
                FontSize = 12,
            });

            panel.Children.Add(new TextBlock
            {
                Text = disk.IsInitialized ? $"{disk.StatusText}  {disk.StyleText}" : disk.StatusText,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 0x60, 0x60, 0x60)),
            });

            return panel;
        }
    }
}
