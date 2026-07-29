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
    public const double OrbDiameter = 150d;
    public const double ShadowCanvasWidth = 190d;
    public const double ShadowCanvasHeight = 198d;
    public const double OrbOffsetX = 20d;
    public const double OrbOffsetY = 12d;

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
