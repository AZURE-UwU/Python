using Microsoft.UI.Xaml;
using MarkdownToWord.Notifications;

namespace MarkdownToWord;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        AppNotificationService.Initialize();
    }

    /// <summary>当前主窗口；文件选择器等 WinRT 组件需要拿到窗口句柄。</summary>
    public Window? MainWindow { get; private set; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }
}
