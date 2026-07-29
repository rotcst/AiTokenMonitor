using System.Windows;
using System.Windows.Input;
using CodexWeeklyMonitor.Controls;
using Point = System.Windows.Point;

namespace CodexWeeklyMonitor;

/// <summary>
/// The compact "orb" mode: a small circular racing-style gauge showing Codex and Claude usage at a
/// glance. Double-clicking it asks the main window to restore; dragging moves it.
/// </summary>
public partial class GaugeWindow : Window
{
    // The visible orb, shrunk from the old 150 so the floating ball reads as an accessory, not a
    // second window. Everything else — shadow canvas, ground shadow — is derived so one number moves it.
    public const double OrbDiameter = 118d;
    private const double SidePad = 18d;
    private const double TopPad = 12d;
    private const double BottomPad = 32d;

    public const double OrbOffsetX = SidePad;
    public const double OrbOffsetY = TopPad;
    public const double ShadowCanvasWidth = OrbDiameter + (2 * SidePad);
    public const double ShadowCanvasHeight = TopPad + OrbDiameter + BottomPad;

    // Soft ground shadow pooled just under the lower rim, scaled to the orb.
    public static double GroundShadowWidth => OrbDiameter * 0.72;
    public static double GroundShadowHeight => OrbDiameter * 0.18;
    public static double GroundShadowLeft => OrbOffsetX + (OrbDiameter / 2) - (GroundShadowWidth / 2);
    public static double GroundShadowTop => OrbOffsetY + OrbDiameter - (GroundShadowHeight * 0.35);

    private readonly GaugeControl _gauge = new();

    public GaugeWindow()
    {
        InitializeComponent();
        GaugeHost.Content = _gauge;
    }

    /// <summary>Raised when the user double-clicks the orb to return to the full window.</summary>
    public event EventHandler? RestoreRequested;

    public void SetValues(int? codexPercent, int? claudePercent) =>
        _gauge.Update(codexPercent, claudePercent);

    /// <summary>
    /// Positions the visible orb at the requested screen coordinate while keeping the transparent
    /// shadow padding out of the caller's placement calculations.
    /// </summary>
    internal void PlaceOrbAt(double left, double top)
    {
        Left = left - OrbOffsetX;
        Top = top - OrbOffsetY;
    }

    /// <summary>Returns the visible orb's screen coordinate rather than the shadow canvas origin.</summary>
    internal Point GetOrbTopLeft() => new(Left + OrbOffsetX, Top + OrbOffsetY);

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            e.Handled = true;
            RestoreRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The button may be released between the event and DragMove.
        }
    }
}
