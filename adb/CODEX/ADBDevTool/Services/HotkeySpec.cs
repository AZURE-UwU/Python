/// @file    HotkeySpec.cs
/// @author  Bowen
/// @module  ADBDevTool / 快捷键描述与解析
///
/// 外部契约（可核验指针，来源 Win32 RegisterHotKey 文档）：
/// 1. fsModifiers 取 MOD_ALT=0x1、MOD_CONTROL=0x2、MOD_SHIFT=0x4、MOD_WIN=0x8；
///    不接受只有主键的组合，必须至少带一个修饰键。
/// 2. 序列化写成 "Ctrl+Alt+M" 这类可读文本，直接存进 settings.json，便于手工改。

using System;
using System.Collections.Generic;
using System.Linq;

namespace ADBDevTool.Services;

internal sealed record HotkeySpec(int Modifiers, int KeyCode)
{
    public const int ModAlt = 0x0001;
    public const int ModControl = 0x0002;
    public const int ModShift = 0x0004;
    public const int ModWin = 0x0008;

    public static readonly HotkeySpec None = new(0, 0);

    private static readonly Dictionary<string, int> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Backspace"] = 0x08,
        ["Tab"] = 0x09,
        ["Enter"] = 0x0D,
        ["Escape"] = 0x1B,
        ["Space"] = 0x20,
        ["PageUp"] = 0x21,
        ["PageDown"] = 0x22,
        ["End"] = 0x23,
        ["Home"] = 0x24,
        ["Left"] = 0x25,
        ["Up"] = 0x26,
        ["Right"] = 0x27,
        ["Down"] = 0x28,
        ["Insert"] = 0x2D,
        ["Delete"] = 0x2E,
    };

    public bool IsEmpty => Modifiers == 0 || KeyCode == 0;

    public override string ToString()
    {
        if (IsEmpty)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        if ((Modifiers & ModControl) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((Modifiers & ModAlt) != 0)
        {
            parts.Add("Alt");
        }

        if ((Modifiers & ModShift) != 0)
        {
            parts.Add("Shift");
        }

        if ((Modifiers & ModWin) != 0)
        {
            parts.Add("Win");
        }

        parts.Add(KeyName(KeyCode));
        return string.Join("+", parts);
    }

    public static bool TryParse(string? text, out HotkeySpec spec)
    {
        spec = None;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var modifiers = 0;
        var virtualKey = 0;
        foreach (var token in text.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = token.Trim();
            switch (trimmed.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= ModControl;
                    break;
                case "alt":
                    modifiers |= ModAlt;
                    break;
                case "shift":
                    modifiers |= ModShift;
                    break;
                case "win":
                    modifiers |= ModWin;
                    break;
                default:
                    var parsed = ParseKey(trimmed);
                    if (parsed == 0)
                    {
                        return false;
                    }

                    virtualKey = parsed;
                    break;
            }
        }

        if (modifiers == 0 || virtualKey == 0)
        {
            return false;
        }

        spec = new HotkeySpec(modifiers, virtualKey);
        return true;
    }

    private static string KeyName(int virtualKey) => virtualKey switch
    {
        >= 0x41 and <= 0x5A => ((char)virtualKey).ToString(),
        >= 0x30 and <= 0x39 => ((char)virtualKey).ToString(),
        >= 0x70 and <= 0x87 => "F" + (virtualKey - 0x6F),
        _ => NamedKeys.FirstOrDefault(pair => pair.Value == virtualKey) is { Key: not null } named
            ? named.Key
            : $"0x{virtualKey:X2}",
    };

    private static int ParseKey(string token)
    {
        if (token.Length == 1)
        {
            var character = char.ToUpperInvariant(token[0]);
            if (character is (>= 'A' and <= 'Z') or (>= '0' and <= '9'))
            {
                return character;
            }
        }

        if (token.Length is >= 2 and <= 3
            && (token[0] is 'F' or 'f')
            && int.TryParse(token[1..], out var index)
            && index is >= 1 and <= 24)
        {
            return 0x6F + index;
        }

        return NamedKeys.TryGetValue(token, out var named) ? named : 0;
    }
}
