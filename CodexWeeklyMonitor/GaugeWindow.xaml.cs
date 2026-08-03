using System.Windows;
using System.Windows.Input;
using CodexWeeklyMonitor.Controls;
using CodexWeeklyMonitor.Models;
using CodexWeeklyMonitor.Services;
using Point = System.Windows.Point;

namespace CodexWeeklyMonitor;

/// <summary>
/// The compact "orb" mode: a small circular racing-style gauge showing Codex and Claude usage at a
/// glance. Clicking either half switches that provider's quota window; dragging moves the orb.
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
        GaugeVersionMenuItem.Header = AppVersion.Display;
    }

    /// <summary>Raised when the orb's context menu asks to return to the full window.</summary>
    public event EventHandler? RestoreRequested;

    /// <summary>Raised when the orb menu asks the owner to refresh usage now.</summary>
    public event EventHandler? RefreshRequested;

    /// <summary>Raised when the orb menu asks the owner to check GitHub Releases.</summary>
    public event EventHandler? UpdateRequested;

    /// <summary>Keeps the full card's persisted topmost preference in sync with the orb menu.</summary>
    public event Action<bool>? TopmostChangedRequested;

    public void SetWindows(
        RateLimitWindow? codexFiveHour,
        RateLimitWindow? codexWeekly,
        RateLimitWindow? claudeFiveHour,
        RateLimitWindow? claudeWeekly) =>
        _gauge.Update(codexFiveHour, codexWeekly, claudeFiveHour, claudeWeekly);

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
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var providerPoint = e.GetPosition(_gauge);
        var dragStart = GetOrbTopLeft();
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The button may be released between the event and DragMove.
        }

        var dragEnd = GetOrbTopLeft();
        if (e.ClickCount == 1 &&
            IsClickGesture(dragStart, dragEnd) &&
            _gauge.ToggleProviderAt(providerPoint))
        {
            e.Handled = true;
        }
    }

    internal static bool IsClickGesture(Point start, Point end) =>
        Math.Abs(end.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
        Math.Abs(end.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance;

    private void RefreshMenuItem_Click(object sender, RoutedEventArgs e) =>
        RefreshRequested?.Invoke(this, EventArgs.Empty);

    private void RestoreMenuItem_Click(object sender, RoutedEventArgs e) =>
        RestoreRequested?.Invoke(this, EventArgs.Empty);

    private void CheckUpdatesMenuItem_Click(object sender, RoutedEventArgs e) =>
        UpdateRequested?.Invoke(this, EventArgs.Empty);

    private void GaugeContextMenu_Opened(object sender, RoutedEventArgs e) =>
        GaugeTopmostMenuItem.IsChecked = Topmost;

    private void TopmostMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Topmost = GaugeTopmostMenuItem.IsChecked;
        TopmostChangedRequested?.Invoke(Topmost);
    }
}
