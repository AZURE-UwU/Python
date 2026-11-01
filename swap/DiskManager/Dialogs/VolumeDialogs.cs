using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using DiskManager.Interop;
using DiskManager.Models;
using DiskManager.Services;

namespace DiskManager.Dialogs;

/// <summary>"新建简单卷"向导（压缩成单窗口，字段与系统向导一致）。</summary>
internal sealed class NewVolumeDialog : DialogBase
{
    private readonly TextBox _sizeBox;
    private readonly ComboBox _letterBox;
    private readonly ComboBox _fileSystemBox;
    private readonly TextBox _labelBox;
    private readonly CheckBox _quickFormat;
    private readonly long _maximumBytes;

    public NewVolumeDialog(Window owner, DiskItem disk, long maximumBytes)
        : base("新建简单卷", $"将在磁盘 {disk.Number} 上创建新的简单卷。", 480)
    {
        Owner = owner;
        _maximumBytes = Math.Max(0, maximumBytes);

        _sizeBox = new TextBox { Text = SizeFormatter.FormatMegabytes(_maximumBytes) };
        AddField("卷大小(MB)：", _sizeBox, $"该磁盘最大可用空间约 {SizeFormatter.Format(_maximumBytes)}。");

        _letterBox = new ComboBox();
        _letterBox.Items.Add("不分配驱动器号或驱动器路径");
        foreach (char letter in AvailableLetters())
        {
            _letterBox.Items.Add($"{letter}:");
        }

        _letterBox.SelectedIndex = _letterBox.Items.Count > 1 ? 1 : 0;
        AddField("驱动器号：", _letterBox);

        _fileSystemBox = new ComboBox();
        foreach (string fs in new[] { "NTFS", "exFAT", "FAT32", "ReFS" })
        {
            _fileSystemBox.Items.Add(fs);
        }

        _fileSystemBox.SelectedIndex = 0;
        AddField("文件系统：", _fileSystemBox);

        _labelBox = new TextBox { Text = "新加卷" };
        AddField("卷标：", _labelBox);

        _quickFormat = new CheckBox { Content = "执行快速格式化", IsChecked = true };
        AddFullWidth(_quickFormat);

        AddFullWidthNotice("格式化会清除该分区上的全部数据，请确认后再继续。");
        BuildDefaultButtons("下一步");
    }

    /*
     * 下面这些属性全部是"确定时拍下来的快照"，读取时不会碰到任何控件。
     * 原因：写操作是在后台线程执行的，如果在后台线程里读 TextBox.Text / CheckBox.IsChecked，
     * WPF 会抛"调用线程无法访问此对象，因为另一个线程拥有该对象"。
     */

    /// <summary>用户选择的卷大小（字节）。</summary>
    public long RequestedSize { get; private set; }

    /// <summary>要分配的盘符；null 表示不分配。</summary>
    public char? DriveLetter { get; private set; }

    public string FileSystem { get; private set; } = "NTFS";

    public string VolumeLabel { get; private set; } = string.Empty;

    public bool QuickFormat { get; private set; } = true;

    /// <summary>取当前空闲的盘符。</summary>
    private static IEnumerable<char> AvailableLetters()
    {
        HashSet<char> used = VolumeDevice.GetUsedDriveLetters();
        for (char letter = 'D'; letter <= 'Z'; letter++)
        {
            if (!used.Contains(letter))
            {
                yield return letter;
            }
        }

        for (char letter = 'A'; letter <= 'C'; letter++)
        {
            if (!used.Contains(letter))
            {
                yield return letter;
            }
        }
    }

    protected override bool Validate()
    {
        // 所有控件读取都发生在界面线程（点击"下一步"时），读到的值立刻存成快照。
        FileSystem = _fileSystemBox.SelectedItem as string ?? "NTFS";
        VolumeLabel = _labelBox.Text.Trim();
        QuickFormat = _quickFormat.IsChecked == true;

        if (!long.TryParse(_sizeBox.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out long megabytes) || megabytes <= 0)
        {
            MessageBox.Show(this, "卷大小必须是大于 0 的整数（单位 MB）。", "新建简单卷", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        long bytes = SizeFormatter.FromMegabytes(megabytes);
        if (bytes > _maximumBytes)
        {
            MessageBox.Show(
                this,
                $"卷大小超过了最大可用空间（{SizeFormatter.Format(_maximumBytes)}）。",
                "新建简单卷",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        if (FileSystem == "FAT32" && bytes > 32L * 1024 * 1024 * 1024)
        {
            MessageBox.Show(this, "FAT32 卷最大支持 32 GB，请改用 NTFS 或 exFAT。", "新建简单卷", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (string.Equals(FileSystem, "ReFS", StringComparison.Ordinal) && bytes < 2L * 1024 * 1024 * 1024)
        {
            MessageBox.Show(this, "ReFS 卷最小为 2 GB，且仅专业工作站版支持。", "新建简单卷", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        string selection = _letterBox.SelectedItem as string ?? string.Empty;
        DriveLetter = selection.Length >= 2 && selection[1] == ':' ? selection[0] : null;
        RequestedSize = bytes;
        return true;
    }
}

/// <summary>"格式化"对话框。</summary>
internal sealed class FormatVolumeDialog : DialogBase
{
    private readonly TextBox _labelBox;
    private readonly ComboBox _fileSystemBox;
    private readonly ComboBox _unitBox;
    private readonly CheckBox _quickFormat;

    public FormatVolumeDialog(Window owner, VolumeItem volume, string[] allocationUnits)
        : base("格式化", $"卷 {volume.DisplayName}（{volume.CapacityText}）", 470)
    {
        Owner = owner;

        _labelBox = new TextBox { Text = volume.Label };
        AddField("卷标：", _labelBox);

        _fileSystemBox = new ComboBox();
        foreach (string fs in new[] { "NTFS", "exFAT", "FAT32", "ReFS" })
        {
            _fileSystemBox.Items.Add(fs);
        }

        _fileSystemBox.SelectedItem = volume.FileSystem.Length > 0 ? volume.FileSystem : "NTFS";
        AddField("文件系统：", _fileSystemBox);

        _unitBox = new ComboBox();
        foreach (string unit in allocationUnits)
        {
            _unitBox.Items.Add(unit);
        }

        _unitBox.SelectedIndex = 0;
        AddField("分配单元大小：", _unitBox, "选择\"默认\"时由系统按卷大小自动决定。");

        _quickFormat = new CheckBox { Content = "执行快速格式化", IsChecked = true };
        AddFullWidth(_quickFormat);

        AddFullWidthNotice($"格式化将删除 {volume.DisplayName} 上的所有数据，且无法撤销。");
        BuildDefaultButtons("开始");
    }

    /// <summary>卷标（确定时拍的快照，后台线程读它安全）。</summary>
    public string VolumeLabel { get; private set; } = string.Empty;

    /// <summary>文件系统（确定时拍的快照）。</summary>
    public string FileSystem { get; private set; } = "NTFS";

    /// <summary>是否快速格式化（确定时拍的快照）。</summary>
    public bool QuickFormat { get; private set; } = true;

    public uint AllocationUnitSize { get; private set; }

    protected override bool Validate()
    {
        VolumeLabel = _labelBox.Text.Trim();
        FileSystem = _fileSystemBox.SelectedItem as string ?? "NTFS";
        QuickFormat = _quickFormat.IsChecked == true;

        string unit = _unitBox.SelectedItem as string ?? "默认";
        AllocationUnitSize = unit == "默认" ? 0 : uint.Parse(unit.Split(' ')[0], CultureInfo.CurrentCulture);
        return true;
    }
}

/// <summary>"更改驱动器号和路径"对话框。</summary>
internal sealed class ChangeDriveLetterDialog : DialogBase
{
    private readonly RadioButton _assignRadio;
    private readonly RadioButton _removeRadio;
    private readonly RadioButton _folderRadio;
    private readonly ComboBox _letterBox;
    private readonly TextBox _folderBox;

    public ChangeDriveLetterDialog(Window owner, VolumeItem volume)
        : base("更改驱动器号和路径", $"卷 {volume.DisplayName}", 480)
    {
        Owner = owner;

        StackPanel modePanel = new();
        _assignRadio = new RadioButton { Content = "分配以下驱动器号：", IsChecked = true };
        _removeRadio = new RadioButton { Content = "删除驱动器号（该卷将不再占用盘符）" };
        _folderRadio = new RadioButton { Content = "装入以下空白 NTFS 文件夹中：" };
        modePanel.Children.Add(_assignRadio);
        modePanel.Children.Add(_removeRadio);
        modePanel.Children.Add(_folderRadio);
        AddFullWidth(modePanel, 0);

        _letterBox = new ComboBox();
        foreach (char letter in AvailableLetters(volume.LetterText))
        {
            _letterBox.Items.Add($"{letter}:");
        }

        if (_letterBox.Items.Count > 0)
        {
            _letterBox.SelectedIndex = 0;
        }

        AddField("驱动器号：", _letterBox);

        _folderBox = new TextBox { Text = @"C:\Mount\卷" };
        AddField("文件夹：", _folderBox, "目标文件夹必须存在且为空，所在卷必须是 NTFS。");

        AddButton("确定", isDefault: true, onClick: Confirm);
        AddButton("取消", isCancel: true, onClick: () => Close());
    }

    public ChangeLetterMode Mode { get; private set; } = ChangeLetterMode.Assign;

    public char? DriveLetter { get; private set; }

    /// <summary>文件夹路径（确定时拍的快照，后台线程读它安全）。</summary>
    public string FolderPath { get; private set; } = string.Empty;

    private static IEnumerable<char> AvailableLetters(string current)
    {
        HashSet<char> used = VolumeDevice.GetUsedDriveLetters();
        foreach (char letter in current.Where(char.IsLetter))
        {
            used.Remove(letter);
        }

        for (char letter = 'D'; letter <= 'Z'; letter++)
        {
            if (!used.Contains(letter))
            {
                yield return letter;
            }
        }
    }

    protected override bool Validate()
    {
        FolderPath = _folderBox.Text.Trim();

        if (_removeRadio.IsChecked == true)
        {
            Mode = ChangeLetterMode.Remove;
            return true;
        }

        if (_folderRadio.IsChecked == true)
        {
            if (FolderPath.Length < 3 || FolderPath[1] != ':' || !FolderPath.Contains('\\'))
            {
                MessageBox.Show(this, "请输入完整的文件夹路径，例如 C:\\Mount\\卷。", "更改驱动器号", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            Mode = ChangeLetterMode.MountFolder;
            return true;
        }

        string selection = _letterBox.SelectedItem as string ?? string.Empty;
        if (selection.Length < 2)
        {
            MessageBox.Show(this, "请选择一个驱动器号。", "更改驱动器号", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        Mode = ChangeLetterMode.Assign;
        DriveLetter = selection[0];
        return true;
    }
}

/// <summary>"扩展卷"对话框。</summary>
internal sealed class ExtendVolumeDialog : DialogBase
{
    private readonly TextBox _sizeBox;
    private readonly long _currentSize;
    private readonly long _maximumSize;

    public ExtendVolumeDialog(Window owner, VolumeItem volume, long maximumSize)
        : base("扩展卷", $"卷 {volume.DisplayName}", 460)
    {
        Owner = owner;
        _currentSize = volume.Size;
        _maximumSize = maximumSize;

        AddFullWidthNotice($"当前大小：{SizeFormatter.Format(_currentSize)}    最大可扩展至：{SizeFormatter.Format(_maximumSize)}", foreground: null);
        _sizeBox = new TextBox { Text = SizeFormatter.FormatMegabytes(_maximumSize - _currentSize) };
        AddField("选择要扩展的空间量(MB)：", _sizeBox);
        BuildDefaultButtons("扩展");
    }

    /// <summary>扩展后的新总大小（字节）。</summary>
    public long NewSize { get; private set; }

    protected override bool Validate()
    {
        if (!long.TryParse(_sizeBox.Text.Trim(), out long megabytes) || megabytes <= 0)
        {
            MessageBox.Show(this, "请输入大于 0 的整数（单位 MB）。", "扩展卷", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        long newSize = _currentSize + SizeFormatter.FromMegabytes(megabytes);
        if (newSize > _maximumSize)
        {
            MessageBox.Show(
                this,
                $"扩展后的大小会超过可用的最大空间（{SizeFormatter.Format(_maximumSize)}）。",
                "扩展卷",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        NewSize = newSize;
        return true;
    }
}

/// <summary>"压缩卷"对话框。</summary>
internal sealed class ShrinkVolumeDialog : DialogBase
{
    private readonly TextBox _sizeBox;
    private readonly long _currentSize;
    private readonly long _minimumSize;

    public ShrinkVolumeDialog(Window owner, VolumeItem volume, long minimumSize)
        : base("压缩卷", $"卷 {volume.DisplayName}", 470)
    {
        Owner = owner;
        _currentSize = volume.Size;
        _minimumSize = minimumSize;

        long shrinkable = Math.Max(0, _currentSize - _minimumSize);
        AddFullWidthNotice(
            $"压缩前总大小：{SizeFormatter.Format(_currentSize)}    可用压缩空间：{SizeFormatter.Format(shrinkable)}",
            foreground: null);

        _sizeBox = new TextBox { Text = SizeFormatter.FormatMegabytes(shrinkable / 2) };
        AddField("输入要压缩的空间量(MB)：", _sizeBox);
        BuildDefaultButtons("压缩");
    }

    /// <summary>压缩后的新总大小（字节）。</summary>
    public long NewSize { get; private set; }

    protected override bool Validate()
    {
        if (!long.TryParse(_sizeBox.Text.Trim(), out long megabytes) || megabytes <= 0)
        {
            MessageBox.Show(this, "请输入大于 0 的整数（单位 MB）。", "压缩卷", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        long newSize = _currentSize - SizeFormatter.FromMegabytes(megabytes);
        if (newSize < _minimumSize)
        {
            MessageBox.Show(
                this,
                $"最多只能压缩到 {SizeFormatter.Format(_minimumSize)}（受不可移动文件限制）。",
                "压缩卷",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        NewSize = newSize;
        return true;
    }
}
