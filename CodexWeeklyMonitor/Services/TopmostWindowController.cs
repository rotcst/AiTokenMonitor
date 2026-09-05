using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace CodexWeeklyMonitor.Services;

/// <summary>Maintains the native Z order without activating the monitor or reopening hidden windows.</summary>
internal sealed class TopmostWindowController : IDisposable
{
    private static readonly DependencyPropertyDescriptor TopmostDescriptor =
        DependencyPropertyDescriptor.FromProperty(Window.TopmostProperty, typeof(Window));
    private static readonly DependencyPropertyDescriptor TaskbarDescriptor =
        DependencyPropertyDescriptor.FromProperty(Window.ShowInTaskbarProperty, typeof(Window));
    private readonly Window _window;
    private readonly DispatcherTimer _timer;
    private readonly WinEventCallback _foregroundChanged;
    private nint _foregroundHook;
    private bool _queued;
    private bool _disposed;

    internal TopmostWindowController(Window window)
    {
        _window = window;
        _foregroundChanged = OnForegroundChanged;
        // Foreground events handle app switches immediately. The low-frequency fallback also
        // catches overlays shown without activation and native state changes WPF does not observe.
        _timer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _timer.Tick += OnTick;
        window.SourceInitialized += OnStateChanged;
        window.IsVisibleChanged += OnVisibilityChanged;
        window.StateChanged += OnStateChanged;
        window.Activated += OnStateChanged;
        window.Deactivated += OnStateChanged;
        window.Closed += OnClosed;
        TopmostDescriptor.AddValueChanged(window, OnStateChanged);
        TaskbarDescriptor.AddValueChanged(window, OnStateChanged);
        UpdateMonitoring();
    }

    private bool ShouldMaintain => !_disposed && _window.Topmost && _window.IsVisible &&
                                   _window.WindowState != WindowState.Minimized;

    private void OnStateChanged(object? sender, EventArgs e) => UpdateMonitoring();
    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateMonitoring();
    private void OnTick(object? sender, EventArgs e) => MaintainTopmost();
    private void OnClosed(object? sender, EventArgs e) => Dispose();

    private void UpdateMonitoring()
    {
        if (ShouldMaintain)
        {
            _timer.Start();
            if (_foregroundHook == 0)
            {
                // OUTOFCONTEXT: no injection into other processes. A failed hook still has the timer.
                _foregroundHook = SetWinEventHook(3, 3, 0, _foregroundChanged, 0, 0, 0);
            }
            QueueMaintenance();
        }
        else
        {
            _timer.Stop();
            ReleaseHook();
        }
    }

    private void OnForegroundChanged(nint hook, uint eventType, nint hwnd, int objectId,
        int childId, uint threadId, uint eventTime) => QueueMaintenance();

    private void QueueMaintenance()
    {
        if (_disposed || _queued || _window.Dispatcher.HasShutdownStarted)
        {
            return;
        }
        _queued = true;
        _window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _queued = false;
            MaintainTopmost();
        }));
    }

    private void MaintainTopmost()
    {
        if (!ShouldMaintain || !_window.IsEnabled)
        {
            return;
        }

        // Read the current HWND each time: taskbar/style transitions can recreate WPF handles.
        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == 0 || !IsWindowVisible(handle) || !GetWindowRect(handle, out var bounds))
        {
            return;
        }

        var needsRaise = (GetWindowLong(handle, -20) & 0x0008) == 0; // WS_EX_TOPMOST
        var above = GetWindow(handle, 3); // GW_HWNDPREV
        for (var count = 0; above != 0 && count < 256; count++, above = GetWindow(above, 3))
        {
            if (!IsWindowVisible(above) || IsIconic(above))
            {
                continue;
            }
            _ = GetWindowThreadProcessId(above, out var processId);
            if (processId == (uint)Environment.ProcessId)
            {
                // Let our context menus, tray menu and update dialogs stay above their windows.
                return;
            }
            if (GetWindowRect(above, out var other) &&
                other.Left < bounds.Right && other.Right > bounds.Left &&
                other.Top < bounds.Bottom && other.Bottom > bounds.Top && !IsCloaked(above))
            {
                needsRaise = true;
            }
        }
        if (needsRaise)
        {
            // HWND_TOPMOST + NOSIZE | NOMOVE | NOACTIVATE | NOOWNERZORDER. Never toggle the
            // WPF preference or call Activate(), which would steal the user's keyboard focus.
            _ = SetWindowPos(handle, new nint(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0200);
        }
    }

    private static bool IsCloaked(nint handle) =>
        DwmGetWindowAttribute(handle, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    private void ReleaseHook()
    {
        if (_foregroundHook != 0)
        {
            _ = UnhookWinEvent(_foregroundHook);
            _foregroundHook = 0;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        ReleaseHook();
        _window.SourceInitialized -= OnStateChanged;
        _window.IsVisibleChanged -= OnVisibilityChanged;
        _window.StateChanged -= OnStateChanged;
        _window.Activated -= OnStateChanged;
        _window.Deactivated -= OnStateChanged;
        _window.Closed -= OnClosed;
        TopmostDescriptor.RemoveValueChanged(_window, OnStateChanged);
        TaskbarDescriptor.RemoveValueChanged(_window, OnStateChanged);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    private delegate void WinEventCallback(nint hook, uint eventType, nint hwnd, int objectId,
        int childId, uint threadId, uint eventTime);

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module,
        WinEventCallback callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint handle, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint handle, int index);
    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint handle, uint command);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint handle);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint handle, out NativeRect bounds);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint handle, int attribute, out int value, int size);
}
