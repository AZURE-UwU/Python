namespace Passwrd.Models;

[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Win = 8,
}
