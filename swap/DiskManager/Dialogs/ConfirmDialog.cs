using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DiskManager.Dialogs;

/// <summary>
/// 危险操作确认框。
/// 与系统磁盘管理一致：说明后果，必要时要求勾选"我已了解"才能继续。
/// </summary>
internal sealed class ConfirmDialog : DialogBase
{
    private readonly CheckBox? _acknowledge;

    private ConfirmDialog(string title, string message, string detail, bool requireAcknowledge, string confirmText)
        : base(title, message, 470)
    {
        if (!string.IsNullOrEmpty(detail))
        {
            AddFullWidthNotice(detail, new SolidColorBrush(Color.FromRgb(0xA0, 0x30, 0x10)));
        }

        if (requireAcknowledge)
        {
            _acknowledge = new CheckBox
            {
                Content = "我已确认上述后果，并了解此操作无法撤销",
            };

            AddFullWidth(_acknowledge, 12);
        }

        AddButton(confirmText, isDefault: false, onClick: Confirm);
        AddButton("取消", isCancel: true, onClick: () => Close());
    }

    protected override bool Validate()
    {
        if (_acknowledge is { IsChecked: false })
        {
            MessageBox.Show(this, "请先勾选确认项。", "磁盘管理器", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        return true;
    }

    /// <summary>弹出确认框，返回用户是否确认。</summary>
    public static bool Ask(
        Window? owner,
        string title,
        string message,
        string detail = "",
        bool requireAcknowledge = false,
        string confirmText = "确定")
    {
        ConfirmDialog dialog = new(title, message, detail, requireAcknowledge, confirmText);
        if (owner is not null)
        {
            dialog.Owner = owner;
        }

        return dialog.ShowDialog() == true;
    }

}
