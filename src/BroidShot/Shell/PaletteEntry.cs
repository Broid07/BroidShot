using System;
using System.Threading.Tasks;

namespace BroidShot.Shell;

/// <summary>One row in the command palette: a module page or a module command.</summary>
public sealed class PaletteEntry
{
    public PaletteEntry(string title, string subtitle, string icon, string source, bool isModule, Func<Task> run)
    {
        Title = title; Subtitle = subtitle; Icon = icon; Source = source; IsModule = isModule; Run = run;
    }

    public string Title { get; }
    public string Subtitle { get; }
    public string Icon { get; }
    /// <summary>Owning module title (right-aligned in the palette).</summary>
    public string Source { get; }
    public bool IsModule { get; }
    public string KindText => IsModule ? "Araç" : "Komut";
    public bool HasSubtitle => Subtitle.Length > 0;
    public Func<Task> Run { get; }
    internal int Score { get; set; }
    internal int Order { get; set; }
}
