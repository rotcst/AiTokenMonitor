using System.Threading;

namespace CodexWeeklyMonitor.Services;

/// <summary>
/// Keeps one monitor process alive and lets later launches ask that process to restore its window.
/// The activation event is created before the mutex is checked, so a very early second launch is
/// retained until the primary process starts listening.
/// </summary>
internal sealed class SingleInstanceCoordinator : IDisposable
{
    private const string DefaultMutexName = "Local\\CodexUsageMonitor.SingleInstance";
    private const string DefaultActivationEventName = "Local\\CodexUsageMonitor.ShowMainWindow";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activationEvent;
    private RegisteredWaitHandle? _activationRegistration;
    private bool _disposed;

    public SingleInstanceCoordinator()
        : this(DefaultMutexName, DefaultActivationEventName)
    {
    }

    internal SingleInstanceCoordinator(string mutexName, string activationEventName)
    {
        _activationEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            activationEventName);
        _mutex = new Mutex(initiallyOwned: false, mutexName);

        try
        {
            IsPrimary = _mutex.WaitOne(0, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            // The previous process terminated without releasing the mutex. This process now owns it.
            IsPrimary = true;
        }
    }

    public event EventHandler? ActivationRequested;

    public bool IsPrimary { get; }

    public void StartListening()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsPrimary)
        {
            throw new InvalidOperationException("Only the primary instance can listen for activation.");
        }

        _activationRegistration ??= ThreadPool.RegisterWaitForSingleObject(
            _activationEvent,
            static (state, _) => ((SingleInstanceCoordinator)state!).RaiseActivationRequested(),
            this,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    public void SignalPrimary()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsPrimary)
        {
            _activationEvent.Set();
        }
    }

    private void RaiseActivationRequested()
    {
        if (!_disposed)
        {
            ActivationRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var registration = Interlocked.Exchange(ref _activationRegistration, null);
        if (registration is not null)
        {
            using var unregistered = new ManualResetEvent(initialState: false);
            if (registration.Unregister(unregistered))
            {
                _ = unregistered.WaitOne(TimeSpan.FromSeconds(2));
            }
        }

        _activationEvent.Dispose();

        if (IsPrimary)
        {
            _mutex.ReleaseMutex();
        }

        _mutex.Dispose();
    }
}
