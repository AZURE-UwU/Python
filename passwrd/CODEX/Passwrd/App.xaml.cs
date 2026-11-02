using Microsoft.UI.Xaml;
using Passwrd.Services;

namespace Passwrd;

public partial class App : Application
{
    private MainWindow? _mainWindow;
    private SingleInstance? _singleInstance;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) =>
            CrashLogger.Write("WinUI.UnhandledException", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            CrashLogger.Write("AppDomain.UnhandledException", args.ExceptionObject as Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _singleInstance = SingleInstance.Create();
            if (!_singleInstance.IsOwner)
            {
                _singleInstance.NotifyOwner();
                Exit();
                return;
            }

            _mainWindow = new MainWindow();
            _singleInstance.ActivationRequested += (_, _) =>
                _mainWindow?.DispatcherQueue.TryEnqueue(() => _mainWindow.ShowFromTray());
            _mainWindow.Activate();
        }
        catch (Exception ex)
        {
            CrashLogger.Write("App.OnLaunched", ex);
            throw;
        }
    }
}
