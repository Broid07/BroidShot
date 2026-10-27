using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using BroidShot.Core;

namespace BroidShot.Modules.Clipboard;

/// <summary>Saved to &lt;DataRoot&gt;/modules/clipboard/settings.json.</summary>
public sealed class ClipboardSettings
{
    public const int DefaultMaxItems = 1000;
    public const int DefaultMaxAgeDays = 30;
    /// <summary>Win+V cannot be registered as a hotkey; the module takes it over with a keyboard hook (see WinVTakeover).</summary>
    public const string DefaultQuickPanelHotkey = "Win+V";
    /// <summary>Default of earlier versions; settings still holding it are moved to <see cref="DefaultQuickPanelHotkey"/>.</summary>
    public const string LegacyQuickPanelHotkey = "Ctrl+Shift+V";

    public bool MonitoringEnabled { get; set; } = true;
    /// <summary>Unpinned items kept at most; pinned items are exempt.</summary>
    public int MaxItems { get; set; } = DefaultMaxItems;
    /// <summary>Unpinned items unused for longer than this are deleted.</summary>
    public int MaxAgeDays { get; set; } = DefaultMaxAgeDays;
    /// <summary>Process names (without .exe) whose copies are never recorded.</summary>
    public List<string> BlockedApps { get; set; } = new() { "KeePass", "KeePassXC", "1Password", "Bitwarden" };
    /// <summary>Quick panel on its shortcut. With the default Win+V this is the takeover switch: off leaves Win+V to Windows.</summary>
    public bool QuickPanelEnabled { get; set; } = true;
    public string QuickPanelHotkey { get; set; } = DefaultQuickPanelHotkey;

    public ClipboardSettings Normalized()
    {
        MaxItems = Math.Clamp(MaxItems, 10, 100_000);
        MaxAgeDays = Math.Clamp(MaxAgeDays, 1, 3650);
        BlockedApps = (BlockedApps ?? new List<string>()).Select(NormalizeProcessName).Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (string.IsNullOrWhiteSpace(QuickPanelHotkey) || SameGesture(QuickPanelHotkey, LegacyQuickPanelHotkey)) QuickPanelHotkey = DefaultQuickPanelHotkey;
        return this;
    }

    /// <summary>True when the quick panel shortcut is Win+V, which needs the keyboard hook instead of RegisterHotKey.</summary>
    [JsonIgnore]
    public bool UsesWinVTakeover => SameGesture(QuickPanelHotkey, DefaultQuickPanelHotkey);

    private static bool SameGesture(string? a, string b) =>
        HotkeyGesture.TryParse(a, out HotkeyGesture first) && first == HotkeyGesture.Parse(b);

    public RetentionPolicy Retention => new(MaxItems, TimeSpan.FromDays(MaxAgeDays));

    public bool IsBlocked(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        string name = NormalizeProcessName(processName);
        return BlockedApps.Any(b => string.Equals(NormalizeProcessName(b), name, StringComparison.OrdinalIgnoreCase));
    }

    public static string NormalizeProcessName(string? name)
    {
        string value = (name ?? "").Trim().Trim('"');
        if (value.Length == 0) return "";
        try { value = Path.GetFileName(value); } catch (ArgumentException) { }
        if (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) value = value[..^4];
        return value.Trim();
    }
}
