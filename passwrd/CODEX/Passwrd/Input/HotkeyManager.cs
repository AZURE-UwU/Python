using System.ComponentModel;
using System.Runtime.InteropServices;
using Passwrd.Interop;
using Passwrd.Models;

namespace Passwrd.Input;

internal sealed record HotkeyRegistrationResult(
    int RegisteredCount,
    IReadOnlyList<string> Conflicts);

internal sealed class HotkeyManager : IDisposable
{
    private const int FirstHotkeyId = 0x100;

    private readonly nint _windowHandle;
    private readonly Dictionary<int, Guid> _entryIds = [];
    private bool _disposed;

    public HotkeyManager(nint windowHandle)
    {
        _windowHandle = windowHandle;
    }

    public event EventHandler<Guid>? HotkeyPressed;

    public HotkeyRegistrationResult Register(
        IEnumerable<ShortcutEntry> entries,
        bool hotkeysEnabled)
    {
        UnregisterAll();

        if (!hotkeysEnabled)
        {
            return new HotkeyRegistrationResult(0, []);
        }

        int registeredCount = 0;
        List<string> conflicts = [];
        int hotkeyId = FirstHotkeyId;

        foreach (ShortcutEntry entry in entries)
        {
            if (!entry.IsEnabled)
            {
                continue;
            }

            if (entry.Modifiers == HotkeyModifiers.None)
            {
                conflicts.Add($"{entry.Name}：至少需要一个修饰键");
                continue;
            }

            uint modifiers = (uint)entry.Modifiers | NativeMethods.ModNoRepeat;
            if (NativeMethods.RegisterHotKey(
                    _windowHandle,
                    hotkeyId,
                    modifiers,
                    (uint)entry.Key))
            {
                _entryIds[hotkeyId] = entry.Id;
                registeredCount++;
            }
            else
            {
                int errorCode = Marshal.GetLastWin32Error();
                string reason = errorCode == 1409
                    ? "组合已被占用"
                    : new Win32Exception(errorCode).Message;
                conflicts.Add($"{entry.HotkeyDisplay}：{reason}");
            }

            hotkeyId++;
        }

        return new HotkeyRegistrationResult(registeredCount, conflicts);
    }

    public void HandleHotkey(int hotkeyId)
    {
        if (_entryIds.TryGetValue(hotkeyId, out Guid entryId))
        {
            HotkeyPressed?.Invoke(this, entryId);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        UnregisterAll();
        _disposed = true;
    }

    private void UnregisterAll()
    {
        foreach (int hotkeyId in _entryIds.Keys)
        {
            NativeMethods.UnregisterHotKey(_windowHandle, hotkeyId);
        }

        _entryIds.Clear();
    }
}
