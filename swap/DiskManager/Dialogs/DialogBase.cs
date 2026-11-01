using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DiskManager.Dialogs;

/// <summary>
/// 所有对话框的基类：统一字体、间距、按钮排布，子类只管往里加字段。
/// 用代码搭界面是为了让"校验 + 结果"集中在一处，避免 XAML 与逻辑分离后校验漏掉。
/// </summary>
internal abstract class DialogBase : Window
{
    private readonly Grid _fieldGrid;
    private readonly StackPanel _buttonPanel;

    protected DialogBase(string title, string? description = null, double width = 460)
    {
        Title = title;
        Width = width;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        FontSize = 12.5;
        Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF7, 0xF7));

        _fieldGrid = new Grid { Margin = new Thickness(18, 10, 18, 0) };
        _fieldGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _fieldGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(18, 16, 18, 16),
        };

        StackPanel content = new();
        if (!string.IsNullOrEmpty(description))
        {
            content.Children.Add(new TextBlock
            {
                Text = description,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(18, 16, 18, 4),
                Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            });
        }

        content.Children.Add(_fieldGrid);

        DockPanel root = new();
        DockPanel.SetDock(_buttonPanel, Dock.Bottom);
        root.Children.Add(_buttonPanel);
        root.Children.Add(content);
        Content = root;
    }

    /// <summary>添加一行"标签 + 控件"。</summary>
    protected void AddField(string label, UIElement control, string? hint = null)
    {
        int row = _fieldGrid.RowDefinitions.Count;
        _fieldGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        TextBlock caption = new()
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 6, 14, 6),
        };

        StackPanel field = new() { Margin = new Thickness(0, 6, 0, 6) };
        if (control is FrameworkElement element)
        {
            element.HorizontalAlignment = HorizontalAlignment.Stretch;
            element.VerticalAlignment = VerticalAlignment.Center;
        }

        field.Children.Add(control);

        if (!string.IsNullOrEmpty(hint))
        {
            field.Children.Add(new TextBlock
            {
                Text = hint,
                Foreground = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77)),
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0),
            });
        }

        Grid.SetRow(caption, row);
        Grid.SetColumn(caption, 0);
        Grid.SetRow(field, row);
        Grid.SetColumn(field, 1);
        _fieldGrid.Children.Add(caption);
        _fieldGrid.Children.Add(field);
    }

    /// <summary>在字段下方插入一段整行提示（如警告文本）。</summary>
    protected void AddFullWidthNotice(string text, Brush? foreground = null)
    {
        int row = _fieldGrid.RowDefinitions.Count;
        _fieldGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        TextBlock notice = new()
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = foreground ?? new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
        };

        Grid.SetRow(notice, row);
        Grid.SetColumn(notice, 0);
        Grid.SetColumnSpan(notice, 2);
        _fieldGrid.Children.Add(notice);
    }

    /// <summary>插入一个跨两列的整行控件（如确认复选框）。</summary>
    protected void AddFullWidth(UIElement element, double topMargin = 8)
    {
        int row = _fieldGrid.RowDefinitions.Count;
        _fieldGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        if (element is FrameworkElement frameworkElement)
        {
            frameworkElement.Margin = new Thickness(0, topMargin, 0, 0);
        }

        Grid.SetRow(element, row);
        Grid.SetColumn(element, 0);
        Grid.SetColumnSpan(element, 2);
        _fieldGrid.Children.Add(element);
    }

    /// <summary>添加按钮。</summary>
    protected Button AddButton(string text, bool isDefault = false, bool isCancel = false, Action? onClick = null)
    {
        Button button = new()
        {
            Content = text,
            MinWidth = 88,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(8, 0, 0, 0),
        };

        if (isDefault)
        {
            button.IsDefault = true;
        }

        if (isCancel)
        {
            button.IsCancel = true;
        }

        button.Click += (_, _) =>
        {
            if (onClick is null)
            {
                Close();
                return;
            }

            onClick();
        };

        _buttonPanel.Children.Add(button);
        return button;
    }

    /// <summary>生成标准的"确定 / 取消"按钮。</summary>
    protected void BuildDefaultButtons(string okText = "确定")
    {
        AddButton(okText, isDefault: true, onClick: Confirm);
        AddButton("取消", isCancel: true, onClick: () => Close());
    }

    /// <summary>校验并关闭；子类重写 Validate 返回 false 可阻止关闭。</summary>
    protected virtual void Confirm()
    {
        if (!Validate())
        {
            return;
        }

        DialogResult = true;
        Close();
    }

    /// <summary>字段校验钩子，默认全部通过。</summary>
    protected virtual bool Validate() => true;
}
