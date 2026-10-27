using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BroidShot.Core;

/// <summary>
/// A command palette entry. Executed on the UI thread after the palette has closed.
/// If <see cref="NavigateToModuleId"/> is set the main window is shown at that module first;
/// a command may only navigate (no execute delegate) or only execute.
/// </summary>
public sealed class ToolCommand
{
    private ToolCommand(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("Komut başlığı boş olamaz.", nameof(title));
        Title = title;
    }

    /// <summary>Asynchronous command. The token is cancelled when the application exits.</summary>
    public ToolCommand(string title, Func<CancellationToken, Task> execute) : this(title)
    {
        Execute = execute ?? throw new ArgumentNullException(nameof(execute));
    }

    /// <summary>Synchronous command.</summary>
    public ToolCommand(string title, Action execute) : this(title, WrapAction(execute)) { }

    /// <summary>A command that only opens a module page.</summary>
    public static ToolCommand Open(string title, string moduleId, string? description = null, IReadOnlyList<string>? keywords = null, string? id = null) =>
        new(title) { NavigateToModuleId = moduleId, Description = description, Keywords = keywords ?? Array.Empty<string>(), Id = id };

    /// <summary>Optional stable id (e.g. "clipboard.open-quick-panel"), for future shortcuts and recents.</summary>
    public string? Id { get; init; }
    public string Title { get; }
    /// <summary>Turkish secondary line in the palette.</summary>
    public string? Description { get; init; }
    public IReadOnlyList<string> Keywords { get; init; } = Array.Empty<string>();
    /// <summary>Segoe Fluent Icons glyph; the owning module's icon is used when null.</summary>
    public string? Icon { get; init; }
    /// <summary>Show the main window at this module before executing.</summary>
    public string? NavigateToModuleId { get; init; }
    public Func<CancellationToken, Task>? Execute { get; }

    private static Func<CancellationToken, Task> WrapAction(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return _ => { action(); return Task.CompletedTask; };
    }
}

/// <summary>A global shortcut declared by a module; registered by the shell after Initialize.</summary>
public sealed class ToolHotkey
{
    public ToolHotkey(string id, string title, HotkeyGesture defaultGesture, Action callback)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Title = title ?? throw new ArgumentNullException(nameof(title));
        DefaultGesture = defaultGesture;
        Callback = callback ?? throw new ArgumentNullException(nameof(callback));
    }

    public string Id { get; }
    public string Title { get; }
    public HotkeyGesture DefaultGesture { get; }
    /// <summary>Runs on the UI thread.</summary>
    public Action Callback { get; }
}
