using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using BroidShot.Core;

namespace BroidShot.Modules.Utilities;

/// <summary>Saved to &lt;DataRoot&gt;/modules/ruler/settings.json.</summary>
public sealed class RulerSettings
{
    /// <summary>Copied measurements, most recent first (max 10).</summary>
    public List<string> Recent { get; set; } = new();
}

/// <summary>Ekran cetveli: drag on a frozen desktop to measure physical pixels.</summary>
public sealed class RulerModule : UtilityModuleBase
{
    private readonly IClipboardService _clipboard;
    private RulerSettings _settings = new();
    private bool _overlayOpen;

    public RulerModule() : this(SystemClipboard.Instance) { }

    internal RulerModule(IClipboardService clipboard) { _clipboard = clipboard; }

    public override string Id => "ruler";
    public override string Title => "Ekran cetveli";
    public override string Description => "Ekranda sürükleyerek piksel cinsinden genişlik, yükseklik ve çapraz uzunluk ölç.";
    public override string Icon => "";
    public override IReadOnlyList<string> Keywords { get; } = new[] { "cetvel", "ruler", "ölç", "ölçü", "piksel", "pixel", "measure", "mesafe" };

    public override IReadOnlyList<ToolCommand> Commands => new[]
    {
        new ToolCommand("Cetvel", () => _ = StartMeasure())
        {
            Id = "ruler.measure", Description = "Ekranda iki nokta arasını piksel olarak ölçer",
            Keywords = new[] { "cetvel", "ruler", "ölç", "piksel", "measure" }
        },
        ToolCommand.Open("Ekran cetvelini aç", Id, "Son ölçümleri gösterir", new[] { "cetvel", "ölçüm" }, "ruler.open"),
    };

    internal RulerSettings Settings => _settings;
    internal event Action? Changed;

    public override void Initialize(IToolHost host)
    {
        base.Initialize(host);
        _settings = host.Settings.Load<RulerSettings>();
    }

    public override FrameworkElement CreateView() => new RulerView(this);

    internal void CopyText(string text)
    {
        try { _clipboard.SetText(text); Toast("Ölçü kopyalandı", text, ToastKind.Success); }
        catch (Exception ex) { LogError("Ölçü panoya kopyalanamadı", ex); Toast("Kopyalanamadı", "Pano şu an kullanılamıyor.", ToastKind.Error); }
    }

    internal void ClearRecent()
    {
        _settings.Recent.Clear();
        Save();
        Changed?.Invoke();
    }

    internal async Task StartMeasure()
    {
        if (_overlayOpen || !IsInitialized) return;
        _overlayOpen = true;
        try
        {
            using (await MainWindowHider.HideAsync())
            {
                var overlay = new RulerOverlay(ScreenSnapshot.Capture());
                overlay.Show();
                RulerMeasurement? measurement = await overlay.Result;
                if (measurement is RulerMeasurement m)
                {
                    string text = m.ToClipboardText();
                    _settings.Recent = new[] { text }.Concat(_settings.Recent.Where(r => r != text)).Take(10).ToList();
                    Save();
                    CopyText(text);
                    Changed?.Invoke();
                }
            }
        }
        catch (Exception ex)
        {
            LogError("Cetvel açılamadı", ex);
            Toast("Cetvel açılamadı", ex.Message, ToastKind.Error);
        }
        finally { _overlayOpen = false; }
    }

    private void Save()
    {
        try { Host.Settings.Save(_settings); }
        catch (Exception ex) { LogError("Cetvel ayarları kaydedilemedi", ex); }
    }
}
