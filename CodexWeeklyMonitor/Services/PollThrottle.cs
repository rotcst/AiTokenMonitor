namespace CodexWeeklyMonitor.Services;

/// <summary>
/// Keeps a polling loop inside a provider's rate limit.
/// </summary>
/// <remarks>
/// Two separate things caused the 429 storm: the window's refresh timer and the monitor's own timer
/// both asked for the same data within milliseconds, and a rejection was retried at the normal
/// cadence instead of backing off. This tracks the two independently — routine spacing, which an
/// explicit user refresh may skip, and a rejection penalty, which nothing may skip.
/// </remarks>
internal sealed class PollThrottle(TimeSpan minimumInterval, TimeSpan maximumBackoff)
{
    private readonly object _gate = new();
    private DateTimeOffset _nextAllowedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _penaltyUntil = DateTimeOffset.MinValue;
    private TimeSpan _backoff = TimeSpan.Zero;

    public TimeSpan CurrentBackoff
    {
        get
        {
            lock (_gate)
            {
                return _backoff;
            }
        }
    }

    /// <summary>How long the current rejection penalty still has to run, if any.</summary>
    public TimeSpan RemainingPenalty(DateTimeOffset now)
    {
        lock (_gate)
        {
            return _penaltyUntil > now ? _penaltyUntil - now : TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Reserves the next call slot. <paramref name="force"/> is for an explicit user refresh: it
    /// skips the routine spacing but never the rejection penalty, because ignoring that is what
    /// keeps a rate limit alive.
    /// </summary>
    public bool TryAcquire(DateTimeOffset now, bool force = false)
    {
        lock (_gate)
        {
            if (now < _penaltyUntil)
            {
                return false;
            }

            if (!force && now < _nextAllowedAt)
            {
                return false;
            }

            // Hold the slot up front so two callers racing on the same tick cannot both pass.
            _nextAllowedAt = now + minimumInterval;
            return true;
        }
    }

    public void ReportSuccess(DateTimeOffset now)
    {
        lock (_gate)
        {
            _backoff = TimeSpan.Zero;
            _penaltyUntil = DateTimeOffset.MinValue;
            _nextAllowedAt = now + minimumInterval;
        }
    }

    /// <summary>
    /// Applies the server's own <c>Retry-After</c> when it sent one, otherwise doubles the penalty.
    /// </summary>
    public void ReportThrottled(DateTimeOffset now, TimeSpan? retryAfter)
    {
        lock (_gate)
        {
            var next = retryAfter is { } hint && hint > TimeSpan.Zero
                ? hint
                : _backoff == TimeSpan.Zero
                    ? minimumInterval
                    : _backoff + _backoff;
            _backoff = next > maximumBackoff ? maximumBackoff : next;
            _penaltyUntil = now + _backoff;
            _nextAllowedAt = _penaltyUntil;
        }
    }

    /// <summary>A transient failure waits one interval; it is not evidence of a rate limit.</summary>
    public void ReportFailure(DateTimeOffset now)
    {
        lock (_gate)
        {
            _nextAllowedAt = now + minimumInterval;
        }
    }
}
