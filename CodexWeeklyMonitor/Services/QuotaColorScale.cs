using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace CodexWeeklyMonitor.Services;

/// <summary>
/// Maps remaining quota to one continuous traffic-light color scale. The midpoint is the existing
/// warning color, so the scale remains recognizable while avoiding abrupt threshold changes.
/// </summary>
internal static class QuotaColorScale
{
    internal static readonly Color EmptyColor = Color.FromRgb(0xFF, 0x6B, 0x6B);
    internal static readonly Color MidColor = Color.FromRgb(0xF0, 0xB9, 0x5C);
    internal static readonly Color FullColor = Color.FromRgb(0x7E, 0xE7, 0x87);

    internal static Color ColorForRemaining(int remainingPercent)
    {
        var remaining = Math.Clamp(remainingPercent, 0, 100);
        return remaining >= 50
            ? Interpolate(MidColor, FullColor, (remaining - 50) / 50d)
            : Interpolate(EmptyColor, MidColor, remaining / 50d);
    }

    internal static SolidColorBrush BrushForRemaining(int remainingPercent)
    {
        var brush = new SolidColorBrush(ColorForRemaining(remainingPercent));
        brush.Freeze();
        return brush;
    }

    private static Color Interpolate(Color from, Color to, double amount) => Color.FromRgb(
        InterpolateChannel(from.R, to.R, amount),
        InterpolateChannel(from.G, to.G, amount),
        InterpolateChannel(from.B, to.B, amount));

    private static byte InterpolateChannel(byte from, byte to, double amount) =>
        (byte)Math.Round(from + ((to - from) * amount), MidpointRounding.AwayFromZero);
}
