namespace CodexWeeklyMonitor.Models;

/// <summary>
/// Supplementary data that only the Claude Code status line exposes (model name, context window).
/// Quota numbers now come from <see cref="ClaudeAccountUsage"/> instead.
/// </summary>
public sealed record ClaudeStatusUsage(
    RateLimitWindow? FiveHour,
    RateLimitWindow? Weekly,
    string? ModelName,
    int? ContextUsedPercent,
    DateTimeOffset ObservedAt);

/// <summary>
/// A model or surface scoped limit reported by /api/oauth/usage (Opus weekly, Sonnet weekly, ...).
/// </summary>
public sealed record ClaudeScopedLimit(
    string Key,
    string DisplayName,
    int UsedPercent,
    DateTimeOffset? ResetsAt)
{
    public int RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);
}

/// <summary>
/// The "usage credits" pool that keeps working after the plan limits are exhausted.
/// Amounts are already converted from minor units to whole currency units.
/// </summary>
public sealed record ClaudeExtraUsage(
    bool IsEnabled,
    decimal? UsedAmount,
    decimal? LimitAmount,
    int? UsedPercent,
    string Currency,
    string? DisabledReason);

/// <summary>
/// The prepaid credit wallet shown on the billing page ("现在余额 / 自动充值"), which is a
/// different number from <see cref="ClaudeExtraUsage"/>: that one tracks this period's spend
/// against the cap, this one is the money still sitting in the account.
/// </summary>
public sealed record ClaudeCreditWallet(
    decimal? Balance,
    string Currency,
    bool AutoReloadEnabled,
    decimal? AutoReloadThreshold,
    decimal? AutoReloadAmount,
    bool CanPurchase);

/// <summary>
/// Authoritative account quota, mirroring what <c>/usage</c> shows inside Claude Code.
/// </summary>
public sealed record ClaudeAccountUsage(
    RateLimitWindow? FiveHour,
    RateLimitWindow? Weekly,
    IReadOnlyList<ClaudeScopedLimit> ScopedLimits,
    ClaudeExtraUsage? ExtraUsage,
    string? SubscriptionType,
    DateTimeOffset FetchedAt,
    ClaudeCreditWallet? Wallet = null);

public sealed record ClaudeUsageSnapshot(
    ClaudeAccountUsage? Account,
    string? AccountError,
    ClaudeStatusUsage? Status,
    AccountTokenUsage? TokenUsage,
    bool IsClaudeAvailable,
    bool IsBridgeConfigured,
    DateTimeOffset FetchedAt,
    TimeSpan ThrottledFor = default,
    ClaudeSessionState? Session = null)
{
    /// <summary>True while a rate-limit penalty is holding refreshes back.</summary>
    public bool IsThrottled => ThrottledFor > TimeSpan.Zero;

    /// <summary>Prefers the transcript, which works for the desktop app too.</summary>
    public string? ModelName => Session?.DisplayModelName ?? Status?.ModelName;

    public int? ContextUsedPercent => Session?.ContextUsedPercent ?? Status?.ContextUsedPercent;

    /// <summary>Quota is only trustworthy when it comes from the official usage endpoint.</summary>
    public RateLimitWindow? FiveHour => Account?.FiveHour;

    public RateLimitWindow? Weekly => Account?.Weekly;
}

/// <summary>
/// The live session, read from the local transcript rather than the status line: the desktop app
/// renders its own status bar and never invokes a user-configured <c>statusLine</c> command.
/// </summary>
public sealed record ClaudeSessionState(
    string? ModelName,
    long ContextTokens,
    string? Effort,
    string? Version,
    DateTimeOffset ObservedAt)
{
    /// <summary>
    /// Claude Code does not record the window size, so infer it from what the session has actually
    /// held. Anything past the 200K tier must be running on the 1M one.
    /// </summary>
    public long ContextWindow => ContextTokens > 200_000 ? 1_000_000 : 200_000;

    public int ContextUsedPercent =>
        (int)Math.Clamp(Math.Round(ContextTokens * 100d / ContextWindow), 0, 100);

    /// <summary>Trims the API id down to what the UI shows ("claude-opus-5" -> "Opus 5").</summary>
    public string? DisplayModelName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ModelName))
            {
                return null;
            }

            var name = ModelName;
            if (name.StartsWith("claude-", StringComparison.OrdinalIgnoreCase))
            {
                name = name["claude-".Length..];
            }

            // Drop a trailing date stamp such as "-20251001".
            var parts = name.Split('-', StringSplitOptions.RemoveEmptyEntries)
                .Where(part => !(part.Length == 8 && part.All(char.IsDigit)))
                .Select(part => part.Length <= 1
                    ? part.ToUpperInvariant()
                    : char.ToUpperInvariant(part[0]) + part[1..]);
            return string.Join(' ', parts);
        }
    }
}

internal sealed record ClaudeTokenRecord(
    string Key,
    DateOnly Date,
    long Tokens,
    DateTimeOffset ObservedAt);
