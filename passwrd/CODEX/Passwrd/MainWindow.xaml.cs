using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Passwrd.Input;
using Passwrd.Interop;
using Passwrd.Models;
using Passwrd.Services;
using Windows.Graphics;
using VirtualKey = Windows.System.VirtualKey;

namespace Passwrd;

public sealed partial class MainWindow : Window
{
    private const nuint WindowSubclassId = 1;

    private readonly SettingsStore _settingsStore = new();
    private readonly AppSettings _settings;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _saveTimer;
    private readonly SemaphoreSlim _inputLock = new(1, 1);
    private readonly NativeMethods.SubclassProc _windowSubclassProc;

    private HotkeyManager? _hotkeyManager;
    private TrayIcon? _trayIcon;
    private nint _windowHandle;
    private bool _nativeInitialized;
    private bool _allowClose;
    private bool _isSynchronizingEditor;
    private bool _isCapturingHotkey;
    private bool _isFirstRun;
    private int _registeredHotkeyCount;
    private IReadOnlyList<string> _hotkeyConflicts = [];
    private string? _storageError;
    private string? _startupMessage;

    public MainWindow()
    {
        InitializeComponent();

        SettingsLoadResult loadResult = _settingsStore.Load();
        _settings = loadResult.Settings;
        _isFirstRun = loadResult.IsFirstRun;
        _startupMessage = loadResult.ErrorMessage;

        AppWindow.Title = "Passwrd";
        string iconPath = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Passwrd.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }

        AppWindow.Resize(new SizeInt32(1120, 800));
        AppWindow.Closing += MainWindow_Closing;
        Activated += MainWindow_Activated;

        ShortcutList.ItemsSource = _settings.Shortcuts;
        KeyComboBox.ItemsSource = KeyCatalog.Options;
        KeyComboBox.DisplayMemberPath = nameof(KeyOption.Label);

        _isSynchronizingEditor = true;
        HotkeysEnabledSwitch.IsOn = _settings.HotkeysEnabled;
        _isSynchronizingEditor = false;

        _saveTimer = DispatcherQueue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(350);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => SaveSettings();

        _windowSubclassProc = WindowSubclassProc;

        if (_settings.Shortcuts.Count > 0)
        {
            ShortcutList.SelectedIndex = 0;
        }
        else
        {
            SetEditorAvailable(false);
        }
    }

    public void ShowFromTray()
    {
        if (_windowHandle == nint.Zero)
        {
            Activate();
            return;
        }

        NativeMethods.ShowWindow(_windowHandle, NativeMethods.SwRestore);
        NativeMethods.ShowWindow(_windowHandle, NativeMethods.SwShowNormal);
        Activate();
        NativeMethods.SetForegroundWindow(_windowHandle);
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (_nativeInitialized)
        {
            return;
        }

        _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        uint dpi = NativeMethods.GetDpiForWindow(_windowHandle);
        double scale = Math.Max(1d, dpi / 96d);
        AppWindow.Resize(new SizeInt32(
            (int)Math.Round(1120 * scale),
            (int)Math.Round(800 * scale)));

        if (!NativeMethods.SetWindowSubclass(
                _windowHandle,
                _windowSubclassProc,
                WindowSubclassId,
                nint.Zero))
        {
            _storageError = "无法初始化窗口消息处理。";
        }

        _hotkeyManager = new HotkeyManager(_windowHandle);
        _hotkeyManager.HotkeyPressed += HotkeyManager_HotkeyPressed;

        _trayIcon = new TrayIcon(
            _windowHandle,
            ShowFromTray,
            ToggleHotkeys,
            ExitApplication,
            () => _settings.HotkeysEnabled);
        if (!_trayIcon.Add())
        {
            _storageError = "托盘图标创建失败。";
        }

        _nativeInitialized = true;
        RefreshHotkeys();

        if (_isFirstRun)
        {
            SaveSettings();
        }
    }

    private void MainWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose)
        {
            return;
        }

        args.Cancel = true;
        HideToTray();
    }

    private void ShortcutList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ShortcutList.SelectedItem is not ShortcutEntry entry)
        {
            SetEditorAvailable(false);
            return;
        }

        SetEditorAvailable(true);
        _isSynchronizingEditor = true;

        NameTextBox.Text = entry.Name;
        ContentTextBox.Text = entry.Text;
        AutoEnterCheckBox.IsChecked = entry.AutoEnter;
        ItemEnabledSwitch.IsOn = entry.IsEnabled;
        CtrlToggle.IsChecked = entry.Modifiers.HasFlag(HotkeyModifiers.Control);
        AltToggle.IsChecked = entry.Modifiers.HasFlag(HotkeyModifiers.Alt);
        ShiftToggle.IsChecked = entry.Modifiers.HasFlag(HotkeyModifiers.Shift);
        WinToggle.IsChecked = entry.Modifiers.HasFlag(HotkeyModifiers.Win);
        KeyComboBox.SelectedItem = KeyCatalog.Find(entry.Key) ?? KeyCatalog.Options[0];

        _isSynchronizingEditor = false;
        EndHotkeyCapture();
        UpdateHotkeyPreview();
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        int number = FindAvailableNumber();
        ShortcutEntry entry = new()
        {
            Name = $"快捷输入 {_settings.Shortcuts.Count + 1}",
            Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt,
            Key = 0x30 + number,
        };

        _settings.Shortcuts.Add(entry);
        ShortcutList.SelectedItem = entry;
        ShortcutList.ScrollIntoView(entry);
        RefreshHotkeys();
        ScheduleSave();
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (ShortcutList.SelectedItem is not ShortcutEntry entry)
        {
            return;
        }

        if (_settings.Shortcuts.Count <= 1)
        {
            UpdateStatus("至少保留一条快捷输入。");
            return;
        }

        ContentDialog dialog = new()
        {
            Title = "删除快捷输入",
            Content = $"确定删除“{entry.Name}”吗？",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        int index = _settings.Shortcuts.IndexOf(entry);
        _settings.Shortcuts.Remove(entry);
        ShortcutList.SelectedIndex = Math.Min(index, _settings.Shortcuts.Count - 1);
        RefreshHotkeys();
        ScheduleSave();
    }

    private void EditorField_Changed(object sender, TextChangedEventArgs e)
    {
        if (_isSynchronizingEditor || ShortcutList.SelectedItem is not ShortcutEntry entry)
        {
            return;
        }

        entry.Name = NameTextBox.Text;
        entry.Text = ContentTextBox.Text;
        ScheduleSave();
    }

    private void EditorOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_isSynchronizingEditor || ShortcutList.SelectedItem is not ShortcutEntry entry)
        {
            return;
        }

        entry.AutoEnter = AutoEnterCheckBox.IsChecked == true;
        entry.IsEnabled = ItemEnabledSwitch.IsOn;
        ScheduleSave();
    }

    private void HotkeyModifier_Click(object sender, RoutedEventArgs e)
    {
        if (_isSynchronizingEditor || ShortcutList.SelectedItem is not ShortcutEntry entry)
        {
            return;
        }

        entry.Modifiers = ReadModifierToggles();
        UpdateHotkeyPreview();
        RefreshHotkeys();
        ScheduleSave();
    }

    private void KeyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSynchronizingEditor ||
            ShortcutList.SelectedItem is not ShortcutEntry entry ||
            KeyComboBox.SelectedItem is not KeyOption option)
        {
            return;
        }

        entry.Key = option.VirtualKey;
        UpdateHotkeyPreview();
        RefreshHotkeys();
        ScheduleSave();
    }

    private void HotkeysEnabledSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isSynchronizingEditor)
        {
            return;
        }

        _settings.HotkeysEnabled = HotkeysEnabledSwitch.IsOn;
        RefreshHotkeys();
        ScheduleSave();
    }

    private void CaptureHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isCapturingHotkey)
        {
            EndHotkeyCapture();
            return;
        }

        _isCapturingHotkey = true;
        CaptureHotkeyButton.Content = "请按组合键…";
        UpdateStatus("请按下修饰键和主键；Esc 可取消。");
        RootGrid.Focus(FocusState.Programmatic);
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_isCapturingHotkey || ShortcutList.SelectedItem is not ShortcutEntry entry)
        {
            return;
        }

        e.Handled = true;

        if (e.Key == VirtualKey.Escape)
        {
            EndHotkeyCapture();
            UpdateStatus("已取消快捷键录制。");
            return;
        }

        int key = (int)e.Key;
        if (key is NativeMethods.VkShift or NativeMethods.VkControl or
            NativeMethods.VkMenu or NativeMethods.VkLWin or NativeMethods.VkRWin)
        {
            return;
        }

        KeyOption? option = KeyCatalog.Find(key);
        if (option is null)
        {
            UpdateStatus("此按键不受支持，请换一个按键。");
            return;
        }

        HotkeyModifiers modifiers = ReadPressedModifiers();
        if (modifiers == HotkeyModifiers.None)
        {
            UpdateStatus("全局快捷键至少需要一个 Ctrl、Alt、Shift 或 Win。");
            return;
        }

        _isSynchronizingEditor = true;
        CtrlToggle.IsChecked = modifiers.HasFlag(HotkeyModifiers.Control);
        AltToggle.IsChecked = modifiers.HasFlag(HotkeyModifiers.Alt);
        ShiftToggle.IsChecked = modifiers.HasFlag(HotkeyModifiers.Shift);
        WinToggle.IsChecked = modifiers.HasFlag(HotkeyModifiers.Win);
        KeyComboBox.SelectedItem = option;
        _isSynchronizingEditor = false;

        entry.Modifiers = modifiers;
        entry.Key = option.VirtualKey;
        EndHotkeyCapture();
        UpdateHotkeyPreview();
        RefreshHotkeys();
        ScheduleSave();
    }

    private void HotkeyManager_HotkeyPressed(object? sender, Guid entryId)
    {
        _ = ExecuteShortcutAsync(entryId);
    }

    private async Task ExecuteShortcutAsync(Guid entryId)
    {
        ShortcutEntry? entry = _settings.Shortcuts.FirstOrDefault(item => item.Id == entryId);
        if (entry is null || !entry.IsEnabled || !_settings.HotkeysEnabled)
        {
            return;
        }

        if (NativeMethods.GetForegroundWindow() == _windowHandle)
        {
            UpdateStatus("设置窗口获得焦点时不会模拟输入。");
            return;
        }

        if (!await _inputLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            string text = entry.Text;
            bool pressEnterAfter = entry.AutoEnter;
            await KeyboardInputSender.SendAsync(text, pressEnterAfter);
        }
        catch (Exception ex)
        {
            if (_windowHandle != nint.Zero)
            {
                UpdateStatus($"输入失败：{ex.Message}");
            }
        }
        finally
        {
            _inputLock.Release();
        }
    }

    private nint WindowSubclassProc(
        nint windowHandle,
        uint message,
        nint wParam,
        nint lParam,
        nuint subclassId,
        nint referenceData)
    {
        if (message == NativeMethods.WmHotkey)
        {
            _hotkeyManager?.HandleHotkey(wParam.ToInt32());
            return nint.Zero;
        }

        if (_trayIcon?.TryHandleMessage(message, lParam) == true)
        {
            return nint.Zero;
        }

        return NativeMethods.DefSubclassProc(windowHandle, message, wParam, lParam);
    }

    private void RefreshHotkeys()
    {
        if (_hotkeyManager is null)
        {
            return;
        }

        HotkeyRegistrationResult result = _hotkeyManager.Register(
            _settings.Shortcuts,
            _settings.HotkeysEnabled);
        _registeredHotkeyCount = result.RegisteredCount;
        _hotkeyConflicts = result.Conflicts;
        UpdateStatus();
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveSettings()
    {
        try
        {
            _settingsStore.Save(_settings);
            _storageError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _storageError = $"保存失败：{ex.Message}";
        }

        RefreshHotkeys();
        UpdateStatus();
    }

    private void ToggleHotkeys()
    {
        _settings.HotkeysEnabled = !_settings.HotkeysEnabled;
        _isSynchronizingEditor = true;
        HotkeysEnabledSwitch.IsOn = _settings.HotkeysEnabled;
        _isSynchronizingEditor = false;
        RefreshHotkeys();
        SaveSettings();
    }

    private void ExitApplication()
    {
        _allowClose = true;
        _saveTimer.Stop();
        SaveSettings();

        _hotkeyManager?.Dispose();
        _trayIcon?.Dispose();

        if (_windowHandle != nint.Zero)
        {
            NativeMethods.RemoveWindowSubclass(
                _windowHandle,
                _windowSubclassProc,
                WindowSubclassId);
        }

        Application.Current.Exit();
    }

    private void HideToTray()
    {
        EndHotkeyCapture();
        if (_windowHandle != nint.Zero)
        {
            NativeMethods.ShowWindow(_windowHandle, NativeMethods.SwHide);
        }
    }

    private void UpdateHotkeyPreview()
    {
        if (ShortcutList.SelectedItem is not ShortcutEntry entry)
        {
            HotkeyPreviewText.Text = string.Empty;
            return;
        }

        HotkeyPreviewText.Text = $"当前组合：{entry.HotkeyDisplay}";
    }

    private void SetEditorAvailable(bool isAvailable)
    {
        EditorPanel.IsHitTestVisible = isAvailable;
        EditorPanel.Opacity = isAvailable ? 1 : 0.55;
    }

    private HotkeyModifiers ReadModifierToggles()
    {
        HotkeyModifiers modifiers = HotkeyModifiers.None;

        if (CtrlToggle.IsChecked == true)
        {
            modifiers |= HotkeyModifiers.Control;
        }

        if (AltToggle.IsChecked == true)
        {
            modifiers |= HotkeyModifiers.Alt;
        }

        if (ShiftToggle.IsChecked == true)
        {
            modifiers |= HotkeyModifiers.Shift;
        }

        if (WinToggle.IsChecked == true)
        {
            modifiers |= HotkeyModifiers.Win;
        }

        return modifiers;
    }

    private static HotkeyModifiers ReadPressedModifiers()
    {
        HotkeyModifiers modifiers = HotkeyModifiers.None;

        if (IsKeyDown(NativeMethods.VkControl))
        {
            modifiers |= HotkeyModifiers.Control;
        }

        if (IsKeyDown(NativeMethods.VkMenu))
        {
            modifiers |= HotkeyModifiers.Alt;
        }

        if (IsKeyDown(NativeMethods.VkShift))
        {
            modifiers |= HotkeyModifiers.Shift;
        }

        if (IsKeyDown(NativeMethods.VkLWin) || IsKeyDown(NativeMethods.VkRWin))
        {
            modifiers |= HotkeyModifiers.Win;
        }

        return modifiers;
    }

    private static bool IsKeyDown(int virtualKey) =>
        (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private void EndHotkeyCapture()
    {
        _isCapturingHotkey = false;
        CaptureHotkeyButton.Content = "直接按键";
    }

    private int FindAvailableNumber()
    {
        HashSet<int> usedKeys = _settings.Shortcuts
            .Where(entry =>
                entry.Modifiers == (HotkeyModifiers.Control | HotkeyModifiers.Alt))
            .Select(entry => entry.Key)
            .ToHashSet();

        for (int number = 1; number <= 9; number++)
        {
            int virtualKey = 0x30 + number;
            if (!usedKeys.Contains(virtualKey))
            {
                return number;
            }
        }

        return 0;
    }

    private void UpdateStatus(string? temporaryMessage = null)
    {
        string message;

        if (!string.IsNullOrWhiteSpace(temporaryMessage))
        {
            message = temporaryMessage;
        }
        else if (!string.IsNullOrWhiteSpace(_storageError))
        {
            message = _storageError;
        }
        else if (!_settings.HotkeysEnabled)
        {
            message = "全局快捷键已暂停。";
        }
        else if (_hotkeyConflicts.Count > 0)
        {
            message = $"{_registeredHotkeyCount} 个快捷键生效；{_hotkeyConflicts[0]}";
        }
        else if (_registeredHotkeyCount == 0)
        {
            message = "没有启用的快捷输入。";
        }
        else if (!string.IsNullOrWhiteSpace(_startupMessage))
        {
            message = _startupMessage;
        }
        else
        {
            message = $"{_registeredHotkeyCount} 个快捷键生效 · 已自动保存";
        }

        StatusText.Text = message;
        ToolTipService.SetToolTip(StatusText, message);
    }
}
