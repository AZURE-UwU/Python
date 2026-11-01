using Microsoft.UI.Xaml;

namespace DiskManagerWinUI;

/// <summary>应用入口（非打包 WinUI 3）。</summary>
public partial class App : Application
{
    private Window? _window;

    /// <summary>命令行参数，供 --demo 之类的调试开关使用。</summary>
    public static string[] StartupArguments { get; private set; } = [];

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        StartupArguments = Environment.GetCommandLineArgs();

        _window = new MainWindow();
        _window.Activate();
    }
}
