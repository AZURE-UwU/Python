using Passwrd.Models;

namespace Passwrd.Input;

public sealed record KeyOption(int VirtualKey, string Label);

public static class KeyCatalog
{
    private static readonly IReadOnlyList<KeyOption> AllOptions = BuildOptions();
    private static readonly IReadOnlyDictionary<int, KeyOption> ByKey =
        AllOptions.ToDictionary(option => option.VirtualKey);

    public static IReadOnlyList<KeyOption> Options => AllOptions;

    public static KeyOption? Find(int virtualKey) =>
        ByKey.GetValueOrDefault(virtualKey);

    public static string GetLabel(int virtualKey) =>
        Find(virtualKey)?.Label ?? $"VK_{virtualKey:X2}";

    private static IReadOnlyList<KeyOption> BuildOptions()
    {
        List<KeyOption> options = [];

        for (int key = 0x41; key <= 0x5A; key++)
        {
            options.Add(new KeyOption(key, ((char)key).ToString()));
        }

        for (int key = 0x30; key <= 0x39; key++)
        {
            options.Add(new KeyOption(key, ((char)key).ToString()));
        }

        for (int number = 1; number <= 24; number++)
        {
            options.Add(new KeyOption(0x6F + number, $"F{number}"));
        }

        options.AddRange(
        [
            new(0x20, "Space"),
            new(0x0D, "Enter"),
            new(0x09, "Tab"),
            new(0x2D, "Insert"),
            new(0x2E, "Delete"),
            new(0x24, "Home"),
            new(0x23, "End"),
            new(0x21, "Page Up"),
            new(0x22, "Page Down"),
            new(0x25, "Left"),
            new(0x26, "Up"),
            new(0x27, "Right"),
            new(0x28, "Down"),
            new(0x6A, "Num *"),
            new(0x6B, "Num +"),
            new(0x6D, "Num -"),
            new(0x6E, "Num ."),
            new(0x6F, "Num /"),
            new(0xBA, ";"),
            new(0xBB, "="),
            new(0xBC, ","),
            new(0xBD, "-"),
            new(0xBE, "."),
            new(0xBF, "/"),
            new(0xC0, "`"),
            new(0xDB, "["),
            new(0xDC, "\\"),
            new(0xDD, "]"),
            new(0xDE, "'"),
        ]);

        return options;
    }
}

public static class HotkeyFormatter
{
    public static string Format(HotkeyModifiers modifiers, int virtualKey)
    {
        List<string> parts = [];

        if (modifiers.HasFlag(HotkeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (modifiers.HasFlag(HotkeyModifiers.Win))
        {
            parts.Add("Win");
        }

        parts.Add(KeyCatalog.GetLabel(virtualKey));
        return string.Join(" + ", parts);
    }
}
