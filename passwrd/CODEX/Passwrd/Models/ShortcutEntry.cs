using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Passwrd.Input;

namespace Passwrd.Models;

public sealed class ShortcutEntry : INotifyPropertyChanged
{
    private string _name = "新快捷输入";
    private string _text = string.Empty;
    private bool _autoEnter;
    private bool _isEnabled = true;
    private HotkeyModifiers _modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt;
    private int _key = 0x31;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name
    {
        get => _name;
        set
        {
            if (SetField(ref _name, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(DisplayText));
            }
        }
    }

    public string Text
    {
        get => _text;
        set => SetField(ref _text, value ?? string.Empty);
    }

    public bool AutoEnter
    {
        get => _autoEnter;
        set => SetField(ref _autoEnter, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetField(ref _isEnabled, value);
    }

    public HotkeyModifiers Modifiers
    {
        get => _modifiers;
        set
        {
            if (SetField(ref _modifiers, value))
            {
                OnPropertyChanged(nameof(HotkeyDisplay));
                OnPropertyChanged(nameof(DisplayText));
            }
        }
    }

    public int Key
    {
        get => _key;
        set
        {
            if (SetField(ref _key, value))
            {
                OnPropertyChanged(nameof(HotkeyDisplay));
                OnPropertyChanged(nameof(DisplayText));
            }
        }
    }

    [JsonIgnore]
    public string HotkeyDisplay => HotkeyFormatter.Format(Modifiers, Key);

    [JsonIgnore]
    public string DisplayText => $"{Name}{Environment.NewLine}{HotkeyDisplay}";

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
