using CodexWeeklyMonitor.Models;

namespace CodexWeeklyMonitor.Services;

/// <summary>
/// Supplies Codex usage from the supported local <c>codex app-server</c>, enriching it with
/// account details from the ChatGPT HTTP endpoints when those endpoints are available.
/// </summary>
/// <remarks>
/// The app-server owns the headline windows because <c>account/rateLimits/read</c> is the supported
/// Codex contract and also emits live rate-limit notifications. The HTTP path still returns useful
/// supplementary data (per-model limits, credit estimates, profile statistics) and remains a
/// fallback when Codex is not installed or cannot start.
/// </remarks>
public sealed class CodexUsageProvider : IAsyncDisposable
{
    private readonly CodexApiClient? _apiClient;
    private readonly CodexRateLimitClient _appServerClient;
    private readonly PollThrottle _throttle = new(
        minimumInterval: TimeSpan.FromSeconds(20),
        maximumBackoff: TimeSpan.FromMinutes(15));

    private bool _disposed;

    public CodexUsageProvider()
        : this(new CodexApiClient(), new CodexRateLimitClient())
    {
    }

    internal CodexUsageProvider(
        CodexApiClient? apiClient,
        CodexRateLimitClient? appServerClient = null)
    {
        _apiClient = apiClient;
        _appServerClient = appServerClient ?? new CodexRateLimitClient();
        _appServerClient.RefreshSuggested += (sender, e) => RefreshSuggested?.Invoke(sender, e);
    }

    public event EventHandler? RefreshSuggested;

    /// <summary>Which route produced the last successful snapshot, for display and diagnostics.</summary>
    public string? ActiveSource { get; private set; }

    public async Task<CodexUsageSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Task<CodexUsageSnapshot>? apiTask = null;
        if (_apiClient is not null && _throttle.TryAcquire(DateTimeOffset.UtcNow))
        {
            apiTask = _apiClient.FetchAsync(cancellationToken);
        }

        var appServerTask = _appServerClient.RefreshAsync(cancellationToken);
        CodexUsageSnapshot? appServerSnapshot = null;
        Exception? appServerFailure = null;
        try
        {
            appServerSnapshot = await appServerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            appServerFailure = exception;
            DiagnosticsLog.Write(
                "CodexUsageProvider",
                $"app-server read failed: {exception.GetType().Name}: {exception.Message}");
        }

        CodexUsageSnapshot? apiSnapshot = null;
        CodexApiException? apiFailure = null;
        if (apiTask is not null)
        {
            try
            {
                apiSnapshot = await apiTask.ConfigureAwait(false);
                _throttle.ReportSuccess(DateTimeOffset.UtcNow);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (CodexApiException exception)
            {
                apiFailure = exception;
                ReportApiFailure(exception);
            }
        }

        if (appServerSnapshot is not null)
        {
            var snapshot = apiSnapshot is null
                ? EnsureAppServerDetail(appServerSnapshot)
                : MergeSnapshots(appServerSnapshot, apiSnapshot);
            ActiveSource = "source.appServer";
            LogSnapshot(apiSnapshot is null ? "app-server" : "app-server+official-details", snapshot);
            return snapshot;
        }

        if (apiSnapshot is not null)
        {
            ActiveSource = "source.official";
            LogSnapshot("official-fallback", apiSnapshot);
            return apiSnapshot;
        }

        if (appServerFailure is not null)
        {
            if (apiFailure is not null)
            {
                DiagnosticsLog.Write(
                    "CodexUsageProvider",
                    $"both quota sources failed; HTTP={apiFailure.Failure}, " +
                    $"app-server={appServerFailure.GetType().Name}");
            }

            throw appServerFailure;
        }

        if (apiFailure is not null)
        {
            throw apiFailure;
        }

        throw new InvalidOperationException("No Codex usage source was available.");
    }

    internal static CodexUsageSnapshot MergeSnapshots(
        CodexUsageSnapshot appServerSnapshot,
        CodexUsageSnapshot apiSnapshot)
    {
        var rateLimits = appServerSnapshot.RateLimits with
        {
            Credits = appServerSnapshot.RateLimits.Credits ?? apiSnapshot.RateLimits.Credits,
            AvailableResetCount = appServerSnapshot.RateLimits.AvailableResetCount ??
                                  apiSnapshot.RateLimits.AvailableResetCount,
            PlanType = appServerSnapshot.RateLimits.PlanType ?? apiSnapshot.RateLimits.PlanType,
        };
        var detail = apiSnapshot.Detail is { } apiDetail
            ? apiDetail with { Source = "source.appServerWithOfficialDetails" }
            : EnsureAppServerDetail(appServerSnapshot).Detail;
        var tokenUsage = apiSnapshot.TokenUsage ?? appServerSnapshot.TokenUsage;

        return appServerSnapshot with
        {
            RateLimits = rateLimits,
            TokenUsage = tokenUsage,
            TokenUsageError = tokenUsage is null
                ? apiSnapshot.TokenUsageError ?? appServerSnapshot.TokenUsageError
                : null,
            Detail = detail,
        };
    }

    private static CodexUsageSnapshot EnsureAppServerDetail(CodexUsageSnapshot snapshot)
    {
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

    private void ReportApiFailure(CodexApiException exception)
    {
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

    private static void LogSnapshot(string source, CodexUsageSnapshot snapshot)
    {
        DiagnosticsLog.Write(
            "CodexUsageProvider",
            $"snapshot source={source}, fetchedAt={snapshot.FetchedAt:O}, " +
            $"fiveHour=[{FormatWindow(snapshot.RateLimits.FiveHour)}], " +
            $"weekly=[{FormatWindow(snapshot.RateLimits.Weekly)}]");
    }

    private static string FormatWindow(RateLimitWindow? window)
    {
        return window is null
            ? "none"
            : $"usedPercent={window.UsedPercent}, " +
              $"durationMins={window.DurationMinutes?.ToString() ?? "unknown"}, " +
              $"resetsAt={window.ResetsAt?.ToString("O") ?? "unknown"}";
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
