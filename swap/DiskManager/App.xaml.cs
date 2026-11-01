using System.Windows;
using System.Windows.Threading;

namespace DiskManager;

/// <summary>应用程序入口：统一兜住未处理异常，避免界面直接崩掉。</summary>
public partial class App : Application
{
    /// <summary>界面线程未处理异常。</summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"程序遇到未处理的错误：\n\n{e.Exception.Message}\n\n操作已中止，磁盘未被修改。",
            "磁盘管理器",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }
}
