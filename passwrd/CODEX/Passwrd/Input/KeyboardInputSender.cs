using Passwrd.Interop;

namespace Passwrd.Input;

internal static class KeyboardInputSender
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;

    public static async Task SendAsync(string text, bool pressEnterAfter)
    {
        if (!await WaitForHotkeyModifiersAsync())
        {
            return;
        }

        await Task.Delay(30);

        for (int index = 0; index < text.Length; index++)
        {
            char character = text[index];

            if (character == '\r')
            {
                SendVirtualKey(0x0D);
                if (index + 1 < text.Length && text[index + 1] == '\n')
                {
                    index++;
                }
            }
            else if (character == '\n')
            {
                SendVirtualKey(0x0D);
            }
            else if (character == '\t')
            {
                SendVirtualKey(0x09);
            }
            else
            {
                SendUnicodeCharacter(character);
            }

            if ((index & 31) == 31)
            {
                await Task.Delay(1);
            }
        }

        if (pressEnterAfter)
        {
            SendVirtualKey(0x0D);
        }
    }

    private static async Task<bool> WaitForHotkeyModifiersAsync()
    {
        int[] modifierKeys =
        [
            NativeMethods.VkShift,
            NativeMethods.VkControl,
            NativeMethods.VkMenu,
            NativeMethods.VkLWin,
            NativeMethods.VkRWin,
        ];

        for (int attempt = 0; attempt < 150; attempt++)
        {
            bool anyModifierDown = modifierKeys.Any(IsKeyDown);
            if (!anyModifierDown)
            {
                return true;
            }

            await Task.Delay(10);
        }

        return false;
    }

    private static bool IsKeyDown(int virtualKey) =>
        (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static void SendUnicodeCharacter(char character)
    {
        NativeMethods.Input[] inputs =
        [
            CreateUnicodeInput(character, keyUp: false),
            CreateUnicodeInput(character, keyUp: true),
        ];

        NativeMethods.SendInput(
            (uint)inputs.Length,
            inputs,
            System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.Input>());
    }

    private static NativeMethods.Input CreateUnicodeInput(char character, bool keyUp) =>
        new()
        {
            Type = InputKeyboard,
            Data = new NativeMethods.InputUnion
            {
                Keyboard = new NativeMethods.KeyboardInput
                {
                    ScanCode = character,
                    Flags = KeyEventUnicode | (keyUp ? KeyEventKeyUp : 0),
                },
            },
        };

    private static void SendVirtualKey(ushort virtualKey)
    {
        NativeMethods.Input[] inputs =
        [
            CreateVirtualKeyInput(virtualKey, keyUp: false),
            CreateVirtualKeyInput(virtualKey, keyUp: true),
        ];

        NativeMethods.SendInput(
            (uint)inputs.Length,
            inputs,
            System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.Input>());
    }

    private static NativeMethods.Input CreateVirtualKeyInput(ushort virtualKey, bool keyUp) =>
        new()
        {
            Type = InputKeyboard,
            Data = new NativeMethods.InputUnion
            {
                Keyboard = new NativeMethods.KeyboardInput
                {
                    VirtualKey = virtualKey,
                    Flags = keyUp ? KeyEventKeyUp : 0,
                },
            },
        };
}
