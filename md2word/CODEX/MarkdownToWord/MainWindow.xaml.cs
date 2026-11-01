using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MarkdownToWord.Clipboard;
using MarkdownToWord.Core;
using MarkdownToWord.Notifications;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.Web.WebView2.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace MarkdownToWord;

public sealed partial class MainWindow : Window
{
    private const string VirtualHost = "assets.local";
    private const uint ToggleHotkeyId = 0x4D57;
    private const uint BackgroundConvertHotkeyId = 0x4D58;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint AllowedModifiers = ModAlt | ModControl | ModShift | ModWin;
    private const uint ModNoRepeat = 0x4000;
    private const int WmHotkey = 0x0312;
    private const int SwMinimize = 6;
    private const int SwRestore = 9;

    private readonly ConversionOptions _options = new();
    private readonly MarkdownConverter _converter;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _previewTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _messageTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _windowStateTimer;
    private readonly WindowMessageProc _hotkeyWindowProc;
    private TrayIconService? _trayIcon;
    private Storyboard? _messageStoryboard;
    private bool _messageVisible;
    private bool _previewSuspended;
    private bool _previewFailed;
    private IntPtr _windowHandle;
    private IntPtr _hotkeyWindow;
    private string _hotkeyWindowClass = string.Empty;
    private bool _toggleHotkeyRegistered;
    private bool _backgroundConvertHotkeyRegistered;
    private HotkeyTarget _recordingHotkeyTarget;
    private HotkeyGesture _toggleHotkey;
    private HotkeyGesture? _backgroundConvertHotkey;
    private HotkeyGesture? _pendingHotkey;
    private bool _autoCopyAfterPaste = true;
    private int _clipboardGeneration;
    private int _backgroundClipboardBusy;

    private static readonly string[] PageSizeOptions = ["A4", "Letter", "A5", "Legal"];
    private static readonly string[] OrientationOptions = ["纵向", "横向"];
    private static readonly string[] FontOptions = ["等线", "微软雅黑", "宋体", "仿宋", "Segoe UI"];
    private static readonly string[] SizeOptions = ["10", "10.5", "11", "12", "14"];
    private static readonly string[] ThemeOptions = ["跟随系统", "浅色", "深色"];

    private int _pageSizeIndex;
    private int _orientationIndex;
    private int _fontIndex;
    private int _sizeIndex;
    private int _themeIndex;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private AppSettings _settings = new();
    private string? _currentPath;
    private bool _previewReady;
    private bool _initializing = true;
    private bool _busy;
    private bool _allowExit;

    public MainWindow()
    {
        InitializeComponent();
        _hotkeyWindowProc = HotkeyWindowProcImpl;
        Title = "Markdown 转 Word · 离线版";
        ApplyWindowIcon();
        AppWindow.Closing += OnAppWindowClosing;

        _converter = new MarkdownConverter(_options);
        _previewTimer = DispatcherQueue.CreateTimer();
        _previewTimer.Interval = TimeSpan.FromMilliseconds(320);
        _previewTimer.IsRepeating = false;
        _previewTimer.Tick += (_, _) => UpdatePreview();

        // 成功/提示横幅看一眼就够了，1 秒后自己收起来（警告和错误留着，等人处理）
        _messageTimer = DispatcherQueue.CreateTimer();
        _messageTimer.Interval = TimeSpan.FromSeconds(1);
        _messageTimer.IsRepeating = false;
        _messageTimer.Tick += (_, _) => HideMessage();

        // 外部契约：WebView2 宿主的顶层窗口不随 DWM cloak（切换虚拟桌面）隐藏，会留在当前桌面
        // 作为文件拖放落点（WebView2 运行时 1.0.x；窗口类 Chrome_WidgetWin_1）。
        // 主窗口 cloak 或最小化期间折叠预览，宿主窗口随之隐藏；恢复时重新显示。
        _windowStateTimer = DispatcherQueue.CreateTimer();
        _windowStateTimer.Interval = TimeSpan.FromMilliseconds(400);
        _windowStateTimer.IsRepeating = true;
        _windowStateTimer.Tick += (_, _) => SyncPreviewHostVisibility();
        _windowStateTimer.Start();

        // 横幅常驻在树里、高度为 0：这样弹出/收回只动高度，不会让下面的内容"跳"一下
        MessageBar.IsOpen = true;
        MessageBar.Height = 0;
        MessageBar.Opacity = 0;
        MessageBar.Closing += OnMessageBarClosing;

        LoadSettings();
        InitChoices();
        ApplySettingsToUi();
        ApplyOptionsFromUi();
        _initializing = false;
        RootGrid.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnHotkeyRecordingKeyDown), true);
        InitializeGlobalHotkey();

        EditorBox.TextChanged += OnEditorTextChanged;
        EditorBox.Paste += OnEditorPaste;
        EditorBox.Text = SampleMarkdown;
        UpdateStats();
        SchedulePreview();

        RootGrid.DragOver += OnRootDragOver;
        RootGrid.Drop += OnRootDrop;
        Activated += OnWindowActivated;
        Closed += OnWindowClosed;

        AddAccelerator(VirtualKeyModifiers.Control, VirtualKey.O, OnOpenClicked);
        AddAccelerator(VirtualKeyModifiers.Control, VirtualKey.S, OnSaveMarkdownClicked);
        AddAccelerator(VirtualKeyModifiers.Control, VirtualKey.E, OnExportClicked);

        ResizeWindow();
        _ = InitializePreviewAsync();
    }

    // ------------------------------------------------------------ 初始化

    private void ResizeWindow()
    {
        try
        {
            var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
            appWindow.Resize(new Windows.Graphics.SizeInt32(1360, 900));
        }
        catch
        {
            // 调整窗口大小失败不影响功能
        }
    }

    private void AddAccelerator(VirtualKeyModifiers modifiers, VirtualKey key, RoutedEventHandler handler)
    {
        var accelerator = new KeyboardAccelerator { Modifiers = modifiers, Key = key };
        accelerator.Invoked += (_, args) =>
        {
            args.Handled = true;
            handler(this, new RoutedEventArgs());
        };

        RootGrid.KeyboardAccelerators.Add(accelerator);
    }

    // ------------------------------------------------------------ 全局快捷键

    private void InitializeGlobalHotkey()
    {
        if (!CreateHotkeyMessageWindow())
        {
            ShowHotkeyError("无法创建快捷键消息窗口，全局快捷键未启用。");
            return;
        }

        InitializeTrayIcon();
        _toggleHotkey = ReadConfiguredToggleHotkey();
        _backgroundConvertHotkey = ReadConfiguredBackgroundConvertHotkey();
        UpdateHotkeyDisplay(HotkeyTarget.ToggleWindow, _toggleHotkey);
        UpdateHotkeyDisplay(HotkeyTarget.BackgroundConvert, _backgroundConvertHotkey);

        if (_settings.ToggleWindowHotkeyEnabled &&
            !TryRegisterHotkey(HotkeyTarget.ToggleWindow, _toggleHotkey, out var error))
        {
            ShowHotkeyError($"窗口快捷键 {_toggleHotkey.DisplayText} 注册失败：{error}");
        }

        if (_settings.BackgroundConvertEnabled && _backgroundConvertHotkey is { } backgroundHotkey &&
            !TryRegisterHotkey(HotkeyTarget.BackgroundConvert, backgroundHotkey, out error))
        {
            ShowHotkeyError($"后台转换快捷键 {backgroundHotkey.DisplayText} 注册失败：{error}");
        }

        if (_settings.BackgroundConvertEnabled && !AppNotificationService.Initialize())
        {
            ShowHotkeyError("后台转换可用，但当前系统通知不可用：" + AppNotificationService.LastError);
        }
    }

    private HotkeyGesture ReadConfiguredToggleHotkey()
    {
        var modifiers = (uint)_settings.ToggleHotkeyModifiers & AllowedModifiers;
        var key = (uint)_settings.ToggleHotkeyKey;
        return modifiers != 0 && IsValidHotkeyKey(key)
            ? new HotkeyGesture(modifiers, key)
            : HotkeyGesture.Default;
    }

    private HotkeyGesture? ReadConfiguredBackgroundConvertHotkey()
    {
        var modifiers = (uint)_settings.BackgroundConvertHotkeyModifiers & AllowedModifiers;
        var key = (uint)_settings.BackgroundConvertHotkeyKey;
        return modifiers != 0 && IsValidHotkeyKey(key)
            ? new HotkeyGesture(modifiers, key)
            : null;
    }

    private bool TryRegisterHotkey(HotkeyTarget target, HotkeyGesture hotkey, out string error)
    {
        UnregisterHotkey(target);
        if (_hotkeyWindow == IntPtr.Zero)
        {
            error = "快捷键消息窗口不可用。";
            return false;
        }

        if (RegisterHotKey(
                _hotkeyWindow,
                (int)HotkeyId(target),
                hotkey.Modifiers | ModNoRepeat,
                hotkey.Key))
        {
            SetHotkeyRegistered(target, true);
            error = string.Empty;
            return true;
        }

        error = Marshal.GetLastWin32Error() switch
        {
            1409 => "该组合键已被其他程序占用。",
            _ => "Windows 拒绝注册该组合键。",
        };
        return false;
    }

    private void UnregisterHotkey(HotkeyTarget target)
    {
        if (!IsHotkeyRegistered(target))
        {
            return;
        }

        UnregisterHotKey(_hotkeyWindow, (int)HotkeyId(target));
        SetHotkeyRegistered(target, false);
    }

    private void UnregisterAllHotkeys()
    {
        UnregisterHotkey(HotkeyTarget.ToggleWindow);
        UnregisterHotkey(HotkeyTarget.BackgroundConvert);
    }

    private static uint HotkeyId(HotkeyTarget target) =>
        target == HotkeyTarget.BackgroundConvert ? BackgroundConvertHotkeyId : ToggleHotkeyId;

    private bool IsHotkeyRegistered(HotkeyTarget target) =>
        target == HotkeyTarget.BackgroundConvert
            ? _backgroundConvertHotkeyRegistered
            : _toggleHotkeyRegistered;

    private void SetHotkeyRegistered(HotkeyTarget target, bool registered)
    {
        if (target == HotkeyTarget.BackgroundConvert)
        {
            _backgroundConvertHotkeyRegistered = registered;
        }
        else
        {
            _toggleHotkeyRegistered = registered;
        }
    }

    private bool CreateHotkeyMessageWindow()
    {
        if (_hotkeyWindow != IntPtr.Zero)
        {
            return true;
        }

        var instance = GetModuleHandle(null);
        _hotkeyWindowClass = $"MarkdownToWord.Hotkey.{Environment.ProcessId}";
        var windowClass = new WindowClassEx
        {
            Size = (uint)Marshal.SizeOf<WindowClassEx>(),
            WindowProc = _hotkeyWindowProc,
            Instance = instance,
            ClassName = _hotkeyWindowClass,
        };

        if (RegisterClassEx(ref windowClass) == 0)
        {
            return false;
        }

        _hotkeyWindow = CreateWindowEx(
            0,
            _hotkeyWindowClass,
            string.Empty,
            0,
            0,
            0,
            0,
            0,
            IntPtr.Zero,
            IntPtr.Zero,
            instance,
            IntPtr.Zero);
        if (_hotkeyWindow != IntPtr.Zero)
        {
            return true;
        }

        UnregisterClass(_hotkeyWindowClass, instance);
        _hotkeyWindowClass = string.Empty;
        return false;
    }

    private void OnToggleHotkeyClicked(object sender, RoutedEventArgs e)
    {
        if (!WindowHotkeyToggle.IsOn)
        {
            return;
        }

        if (_recordingHotkeyTarget == HotkeyTarget.ToggleWindow)
        {
            CancelHotkeyRecording(HotkeyTarget.ToggleWindow);
            return;
        }

        StartHotkeyRecording(HotkeyTarget.ToggleWindow);
    }

    private void OnWindowHotkeyToggled(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        var enabled = WindowHotkeyToggle.IsOn;
        _settings.ToggleWindowHotkeyEnabled = enabled;
        WindowHotkeyPanel.IsHitTestVisible = enabled;
        WindowHotkeyPanel.Opacity = enabled ? 1 : 0.55;

        if (!enabled)
        {
            if (_recordingHotkeyTarget == HotkeyTarget.ToggleWindow)
            {
                CancelHotkeyRecording(HotkeyTarget.ToggleWindow);
            }

            UnregisterHotkey(HotkeyTarget.ToggleWindow);
            ToggleHotkeyHint.Text = "窗口快捷键已关闭。";
            SaveSettings();
            return;
        }

        if (TryRegisterHotkey(HotkeyTarget.ToggleWindow, _toggleHotkey, out var error))
        {
            ToggleHotkeyHint.Text = "按快捷键可在前台最小化，或在后台恢复并聚焦。";
        }
        else
        {
            ToggleHotkeyHint.Text = error + " 请点击按钮更换组合键。";
            ShowHotkeyError(error);
        }

        SaveSettings();
    }

    private void OnBackgroundConvertHotkeyClicked(object sender, RoutedEventArgs e)
    {
        if (!BackgroundConvertToggle.IsOn)
        {
            return;
        }

        if (_recordingHotkeyTarget == HotkeyTarget.BackgroundConvert)
        {
            CancelHotkeyRecording(HotkeyTarget.BackgroundConvert);
            return;
        }

        StartHotkeyRecording(HotkeyTarget.BackgroundConvert);
    }

    private void OnBackgroundConvertToggled(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        var enabled = BackgroundConvertToggle.IsOn;
        _settings.BackgroundConvertEnabled = enabled;
        BackgroundConvertHotkeyPanel.IsHitTestVisible = enabled;
        BackgroundConvertHotkeyPanel.Opacity = enabled ? 1 : 0.55;

        if (enabled && !AppNotificationService.Initialize())
        {
            ShowHotkeyError("后台转换可用，但当前系统通知不可用：" + AppNotificationService.LastError);
        }

        if (!enabled)
        {
            if (_recordingHotkeyTarget == HotkeyTarget.BackgroundConvert)
            {
                CancelHotkeyRecording(HotkeyTarget.BackgroundConvert);
            }

            UnregisterHotkey(HotkeyTarget.BackgroundConvert);
            BackgroundConvertHotkeyHint.Text = "开启后点击按钮录入组合键；操作在后台完成并发送系统通知。";
            SaveSettings();
            return;
        }

        if (_backgroundConvertHotkey is { } hotkey)
        {
            if (TryRegisterHotkey(HotkeyTarget.BackgroundConvert, hotkey, out var error))
            {
                BackgroundConvertHotkeyHint.Text = "按快捷键会在后台转换剪贴板，并发送系统通知。";
            }
            else
            {
                BackgroundConvertHotkeyHint.Text = error + " 请点击按钮更换组合键。";
                ShowHotkeyError(error);
            }
        }
        else
        {
            BackgroundConvertHotkeyHint.Text = "点击按钮录入组合键；未设置时功能不会触发。";
        }

        SaveSettings();
    }

    private void StartHotkeyRecording(HotkeyTarget target)
    {
        if (target == HotkeyTarget.ToggleWindow && !WindowHotkeyToggle.IsOn)
        {
            return;
        }

        if (_recordingHotkeyTarget is not HotkeyTarget.None)
        {
            CancelHotkeyRecording(_recordingHotkeyTarget);
        }

        UnregisterHotkey(target);
        _recordingHotkeyTarget = target;
        _pendingHotkey = null;
        SetHotkeyRecordingText(target, "请按新的组合键…", "组合键需包含 Ctrl、Alt、Shift 或 Win；按 Esc 取消。");

        if (target == HotkeyTarget.BackgroundConvert)
        {
            BackgroundConvertHotkeyButton.Focus(FocusState.Programmatic);
        }
        else
        {
            ToggleHotkeyButton.Focus(FocusState.Programmatic);
        }
    }

    private void OnHotkeyRecordingKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_recordingHotkeyTarget is HotkeyTarget.None || e.KeyStatus.WasKeyDown)
        {
            return;
        }

        var target = _recordingHotkeyTarget;
        e.Handled = true;
        if (e.Key == VirtualKey.Escape)
        {
            CancelHotkeyRecording(target);
            return;
        }

        if (e.Key == VirtualKey.Enter)
        {
            CommitHotkeyRecording(target);
            return;
        }

        if (HotkeyGesture.IsModifierKey(e.Key))
        {
            SetHotkeyRecordingHint(target, "继续按一个非修饰键，再按回车确认；按 Esc 取消。");
            return;
        }

        var modifiers = ReadPressedModifiers();
        if (modifiers == 0)
        {
            SetHotkeyRecordingText(target, "组合键无效", "至少按下 Ctrl、Alt、Shift、Win 中的一个，再按其他键。");
            return;
        }

        var key = (uint)e.Key;
        if (!IsValidHotkeyKey(key))
        {
            SetHotkeyRecordingText(target, "该按键不可用", "请换一个字母、数字、功能键或导航键。");
            return;
        }

        _pendingHotkey = new HotkeyGesture(modifiers, key);
        SetHotkeyRecordingText(target, _pendingHotkey.Value.DisplayText, "按回车确认，按 Esc 取消。");
    }

    private void CommitHotkeyRecording(HotkeyTarget target)
    {
        if (_pendingHotkey is not { } pending)
        {
            SetHotkeyRecordingHint(target, "请先按下包含修饰键的新组合键。");
            return;
        }

        if (!TryRegisterHotkey(target, pending, out var error))
        {
            SetHotkeyRecordingHint(target, error + " 请按其他组合键。");
            ShowHotkeyError(error);
            return;
        }

        if (target == HotkeyTarget.BackgroundConvert)
        {
            _backgroundConvertHotkey = pending;
            _settings.BackgroundConvertHotkeyModifiers = (int)pending.Modifiers;
            _settings.BackgroundConvertHotkeyKey = (int)pending.Key;
            BackgroundConvertHotkeyHint.Text = "按快捷键会在后台转换剪贴板，并发送系统通知。";
        }
        else
        {
            _toggleHotkey = pending;
            _settings.ToggleHotkeyModifiers = (int)pending.Modifiers;
            _settings.ToggleHotkeyKey = (int)pending.Key;
            ToggleHotkeyHint.Text = "按快捷键可在前台最小化，或在后台恢复并聚焦。";
        }

        _recordingHotkeyTarget = HotkeyTarget.None;
        _pendingHotkey = null;
        UpdateHotkeyDisplay(target, pending);
        SaveSettings();
        ShowMessage(InfoBarSeverity.Success, $"快捷键已设为 {pending.DisplayText}。");
    }

    private void CancelHotkeyRecording(HotkeyTarget target)
    {
        _recordingHotkeyTarget = HotkeyTarget.None;
        _pendingHotkey = null;

        if (target == HotkeyTarget.BackgroundConvert)
        {
            UpdateHotkeyDisplay(target, _backgroundConvertHotkey);
            BackgroundConvertHotkeyHint.Text = BackgroundConvertToggle.IsOn
                ? _backgroundConvertHotkey is null
                    ? "点击按钮录入组合键；未设置时功能不会触发。"
                    : "按快捷键会在后台转换剪贴板，并发送系统通知。"
                : "开启后点击按钮录入组合键；操作在后台完成并发送系统通知。";
            if (BackgroundConvertToggle.IsOn && _backgroundConvertHotkey is { } backgroundHotkey &&
                !TryRegisterHotkey(target, backgroundHotkey, out var error))
            {
                ShowHotkeyError(error);
            }
        }
        else
        {
            UpdateHotkeyDisplay(target, _toggleHotkey);
            ToggleHotkeyHint.Text = WindowHotkeyToggle.IsOn
                ? "按快捷键可在前台最小化，或在后台恢复并聚焦。"
                : "窗口快捷键已关闭。";
            if (WindowHotkeyToggle.IsOn &&
                !TryRegisterHotkey(target, _toggleHotkey, out var error))
            {
                ShowHotkeyError(error);
            }
        }
    }

    private static uint ReadPressedModifiers()
    {
        uint modifiers = 0;
        if (IsKeyPressed(0x11))
        {
            modifiers |= ModControl;
        }

        if (IsKeyPressed(0x12))
        {
            modifiers |= ModAlt;
        }

        if (IsKeyPressed(0x10))
        {
            modifiers |= ModShift;
        }

        if (IsKeyPressed(0x5B) || IsKeyPressed(0x5C))
        {
            modifiers |= ModWin;
        }

        return modifiers;
    }

    private static bool IsKeyPressed(int virtualKey) => (GetKeyState(virtualKey) & 0x8000) != 0;

    private static bool IsValidHotkeyKey(uint key) =>
        key is > 0 and < 0x100 && !HotkeyGesture.IsModifierKey((VirtualKey)key);

    private void UpdateHotkeyDisplay(HotkeyTarget target, HotkeyGesture? hotkey)
    {
        if (target == HotkeyTarget.BackgroundConvert)
        {
            BackgroundConvertHotkeyValue.Text = hotkey?.DisplayText ?? "未设置";
        }
        else
        {
            ToggleHotkeyValue.Text = hotkey?.DisplayText ?? HotkeyGesture.Default.DisplayText;
        }
    }

    private void SetHotkeyRecordingHint(HotkeyTarget target, string hint)
    {
        if (target == HotkeyTarget.BackgroundConvert)
        {
            BackgroundConvertHotkeyHint.Text = hint;
        }
        else
        {
            ToggleHotkeyHint.Text = hint;
        }
    }

    private void SetHotkeyRecordingText(HotkeyTarget target, string value, string hint)
    {
        if (target == HotkeyTarget.BackgroundConvert)
        {
            BackgroundConvertHotkeyValue.Text = value;
            BackgroundConvertHotkeyHint.Text = hint;
        }
        else
        {
            ToggleHotkeyValue.Text = value;
            ToggleHotkeyHint.Text = hint;
        }
    }

    private void ShowHotkeyError(string error)
    {
        DispatcherQueue.TryEnqueue(() => ShowMessage(InfoBarSeverity.Error, error));
    }

    private IntPtr HotkeyWindowProcImpl(
        IntPtr hWnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam)
    {
        if (_trayIcon?.HandleMessage(message, lParam) == true)
        {
            return IntPtr.Zero;
        }

        if (message == WmHotkey && wParam.ToInt64() == ToggleHotkeyId)
        {
            ToggleWindowFromHotkey();
            return IntPtr.Zero;
        }

        if (message == WmHotkey && wParam.ToInt64() == BackgroundConvertHotkeyId)
        {
            DispatcherQueue.TryEnqueue(async () => await ConvertClipboardFromHotkeyAsync());
            return IntPtr.Zero;
        }

        return DefWindowProc(hWnd, message, wParam, lParam);
    }

    private async Task ConvertClipboardFromHotkeyAsync()
    {
        if (Interlocked.Exchange(ref _backgroundClipboardBusy, 1) != 0)
        {
            AppNotificationService.Show("Markdown 转 Word", "剪贴板转换正在进行。");
            return;
        }

        try
        {
            var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text))
            {
                AppNotificationService.Show("Markdown 转 Word", "剪贴板中没有可转换的文本。");
                return;
            }

            var markdown = await content.GetTextAsync();
            if (string.IsNullOrWhiteSpace(markdown))
            {
                AppNotificationService.Show("Markdown 转 Word", "剪贴板中没有可转换的文本。");
                return;
            }

            if (await IsOwnClipboardContentAsync())
            {
                AppNotificationService.Show("Markdown 转 Word", "剪贴板已经是带格式的转换结果。");
                return;
            }

            var baseDirectory = _currentPath is null
                ? _settings.LastDirectory
                : Path.GetDirectoryName(_currentPath);
            var warnings = await ConvertMarkdownToClipboardAsync(markdown, baseDirectory);
            var message = warnings.Count == 0
                ? "剪贴板已替换为带格式的 Word 正文。"
                : $"剪贴板已替换；另有 {warnings.Count} 条警告：{warnings[0]}";
            AppNotificationService.Show("Markdown 转 Word", message);
        }
        catch (Exception ex)
        {
            AppNotificationService.Show("Markdown 转 Word", "后台转换失败：" + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _backgroundClipboardBusy, 0);
        }
    }

    private void ToggleWindowFromHotkey()
    {
        var handle = EnsureWindowHandle();
        if (handle == IntPtr.Zero)
        {
            return;
        }

        if (IsWindowInForeground(handle))
        {
            ShowWindow(handle, SwMinimize);
            return;
        }

        if (IsIconic(handle))
        {
            ShowWindow(handle, SwRestore);
        }

        Activate();
        SetForegroundWindow(handle);
    }

    private static bool IsWindowInForeground(IntPtr handle)
    {
        var foreground = GetForegroundWindow();
        return foreground == handle || GetAncestor(foreground, 3) == handle;
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _trayIcon?.Dispose();
        _trayIcon = null;
        _recordingHotkeyTarget = HotkeyTarget.None;
        UnregisterAllHotkeys();
        AppNotificationService.Shutdown();
        if (_hotkeyWindow != IntPtr.Zero)
        {
            DestroyWindow(_hotkeyWindow);
            _hotkeyWindow = IntPtr.Zero;
        }

        if (!string.IsNullOrEmpty(_hotkeyWindowClass))
        {
            UnregisterClass(_hotkeyWindowClass, GetModuleHandle(null));
            _hotkeyWindowClass = string.Empty;
        }

        SaveSettings();
    }

    private void InitializeTrayIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(iconPath))
            {
                _trayIcon = new TrayIconService(
                    _hotkeyWindow,
                    iconPath,
                    "Markdown 转 Word · 离线版",
                    ShowWindowFromTray,
                    ExitFromTray);
            }
        }
        catch
        {
            _trayIcon = null;
        }
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowExit || _trayIcon is null)
        {
            return;
        }

        args.Cancel = true;
        sender.Hide();
    }

    private void ShowWindowFromTray()
    {
        var handle = EnsureWindowHandle();
        if (handle == IntPtr.Zero)
        {
            return;
        }

        if (IsIconic(handle))
        {
            ShowWindow(handle, SwRestore);
        }

        AppWindow.Show();
        Activate();
        SetForegroundWindow(handle);
    }

    private void ExitFromTray()
    {
        _allowExit = true;
        _trayIcon?.Dispose();
        _trayIcon = null;
        Close();
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated &&
            _recordingHotkeyTarget is not HotkeyTarget.None)
        {
            CancelHotkeyRecording(_recordingHotkeyTarget);
        }
    }

    private async Task InitializePreviewAsync()
    {
        try
        {
            await PreviewView.EnsureCoreWebView2Async();

            var assets = Path.Combine(AppContext.BaseDirectory, "Assets", "preview");
            PreviewView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                VirtualHost,
                assets,
                CoreWebView2HostResourceAccessKind.Allow);

            PreviewView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            PreviewView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            PreviewView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            PreviewView.CoreWebView2.Settings.IsZoomControlEnabled = true;
            PreviewView.CoreWebView2.WebMessageReceived += OnPreviewWebMessageReceived;
            PreviewView.CoreWebView2.NavigationCompleted += (_, _) =>
            {
                _previewReady = true;
                UpdatePreview();
            };

            PreviewView.CoreWebView2.Navigate($"https://{VirtualHost}/preview.html");
        }
        catch (Exception ex)
        {
            _previewFailed = true;
            PreviewView.Visibility = Visibility.Collapsed;
            PreviewFallback.Visibility = Visibility.Visible;
            PreviewFallbackText.Text =
                "无法启用内置预览（通常是缺少 WebView2 运行时）。\n\n" +
                "转换功能不受影响，导出 Word 依然可用。\n\n详细信息：" + ex.Message;
        }
    }

    /// <summary>主窗口被 cloak（切换虚拟桌面）或最小化时折叠预览，恢复时显示。</summary>
    private void SyncPreviewHostVisibility()
    {
        if (_previewFailed || !_previewReady)
        {
            return;
        }

        var handle = EnsureWindowHandle();
        if (handle == IntPtr.Zero)
        {
            return;
        }

        DwmGetWindowAttribute(handle, DwmwaCloaked, out var cloaked, sizeof(int));
        var visible = cloaked == 0 && !IsIconic(handle);
        if (visible != _previewSuspended)
        {
            return;
        }

        _previewSuspended = !visible;
        PreviewView.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private IntPtr EnsureWindowHandle()
    {
        if (_windowHandle == IntPtr.Zero)
        {
            _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        }

        return _windowHandle;
    }

    private const int DwmwaCloaked = 14;

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    // ------------------------------------------------------------ 预览

    private void SchedulePreview()
    {
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private async void UpdatePreview()
    {
        if (!_previewReady)
        {
            return;
        }

        try
        {
            var html = MarkdownConverter.BuildPreviewFragment(EditorBox.Text, _options.EnableMath);
            var payload = JsonSerializer.Serialize(html);
            var dark = RootGrid.ActualTheme == ElementTheme.Dark ? "true" : "false";
            var math = _options.EnableMath ? "true" : "false";
            await PreviewView.CoreWebView2.ExecuteScriptAsync(
                $"window.renderMarkdown && window.renderMarkdown({payload}, {dark}, {math});");
        }
        catch
        {
            // 预览失败不打扰用户
        }
    }

    private async void OnPreviewWebMessageReceived(
        CoreWebView2 sender,
        CoreWebView2WebMessageReceivedEventArgs args)
    {
        string message;
        try
        {
            message = args.TryGetWebMessageAsString();
        }
        catch
        {
            return;
        }

        if (!message.Equals("copy-word", StringComparison.Ordinal))
        {
            return;
        }

        PlayCopyButtonPressFeedback();
        await CopyWordContentAsync("预览复制失败：");
    }

    private void UpdateStats()
    {
        var text = EditorBox.Text ?? string.Empty;
        // WinUI 的 TextBox 内部把换行统一成 \r，这里统一按 CR/LF/CRLF 计数
        var lines = text.Length == 0
            ? 0
            : text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Length;
        StatsText.Text = $"{text.Length} 字符 · {lines} 行";
    }

    private void OnEditorTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateStats();
        SchedulePreview();
    }

    // ------------------------------------------------------------ 文件操作

    private async void OnOpenClicked(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };

        picker.FileTypeFilter.Add(".md");
        picker.FileTypeFilter.Add(".markdown");
        picker.FileTypeFilter.Add(".mdown");
        picker.FileTypeFilter.Add(".txt");
        InitializePicker(picker);

        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        await LoadFileAsync(file);
    }

    private async Task LoadFileAsync(StorageFile file)
    {
        try
        {
            var text = await ReadTextAsync(file.Path);
            EditorBox.Text = text;
            _currentPath = file.Path;
            _settings.LastDirectory = Path.GetDirectoryName(file.Path);
            SaveSettings();
            Title = $"{Path.GetFileName(file.Path)} — Markdown 转 Word 离线版";
            ShowMessage(InfoBarSeverity.Success, $"已打开 {Path.GetFileName(file.Path)}（{TextUtils.HumanSize(new FileInfo(file.Path).Length)}）");
            UpdateStats();
            SchedulePreview();
        }
        catch (Exception ex)
        {
            ShowMessage(InfoBarSeverity.Error, "打开失败：" + ex.Message);
        }
    }

    private static async Task<string> ReadTextAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // 不是 UTF-8：按 GB18030（中文 Windows 上 .md 文件的常见编码）再试一次
            try
            {
                return Encoding.GetEncoding("GB18030").GetString(bytes);
            }
            catch
            {
                return Encoding.UTF8.GetString(bytes);
            }
        }
    }

    private async void OnSaveMarkdownClicked(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = _currentPath is null ? "document" : Path.GetFileNameWithoutExtension(_currentPath),
        };

        picker.FileTypeChoices.Add("Markdown 文件", [".md"]);
        InitializePicker(picker);

        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(file.Path, EditorBox.Text ?? string.Empty, new UTF8Encoding(true));
            _currentPath = file.Path;
            _settings.LastDirectory = Path.GetDirectoryName(file.Path);
            SaveSettings();
            ShowMessage(InfoBarSeverity.Success, "Markdown 已保存到 " + file.Path);
        }
        catch (Exception ex)
        {
            ShowMessage(InfoBarSeverity.Error, "保存失败：" + ex.Message);
        }
    }

    private async void OnExportClicked(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        var markdown = EditorBox.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(markdown))
        {
            ShowMessage(InfoBarSeverity.Warning, "内容为空，先输入或粘贴 Markdown 再导出。");
            return;
        }

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = _currentPath is null
                ? "document"
                : Path.GetFileNameWithoutExtension(_currentPath),
        };

        picker.FileTypeChoices.Add("Word 文档", [".docx"]);
        InitializePicker(picker);

        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        var baseDirectory = _currentPath is null ? null : Path.GetDirectoryName(_currentPath);

        try
        {
            SetBusy(true, "正在转换…");
            var stats = await _converter.ConvertToFileAsync(markdown, file.Path, baseDirectory);

            var summary =
                $"已导出：{file.Path}\n标题 {stats.Headings} · 段落 {stats.Paragraphs} · 列表 {stats.Lists} · " +
                $"表格 {stats.Tables} · 代码块 {stats.CodeBlocks} · 公式 {stats.Equations} · 图片 {stats.Images} · 脚注 {stats.Footnotes}";

            if (stats.Warnings.Count > 0)
            {
                summary += "\n注意：" + string.Join("；", stats.Warnings.Take(3));
            }

            _settings.LastDirectory = Path.GetDirectoryName(file.Path);
            SaveSettings();
            ShowMessage(InfoBarSeverity.Success, summary);
        }
        catch (Exception ex)
        {
            ShowMessage(InfoBarSeverity.Error, "转换失败：" + ex.Message);
        }
        finally
        {
            SetBusy(false, "就绪");
        }
    }

    /// <summary>
    /// 复制带格式正文：HTML 负责 Word/OneNote，Unicode 文本负责纯文本环境。
    /// </summary>
    private async void OnCopyClicked(object sender, RoutedEventArgs e)
    {
        await CopyWordContentAsync("复制失败：");
    }

    /// <summary>
    /// 「复制 Word」的核心：生成带格式正文并写入剪贴板。
    /// 手动按钮与"粘贴后自动复制"共用这条路径，反馈（横幅、忙碌环）保持一致。
    /// </summary>
    private async Task CopyWordContentAsync(string errorPrefix)
    {
        if (_busy)
        {
            return;
        }

        var markdown = EditorBox.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(markdown))
        {
            ShowMessage(InfoBarSeverity.Warning, "内容为空，先输入或粘贴 Markdown 再复制。");
            return;
        }

        var generation = ++_clipboardGeneration;
        try
        {
            SetBusy(true, "正在生成带格式的正文…");
            var baseDirectory = _currentPath is null ? null : Path.GetDirectoryName(_currentPath);
            var warnings = await ConvertMarkdownToClipboardAsync(markdown, baseDirectory);
            if (generation != _clipboardGeneration)
            {
                return;
            }

            var extra = warnings.Count == 0 ? string.Empty : $"\n警告 {warnings.Count} 条：{warnings[0]}";
            ShowMessage(
                InfoBarSeverity.Success,
                "已复制带格式的正文：在 Word 里 Ctrl+V 会插到光标处，公式还是 Word 原生公式。" + extra);
        }
        catch (Exception ex)
        {
            if (generation == _clipboardGeneration)
            {
                ShowMessage(InfoBarSeverity.Error, errorPrefix + ex.Message);
            }
        }
        finally
        {
            if (generation == _clipboardGeneration)
            {
                SetBusy(false, "就绪");
            }
        }
    }

    private Task<List<string>> ConvertMarkdownToClipboardAsync(string markdown, string? baseDirectory)
    {
        return Task.Run(() => RichTextClipboard.Copy(markdown, _options, baseDirectory));
    }

    private void OnClearClicked(object sender, RoutedEventArgs e)
    {
        EditorBox.Text = string.Empty;
        _currentPath = null;
        Title = "Markdown 转 Word · 离线版";
        UpdateStats();
        SchedulePreview();
    }

    /// <summary>
    /// 编辑器内粘贴后自动跑一次「复制 Word」。
    /// 事件在文本插入前触发，所以推一个 tick 再读全文；成功才写剪贴板。
    /// </summary>
    private void OnEditorPaste(object sender, TextControlPasteEventArgs e)
    {
        if (!_autoCopyAfterPaste)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(async () => await AutoCopyAsync());
    }

    private async Task AutoCopyAsync()
    {
        if (!_autoCopyAfterPaste)
        {
            return;
        }

        var markdown = EditorBox.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return;
        }

        // 粘的是本程序自己生成的剪贴板内容：跳过，避免把渲染后的纯文本当源码再转一次
        if (await IsOwnClipboardContentAsync())
        {
            return;
        }

        // 与手动点击同一条路径：按钮按下动画 + 成功横幅 + 忙碌环
        PlayCopyButtonPressFeedback();
        await CopyWordContentAsync("自动复制失败：");
    }

    /// <summary>程序触发复制时给「复制 Word」按钮补一次按下/回弹动画。</summary>
    private void PlayCopyButtonPressFeedback()
    {
        if (CopyWordButton.RenderTransform is not ScaleTransform)
        {
            CopyWordButton.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
            CopyWordButton.RenderTransform = new ScaleTransform();
        }

        var storyboard = new Storyboard();
        storyboard.Children.Add(CreatePressAnimation("ScaleX"));
        storyboard.Children.Add(CreatePressAnimation("ScaleY"));
        storyboard.Begin();
    }

    private DoubleAnimationUsingKeyFrames CreatePressAnimation(string property)
    {
        var animation = new DoubleAnimationUsingKeyFrames();
        animation.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = TimeSpan.Zero,
            Value = 1,
        });
        animation.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = TimeSpan.FromMilliseconds(90),
            Value = 0.9,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        animation.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = TimeSpan.FromMilliseconds(230),
            Value = 1,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        });

        Storyboard.SetTarget(animation, CopyWordButton);
        Storyboard.SetTargetProperty(animation, "(UIElement.RenderTransform).(ScaleTransform." + property + ")");
        return animation;
    }

    private static async Task<bool> IsOwnClipboardContentAsync()
    {
        try
        {
            // 必须写全名：本类的命名空间 MarkdownToWord.Clipboard 会遮蔽同名类型
            var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Html))
            {
                return false;
            }

            var html = await content.GetHtmlFormatAsync();
            return html.Contains("MarkdownToWord", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static void InitializePicker(object picker)
    {
        var window = (Application.Current as App)?.MainWindow;
        if (window is null)
        {
            return;
        }

        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, handle);
    }

    // ------------------------------------------------------------ 拖放

    private void OnRootDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "打开 Markdown 文件";
        }
    }

    private async void OnRootDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var file = items.OfType<StorageFile>().FirstOrDefault();
            if (file is not null)
            {
                await LoadFileAsync(file);
            }
        }
        catch (Exception ex)
        {
            ShowMessage(InfoBarSeverity.Error, "拖入文件失败：" + ex.Message);
        }
    }

    // ------------------------------------------------------------ 设置

    private void OnOptionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        ApplyOptionsFromUi();
        SaveSettings();
        SchedulePreview();
    }

    private void OnSettingsClicked(object sender, RoutedEventArgs e)
    {
        if (SettingsPanel.Visibility == Visibility.Visible &&
            _recordingHotkeyTarget is not HotkeyTarget.None)
        {
            CancelHotkeyRecording(_recordingHotkeyTarget);
        }

        SettingsPanel.Visibility = SettingsPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void OnSettingsCloseClicked(object sender, RoutedEventArgs e)
    {
        if (_recordingHotkeyTarget is not HotkeyTarget.None)
        {
            CancelHotkeyRecording(_recordingHotkeyTarget);
        }

        SettingsPanel.Visibility = Visibility.Collapsed;
    }

    private void OnOptionToggled(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        ApplyOptionsFromUi();
        SaveSettings();
        SchedulePreview();
    }

    private void OnMarginChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        _options.MarginCm = Math.Round(e.NewValue, 2);
        _settings.MarginCm = _options.MarginCm;
        SaveSettings();
    }

    /// <summary>
    /// 五个下拉项：ComboBox 的弹出列表在本机 WinUI 3 上收不到鼠标点击（键盘与辅助功能可选中），
    /// 改用按钮 + Flyout(RadioButton 列表)，外观仍是下拉，点选走飞出行。
    /// </summary>
    private void InitChoices()
    {
        SetupChoice(PageSizeButton, PageSizeValue, PageSizeOptions, () => _pageSizeIndex, i => _pageSizeIndex = i, OnChoicePicked);
        SetupChoice(OrientationButton, OrientationValue, OrientationOptions, () => _orientationIndex, i => _orientationIndex = i, OnChoicePicked);
        SetupChoice(BodyFontButton, BodyFontValue, FontOptions, () => _fontIndex, i => _fontIndex = i, OnChoicePicked);
        SetupChoice(BodySizeButton, BodySizeValue, SizeOptions, () => _sizeIndex, i => _sizeIndex = i, OnChoicePicked);
        SetupChoice(ThemeButton, ThemeValue, ThemeOptions, () => _themeIndex, i => _themeIndex = i, OnThemePicked);
    }

    private static void SetupChoice(
        Button button,
        TextBlock value,
        string[] options,
        Func<int> getIndex,
        Action<int> setIndex,
        Action afterPick)
    {
        var panel = new StackPanel { Spacing = 2, MinWidth = 200 };
        var flyout = new Flyout
        {
            Content = panel,
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft,
        };
        var syncing = false;

        void Sync()
        {
            syncing = true;
            panel.Children.Clear();
            var current = Math.Clamp(getIndex(), 0, options.Length - 1);
            for (var i = 0; i < options.Length; i++)
            {
                var index = i;
                var item = new RadioButton
                {
                    Content = options[i],
                    GroupName = "choice_" + button.Name,
                    IsChecked = index == current,
                };
                item.Checked += (_, _) =>
                {
                    if (syncing)
                    {
                        return;
                    }

                    flyout.Hide();
                    if (getIndex() == index)
                    {
                        return;
                    }

                    setIndex(index);
                    value.Text = options[index];
                    afterPick();
                };
                panel.Children.Add(item);
            }

            value.Text = options[current];
            syncing = false;
        }

        button.Flyout = flyout;
        flyout.Opening += (_, _) => Sync();
        Sync();
    }

    private void OnChoicePicked()
    {
        ApplyOptionsFromUi();
        SaveSettings();
        SchedulePreview();
    }

    private void OnThemePicked()
    {
        _settings.Theme = _themeIndex switch
        {
            1 => "light",
            2 => "dark",
            _ => "system",
        };

        ApplyTheme();
        SaveSettings();
        UpdatePreview();
    }

    private void ApplyTheme()
    {
        RootGrid.RequestedTheme = _settings.Theme switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }

    private void ApplyOptionsFromUi()
    {
        _options.PageSize = _pageSizeIndex switch
        {
            1 => PageSizePreset.Letter,
            2 => PageSizePreset.A5,
            3 => PageSizePreset.Legal,
            _ => PageSizePreset.A4,
        };

        _options.Orientation = _orientationIndex == 1
            ? PageOrientationPreset.Landscape
            : PageOrientationPreset.Portrait;

        _options.MarginCm = Math.Round(MarginSlider.Value, 2);

        var font = FontOptions[Math.Clamp(_fontIndex, 0, FontOptions.Length - 1)];
        _options.BodyFont = font == "Segoe UI" ? "Calibri" : "Calibri";
        _options.BodyFontEastAsia = font == "Segoe UI" ? "等线" : font;
        _options.HeadingFont = "Calibri";
        _options.HeadingFontEastAsia = font == "Segoe UI" ? "等线" : font;

        _options.NumberHeadings = NumberHeadingsToggle.IsOn;
        _options.SyntaxHighlight = HighlightToggle.IsOn;
        _options.EnableMath = MathToggle.IsOn;
        _options.AllowRemoteImages = RemoteImageToggle.IsOn;
        _options.InsertFrontMatterTitle = TitleFromFrontMatterToggle.IsOn;
        _autoCopyAfterPaste = AutoCopyToggle.IsOn;
        _options.ClipboardMode = ClipboardMode.RichText;

        if (double.TryParse(SizeOptions[Math.Clamp(_sizeIndex, 0, SizeOptions.Length - 1)], out var size))
        {
            _options.BodyFontSizePt = size;
            _options.CodeFontSizePt = Math.Max(8, size - 1.5);
        }

        _settings.PageSize = _options.PageSize.ToString();
        _settings.Landscape = _options.Orientation == PageOrientationPreset.Landscape;
        _settings.MarginCm = _options.MarginCm;
        _settings.BodyFont = font;
        _settings.BodyFontSizePt = _options.BodyFontSizePt;
        _settings.NumberHeadings = _options.NumberHeadings;
        _settings.SyntaxHighlight = _options.SyntaxHighlight;
        _settings.EnableMath = _options.EnableMath;
        _settings.AllowRemoteImages = _options.AllowRemoteImages;
        _settings.TitleFromFrontMatter = _options.InsertFrontMatterTitle;
        _settings.ClipboardMode = _options.ClipboardMode.ToString();
        _settings.AutoCopyAfterPaste = _autoCopyAfterPaste;
    }

    private void ApplySettingsToUi()
    {
        _toggleHotkey = ReadConfiguredToggleHotkey();
        _backgroundConvertHotkey = ReadConfiguredBackgroundConvertHotkey();
        UpdateHotkeyDisplay(HotkeyTarget.ToggleWindow, _toggleHotkey);
        UpdateHotkeyDisplay(HotkeyTarget.BackgroundConvert, _backgroundConvertHotkey);
        WindowHotkeyToggle.IsOn = _settings.ToggleWindowHotkeyEnabled;
        WindowHotkeyPanel.IsHitTestVisible = _settings.ToggleWindowHotkeyEnabled;
        WindowHotkeyPanel.Opacity = _settings.ToggleWindowHotkeyEnabled ? 1 : 0.55;
        ToggleHotkeyHint.Text = _settings.ToggleWindowHotkeyEnabled
            ? "按快捷键可在前台最小化，或在后台恢复并聚焦。"
            : "窗口快捷键已关闭。";
        BackgroundConvertToggle.IsOn = _settings.BackgroundConvertEnabled;
        BackgroundConvertHotkeyPanel.IsHitTestVisible = _settings.BackgroundConvertEnabled;
        BackgroundConvertHotkeyPanel.Opacity = _settings.BackgroundConvertEnabled ? 1 : 0.55;
        BackgroundConvertHotkeyHint.Text = !_settings.BackgroundConvertEnabled
            ? "开启后点击按钮录入组合键；操作在后台完成并发送系统通知。"
            : _backgroundConvertHotkey is null
                ? "点击按钮录入组合键；未设置时功能不会触发。"
                : "按快捷键会在后台转换剪贴板，并发送系统通知。";

        _pageSizeIndex = _settings.PageSize switch
        {
            "Letter" => 1,
            "A5" => 2,
            "Legal" => 3,
            _ => 0,
        };
        PageSizeValue.Text = PageSizeOptions[_pageSizeIndex];

        _orientationIndex = _settings.Landscape ? 1 : 0;
        OrientationValue.Text = OrientationOptions[_orientationIndex];
        MarginSlider.Value = Math.Clamp(_settings.MarginCm, 1, 4);

        _fontIndex = _settings.BodyFont switch
        {
            "微软雅黑" => 1,
            "宋体" => 2,
            "仿宋" => 3,
            "Segoe UI" => 4,
            _ => 0,
        };
        BodyFontValue.Text = FontOptions[_fontIndex];

        _sizeIndex = _settings.BodyFontSizePt switch
        {
            10 => 0,
            10.5 => 1,
            12 => 3,
            14 => 4,
            _ => 2,
        };
        BodySizeValue.Text = SizeOptions[_sizeIndex];

        NumberHeadingsToggle.IsOn = _settings.NumberHeadings;
        HighlightToggle.IsOn = _settings.SyntaxHighlight;
        MathToggle.IsOn = _settings.EnableMath;
        RemoteImageToggle.IsOn = _settings.AllowRemoteImages;
        TitleFromFrontMatterToggle.IsOn = _settings.TitleFromFrontMatter;
        _autoCopyAfterPaste = _settings.AutoCopyAfterPaste;
        AutoCopyToggle.IsOn = _autoCopyAfterPaste;
        _themeIndex = _settings.Theme switch
        {
            "light" => 1,
            "dark" => 2,
            _ => 0,
        };
        ThemeValue.Text = ThemeOptions[_themeIndex];

        ApplyTheme();
    }

    private static string SettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MarkdownToWord",
            "settings.json");

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                _settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
            }
        }
        catch
        {
            _settings = new AppSettings();
        }
    }

    private void SaveSettings()
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(_settings, JsonOptions));
        }
        catch
        {
            // 设置保存失败不影响主流程
        }
    }

    // ------------------------------------------------------------ 分栏拖动

    private void OnSplitterManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        var editorWidth = EditorColumn.ActualWidth;
        var previewWidth = PreviewColumn.ActualWidth;
        var total = editorWidth + previewWidth;
        if (total <= 0)
        {
            return;
        }

        var target = Math.Clamp(editorWidth + e.Delta.Translation.X, 220, total - 220);
        EditorColumn.Width = new GridLength(target, GridUnitType.Pixel);
        PreviewColumn.Width = new GridLength(total - target, GridUnitType.Pixel);
    }

    // ------------------------------------------------------------ 杂项

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        BusyRing.IsActive = busy;
        BusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = status;
    }

    /// <summary>标题栏/任务栏也用同一枚图标（exe 图标由 csproj 的 ApplicationIcon 带上）。</summary>
    private void ApplyWindowIcon()
    {
        try
        {
            var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(icon))
            {
                AppWindow.SetIcon(icon);
            }
        }
        catch
        {
            // 图标设置失败不影响使用
        }
    }

    private void ShowMessage(InfoBarSeverity severity, string message)
    {
        MessageBar.Severity = severity;
        MessageBar.Message = message;
        MessageBar.IsOpen = true;

        AnimateMessage(show: true);

        _messageTimer.Stop();
        if (severity is InfoBarSeverity.Success or InfoBarSeverity.Informational)
        {
            _messageTimer.Start();
        }
    }

    /// <summary>收起横幅：先播收回动画，动画结束后再真正关掉。</summary>
    private void HideMessage()
    {
        _messageTimer.Stop();

        // 已经收起来了就别再播一遍（正在弹出途中被关掉也要能收回去）
        if (!_messageVisible && MessageBar.ActualHeight <= 0.5)
        {
            return;
        }

        AnimateMessage(show: false);
    }

    /// <summary>点横幅自己的关闭按钮时，也走收回动画，而不是"啪"地消失。</summary>
    private void OnMessageBarClosing(InfoBar sender, InfoBarClosingEventArgs args)
    {
        args.Cancel = true;
        HideMessage();
    }

    /// <summary>
    /// 横幅的弹出/收回动画：高度从 0 展开到自然高度（收回时反过来）并配合淡入淡出。
    /// 高度参与布局，所以下面的编辑框/预览会跟着一起平滑移动，不会突然跳一下。
    /// </summary>
    private void AnimateMessage(bool show)
    {
        var fromHeight = Math.Max(0, MessageBar.ActualHeight);
        var toHeight = show ? MeasureMessageHeight() : 0;
        if (show)
        {
            fromHeight = _messageVisible ? fromHeight : 0;
        }

        // 上一次动画还没播完又来了新消息：直接掐掉重来
        _messageStoryboard?.Stop();

        var duration = new Duration(TimeSpan.FromMilliseconds(show ? 220 : 170));
        var easing = new CubicEase { EasingMode = show ? EasingMode.EaseOut : EasingMode.EaseIn };

        // 高度是布局属性，必须开 EnableDependentAnimation 才会真的动
        var grow = new DoubleAnimation
        {
            From = fromHeight,
            To = toHeight,
            Duration = duration,
            EasingFunction = easing,
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(grow, MessageBar);
        Storyboard.SetTargetProperty(grow, "Height");

        var fade = new DoubleAnimation
        {
            From = MessageBar.Opacity,
            To = show ? 1 : 0,
            Duration = duration,
            EasingFunction = easing,
        };
        Storyboard.SetTarget(fade, MessageBar);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(grow);
        storyboard.Children.Add(fade);

        storyboard.Completed += (_, _) =>
        {
            if (show)
            {
                // 固定在量出来的高度：和真实布局一致，落地时不会再来一下微跳
                MessageBar.Height = toHeight;
                MessageBar.Opacity = 1;
                _messageVisible = true;
            }
            else
            {
                MessageBar.Height = 0;
                MessageBar.Opacity = 0;
                _messageVisible = false;
            }
        };

        _messageStoryboard = storyboard;
        storyboard.Begin();
    }

    /// <summary>按当前宽度量一下横幅的自然高度（撑开动画的目标值）。</summary>
    private double MeasureMessageHeight()
    {
        // 先让布局按真实宽度算一遍，量到的高度才和动画结束后的实际高度一致
        MessageBar.Height = double.NaN;
        MessageBar.Opacity = 0;
        MessageBar.UpdateLayout();

        var height = MessageBar.ActualHeight;
        if (double.IsNaN(height) || height <= 0)
        {
            height = MessageBar.DesiredSize.Height;
        }

        if (double.IsNaN(height) || height <= 0)
        {
            height = 48;   // 实在量不出来时的兜底高度
        }

        MessageBar.Height = 0;
        return height;
    }

    private sealed class AppSettings
    {
        public string PageSize { get; set; } = "A4";

        public bool Landscape { get; set; }

        public double MarginCm { get; set; } = 2.54;

        public string BodyFont { get; set; } = "等线";

        public double BodyFontSizePt { get; set; } = 11;

        public bool NumberHeadings { get; set; }

        public bool SyntaxHighlight { get; set; } = true;

        public bool EnableMath { get; set; } = true;

        public bool AllowRemoteImages { get; set; } = true;

        public bool TitleFromFrontMatter { get; set; } = true;

        public string ClipboardMode { get; set; } = "RichText";

        public bool AutoCopyAfterPaste { get; set; } = true;

        public int ToggleHotkeyModifiers { get; set; } = (int)ModAlt;

        public int ToggleHotkeyKey { get; set; } = (int)VirtualKey.D;

        public bool ToggleWindowHotkeyEnabled { get; set; } = true;

        public bool BackgroundConvertEnabled { get; set; }

        public int BackgroundConvertHotkeyModifiers { get; set; }

        public int BackgroundConvertHotkeyKey { get; set; }

        public string Theme { get; set; } = "system";

        public string? LastDirectory { get; set; }
    }

    private enum HotkeyTarget
    {
        None,
        ToggleWindow,
        BackgroundConvert,
    }

    private readonly record struct HotkeyGesture(uint Modifiers, uint Key)
    {
        public static HotkeyGesture Default => new(ModAlt, (uint)VirtualKey.D);

        public string DisplayText => string.Join(
            " + ",
            ModifierParts(Modifiers).Append(KeyLabel(Key)));

        public static bool IsModifierKey(VirtualKey key) => key is
            VirtualKey.Control or
            VirtualKey.Menu or
            VirtualKey.Shift or
            VirtualKey.LeftWindows or
            VirtualKey.RightWindows;

        private static IEnumerable<string> ModifierParts(uint modifiers)
        {
            if ((modifiers & ModControl) != 0)
            {
                yield return "Ctrl";
            }

            if ((modifiers & ModAlt) != 0)
            {
                yield return "Alt";
            }

            if ((modifiers & ModShift) != 0)
            {
                yield return "Shift";
            }

            if ((modifiers & ModWin) != 0)
            {
                yield return "Win";
            }
        }

        private static string KeyLabel(uint key)
        {
            var symbol = key switch
            {
                0xBA => ";",
                0xBB => "=",
                0xBC => ",",
                0xBD => "-",
                0xBE => ".",
                0xBF => "/",
                0xC0 => "`",
                0xDB => "[",
                0xDC => "\\",
                0xDD => "]",
                0xDE => "'",
                _ => null,
            };
            if (symbol is not null)
            {
                return symbol;
            }

            var virtualKey = (VirtualKey)key;
            if (virtualKey is >= VirtualKey.A and <= VirtualKey.Z)
            {
                return ((char)key).ToString();
            }

            if (virtualKey is >= VirtualKey.Number0 and <= VirtualKey.Number9)
            {
                return ((char)key).ToString();
            }

            return virtualKey switch
            {
                VirtualKey.Space => "Space",
                VirtualKey.Tab => "Tab",
                VirtualKey.Back => "Backspace",
                VirtualKey.Delete => "Delete",
                VirtualKey.Insert => "Insert",
                VirtualKey.Home => "Home",
                VirtualKey.End => "End",
                VirtualKey.PageUp => "Page Up",
                VirtualKey.PageDown => "Page Down",
                VirtualKey.Left => "Left",
                VirtualKey.Right => "Right",
                VirtualKey.Up => "Up",
                VirtualKey.Down => "Down",
                _ => virtualKey.ToString(),
            };
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowMessageProc(
        IntPtr hWnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        public uint Size;
        public uint Style;
        public WindowMessageProc WindowProc;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "RegisterClassExW")]
    private static extern ushort RegisterClassEx(ref WindowClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateWindowExW")]
    private static extern IntPtr CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "UnregisterClassW")]
    private static extern bool UnregisterClass(string className, IntPtr instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProc(
        IntPtr hWnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    private const string SampleMarkdown = """
# Markdown 转 Word · 离线版

把左边的内容换成你自己的 Markdown，右边会实时预览，点「导出 Word」即可得到 **.docx**。

## 支持的语法

- **粗体**、*斜体*、~~删除线~~、==高亮==、`行内代码`
- 有序 / 无序 / 嵌套列表、任务列表
- 表格（含对齐）、引用、代码块（自动高亮）
- LaTeX 公式：行内 \(E = mc^2\)，以及独立公式
- 脚注、定义列表、图片、超链接、分隔线

### 表格

| 功能 | 状态 | 备注 |
| :--- | :--: | ---: |
| 基础语法 | ✅ | 标题 / 列表 / 强调 |
| 代码块 | ✅ | C#、Python、JSON、Bash… |
| 数学公式 | ✅ | 转为 Word 原生公式 |

### 代码块

```csharp
public static int Sum(int[] values)
{
    // 语法高亮会一起带进 Word
    return values.Where(v => v > 0).Sum();
}
```

### 公式

$$\int_{0}^{\infty} e^{-x^2}\,\mathrm{d}x = \frac{\sqrt{\pi}}{2}$$

> 提示：所有转换都在本机完成，文档内容不会离开这台电脑。
""";
}
