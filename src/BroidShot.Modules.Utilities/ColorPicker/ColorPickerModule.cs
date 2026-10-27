using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using BroidShot.Core;

namespace BroidShot.Modules.Utilities;

/// <summary>Saved to &lt;DataRoot&gt;/modules/colorpicker/settings.json.</summary>
public sealed class ColorPickerSettings
{
    public ColorFormat DefaultFormat { get; set; } = ColorFormat.Hex;

    /// <summary>Most recent first, "#RRGGBB". At most <see cref="ColorHistory.Limit"/> entries.</summary>
    public List<string> History { get; set; } = new();
}

/// <summary>Colour history rules (most recent first, no duplicates, 50 entries).</summary>
internal static class ColorHistory
{
    public const int Limit = 50;

    public static List<string> Add(IEnumerable<string> history, Rgb color)
    {
        string hex = ColorFormats.ToHex(color);
        var result = new List<string> { hex };
        result.AddRange(history.Where(h => !string.Equals(h, hex, StringComparison.OrdinalIgnoreCase)));
        if (result.Count > Limit) result.RemoveRange(Limit, result.Count - Limit);
        return result;
    }
}

/// <summary>Renk seçici: screen overlay with a magnifier, copy in the chosen format, 50-colour history.</summary>
public sealed class ColorPickerModule : UtilityModuleBase
{
    private readonly IClipboardService _clipboard;
    private ColorPickerSettings _settings = new();
    private bool _overlayOpen;

    public ColorPickerModule() : this(SystemClipboard.Instance) { }

    internal ColorPickerModule(IClipboardService clipboard) { _clipboard = clipboard; }

    public override string Id => "colorpicker";
    public override string Title => "Renk seçici";
    public override string Description => "Ekrandan renk al, büyüteçle piksel piksel seç, HEX/RGB/HSL olarak kopyala.";
    public override string Icon => "";
    public override IReadOnlyList<string> Keywords { get; } = new[] { "renk", "color", "colour", "picker", "hex", "rgb", "hsl", "damlalık", "eyedropper", "pipet" };

    public override IReadOnlyList<ToolCommand> Commands => new[]
    {
        new ToolCommand("Ekrandan renk al", () => _ = StartPick())
        {
            Id = "colorpicker.pick", Description = "Ekranda bir pikselin rengini seçer ve kopyalar",
            Keywords = new[] { "renk", "color", "picker", "damlalık", "hex" }
        },
        ToolCommand.Open("Renk geçmişini aç", Id, "Son 50 rengi ve kopyalama biçimlerini gösterir", new[] { "renk", "geçmiş", "history" }, "colorpicker.open"),
    };

    public override IReadOnlyList<ToolHotkey> Hotkeys => new[]
    {
        new ToolHotkey("colorpicker.pick", "Ekrandan renk al", HotkeyGesture.Parse("Ctrl+Shift+C"), () => _ = StartPick())
    };

    internal ColorPickerSettings Settings => _settings;

    /// <summary>Raised on the UI thread after the history or default format changed.</summary>
    internal event Action? Changed;

    public override void Initialize(IToolHost host)
    {
        base.Initialize(host);
        _settings = host.Settings.Load<ColorPickerSettings>();
        _settings.History = _settings.History.Where(h => ColorFormats.TryParseHex(h, out _)).Take(ColorHistory.Limit).ToList();
        _active = this;
    }

    public override void Shutdown()
    {
        if (ReferenceEquals(_active, this)) _active = null;
        base.Shutdown();
    }

    private static ColorPickerModule? _active;

    /// <summary>
    /// Adds a colour picked elsewhere in BroidShot (the capture overlay) to the history of the running module and
    /// persists it. Call on the UI thread. False when no colour picker module is initialised.
    /// </summary>
    internal static bool RecordExternalPick(Rgb color)
    {
        ColorPickerModule? module = _active;
        if (module is null || !module.IsInitialized) return false;
        module._settings.History = ColorHistory.Add(module._settings.History, color);
        module.Save();
        module.Changed?.Invoke();
        return true;
    }

    public override FrameworkElement CreateView() => new ColorPickerView(this);

    internal void SetDefaultFormat(ColorFormat format)
    {
        _settings.DefaultFormat = format;
        Save();
        Changed?.Invoke();
    }

    internal void RemoveFromHistory(string hex)
    {
        _settings.History.RemoveAll(h => string.Equals(h, hex, StringComparison.OrdinalIgnoreCase));
        Save();
        Changed?.Invoke();
    }

    internal void ClearHistory()
    {
        _settings.History.Clear();
        Save();
        Changed?.Invoke();
    }

    internal void CopyColor(Rgb color, ColorFormat format)
    {
        try
        {
            string text = ColorFormats.Format(color, format);
            _clipboard.SetText(text);
            Toast("Renk kopyalandı", $"{text}  ({ColorFormats.Name(format)})", ToastKind.Success);
        }
        catch (Exception ex)
        {
            LogError("Renk panoya kopyalanamadı", ex);
            Toast("Kopyalanamadı", "Pano şu an kullanılamıyor.", ToastKind.Error);
        }
    }

    /// <summary>Opens the overlay. Safe to call from the hotkey, the palette or the page button.</summary>
    internal async Task StartPick()
    {
        if (_overlayOpen || !IsInitialized) return;
        _overlayOpen = true;
        try
        {
            ScreenSnapshot snapshot;
            using (await MainWindowHider.HideAsync())
            {
                snapshot = ScreenSnapshot.Capture();
                var overlay = new ColorPickerOverlay(snapshot);
                overlay.Show();
                Rgb? picked = await overlay.Result;
                if (picked is Rgb color) Picked(color);
            }
        }
        catch (Exception ex)
        {
            LogError("Renk seçici açılamadı", ex);
            Toast("Renk seçici açılamadı", ex.Message, ToastKind.Error);
        }
        finally { _overlayOpen = false; }
    }

    private void Picked(Rgb color)
    {
        _settings.History = ColorHistory.Add(_settings.History, color);
        Save();
        CopyColor(color, _settings.DefaultFormat);
        Changed?.Invoke();
    }

    private void Save()
    {
        try { Host.Settings.Save(_settings); }
        catch (Exception ex) { LogError("Renk seçici ayarları kaydedilemedi", ex); }
    }
}
