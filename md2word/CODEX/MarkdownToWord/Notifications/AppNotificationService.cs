using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace MarkdownToWord.Notifications;

internal static class AppNotificationService
{
    private static bool _registered;

    public static bool IsRegistered => _registered;

    public static string LastError { get; private set; } = string.Empty;

    public static bool Initialize()
    {
        if (_registered)
        {
            return true;
        }

        try
        {
            if (!AppNotificationManager.IsSupported())
            {
                LastError = "当前系统不支持应用通知。";
                return false;
            }
        }
        catch (Exception ex)
        {
            LastError = ex.ToString();
            return false;
        }

        try
        {
            AppNotificationManager.Default.Register();
            _registered = true;
            LastError = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            _registered = false;
            LastError = ex.ToString();
            return false;
        }
    }

    public static void Show(string title, string message)
    {
        if (!Initialize())
        {
            return;
        }

        try
        {
            var notification = new AppNotificationBuilder()
                .AddText(title)
                .AddText(message)
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
        }
        catch
        {
        }
    }

    public static void Shutdown()
    {
        if (!_registered)
        {
            return;
        }

        try
        {
            AppNotificationManager.Default.Unregister();
        }
        catch
        {
        }

        _registered = false;
    }
}
