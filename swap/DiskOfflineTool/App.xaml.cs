using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DiskOfflineTool;

/// <summary>程序入口。普通启动显示界面；带 --selftest 时在后台跑自检并退出。</summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        if (e.Args.Contains("--selftest", StringComparer.OrdinalIgnoreCase))
        {
            // 自检模式：不显示界面。用后台线程执行，避免阻塞（此时还没有窗口）。
            Thread thread = new(() =>
            {
                int code = SelfTestMode.Run(e.Args);
                Dispatcher.Invoke(() => Shutdown(code));
            });

            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
            return;
        }

        // 自截图模式：显示窗口 → 截图 → 自动退出（用于自动化验证界面，不留残留窗口）。
        int shotIndex = Array.FindIndex(e.Args, a => string.Equals(a, "--shot", StringComparison.OrdinalIgnoreCase));
        if (shotIndex >= 0 && shotIndex + 1 < e.Args.Length)
        {
            string outputPath = e.Args[shotIndex + 1];
            Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(5000); // 等磁盘列表读完
                (MainWindow as MainWindow)?.SelectFirstProtectedDisk();
                await Task.Delay(500);   // 等选中状态与提示文字刷新
                CaptureWindow(outputPath);
                Shutdown(0);
            });
        }
    }

    /// <summary>把主窗口渲染成 PNG。</summary>
    private void CaptureWindow(string path)
    {
        if (MainWindow is not { } window || window.ActualWidth <= 0)
        {
            return;
        }

        RenderTargetBitmap bitmap = new(
            (int)window.ActualWidth,
            (int)window.ActualHeight,
            96,
            96,
            PixelFormats.Pbgra32);

        bitmap.Render(window);

        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"程序遇到未处理的错误：\n\n{e.Exception.Message}\n\n操作已中止，磁盘未被修改。",
            "磁盘脱机工具",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
    }
}
