using System;
using System.Windows.Threading;

namespace BroidShot.Core;

/// <summary>
/// Services the shell gives to one module. Each module receives its own instance, scoped to its
/// <see cref="ModuleId"/> (settings file, data folder, log source). Members may be called from any
/// thread unless noted; UI work is marshalled to <see cref="Dispatcher"/> by the host.
/// </summary>
public interface IToolHost
{
    string ModuleId { get; }

    /// <summary>BroidShot data root (honours BROIDSHOT_DATA_DIR).</summary>
    string DataRoot { get; }

    /// <summary>&lt;DataRoot&gt;/modules/&lt;id&gt;/data — created on first access. Put databases and caches here.</summary>
    string DataFolder { get; }

    /// <summary>&lt;DataRoot&gt;/modules/&lt;id&gt;/settings.json as a typed JSON store.</summary>
    ModuleSettings Settings { get; }

    /// <summary>Typed publish/subscribe between modules (e.g. Clipboard → Media "URL copied").</summary>
    IMessageBus Messages { get; }

    /// <summary>Background job list shown in the shell (title, progress, cancel).</summary>
    IJobTracker Jobs { get; }

    /// <summary>Downloads, verifies and updates external tools such as yt-dlp and ffmpeg.</summary>
    IExternalToolManager ExternalTools { get; }

    /// <summary>The UI dispatcher.</summary>
    Dispatcher Dispatcher { get; }

    /// <summary>Shows the main window at the given module page (use <see cref="ModuleId"/> for this module).</summary>
    void Navigate(string moduleId);

    /// <summary>Small bottom-right notification with an optional action button. Works while the window is hidden.</summary>
    void ShowToast(ToastOptions toast);

    /// <summary>
    /// Registers a system-wide shortcut. Must be called on the UI thread. The result reports failure
    /// (e.g. the shortcut is used by another application) instead of throwing; dispose it to unregister.
    /// All registrations are released when the application exits.
    /// </summary>
    HotkeyRegistration RegisterHotkey(HotkeyGesture gesture, Action callback);

    /// <summary>Writes to &lt;DataRoot&gt;/errors.log with this module's id.</summary>
    void Log(string message, Exception? exception = null);
}

public enum ToastKind { Info, Success, Warning, Error }

/// <summary>Content of a toast notification.</summary>
public sealed class ToastOptions
{
    public ToastOptions(string title, string message)
    {
        Title = title ?? throw new ArgumentNullException(nameof(title));
        Message = message ?? "";
    }

    public string Title { get; }
    public string Message { get; }
    public ToastKind Kind { get; init; } = ToastKind.Info;
    /// <summary>Button text such as "İndir". The toast closes after the action runs.</summary>
    public string? ActionLabel { get; init; }
    /// <summary>Runs on the UI thread when the action button is clicked.</summary>
    public Action? Action { get; init; }
    /// <summary>Auto-dismiss delay; paused while the pointer is over the toast.</summary>
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(6);
}

public static class ToolHostExtensions
{
    public static void ShowToast(this IToolHost host, string title, string message, string? actionLabel = null, Action? action = null, ToastKind kind = ToastKind.Info)
    {
        ArgumentNullException.ThrowIfNull(host);
        host.ShowToast(new ToastOptions(title, message) { ActionLabel = actionLabel, Action = action, Kind = kind });
    }

    /// <summary>Shows the main window at this module's page.</summary>
    public static void NavigateToSelf(this IToolHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        host.Navigate(host.ModuleId);
    }
}
