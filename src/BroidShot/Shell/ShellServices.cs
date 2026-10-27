using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using BroidShot.Core;

namespace BroidShot.Shell;

/// <summary>Services shared by every module host. Created and disposed on the UI thread by MainWindow.</summary>
internal sealed class ShellServices : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();

    public ShellServices(Dispatcher dispatcher, Action<string> navigate)
    {
        Dispatcher = dispatcher;
        Navigate = navigate;
        Messages = new MessageBus(dispatcher);
        Jobs = new JobTracker(dispatcher);
        Tools = new ExternalToolManager();
        Hotkeys = new GlobalHotkeyService();
        Toasts = new ToastPresenter();
    }

    public Dispatcher Dispatcher { get; }
    public Action<string> Navigate { get; }
    public MessageBus Messages { get; }
    public JobTracker Jobs { get; }
    public ExternalToolManager Tools { get; }
    public GlobalHotkeyService Hotkeys { get; }
    public ToastPresenter Toasts { get; }
    /// <summary>Cancelled when the application exits; passed to palette commands.</summary>
    public CancellationToken Lifetime => _lifetime.Token;

    public void Dispose()
    {
        try { _lifetime.Cancel(); } catch (ObjectDisposedException) { }
        foreach (ToolJob job in Jobs.Jobs) job.Cancel();
        Hotkeys.Dispose();
        Toasts.Dispose();
        Tools.Dispose();
        _lifetime.Dispose();
    }
}

/// <summary>The IToolHost a single module receives.</summary>
internal sealed class ModuleHost : IToolHost
{
    private readonly ShellServices _services;
    private readonly List<HotkeyRegistration> _hotkeys = new();
    private ModuleSettings? _settings;

    public ModuleHost(string moduleId, ShellServices services)
    {
        ModuleId = Core.DataPaths.ValidateId(moduleId, nameof(moduleId));
        _services = services;
    }

    public string ModuleId { get; }
    public string DataRoot => Core.DataPaths.Root;
    public string DataFolder
    {
        get
        {
            string folder = Path.Combine(Core.DataPaths.ModuleFolder(ModuleId), "data");
            Directory.CreateDirectory(folder);
            return folder;
        }
    }
    public ModuleSettings Settings => _settings ??= new ModuleSettings(Path.Combine(Core.DataPaths.ModuleFolder(ModuleId), "settings.json"));
    public IMessageBus Messages => _services.Messages;
    public IJobTracker Jobs => _services.Jobs;
    public IExternalToolManager ExternalTools => _services.Tools;
    public Dispatcher Dispatcher => _services.Dispatcher;
    public IReadOnlyList<HotkeyRegistration> HotkeyRegistrations => _hotkeys;

    public void Navigate(string moduleId) => OnUiThread(() => _services.Navigate(moduleId));

    public void ShowToast(ToastOptions toast)
    {
        ArgumentNullException.ThrowIfNull(toast);
        OnUiThread(() => _services.Toasts.Show(toast));
    }

    public HotkeyRegistration RegisterHotkey(HotkeyGesture gesture, Action callback)
    {
        Dispatcher.VerifyAccess();
        HotkeyRegistration registration = _services.Hotkeys.Register(gesture, () =>
        {
            try { callback(); }
            catch (Exception ex) { Log("Kısayol işlenemedi: " + gesture, ex); }
        });
        if (!registration.IsRegistered) Log("Kısayol kaydedilemedi: " + registration.Error);
        _hotkeys.Add(registration);
        return registration;
    }

    public void Log(string message, Exception? exception = null) => ErrorLog.Write(ModuleId, message, exception);

    public void ReleaseHotkeys()
    {
        foreach (HotkeyRegistration registration in _hotkeys) registration.Dispose();
        _hotkeys.Clear();
    }

    private void OnUiThread(Action action)
    {
        if (Dispatcher.CheckAccess()) action();
        else if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(action);
    }
}

/// <summary>Shell-side state of one registered module. Descriptive values are captured once so a faulty getter cannot break the UI.</summary>
internal sealed class ModuleSlot
{
    public ModuleSlot(IToolModule module, bool builtIn)
    {
        Module = module;
        IsBuiltIn = builtIn;
        Id = module.Id;
        Title = module.Title;
        Description = module.Description ?? "";
        Category = module.Category ?? "";
        Icon = module.Icon ?? "";
        Keywords = module.Keywords ?? Array.Empty<string>();
    }

    public IToolModule Module { get; }
    public bool IsBuiltIn { get; }
    public string Id { get; }
    public string Title { get; }
    public string Description { get; }
    public string Category { get; }
    public string Icon { get; }
    public IReadOnlyList<string> Keywords { get; }
    public ModuleHost? Host { get; set; }
    public bool Initialized { get; set; }
    public string? Error { get; set; }
    public FrameworkElement? View { get; set; }
    public System.Windows.Controls.Button? NavButton { get; set; }
    public List<string> HotkeyNotes { get; } = new();
}

/// <summary>A built-in page described as a module (the screenshot page).</summary>
internal sealed class BuiltInModule : IToolModule
{
    private readonly Func<FrameworkElement> _view;
    private readonly Func<IReadOnlyList<ToolCommand>> _commands;

    public BuiltInModule(string id, string title, string description, string category, string icon, IReadOnlyList<string> keywords,
        Func<FrameworkElement> view, Func<IReadOnlyList<ToolCommand>> commands, string? headerTitle = null)
    {
        Id = id; Title = title; Description = description; Category = category; Icon = icon; Keywords = keywords;
        _view = view; _commands = commands; HeaderTitle = headerTitle ?? title;
    }

    public string Id { get; }
    public string Title { get; }
    public string Description { get; }
    public string Category { get; }
    public string Icon { get; }
    public IReadOnlyList<string> Keywords { get; }
    public string HeaderTitle { get; }
    public IReadOnlyList<ToolCommand> Commands => _commands();
    public void Initialize(IToolHost host) { }
    public FrameworkElement CreateView() => _view();
    public void Shutdown() { }
}
