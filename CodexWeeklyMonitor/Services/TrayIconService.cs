using System.Drawing;
using Forms = System.Windows.Forms;
using DrawingIcon = System.Drawing.Icon;
using DrawingSystemIcons = System.Drawing.SystemIcons;

namespace CodexWeeklyMonitor.Services;

/// <summary>Screen position, in physical pixels, where the tray menu should appear.</summary>
internal sealed record TrayMenuRequest(int X, int Y);

internal interface ITrayIconService : IDisposable
{
    event EventHandler? ShowRequested;

    event EventHandler? ExitRequested;

    /// <summary>
    /// Raised on right-click. The window answers by opening its own WPF menu, so the tray and the
    /// card share one control, one style and one popup animation.
    /// </summary>
    event EventHandler<TrayMenuRequest>? MenuRequested;

    bool Visible { get; set; }

    string ToolTipText { get; set; }
}

/// <summary>
/// Owns the notification-area icon only. The menu itself is WPF, built by the window: a WinForms
/// <c>ContextMenuStrip</c> can be recoloured to match but never animates the same way, so the two
/// menus looked different on open no matter how closely the palettes were kept in sync.
/// </summary>
internal sealed class TrayIconService : ITrayIconService
{
    private readonly DrawingIcon _icon;
    private readonly Forms.NotifyIcon _notifyIcon;
    private bool _disposed;

    public TrayIconService()
    {
        _icon = LoadApplicationIcon();
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _icon,
            Text = "AI TOKEN 用量监控",
            Visible = true,
        };
        _notifyIcon.MouseDoubleClick += NotifyIcon_MouseDoubleClick;
        _notifyIcon.MouseUp += NotifyIcon_MouseUp;
    }

    public event EventHandler? ShowRequested;

    public event EventHandler? ExitRequested;

    public event EventHandler<TrayMenuRequest>? MenuRequested;

    public bool Visible
    {
        get => _notifyIcon.Visible;
        set => _notifyIcon.Visible = value;
    }

    public string ToolTipText
    {
        get => _notifyIcon.Text;
        set => _notifyIcon.Text = TrimNotifyText(value);
    }

    internal void RaiseExitRequested() => ExitRequested?.Invoke(this, EventArgs.Empty);

    private void NotifyIcon_MouseDoubleClick(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button == Forms.MouseButtons.Left)
        {
            ShowRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void NotifyIcon_MouseUp(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button != Forms.MouseButtons.Right)
        {
            return;
        }

        var cursor = Forms.Cursor.Position;
        MenuRequested?.Invoke(this, new TrayMenuRequest(cursor.X, cursor.Y));
    }

    private static string TrimNotifyText(string? value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "AI TOKEN 用量监控" : value.Trim();
        return text.Length <= 63 ? text : text[..63];
    }

    private static DrawingIcon LoadApplicationIcon()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            var icon = DrawingIcon.ExtractAssociatedIcon(processPath);
            if (icon is not null)
            {
                return icon;
            }
        }

        return (DrawingIcon)DrawingSystemIcons.Application.Clone();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.MouseDoubleClick -= NotifyIcon_MouseDoubleClick;
        _notifyIcon.MouseUp -= NotifyIcon_MouseUp;
        _notifyIcon.Dispose();
        _icon.Dispose();
    }
}
