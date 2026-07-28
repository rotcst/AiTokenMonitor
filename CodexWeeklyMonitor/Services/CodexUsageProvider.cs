using CodexWeeklyMonitor.Models;

namespace CodexWeeklyMonitor.Services;

/// <summary>
/// Supplies Codex usage from the official HTTP API, falling back to the local
/// <c>codex app-server</c> when the stored credential cannot be used.
/// </summary>
/// <remarks>
/// The API path is preferred because it works whether or not Codex is installed or running, costs
/// no child process, and returns strictly more data (per-model limits, credit estimates, profile
/// statistics). The app-server path remains as the recovery route: it is the only component that
/// can renew an expired token, which this app deliberately does not do itself.
/// </remarks>
public sealed class CodexUsageProvider : IAsyncDisposable
{
    private readonly CodexApiClient? _apiClient;
    private readonly CodexRateLimitClient _appServerClient = new();
    private readonly PollThrottle _throttle = new(
        minimumInterval: TimeSpan.FromSeconds(20),
        maximumBackoff: TimeSpan.FromMinutes(15));

    private bool _disposed;

    public CodexUsageProvider()
        : this(new CodexApiClient())
    {
    }

    internal CodexUsageProvider(CodexApiClient? apiClient)
    {
        _apiClient = apiClient;
        _appServerClient.RefreshSuggested += (sender, e) => RefreshSuggested?.Invoke(sender, e);
    }

    public event EventHandler? RefreshSuggested;

    /// <summary>Which route produced the last successful snapshot, for display and diagnostics.</summary>
    public string? ActiveSource { get; private set; }

    public async Task<CodexUsageSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        CodexApiException? apiFailure = null;
        if (_apiClient is not null && _throttle.TryAcquire(DateTimeOffset.UtcNow))
        {
            try
            {
                var snapshot = await _apiClient.FetchAsync(cancellationToken).ConfigureAwait(false);
                _throttle.ReportSuccess(DateTimeOffset.UtcNow);
                ActiveSource = "source.official";
                return snapshot;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (CodexApiException exception)
            {
                apiFailure = exception;
                if (exception.Failure == CodexApiFailure.Throttled)
                {
                    _throttle.ReportThrottled(DateTimeOffset.UtcNow, exception.RetryAfter);
                }
                else
                {
                    _throttle.ReportFailure(DateTimeOffset.UtcNow);
                }

                DiagnosticsLog.Write(
                    "CodexApiClient",
                    $"{exception.Failure}: {exception.Message}");
            }
        }

        try
        {
            var snapshot = await _appServerClient.RefreshAsync(cancellationToken).ConfigureAwait(false);
            ActiveSource = "source.appServer";
            return snapshot with
            {
                Detail = snapshot.Detail ?? new CodexAccountDetail(
                    Email: null,
                    PlanType: snapshot.RateLimits.PlanType,
                    RateLimitAllowed: null,
                    LimitReached: null,
                    LimitTitle: null,
                    LimitDescription: null,
                    ModelLimits: [],
                    Credits: null,
                    Profile: null,
                    SpendLimitReached: null,
                    Source: "source.appServer"),
            };
        }
        catch (Exception exception) when (apiFailure is not null && exception is not OperationCanceledException)
        {
            // Report the API problem: it is the primary route and its message is the actionable one.
            DiagnosticsLog.Write(
                "CodexUsageProvider",
                $"app-server fallback also failed: {exception.GetType().Name}: {exception.Message}");
            throw apiFailure;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _apiClient?.Dispose();
        await _appServerClient.DisposeAsync().ConfigureAwait(false);
    }
}
