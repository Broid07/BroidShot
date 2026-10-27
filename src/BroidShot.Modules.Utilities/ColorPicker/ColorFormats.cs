using System;
using System.Globalization;

namespace BroidShot.Modules.Utilities;

/// <summary>Copy formats offered by the colour picker.</summary>
public enum ColorFormat { Hex, Rgb, Hsl, CSharp }

internal readonly record struct Rgb(byte R, byte G, byte B);

/// <summary>Hue in degrees [0,360), saturation and lightness in [0,1].</summary>
internal readonly record struct Hsl(double H, double S, double L);

/// <summary>Colour text conversions. Pure functions; invariant culture so output never depends on the user's locale.</summary>
internal static class ColorFormats
{
    public static readonly ColorFormat[] All = { ColorFormat.Hex, ColorFormat.Rgb, ColorFormat.Hsl, ColorFormat.CSharp };

    public static string Name(ColorFormat format) => format switch
    {
        ColorFormat.Hex => "HEX",
        ColorFormat.Rgb => "RGB",
        ColorFormat.Hsl => "HSL",
        _ => "C# Color"
    };

    public static string Format(Rgb color, ColorFormat format) => format switch
    {
        ColorFormat.Hex => ToHex(color),
        ColorFormat.Rgb => ToRgbText(color),
        ColorFormat.Hsl => ToHslText(color),
        _ => ToCSharpText(color)
    };

    /// <summary>"#RRGGBB" (upper case).</summary>
    public static string ToHex(Rgb c) => string.Create(CultureInfo.InvariantCulture, $"#{c.R:X2}{c.G:X2}{c.B:X2}");

    /// <summary>"rgb(18, 52, 86)".</summary>
    public static string ToRgbText(Rgb c) => string.Create(CultureInfo.InvariantCulture, $"rgb({c.R}, {c.G}, {c.B})");

    /// <summary>"hsl(210, 65%, 20%)" with whole-number components.</summary>
    public static string ToHslText(Rgb c)
    {
        Hsl hsl = ToHsl(c);
        int h = (int)Math.Round(hsl.H, MidpointRounding.AwayFromZero) % 360;
        int s = (int)Math.Round(hsl.S * 100, MidpointRounding.AwayFromZero);
        int l = (int)Math.Round(hsl.L * 100, MidpointRounding.AwayFromZero);
        return string.Create(CultureInfo.InvariantCulture, $"hsl({h}, {s}%, {l}%)");
    }

    /// <summary>"Color.FromRgb(18, 52, 86)" (System.Windows.Media.Color).</summary>
    public static string ToCSharpText(Rgb c) => string.Create(CultureInfo.InvariantCulture, $"Color.FromRgb({c.R}, {c.G}, {c.B})");

    public static Hsl ToHsl(Rgb c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2;
        double delta = max - min;
        if (delta < 1e-12) return new Hsl(0, 0, l);
        double s = l > 0.5 ? delta / (2 - max - min) : delta / (max + min);
        double h;
        if (max == r) h = (g - b) / delta + (g < b ? 6 : 0);
        else if (max == g) h = (b - r) / delta + 2;
        else h = (r - g) / delta + 4;
        return new Hsl(h * 60, s, l);
    }

    public static Rgb FromHsl(Hsl hsl)
    {
        double h = ((hsl.H % 360) + 360) % 360 / 360.0, s = Math.Clamp(hsl.S, 0, 1), l = Math.Clamp(hsl.L, 0, 1);
        if (s == 0) { byte v = ToByte(l); return new Rgb(v, v, v); }
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;
        return new Rgb(ToByte(Hue(p, q, h + 1.0 / 3)), ToByte(Hue(p, q, h)), ToByte(Hue(p, q, h - 1.0 / 3)));
    }

    private static double Hue(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 0.5) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }

    private static byte ToByte(double unit) => (byte)Math.Clamp((int)Math.Round(unit * 255, MidpointRounding.AwayFromZero), 0, 255);

    /// <summary>Parses "#RRGGBB", "RRGGBB", "#RGB" (case-insensitive).</summary>
    public static bool TryParseHex(string? text, out Rgb color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = text.Trim().TrimStart('#');
        if (t.Length == 3)
            t = string.Concat(t[0], t[0], t[1], t[1], t[2], t[2]);
        if (t.Length != 6 || !int.TryParse(t, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int value)) return false;
        color = new Rgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }
}
