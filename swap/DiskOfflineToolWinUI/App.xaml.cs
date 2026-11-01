using Microsoft.UI.Xaml;

namespace DiskOfflineToolWinUI;

/// <summary>应用入口（非打包 WinUI 3）。</summary>
public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
