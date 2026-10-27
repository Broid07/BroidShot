using System;
using System.Collections.Generic;
using System.Windows.Input;

namespace BroidShot.Core;

/// <summary>A global shortcut such as Ctrl+Alt+Space. Text form: "Ctrl+Alt+Space".</summary>
public readonly record struct HotkeyGesture(ModifierKeys Modifiers, Key Key)
{
    public bool IsEmpty => Key == Key.None;

    /// <summary>Win32 MOD_* flags (without MOD_NOREPEAT).</summary>
    public uint NativeModifiers =>
        ((Modifiers & ModifierKeys.Alt) != 0 ? 0x1u : 0) | ((Modifiers & ModifierKeys.Control) != 0 ? 0x2u : 0) |
        ((Modifiers & ModifierKeys.Shift) != 0 ? 0x4u : 0) | ((Modifiers & ModifierKeys.Windows) != 0 ? 0x8u : 0);

    public uint VirtualKey => (uint)KeyInterop.VirtualKeyFromKey(Key);

    /// <summary>Settings form, e.g. "Ctrl+Alt+Space".</summary>
    public override string ToString() => string.Join("+", Parts());

    /// <summary>Display form, e.g. "Ctrl + Alt + Space".</summary>
    public string DisplayText => string.Join(" + ", Parts());

    private IEnumerable<string> Parts()
    {
        if ((Modifiers & ModifierKeys.Control) != 0) yield return "Ctrl";
        if ((Modifiers & ModifierKeys.Alt) != 0) yield return "Alt";
        if ((Modifiers & ModifierKeys.Shift) != 0) yield return "Shift";
        if ((Modifiers & ModifierKeys.Windows) != 0) yield return "Win";
        if (Key != Key.None) yield return KeyName(Key);
    }

    private static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        Key.Return => "Enter",
        Key.Escape => "Esc",
        Key.Next => "PageDown",
        Key.Prior => "PageUp",
        Key.Oem3 => "Oem3",
        _ => key.ToString()
    };

    public static HotkeyGesture Parse(string text) =>
        TryParse(text, out HotkeyGesture gesture) ? gesture : throw new FormatException("Kısayol anlaşılamadı: " + text);

    /// <summary>Parses "Ctrl+Alt+Space", "ctrl + shift + v", "Win+Shift+S". Requires a non-modifier key.</summary>
    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        ModifierKeys modifiers = ModifierKeys.None;
        Key key = Key.None;
        foreach (string raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": modifiers |= ModifierKeys.Control; continue;
                case "alt": modifiers |= ModifierKeys.Alt; continue;
                case "shift": modifiers |= ModifierKeys.Shift; continue;
                case "win": case "windows": modifiers |= ModifierKeys.Windows; continue;
            }
            if (key != Key.None) return false;
            if (raw.Length == 1 && char.IsDigit(raw[0])) key = Key.D0 + (raw[0] - '0');
            else if (string.Equals(raw, "enter", StringComparison.OrdinalIgnoreCase)) key = Key.Return;
            else if (string.Equals(raw, "esc", StringComparison.OrdinalIgnoreCase)) key = Key.Escape;
            else if (!Enum.TryParse(raw, true, out key) || int.TryParse(raw, out _)) return false;
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System)
                return false;
        }
        if (key == Key.None) return false;
        gesture = new HotkeyGesture(modifiers, key);
        return true;
    }
}
