using System.Runtime.InteropServices;

namespace CodexWeeklyMonitor.Services;

/// <summary>
/// Asks DWM for the rounded corners and border the window used to draw itself.
/// </summary>
/// <remarks>
/// The card was previously an <c>AllowsTransparency</c> window, which makes it a layered window -
/// and Windows skips the minimise/restore animation for those. Dropping transparency restores the
/// native animation, and DWM supplies the corner rounding and drop shadow in its place.
/// </remarks>
internal static class DwmWindowChrome
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCornerRound = 2;

    /// <summary>Sentinel telling DWM to keep its default border colour.</summary>
    private const int DwmwaColorDefault = unchecked((int)0xFFFFFFFF);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int value,
        int size);

    /// <summary>
    /// Best-effort: the attributes only exist on Windows 11, and squared corners on Windows 10 are
    /// a cosmetic difference rather than a failure.
    /// </summary>
    public static void ApplyRoundedCorners(IntPtr handle, uint? borderColor = null)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var preference = DwmwaCornerRound;
            _ = DwmSetWindowAttribute(
                handle,
                DwmwaWindowCornerPreference,
                ref preference,
                sizeof(int));

            var color = borderColor is { } value ? unchecked((int)value) : DwmwaColorDefault;
            _ = DwmSetWindowAttribute(handle, DwmwaBorderColor, ref color, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // dwmapi.dll is always present on supported versions; ignore anything exotic.
        }
        catch (EntryPointNotFoundException)
        {
            // Older Windows without the attribute; nothing to do.
        }
    }
}
