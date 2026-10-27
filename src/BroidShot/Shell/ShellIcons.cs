using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace BroidShot.Shell;

/// <summary>
/// Glyphs used by the shell (Segoe Fluent Icons on Windows 11, Segoe MDL2 Assets on Windows 10) and
/// a guard for module icons: a private-use glyph that neither icon font contains would render as an
/// empty box, so it is replaced with a neutral "app" glyph.
/// </summary>
internal static class ShellIcons
{
    public static readonly string Screenshot = Glyph(0xE722);   // Camera
    public static readonly string Settings = Glyph(0xE713);
    public static readonly string Search = Glyph(0xE721);
    public static readonly string Add = Glyph(0xE710);
    public static readonly string Close = Glyph(0xE711);
    public static readonly string Jobs = Glyph(0xE895);         // Sync
    public static readonly string Tool = Glyph(0xE71D);         // AllApps
    public static readonly string Info = Glyph(0xE946);
    public static readonly string Success = Glyph(0xE73E);      // CheckMark
    public static readonly string Warning = Glyph(0xE7BA);
    public static readonly string Error = Glyph(0xE783);
    public static readonly string ErrorBadge = Glyph(0xEA39);

    private static string Glyph(int codePoint) => char.ConvertFromUtf32(codePoint);

    private static readonly string[] IconFonts = { "Segoe Fluent Icons", "Segoe MDL2 Assets" };
    private static readonly ConcurrentDictionary<int, bool> Available = new();

    // Glyphs registered by early module versions that show an unrelated picture. Only replaced while the
    // module still declares exactly this glyph, so a module that picks its own icon always wins.
    private static readonly Dictionary<(string ModuleId, string Glyph), string> Corrections = new()
    {
        [("ruler", Glyph(0xE7C9))] = Glyph(0xED5E),   // TouchPointer → Ruler
        [("qr", Glyph(0xE8EF))] = Glyph(0xED14),      // Calculator → QRCode
    };

    /// <summary>The glyph to draw for a module or command icon.</summary>
    public static string Resolve(string? glyph, string moduleId = "")
    {
        if (string.IsNullOrWhiteSpace(glyph)) return Tool;
        if (Corrections.TryGetValue((moduleId, glyph), out string? corrected) && IsAvailable(corrected)) return corrected;
        return IsAvailable(glyph) ? glyph : Tool;
    }

    /// <summary>True when every private-use character in <paramref name="glyph"/> exists in one of the icon fonts.</summary>
    public static bool IsAvailable(string glyph)
    {
        foreach (char c in glyph)
        {
            if (c < 0xE000 || c > 0xF8FF) continue;
            if (!Available.GetOrAdd(c, Lookup)) return false;
        }
        return true;
    }

    private static bool Lookup(int codePoint)
    {
        foreach (string family in IconFonts)
        {
            try
            {
                var typeface = new Typeface(new FontFamily(family), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                if (typeface.TryGetGlyphTypeface(out GlyphTypeface glyphs) && glyphs.CharacterToGlyphMap.ContainsKey(codePoint)) return true;
            }
            catch (Exception ex) when (ex is ArgumentException or System.IO.IOException or UnauthorizedAccessException) { }
        }
        return false;
    }
}
