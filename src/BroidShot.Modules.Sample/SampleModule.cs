using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BroidShot.Core;

namespace BroidShot.Modules.Sample;

/// <summary>Smallest complete module: a page, a palette command that shows a toast, settings and a demo job.</summary>
public sealed class SampleModule : IToolModule
{
    private IToolHost? _host;
    private SampleSettings _settings = new();

    public string Id => "sample";
    public string Title => "Örnek modül";
    public string Description => "Modül yapısını gösteren küçük örnek sayfa.";
    public string Category => ToolCategories.Developer;
    public string Icon => "";
    public IReadOnlyList<string> Keywords { get; } = new[] { "örnek", "deneme", "sample", "demo" };

    public IReadOnlyList<ToolCommand> Commands => new[]
    {
        new ToolCommand("Örnek bildirim göster", ShowGreeting)
        {
            Id = "sample.toast",
            Description = "Sağ altta eylem düğmeli bir bildirim açar",
            Keywords = new[] { "bildirim", "toast", "notification" }
        },
        ToolCommand.Open("Örnek modülü aç", Id),
    };

    internal IToolHost Host => _host ?? throw new InvalidOperationException("Modül henüz başlatılmadı.");
    internal SampleSettings Settings => _settings;

    public void Initialize(IToolHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _settings = host.Settings.Load<SampleSettings>();
    }

    public FrameworkElement CreateView() => new SampleView(this);

    public void Shutdown() { }

    internal void ShowGreeting()
    {
        _settings.ToastCount++;
        Host.Settings.Save(_settings);
        Host.ShowToast("Merhaba", $"Bu bildirim örnek modülden geldi ({_settings.ToastCount}. kez).", "Modülü aç", () => Host.NavigateToSelf());
    }

    internal Task RunDemoJobAsync() => Host.Jobs.RunAsync("Örnek iş", async (job, token) =>
    {
        for (int step = 1; step <= 20; step++)
        {
            await Task.Delay(150, token);
            job.Report(step / 20.0, $"Adım {step}/20");
        }
    });
}

/// <summary>Saved to &lt;DataRoot&gt;/modules/sample/settings.json.</summary>
public sealed class SampleSettings
{
    public int ToastCount { get; set; }
}
