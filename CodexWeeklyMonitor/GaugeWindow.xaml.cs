using System.Windows;
using System.Windows.Input;
using CodexWeeklyMonitor.Controls;

namespace CodexWeeklyMonitor;

/// <summary>
/// The compact "orb" mode: a small circular racing-style gauge showing Codex and Claude usage at a
/// glance. Double-clicking it asks the main window to restore; dragging moves it.
/// </summary>
public partial class GaugeWindow : Window
{
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
