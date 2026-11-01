/// @file    SettingsDialog.cs
/// @author  Bowen
/// @module  ADBDevTool / 设置对话框

using System;
using System.Threading.Tasks;
using ADBDevTool.Services;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.Storage.Pickers;
using Windows.UI.Core;

namespace ADBDevTool.Dialogs;

internal static class SettingsDialog
{
    /// <summary>帧率上限选项；0 表示不限制。</summary>
    private static readonly (int Value, string Label)[] FpsOptions =
    {
        (0, "不限（跟随手机刷新率）"),
        (120, "120 帧"),
        (90, "90 帧"),
        (60, "60 帧"),
        (45, "45 帧"),
        (30, "30 帧"),
        (24, "24 帧"),
    };

    /// <summary>码率选项，单位 Mbps；8 与 scrcpy 自身的默认值一致。</summary>
    private static readonly int[] BitRateOptions = { 1, 2, 3, 4, 6, 8, 12, 16, 24, 32 };

    /// <summary>分辨率上限选项，值为长边像素；0 表示不限制。</summary>
    private static readonly (int Value, string Label)[] MaxSizeOptions =
    {
        (0, "不限制（手机原生）"),
        (1920, "1920（长边）"),
        (1600, "1600（长边）"),
        (1280, "1280（长边）"),
        (1080, "1080（长边）"),
        (720, "720（长边）"),
    };

    /// <summary>键盘输入方式选项，值与 scrcpy 的 --keyboard 取值一致；auto 表示不传该参数。</summary>
    private static readonly (string Value, string Label)[] KeyboardModes =
    {
        ("auto", "自动（scrcpy 默认）"),
        ("uhid", "UHID（模拟物理键盘）"),
        ("sdk", "SDK（系统接口注入）"),
        ("disabled", "关闭（不发送键盘输入）"),
    };

    /// <summary>弹出设置对话框；点「保存」返回新设置，取消或关闭返回 null。hwnd 供文件夹选择器初始化用。</summary>
    public static async Task<AppSettings?> ShowAsync(XamlRoot xamlRoot, IntPtr hwnd, AppSettings current)
    {
        var screenOff = new ToggleSwitch
        {
            Header = "投屏时关闭手机屏幕",
            OnContent = "关屏",
            OffContent = "保持亮屏",
            IsOn = current.TurnScreenOffOnMirror,
        };

        var console = new ToggleSwitch
        {
            Header = "显示 scrcpy 命令行窗口",
            OnContent = "显示",
            OffContent = "隐藏",
            IsOn = current.ShowScrcpyConsole,
        };

        var toggleBox = CreateShortcutBox(current.ToggleShortcut);

        var fpsBox = new ComboBox
        {
            Header = "视频帧率上限",
            MinWidth = 220,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        foreach (var option in FpsOptions)
        {
            fpsBox.Items.Add(new ComboBoxItem { Content = option.Label, Tag = option.Value });
        }

        var fpsIndex = Array.FindIndex(FpsOptions, option => option.Value == current.VideoMaxFps);
        fpsBox.SelectedIndex = fpsIndex >= 0 ? fpsIndex : 0;

        var bitRateBox = new ComboBox
        {
            Header = "视频码率",
            MinWidth = 220,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        foreach (var value in BitRateOptions)
        {
            bitRateBox.Items.Add(new ComboBoxItem { Content = $"{value} Mbps", Tag = value });
        }

        var bitRateIndex = Array.IndexOf(BitRateOptions, current.VideoBitRateMbps);
        bitRateBox.SelectedIndex = bitRateIndex >= 0 ? bitRateIndex : Array.IndexOf(BitRateOptions, 8);

        var maxSizeBox = new ComboBox
        {
            Header = "分辨率上限",
            MinWidth = 220,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        foreach (var option in MaxSizeOptions)
        {
            maxSizeBox.Items.Add(new ComboBoxItem { Content = option.Label, Tag = option.Value });
        }

        var maxSizeIndex = Array.FindIndex(MaxSizeOptions, option => option.Value == current.VideoMaxSize);
        maxSizeBox.SelectedIndex = maxSizeIndex >= 0 ? maxSizeIndex : 0;

        var keyboardBox = new ComboBox
        {
            Header = "键盘输入方式",
            MinWidth = 220,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        foreach (var option in KeyboardModes)
        {
            keyboardBox.Items.Add(new ComboBoxItem { Content = option.Label, Tag = option.Value });
        }

        var keyboardIndex = Array.FindIndex(
            KeyboardModes,
            option => string.Equals(option.Value, current.KeyboardMode, StringComparison.OrdinalIgnoreCase));
        keyboardBox.SelectedIndex = keyboardIndex >= 0 ? keyboardIndex : 0;

        // 投屏相关设置收进两个可折叠分组，避免对话框一屏放不下。
        var videoPanel = new StackPanel { Spacing = 2 };
        var windowPanel = new StackPanel { Spacing = 2 };
        windowPanel.Children.Add(screenOff);
        windowPanel.Children.Add(Help("关闭后手机屏幕立即熄灭，投屏画面与鼠标键盘控制照常；scrcpy 退出时会自行恢复屏幕状态。"));
        windowPanel.Children.Add(console);
        windowPanel.Children.Add(Help("隐藏后不创建 scrcpy 的控制台窗口，scrcpy 的输出会转到执行日志。"));
        windowPanel.Children.Add(ShortcutRow("最小化 / 恢复投屏窗口（全局快捷键）", toggleBox));
        windowPanel.Children.Add(Help("同一个键切换：投屏窗口已最小化时按下就是恢复并置于前台，否则是把它最小化。快捷键只在投屏会话运行期间生效，结束投屏后自动注销。点输入框后按下组合键即可录入，按 Backspace 清除；必须带至少一个修饰键（Ctrl / Alt / Shift / Win）。"));
        windowPanel.Children.Add(Help("本组各项都在下一次启动投屏时生效。"));
        videoPanel.Children.Add(fpsBox);
        videoPanel.Children.Add(Help("限制每秒采集并编码的帧数，直接决定投屏占用的带宽和手机的编码负载。手机上滑动卡顿时先试 60 或 30 帧；数值越低越省带宽、越不容易卡，代价是画面流畅度下降。"));
        videoPanel.Children.Add(bitRateBox);
        videoPanel.Children.Add(Help("编码目标码率，scrcpy 自身的默认是 8 Mbps。网络不稳或画面出现马赛克、拖影时，调低比调高更有效；调高只在画面细节多（比如看视频）时才有意义。"));
        videoPanel.Children.Add(maxSizeBox);
        videoPanel.Children.Add(Help("限制长边像素数，scrcpy 会按比例缩小短边。手机竖屏 1200×2667 选 1600 时实际是 719×1600，像素数降到约 1/2.8。想减轻手机编码负载和带宽压力，降分辨率比降帧率划算得多。"));
        windowPanel.Children.Add(keyboardBox);
        windowPanel.Children.Add(Help("自动（默认）下 scrcpy 自己决定，实测这台小米上走的是 SDK 注入——手机不认为接了外接键盘，输入框一聚焦就弹手机输入法，字母会被中文输入法当拼音吃掉。改成 UHID 后手机能识别出一个名为 scrcpy 的键盘设备；SDK 适用于 UHID 不可用的情况。"));

        var videoCaption = Caption();
        var videoGroup = new Expander
        {
            Header = GroupHeader("投屏画面", videoCaption),
            Content = videoPanel,
            IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };

        var windowCaption = Caption();
        var windowGroup = new Expander
        {
            Header = GroupHeader("投屏窗口与输入", windowCaption),
            Content = windowPanel,
            IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };

        var downloadBox = new TextBox
        {
            Text = current.ResolveDownloadDirectory(),
            Width = 300,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var browseButton = new Button { Content = "浏览…" };
        browseButton.Click += async (_, _) =>
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
            {
                downloadBox.Text = folder.Path;
            }
        };

        var downloadRow = new StackPanel { Spacing = 6, Margin = new Thickness(0, 14, 0, 6) };
        downloadRow.Children.Add(new TextBlock
        {
            Text = "手机文件下载目录",
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        });
        var downloadLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        downloadLine.Children.Add(downloadBox);
        downloadLine.Children.Add(browseButton);
        downloadRow.Children.Add(downloadLine);
        downloadRow.Children.Add(Help("从手机取回的文件存到这里；拖拽上传不受影响。"));

        void RefreshCaptions()
        {
            videoCaption.Text = $"{SelectedLabel(fpsBox)} · {SelectedLabel(bitRateBox)} · {SelectedLabel(maxSizeBox)}";
            windowCaption.Text = $"关屏 {(screenOff.IsOn ? "开" : "关")} · 命令行 {(console.IsOn ? "显示" : "隐藏")} · "
                + $"{(string.IsNullOrWhiteSpace(toggleBox.Text) ? "快捷键未设置" : toggleBox.Text)} · {SelectedLabel(keyboardBox)}";
        }

        screenOff.Toggled += (_, _) => RefreshCaptions();
        console.Toggled += (_, _) => RefreshCaptions();
        toggleBox.TextChanged += (_, _) => RefreshCaptions();
        fpsBox.SelectionChanged += (_, _) => RefreshCaptions();
        bitRateBox.SelectionChanged += (_, _) => RefreshCaptions();
        maxSizeBox.SelectionChanged += (_, _) => RefreshCaptions();
        keyboardBox.SelectionChanged += (_, _) => RefreshCaptions();
        RefreshCaptions();

        // 右侧留出滚动条的位置，避免滚动条压住文字。
        var panel = new StackPanel { Spacing = 2, MinWidth = 430, Padding = new Thickness(0, 0, 20, 0) };
        panel.Children.Add(videoGroup);
        panel.Children.Add(windowGroup);
        panel.Children.Add(downloadRow);

        // 内容比窗口高时让对话框内部滚动，而不是把按钮顶出屏幕。
        var scroll = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = Math.Max(220, xamlRoot.Size.Height - 260),
        };

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "设置",
            Content = scroll,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return null;
        }

        return new AppSettings
        {
            TurnScreenOffOnMirror = screenOff.IsOn,
            ShowScrcpyConsole = console.IsOn,
            ToggleShortcut = toggleBox.Text.Trim(),
            VideoMaxFps = fpsBox.SelectedItem is ComboBoxItem { Tag: int fps } ? fps : 0,
            VideoBitRateMbps = bitRateBox.SelectedItem is ComboBoxItem { Tag: int bitRate } ? bitRate : 8,
            VideoMaxSize = maxSizeBox.SelectedItem is ComboBoxItem { Tag: int maxSize } ? maxSize : 0,
            KeyboardMode = keyboardBox.SelectedItem is ComboBoxItem { Tag: string mode } ? mode : "auto",
            DownloadDirectory = downloadBox.Text.Trim(),
        };
    }

    private static TextBlock Caption() => new()
    {
        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>分组标题：主标题 + 一行随设置变化的值摘要，收起时也能看到当前配置。</summary>
    private static StackPanel GroupHeader(string title, TextBlock caption)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        });
        panel.Children.Add(caption);
        return panel;
    }

    private static string SelectedLabel(ComboBox box)
        => box.SelectedItem is ComboBoxItem item && item.Content is string text ? text : "—";

    private static StackPanel ShortcutRow(string label, TextBox box)
    {
        var panel = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 14) };
        panel.Children.Add(new TextBlock
        {
            Text = label,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        });
        panel.Children.Add(box);
        return panel;
    }

    /// <summary>按键录制框：获得焦点后按下的第一个非修饰键与当前修饰键组合成快捷键。</summary>
    private static TextBox CreateShortcutBox(string initial)
    {
        var box = new TextBox
        {
            Text = initial,
            IsReadOnly = true,
            PlaceholderText = "点击后按组合键",
            Width = 240,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        box.KeyDown += (_, args) =>
        {
            args.Handled = true;

            if (args.Key is VirtualKey.Control or VirtualKey.Menu or VirtualKey.Shift
                or VirtualKey.LeftWindows or VirtualKey.RightWindows)
            {
                return;
            }

            if (args.Key is VirtualKey.Back or VirtualKey.Delete)
            {
                box.Text = string.Empty;
                return;
            }

            var modifiers = CurrentModifiers();
            if (modifiers != 0)
            {
                box.Text = new HotkeySpec(modifiers, (int)args.Key).ToString();
            }
        };

        return box;
    }

    /// <summary>读取当前按下的修饰键；只是主键按下时返回 0（RegisterHotKey 不接受无修饰键的组合）。</summary>
    private static int CurrentModifiers()
    {
        var modifiers = 0;
        if (IsKeyDown(VirtualKey.Control))
        {
            modifiers |= HotkeySpec.ModControl;
        }

        if (IsKeyDown(VirtualKey.Menu))
        {
            modifiers |= HotkeySpec.ModAlt;
        }

        if (IsKeyDown(VirtualKey.Shift))
        {
            modifiers |= HotkeySpec.ModShift;
        }

        if (IsKeyDown(VirtualKey.LeftWindows) || IsKeyDown(VirtualKey.RightWindows))
        {
            modifiers |= HotkeySpec.ModWin;
        }

        return modifiers;
    }

    private static bool IsKeyDown(VirtualKey key)
        => (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;

    private static TextBlock Help(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 14),
        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
    };
}
