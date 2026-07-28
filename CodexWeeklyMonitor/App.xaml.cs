using System.Windows;
using CodexWeeklyMonitor.Services;

namespace CodexWeeklyMonitor;

public partial class App : System.Windows.Application
{
    private SingleInstanceCoordinator? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new SingleInstanceCoordinator();
        if (!_singleInstance.IsPrimary)
        {
            _singleInstance.SignalPrimary();
            Shutdown();
            return;
        }

        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        window.Activate();

        _singleInstance.ActivationRequested += SingleInstance_ActivationRequested;
        _singleInstance.StartListening();
    }

    private void SingleInstance_ActivationRequested(object? sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (MainWindow is MainWindow window)
            {
                window.RestoreFromExternalLaunch();
            }
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_singleInstance is not null)
        {
            _singleInstance.ActivationRequested -= SingleInstance_ActivationRequested;
            _singleInstance.Dispose();
            _singleInstance = null;
        }

        base.OnExit(e);
    }
}
