using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinRT.Interop;

namespace VhdxCopier;

public sealed partial class MainWindow : Window
{
    private string? _sourcePath;
    private string? _destinationFolder;
    private CancellationTokenSource? _cts;
    private bool _isCopying;

    public MainWindow()
    {
        InitializeComponent();
        AppWindow.Resize(new SizeInt32(980, 700));
    }

    private void PickSourceButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = FilePickerInterop.PickVhdx(WindowNative.GetWindowHandle(this));
            if (path is null)
            {
                return;
            }

            _sourcePath = path;
            SourcePathBox.Text = _sourcePath;
            ResultInfoBar.IsOpen = false;
            UpdateStartButton();
        }
        catch (Exception ex)
        {
            ShowError($"打开文件选择器失败：{ex.Message}");
        }

    }

    private void PickDestinationButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = FilePickerInterop.PickFolder(WindowNative.GetWindowHandle(this));
            if (path is null)
            {
                return;
            }

            _destinationFolder = path;
            DestinationPathBox.Text = _destinationFolder;
            ResultInfoBar.IsOpen = false;
            UpdateStartButton();
        }
        catch (Exception ex)
        {
            ShowError($"打开文件夹选择器失败：{ex.Message}");
        }

    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isCopying || _sourcePath is null || _destinationFolder is null)
        {
            return;
        }

        var destinationPath = Path.Combine(_destinationFolder, Path.GetFileName(_sourcePath));
        if (string.Equals(
                Path.GetFullPath(_sourcePath),
                Path.GetFullPath(destinationPath),
                StringComparison.OrdinalIgnoreCase))
        {
            ShowError("目标文件不能和源文件相同。");
            return;
        }

        if (File.Exists(destinationPath))
        {
            var dialog = new ContentDialog
            {
                Title = "目标文件已存在",
                Content = $"是否覆盖？\n\n{destinationPath}",
                PrimaryButtonText = "覆盖",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
        }

        try
        {
            var sourceLength = new FileInfo(_sourcePath).Length;
            var freeSpace = GetAvailableFreeSpace(destinationPath);
            if (freeSpace.HasValue &&
                freeSpace.Value < sourceLength + 64L * 1024 * 1024)
            {
                ShowError(
                    $"目标磁盘空间不足：需要约 {VhdxCopyService.FormatBytes(sourceLength)}，" +
                    $"可用 {VhdxCopyService.FormatBytes(freeSpace.Value)}。");
                return;
            }
        }
        catch (Exception ex)
        {
            ShowError($"检查目标空间失败：{ex.Message}");
            return;
        }

        _isCopying = true;
        _cts = new CancellationTokenSource();
        SetBusy(true);
        CopyProgressBar.Value = 0;
        StatusText.Text = "准备复制...";
        DetailText.Text = string.Empty;
        ResultInfoBar.IsOpen = false;

        var copyFinished = false;
        var progress = new DispatcherProgress<CopyProgressInfo>(DispatcherQueue, p =>
        {
            if (copyFinished)
            {
                return;
            }

            if (p.TotalBytes > 0)
            {
                CopyProgressBar.Value = Math.Min(100, p.BytesCopied * 100.0 / p.TotalBytes);
            }

            StatusText.Text =
                $"已复制 {VhdxCopyService.FormatBytes(p.BytesCopied)} / " +
                $"{VhdxCopyService.FormatBytes(p.TotalBytes)}";

            var detail = $"速度 {VhdxCopyService.FormatBytes((long)Math.Max(0, p.BytesPerSecond))}/s";
            if (p.Remaining is { } remaining)
            {
                detail += $" · 剩余约 {FormatDuration(remaining)}";
            }

            DetailText.Text = detail;
        });
        var status = new DispatcherProgress<string>(DispatcherQueue, s =>
        {
            if (!copyFinished)
            {
                StatusText.Text = s;
            }
        });

        try
        {
            var service = new VhdxCopyService();
            var result = await service.CopyAsync(
                _sourcePath,
                destinationPath,
                UseVssCheckBox.IsChecked == true,
                progress,
                status,
                _cts.Token);

            copyFinished = true;
            CopyProgressBar.Value = 100;
            StatusText.Text = result.UsedVss ? "复制完成（来自 VSS 快照）" : "复制完成";
            DetailText.Text =
                $"{VhdxCopyService.FormatBytes(result.BytesCopied)} · " +
                $"用时 {FormatDuration(result.Elapsed)}";
            ResultInfoBar.Severity = InfoBarSeverity.Success;
            ResultInfoBar.Title = "复制完成";
            ResultInfoBar.Message = destinationPath;
            ResultInfoBar.IsOpen = true;
        }
        catch (OperationCanceledException)
        {
            copyFinished = true;
            StatusText.Text = "已取消";
            DetailText.Text = "目标位置可能留下不完整文件，请检查后重新复制。";
            ResultInfoBar.Severity = InfoBarSeverity.Warning;
            ResultInfoBar.Title = "已取消";
            ResultInfoBar.Message = "复制已取消。";
            ResultInfoBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            copyFinished = true;
            StatusText.Text = "复制失败";
            DetailText.Text = ex.Message;
            ResultInfoBar.Severity = InfoBarSeverity.Error;
            ResultInfoBar.Title = "复制失败";
            ResultInfoBar.Message = ex.Message;
            ResultInfoBar.IsOpen = true;
        }
        finally
        {
            _isCopying = false;
            _cts?.Dispose();
            _cts = null;
            SetBusy(false);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is null)
        {
            return;
        }

        CancelButton.IsEnabled = false;
        StatusText.Text = "正在取消...";
        _cts.Cancel();
    }

    private void SetBusy(bool busy)
    {
        PickSourceButton.IsEnabled = !busy;
        PickDestinationButton.IsEnabled = !busy;
        UseVssCheckBox.IsEnabled = !busy;
        StartButton.IsEnabled = !busy && _sourcePath is not null && _destinationFolder is not null;
        CancelButton.IsEnabled = busy;
    }

    private void UpdateStartButton()
    {
        StartButton.IsEnabled =
            !_isCopying && _sourcePath is not null && _destinationFolder is not null;
    }

    private void ShowError(string message)
    {
        ResultInfoBar.Severity = InfoBarSeverity.Error;
        ResultInfoBar.Title = "无法开始";
        ResultInfoBar.Message = message;
        ResultInfoBar.IsOpen = true;
    }

    private static long? GetAvailableFreeSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root) ||
                root.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return null;
            }

            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            return null;
        }
    }

    private static string FormatDuration(TimeSpan value)
    {
        return value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss")
            : value.ToString(@"m\:ss");
    }

    private sealed class DispatcherProgress<T> : IProgress<T>
    {
        private readonly DispatcherQueue _dispatcherQueue;
        private readonly Action<T> _handler;

        public DispatcherProgress(DispatcherQueue dispatcherQueue, Action<T> handler)
        {
            _dispatcherQueue = dispatcherQueue;
            _handler = handler;
        }

        public void Report(T value)
        {
            _dispatcherQueue.TryEnqueue(() => _handler(value));
        }
    }
}
