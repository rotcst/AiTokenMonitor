using System.Windows;
using CodexWeeklyMonitor.Services;

namespace CodexWeeklyMonitor;

public partial class App : System.Windows.Application
{
    private SingleInstanceCoordinator? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            if (UpdateInstaller.TryApplyFromArguments(e.Args))
            {
                Shutdown();
                return;
            }
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                Loc.T("update.installErrorDetail", exception.Message),
                Loc.T("update.title"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
            return;
        }

        UpdateInstaller.ScheduleCleanup(UpdateInstaller.GetCleanupPath(e.Args));

        _singleInstance = new SingleInstanceCoordinator();
        if (!_singleInstance.IsPrimary)
        {
            _singleInstance.SignalPrimary();
            Shutdown();
            return;
        }

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
