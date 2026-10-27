using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Animation;
using System.Windows;
using BroidShot.Lifecycle;
using BroidShot.Core;
using BroidShot.Startup;
using BroidShot.Updates;

namespace BroidShot;

public partial class App : Application
{
    private Mutex? _instance;
    private LauncherWindow? _launcher;
    protected override async void OnStartup(StartupEventArgs e)
    {
        BroidShot.Core.ThemeResources.ApplyDefaults();
        base.OnStartup(e);
        _instance = new Mutex(true, "Local\\BroidShot.Desktop", out bool created);
        if (!created)
        {
            if (LifecyclePolicy.ShouldRevealExistingWindow(e.Args) && !WindowActivation.RequestExistingWindow())
                MessageBox.Show("BroidShot zaten çalışıyor. Sistem tepsisindeki simgeden açabilirsin.", "BroidShot");
            Shutdown();
            return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                string root = DataPaths.Root;
                Directory.CreateDirectory(root);
                File.AppendAllText(Path.Combine(root, "errors.log"), $"{DateTimeOffset.Now:O} {args.Exception}\n");
            }
            catch { }
            MessageBox.Show("İşlem tamamlanamadı: " + args.Exception.Message, "BroidShot", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        UserPreferences preferences = UserPreferences.Load();
        LauncherWindow? launcher = null;
        try
        {
            bool hidden = LifecyclePolicy.ShouldStartHidden(e.Args, preferences);
            bool healthLaunch = UpdateHandoff.IsHealthLaunch(e.Args);
            bool recovered = e.Args.Contains("--update-recovered", StringComparer.Ordinal);
            using (var tools = new ExternalToolManager())
            using (var updates = new AppUpdateService())
            {
                launcher = new LauncherWindow(new StartupPreparation(tools, appUpdates: healthLaunch || recovered ? null : updates, verifyOnly: healthLaunch || recovered));
                _launcher = launcher;
                MainWindow = launcher;
                launcher.Show();
                if (!await launcher.PrepareAsync())
                {
                    await launcher.DismissAsync();
                    Shutdown();
                    return;
                }
                if (launcher.PendingPackage is { } package)
                {
                    await UpdateHandoff.ScheduleAsync(package, AppContext.BaseDirectory, e.Args);
                    await launcher.DismissAsync();
                    Shutdown();
                    return;
                }
            }
            var window = new MainWindow(preferences: preferences);
            MainWindow = window;
            window.InitializeSession();
            if (!hidden)
            {
                if (SystemParameters.ClientAreaAnimation)
                    window.BeginAnimation(Window.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280)));
                window.Show();
            }
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            UpdateHandoff.Acknowledge(e.Args);
            if (launcher is not null) await launcher.DismissAsync();
            _launcher = null;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("startup", "Uygulama başlatılamadı.", ex);
            MessageBox.Show("BroidShot başlatılamadı. Uygulamayı yeniden açmayı dene.", "BroidShot", MessageBoxButton.OK, MessageBoxImage.Warning);
            if (launcher is not null) await launcher.DismissAsync();
            Shutdown(1);
        }
    }

    internal async Task ShowLauncherForExistingAsync(MainWindow window)
    {
        if (_launcher is not null) { _launcher.Activate(); return; }
        using var tools = new ExternalToolManager();
        using var updates = new AppUpdateService();
        async Task WaitForJobs(CancellationToken token)
        {
            while (await window.Dispatcher.InvokeAsync(() => window.Jobs.Jobs.Any(j => j.State == ToolJobState.Running)))
                await Task.Delay(250, token);
        }
        var launcher = new LauncherWindow(new StartupPreparation(tools, WaitForJobs, updates));
        _launcher = launcher;
        try
        {
            launcher.Show();
            if (await launcher.PrepareAsync())
            {
                if (launcher.PendingPackage is { } package)
                {
                    await WaitForJobs(CancellationToken.None);
                    await UpdateHandoff.ScheduleAsync(package, AppContext.BaseDirectory, Array.Empty<string>());
                    Shutdown();
                }
                else window.RevealAfterPreparation();
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("launcher", "Açılış kontrolü tamamlanamadı.", ex);
            window.RevealAfterPreparation();
            window.Toasts.Show(new ToastOptions("Güncelleme tamamlanamadı", "Mevcut uygulama korunuyor. Ayrıntılar hata günlüğünde.") { Kind = ToastKind.Warning });
        }
        finally
        {
            await launcher.DismissAsync();
            _launcher = null;
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }
}
