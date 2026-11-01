using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DiskManager.Models;

namespace DiskManager.Controls;

/// <summary>磁盘图上的右键请求：命中分区时为分区，命中空白区域时为 null。</summary>
internal sealed class MapContextEventArgs : EventArgs
{
    public required DiskItem Disk { get; init; }

    public PartitionItem? Partition { get; init; }

    /// <summary>鼠标相对屏幕的位置，用于弹出菜单。</summary>
    public Point ScreenPoint { get; init; }
}

/// <summary>
/// 下半区的磁盘图形视图。
/// 每块磁盘一行：左边是磁盘标签栏，右边是按容量比例绘制的分区矩形图。
/// </summary>
internal sealed class DiskMapView : UserControl
{
    private readonly ScrollViewer _scroll;
    private readonly StackPanel _host;
    private readonly List<DiskMapRow> _rows = [];
    private PartitionItem? _selectedPartition;
    private int? _selectedDiskNumber;

    public DiskMapView()
    {
        _host = new StackPanel { Orientation = Orientation.Vertical, Background = Brushes.White };
        _scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,

            // 窗口变窄时允许左右滚动：原来设成 Disabled，结果分区图会被直接裁掉、滚不动。
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _host,
            Background = Brushes.White,
            BorderThickness = new Thickness(0),
        };

        Content = _scroll;
        Background = Brushes.White;
    }

    /// <summary>用户点击了某个分区（null 表示点了空白区域）。</summary>
    public event EventHandler<PartitionItem?>? PartitionSelected;

    /// <summary>用户在磁盘图上点了右键。</summary>
    public event EventHandler<MapContextEventArgs>? ContextMenuRequested;

    /// <summary>用新的磁盘列表重建整个磁盘图。</summary>
    public void SetDisks(IReadOnlyList<DiskItem> disks)
    {
        _host.Children.Clear();
        _rows.Clear();

        foreach (DiskItem disk in disks)
        {
            DiskMapRow row = new(disk);
            row.PartitionClicked += OnRowPartitionClicked;
            row.ContextRequested += OnRowContextRequested;
            row.SelectedPartition = _selectedPartition;
            _rows.Add(row);
            _host.Children.Add(row);
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
        foreach (DiskMapRow row in _rows)
        {
            row.SelectedPartition = _selectedPartition;
            row.IsHighlighted = _selectedDiskNumber == row.Disk.Number;
            row.InvalidateVisual();
        }
    }

    private void OnRowPartitionClicked(object? sender, PartitionItem? partition)
    {
        if (sender is DiskMapRow row)
        {
            _selectedDiskNumber = row.Disk.Number;
        }

        _selectedPartition = partition;
        ApplySelection();
        PartitionSelected?.Invoke(this, partition);
    }

    private void OnRowContextRequested(object? sender, MapContextEventArgs e) => ContextMenuRequested?.Invoke(this, e);
}

/// <summary>磁盘图中的一行：左侧标签 + 右侧分区矩形图。</summary>
internal sealed class DiskMapRow : FrameworkElement
{
    /// <summary>行高与左栏宽度，取接近系统磁盘管理的视觉比例。</summary>
    public const double RowHeight = 74;
    private const double HeaderWidth = 208;

    /// <summary>磁盘图区域的最小可读宽度（窗口更窄时靠横向滚动查看）。</summary>
    private const double MinimumMapWidth = 520;

    private const double Gap = 6;

    private static readonly Brush HeaderBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0xF6, 0xF8));
    private static readonly Brush VolumeFill = new LinearGradientBrush(
        Color.FromRgb(0xEA, 0xF3, 0xFD),
        Color.FromRgb(0xD3, 0xE5, 0xF8),
        new Point(0, 0),
        new Point(0, 1));
    private static readonly Brush SystemPartitionFill = new LinearGradientBrush(
        Color.FromRgb(0xF2, 0xF2, 0xF2),
        Color.FromRgb(0xE2, 0xE2, 0xE2),
        new Point(0, 0),
        new Point(0, 1));
    private static readonly Brush UnallocatedFill = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
    private static readonly Pen BorderPen = new(new SolidColorBrush(Color.FromRgb(0x87, 0xA6, 0xC4)), 1);
    private static readonly Pen SystemBorderPen = new(new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E)), 1);
    private static readonly Pen UnallocatedPen = new(new SolidColorBrush(Color.FromRgb(0xB4, 0xB4, 0xB4)), 1);
    private static readonly Pen SelectedPen = new(new SolidColorBrush(Color.FromRgb(0x1E, 0x6F, 0xD9)), 2);
    private static readonly Pen HighlightPen = new(new SolidColorBrush(Color.FromRgb(0x33, 0x99, 0xFF)), 1)
    {
        DashStyle = DashStyles.Dash,
    };
    private static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
    private static readonly Brush SubTextBrush = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60));

    private readonly List<(Rect Rect, PartitionItem? Partition)> _hitAreas = [];
    private PartitionItem? _selectedPartition;

    public DiskMapRow(DiskItem disk)
    {
        Disk = disk;
        Height = RowHeight;
        SnapsToDevicePixels = true;

        // 给磁盘图一个最小宽度：窗口比它窄时出现横向滚动条，
        // 而不是把分区压扁到看不清或直接裁掉。
        MinWidth = HeaderWidth + MinimumMapWidth;
    }

    public DiskItem Disk { get; }

    /// <summary>当前被选中的分区（由上层同步）。</summary>
    public PartitionItem? SelectedPartition
    {
        get => _selectedPartition;
        set => _selectedPartition = value;
    }

    /// <summary>是否高亮整行（选中的卷所属磁盘）。</summary>
    public bool IsHighlighted { get; set; }

    public event EventHandler<PartitionItem?>? PartitionClicked;

    public event EventHandler<MapContextEventArgs>? ContextRequested;

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 900 : availableSize.Width;
        return new Size(width, RowHeight);
    }

    protected override void OnRender(DrawingContext dc)
    {
        _hitAreas.Clear();

        double width = Math.Max(0, ActualWidth);
        double height = Math.Max(0, RowHeight);

        // ---------- 左栏：磁盘标签 ----------
        Rect headerRect = new(0, 0, HeaderWidth, height);
        dc.DrawRectangle(HeaderBrush, null, headerRect);
        DrawDiskHeader(dc, headerRect);

        // ---------- 右栏：分区图 ----------
        double mapLeft = HeaderWidth + Gap;
        double mapWidth = width - mapLeft - Gap;
        if (mapWidth <= 20)
        {
            return;
        }

        Rect mapRect = new(mapLeft, Gap, mapWidth, height - (Gap * 2));
        dc.DrawRectangle(Brushes.White, new Pen(new SolidColorBrush(Color.FromRgb(0xD0, 0xD7, 0xDE)), 1), mapRect);

        if (IsHighlighted)
        {
            dc.DrawRectangle(null, HighlightPen, new Rect(mapRect.X - 1, mapRect.Y - 1, mapRect.Width + 2, mapRect.Height + 2));
        }

        if (Disk.Size <= 0 || Disk.Partitions.Count == 0)
        {
            DrawCenteredText(dc, mapRect, Disk.Size <= 0 ? "没有介质" : "未初始化", 12, SubTextBrush);
            _hitAreas.Add((mapRect, null));
            return;
        }

        // 按容量比例换算像素宽度；区间按偏移排序后，缝隙即为"未分配"空间。
        List<PartitionItem> ordered = [.. Disk.Partitions.OrderBy(p => p.Offset)];
        double pixelsPerByte = mapRect.Width / Disk.Size;
        long cursor = 0;

        foreach (PartitionItem partition in ordered)
        {
            if (partition.Offset > cursor)
            {
                double gapWidth = (partition.Offset - cursor) * pixelsPerByte;
                Rect gapRect = new(mapRect.X + (cursor * pixelsPerByte), mapRect.Y, gapWidth, mapRect.Height);
                DrawUnallocated(dc, gapRect, partition.Offset - cursor);
                _hitAreas.Add((gapRect, null));
            }

            double left = mapRect.X + (partition.Offset * pixelsPerByte);
            double partitionWidth = Math.Max(1, partition.Length * pixelsPerByte);
            Rect rect = new(left, mapRect.Y, partitionWidth, mapRect.Height);
            DrawPartition(dc, rect, partition);
            _hitAreas.Add((rect, partition));

            cursor = Math.Max(cursor, partition.Offset + partition.Length);
        }

        if (cursor < Disk.Size)
        {
            Rect tailRect = new(mapRect.X + (cursor * pixelsPerByte), mapRect.Y, (Disk.Size - cursor) * pixelsPerByte, mapRect.Height);
            DrawUnallocated(dc, tailRect, Disk.Size - cursor);
            _hitAreas.Add((tailRect, null));
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        PartitionItem? hit = HitTest(e.GetPosition(this));
        PartitionClicked?.Invoke(this, hit);
        e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);

        Point position = e.GetPosition(this);
        PartitionItem? hit = HitTest(position);
        if (hit is not null)
        {
            PartitionClicked?.Invoke(this, hit);
        }

        ContextRequested?.Invoke(this, new MapContextEventArgs
        {
            Disk = Disk,
            Partition = hit,
            ScreenPoint = PointToScreen(position),
        });

        e.Handled = true;
    }

    /// <summary>命中的分区；落在未分配区域返回 null。</summary>
    private PartitionItem? HitTest(Point point)
    {
        foreach ((Rect rect, PartitionItem? partition) in _hitAreas)
        {
            if (rect.Contains(point))
            {
                return partition;
            }
        }

        return null;
    }

    /// <summary>画左栏：磁盘号、设备名、类型容量、状态。</summary>
    private void DrawDiskHeader(DrawingContext dc, Rect rect)
    {
        double x = rect.X + 12;
        double y = rect.Y + 8;

        FormattedText title = CreateText(Disk.NumberText, 13, TextBrush, FontWeights.SemiBold);
        dc.DrawText(title, new Point(x, y));
        y += title.Height + 1;

        string deviceText = string.IsNullOrWhiteSpace(Disk.FriendlyName) ? "未知设备" : Disk.FriendlyName;
        FormattedText device = CreateText(Truncate(deviceText, 22), 10.5, SubTextBrush, FontWeights.Normal);
        dc.DrawText(device, new Point(x, y));
        y += device.Height + 3;

        string typeText = Disk.IsInitialized ? $"基本  {Disk.CapacityText}" : Disk.CapacityText;
        FormattedText type = CreateText(typeText, 11, TextBrush, FontWeights.Normal);
        dc.DrawText(type, new Point(x, y));
        y += type.Height + 1;

        string stateText = Disk.IsInitialized ? $"{Disk.StatusText}  {Disk.StyleText}" : Disk.StatusText;
        FormattedText state = CreateText(stateText, 11, SubTextBrush, FontWeights.Normal);
        dc.DrawText(state, new Point(x, y));
    }

    /// <summary>画一个分区矩形。</summary>
    private void DrawPartition(DrawingContext dc, Rect rect, PartitionItem partition)
    {
        bool isSystemPartition = partition.IsEfiSystem || partition.IsMicrosoftReserved || partition.IsRecovery;
        Brush fill = isSystemPartition ? SystemPartitionFill : VolumeFill;
        Pen border = isSystemPartition ? SystemBorderPen : BorderPen;

        dc.DrawRectangle(fill, border, rect);

        if (ReferenceEquals(partition, _selectedPartition))
        {
            dc.DrawRectangle(null, SelectedPen, new Rect(rect.X + 1, rect.Y + 1, Math.Max(0, rect.Width - 2), Math.Max(0, rect.Height - 2)));
        }

        double width = rect.Width;
        if (width < 26)
        {
            return; // 太窄就不画字，避免糊成一团
        }

        double centerX = rect.X + (width / 2);
        double y = rect.Y + 8;

        FormattedText title = CreateText(Truncate(partition.MapTitle, (int)Math.Max(3, width / 7)), 11.5, TextBrush, FontWeights.SemiBold);
        dc.DrawText(title, new Point(centerX - (title.Width / 2), y));
        y += title.Height + 2;

        string secondLine = partition.Volume is { FileSystem.Length: > 0 } volume
            ? $"{partition.Volume.CapacityText} {volume.FileSystem}"
            : partition.Volume is { } v ? v.CapacityText : Models.SizeFormatter.Format(partition.Length);

        if (width > 70)
        {
            FormattedText size = CreateText(Truncate(secondLine, (int)Math.Max(4, width / 6)), 10, SubTextBrush, FontWeights.Normal);
            dc.DrawText(size, new Point(centerX - (size.Width / 2), y));
            y += size.Height + 1;
        }

        if (width > 110)
        {
            string thirdLine = partition.Volume is not null ? partition.TypeName : partition.TypeName;
            FormattedText type = CreateText(Truncate(thirdLine, (int)Math.Max(4, width / 6)), 10, SubTextBrush, FontWeights.Normal);
            dc.DrawText(type, new Point(centerX - (type.Width / 2), y));
        }
    }

    /// <summary>画未分配空间。</summary>
    private void DrawUnallocated(DrawingContext dc, Rect rect, long length)
    {
        if (rect.Width < 0.5)
        {
            return;
        }

        dc.DrawRectangle(UnallocatedFill, UnallocatedPen, rect);

        if (rect.Width < 50)
        {
            return;
        }

        double centerX = rect.X + (rect.Width / 2);
        FormattedText title = CreateText("未分配", 11, SubTextBrush, FontWeights.Normal);
        dc.DrawText(title, new Point(centerX - (title.Width / 2), rect.Y + 10));

        if (rect.Width > 90)
        {
            FormattedText size = CreateText(Models.SizeFormatter.Format(length), 10, SubTextBrush, FontWeights.Normal);
            dc.DrawText(size, new Point(centerX - (size.Width / 2), rect.Y + 26));
        }
    }

    private void DrawCenteredText(DrawingContext dc, Rect rect, string text, double fontSize, Brush brush)
    {
        FormattedText formatted = CreateText(text, fontSize, brush, FontWeights.Normal);
        dc.DrawText(formatted, new Point(rect.X + ((rect.Width - formatted.Width) / 2), rect.Y + ((rect.Height - formatted.Height) / 2)));
    }

    private FormattedText CreateText(string text, double size, Brush brush, FontWeight weight)
        => new(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, weight, FontStretches.Normal),
            size,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static string Truncate(string text, int maxChars)
        => text.Length <= maxChars ? text : string.Concat(text.AsSpan(0, Math.Max(1, maxChars - 1)), "…");
}
