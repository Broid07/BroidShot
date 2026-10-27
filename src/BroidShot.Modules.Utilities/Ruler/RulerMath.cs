using System;
using System.Globalization;

namespace BroidShot.Modules.Utilities;

/// <summary>A distance measured between two screen points, in physical pixels.</summary>
internal readonly record struct RulerMeasurement(int Width, int Height, double Diagonal, double DpiScale)
{
    /// <summary>Width in device-independent units (1/96 inch) at <see cref="DpiScale"/>.</summary>
    public double WidthDip => Width / DpiScale;
    public double HeightDip => Height / DpiScale;
    public double DiagonalDip => Diagonal / DpiScale;

    /// <summary>"300 × 200 px · çapraz 360.56 px" (invariant numbers so the text pastes the same everywhere).</summary>
    public string ToClipboardText()
    {
        var c = CultureInfo.InvariantCulture;
        return string.Create(c, $"{Width} × {Height} px, çapraz {Diagonal:0.##} px");
    }

    /// <summary>Longer line for the overlay label; adds the logical size on scaled displays.</summary>
    public string ToLabel()
    {
        var c = CultureInfo.InvariantCulture;
        string text = string.Create(c, $"{Width} × {Height} px   çapraz {Diagonal:0.##}");
        if (Math.Abs(DpiScale - 1.0) > 0.01)
            text += string.Create(c, $"   (%{Math.Round(DpiScale * 100)} ölçekte {WidthDip:0.#} × {HeightDip:0.#} dp)");
        return text;
    }
}

internal static class RulerMath
{
    /// <summary>
    /// Applies Shift snapping to the dragged end point: the longer axis is kept and the other one is locked to the
    /// start, giving an exactly horizontal or vertical measurement.
    /// </summary>
    public static (int X, int Y) Snap(int startX, int startY, int endX, int endY, bool shift)
    {
        if (!shift) return (endX, endY);
        return Math.Abs(endX - startX) >= Math.Abs(endY - startY) ? (endX, startY) : (startX, endY);
    }

    public static RulerMeasurement Measure(int startX, int startY, int endX, int endY, bool shift = false, double dpiScale = 1.0)
    {
        (int ex, int ey) = Snap(startX, startY, endX, endY, shift);
        int width = Math.Abs(ex - startX), height = Math.Abs(ey - startY);
        double diagonal = Math.Sqrt((double)width * width + (double)height * height);
        return new RulerMeasurement(width, height, Math.Round(diagonal, 2), dpiScale > 0 ? dpiScale : 1.0);
    }

    /// <summary>Converts device-independent units to physical pixels at the given scale (1.5 = 150 %).</summary>
    public static int ToPhysical(double dip, double dpiScale) => (int)Math.Round(dip * dpiScale, MidpointRounding.AwayFromZero);

    /// <summary>Converts physical pixels to device-independent units.</summary>
    public static double ToDip(double pixels, double dpiScale) => dpiScale > 0 ? pixels / dpiScale : pixels;
}
