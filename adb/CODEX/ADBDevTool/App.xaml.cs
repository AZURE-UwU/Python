/// @file    App.xaml.cs
/// @author  Bowen
/// @module  ADBDevTool / 应用入口

using System;
using System.IO;
using Microsoft.UI.Xaml;

namespace ADBDevTool;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }

    // 未处理异常落到程序目录，便于在没有调试器时定位崩溃点。
    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "crash.log");
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Message}{Environment.NewLine}{e.Exception}{Environment.NewLine}");
        }
        catch
        {
            // 写日志失败时不再抛出，避免掩盖原始异常。
        }
    }
}
