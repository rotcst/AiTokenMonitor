using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using CodexWeeklyMonitor;
using CodexWeeklyMonitor.Services;

internal static class TopmostWindowTests
{
    private static readonly nint HwndTopmost = new(-1);
    private static readonly nint HwndNotTopmost = new(-2);
    private const uint PositionFlags = 0x0001 | 0x0002 | 0x0010;

    internal static void RestoresNativeTopmost()
    {
        var window = new MainWindow(new FakeTrayIconService(), enableMonitoring: false);
        try
        {
            window.Show();
            Pump(TimeSpan.FromMilliseconds(100));
            window.Topmost = true;
            var handle = new WindowInteropHelper(window).Handle;
            Check(SetWindowPos(handle, HwndNotTopmost, 0, 0, 0, 0, PositionFlags), "原生降层失败。");
            Check(window.Topmost, "WPF 属性应仍为 true，才能复现实际层级与菜单不同步。");
            Pump(TimeSpan.FromMilliseconds(1300));
            Check(IsTopmost(handle), "主窗口没有恢复被清除的原生置顶状态。");

            window.EnterGaugeMode();
            var gauge = window.GaugeWindowForTesting!;
            var gaugeHandle = new WindowInteropHelper(gauge).Handle;
            Check(SetWindowPos(gaugeHandle, HwndNotTopmost, 0, 0, 0, 0, PositionFlags), "悬浮球原生降层失败。");
            Pump(TimeSpan.FromMilliseconds(1300));
            Check(IsTopmost(gaugeHandle), "悬浮球没有恢复被清除的原生置顶状态。");

            window.ExitGaugeMode();
            window.WindowState = WindowState.Minimized;
            Pump(TimeSpan.FromMilliseconds(100));
            Check(window.WindowState == WindowState.Minimized, "置顶维护不应恢复最小化窗口。");
            window.WindowState = WindowState.Normal;
            window.ShowInTaskbar = false;
            window.Hide();
            Pump(TimeSpan.FromMilliseconds(100));
            Check(!window.IsVisible, "置顶维护不应唤醒隐藏窗口。");
            window.RestoreFromExternalLaunch();
            Pump(TimeSpan.FromMilliseconds(1300));
            Check(IsTopmost(new WindowInteropHelper(window).Handle), "恢复窗口后置顶丢失。");

            window.Topmost = false;
            Pump(TimeSpan.FromMilliseconds(1300));
            Check(!IsTopmost(new WindowInteropHelper(window).Handle), "关闭置顶后仍被强制置顶。");
        }
        finally
        {
            window.Close();
        }
    }

    internal static void StaysAboveOtherProcessWithoutFocus()
    {
        using var peer = StartPeer();
        var peerHandleText = peer.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        var peerHandle = new nint(long.Parse(peerHandleText!));
        var gauge = new GaugeWindow { Left = 80, Top = 80, Topmost = true };
        try
        {
            gauge.Show();
            Pump(TimeSpan.FromMilliseconds(100));
            var handle = new WindowInteropHelper(gauge).Handle;
            Check(SetForegroundWindow(peerHandle), "无法激活测试窗口。");
            Check(SetWindowPos(peerHandle, HwndTopmost, 0, 0, 0, 0, PositionFlags), "无法置顶测试窗口。");
            var foreground = GetForegroundWindow();
            Check(IsAbove(peerHandle, handle), "测试窗口必须先压住悬浮球。");
            Pump(TimeSpan.FromMilliseconds(1300));
            Check(IsAbove(handle, peerHandle), "其他进程的置顶窗口仍压住悬浮球。");
            Check(GetForegroundWindow() == foreground, "恢复置顶抢走了其他窗口的输入焦点。");

            var menu = ((Grid)gauge.FindName("OrbSurface")).ContextMenu!;
            menu.PlacementTarget = gauge;
            menu.IsOpen = true;
            Pump(TimeSpan.FromMilliseconds(1300));
            Check(menu.IsOpen, "置顶维护关闭了右键菜单。");
            var menuHandle = ((HwndSource)PresentationSource.FromVisual(menu)!).Handle;
            Check(IsAbove(menuHandle, handle), "悬浮球压住了自己的右键菜单。");
            menu.IsOpen = false;

            var dialog = new Window { Owner = gauge, Width = 180, Height = 100, ShowInTaskbar = false };
            try
            {
                dialog.Show();
                Pump(TimeSpan.FromMilliseconds(1300));
                Check(IsAbove(new WindowInteropHelper(dialog).Handle, handle), "置顶维护压住了自己的对话框。");
            }
            finally
            {
                dialog.Close();
            }
            gauge.Topmost = false;
            Pump(TimeSpan.FromMilliseconds(1300));
            Check(IsAbove(peerHandle, handle), "关闭置顶后仍覆盖其他置顶窗口。");
        }
        finally
        {
            gauge.Close();
            peer.StandardInput.WriteLine("close");
            if (!peer.WaitForExit(5000))
            {
                peer.Kill();
            }
        }
    }

    internal static void IdleMaintenanceIsQuiet()
    {
        var window = new Window { Width = 120, Height = 100, Left = 80, Top = 80, Topmost = true };
        using var controller = new TopmostWindowController(window);
        try
        {
            window.Show();
            Pump(TimeSpan.FromMilliseconds(1300));
            var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)!;
            var moves = 0;
            nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
            {
                if (message == 0x0047) moves++; // WM_WINDOWPOSCHANGED
                return 0;
            }
            source.AddHook(Hook);
            using var process = Process.GetCurrentProcess();
            var cpuStart = process.TotalProcessorTime;
            var elapsed = Stopwatch.StartNew();
            Pump(TimeSpan.FromMilliseconds(3200));
            var cpu = process.TotalProcessorTime - cpuStart;
            source.RemoveHook(Hook);
            Check(moves == 0, "稳定置顶时不应反复调用原生窗口重排。");
            Console.WriteLine($"PERF  置顶静置 {elapsed.Elapsed.TotalSeconds:F2}s，进程 CPU {cpu.TotalMilliseconds:F1}ms，窗口重排 {moves} 次。");
        }
        finally
        {
            window.Close();
        }
    }

    internal static int RunPeer()
    {
        using var runner = new StaTestRunner();
        Window? window = null;
        var failure = runner.Execute(() =>
        {
            window = new Window
            {
                Title = "AiTokenMonitor topmost regression peer",
                Width = 300,
                Height = 250,
                Left = 80,
                Top = 80,
                Topmost = true,
                ShowInTaskbar = false,
            };
            window.Show();
            Console.WriteLine(new WindowInteropHelper(window).Handle.ToInt64());
        });
        if (failure is not null)
        {
            Console.Error.WriteLine(failure);
            return 1;
        }
        Console.ReadLine();
        runner.Execute(() => window!.Close());
        return 0;
    }

    private static Process StartPeer()
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        };
        info.ArgumentList.Add("--topmost-peer");
        return Process.Start(info)!;
    }

    private static bool IsTopmost(nint handle) => (GetWindowLong(handle, -20) & 0x0008) != 0;

    private static bool IsAbove(nint upper, nint lower)
    {
        for (var handle = GetWindow(lower, 3); handle != 0; handle = GetWindow(handle, 3))
        {
            if (handle == upper)
            {
                return true;
            }
        }
        return false;
    }

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint handle, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint handle, int index);
    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint handle, uint command);
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint handle);
}
