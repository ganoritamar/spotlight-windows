using System.Windows.Input;
using Spotlight.Interop;

namespace Spotlight.Services;

/// <summary>A parsed global shortcut such as "Alt+Space".</summary>
internal readonly record struct Hotkey(uint Modifiers, uint VirtualKey)
{
    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        uint modifiers = 0;
        Key key = Key.None;
        foreach (var part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "alt": modifiers |= Native.MOD_ALT; break;
                case "ctrl":
                case "control": modifiers |= Native.MOD_CONTROL; break;
                case "shift": modifiers |= Native.MOD_SHIFT; break;
                case "win":
                case "windows": modifiers |= Native.MOD_WIN; break;
                default:
                    if (key != Key.None || !Enum.TryParse(part, ignoreCase: true, out key) || int.TryParse(part, out _)) return false;
                    break;
            }
        }

        if (key == Key.None || modifiers == 0) return false;
        hotkey = new Hotkey(modifiers, (uint)KeyInterop.VirtualKeyFromKey(key));
        return true;
    }
}
