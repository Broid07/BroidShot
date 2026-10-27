using System;
using System.Collections.Generic;
using System.Windows;

namespace BroidShot.Core;

/// <summary>
/// A BroidShot tool (sidebar page + command palette entries + optional background work).
/// Lifecycle, all on the UI thread: the shell reads the descriptive properties, calls
/// <see cref="Initialize"/> once, reads <see cref="Hotkeys"/> once, calls <see cref="CreateView"/>
/// lazily on first navigation (the view is cached), reads <see cref="Commands"/> every time the
/// palette opens, and calls <see cref="Shutdown"/> once when the application exits.
/// An exception from Initialize or CreateView is logged and replaced by an error page; it never
/// stops the application.
/// </summary>
public interface IToolModule
{
    /// <summary>Stable id: lowercase ASCII, digits, '.', '_' or '-' (e.g. "clipboard"). Used for folder names.</summary>
    string Id { get; }

    /// <summary>Turkish display name in the sidebar and palette (e.g. "Pano").</summary>
    string Title { get; }

    /// <summary>Turkish one-line description; shown as the page subtitle and in the palette.</summary>
    string Description { get; }

    /// <summary>Sidebar group. Prefer <see cref="ToolCategories"/> constants.</summary>
    string Category { get; }

    /// <summary>Segoe Fluent Icons glyph, e.g. "". Rendered with the theme's IconFont.</summary>
    string Icon { get; }

    /// <summary>Extra search words for the palette (Turkish and English synonyms).</summary>
    IReadOnlyList<string> Keywords { get; }

    /// <summary>Palette actions. Read each time the palette opens, so the list may change at run time.</summary>
    IReadOnlyList<ToolCommand> Commands { get; }

    /// <summary>Optional global shortcuts. Read once after <see cref="Initialize"/>; the shell registers them.</summary>
    IReadOnlyList<ToolHotkey> Hotkeys => Array.Empty<ToolHotkey>();

    /// <summary>Called once at startup, before the main window may be shown. Start listeners here.</summary>
    void Initialize(IToolHost host);

    /// <summary>Creates the page shown in the content area. Called at most once; the result is cached.</summary>
    FrameworkElement CreateView();

    /// <summary>Stop listeners, flush data. Must not block for long and must not throw.</summary>
    void Shutdown();
}

/// <summary>Sidebar group names. Groups appear in the order of their first module in the registry.</summary>
public static class ToolCategories
{
    public const string Capture = "Yakalama";
    public const string Clipboard = "Pano";
    public const string Media = "Medya";
    public const string Files = "Dosyalar";
    public const string Utilities = "Küçük araçlar";
    public const string Developer = "Geliştirme";
}
