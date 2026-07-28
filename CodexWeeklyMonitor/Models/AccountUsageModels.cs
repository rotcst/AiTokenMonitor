namespace CodexWeeklyMonitor.Models;

public sealed record RateLimitWindow(
    int UsedPercent,
    DateTimeOffset? ResetsAt,
    long? DurationMinutes)
{
    public int RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);
}

public sealed record CreditBalance(
    string? Balance,
    bool HasCredits,
    bool Unlimited);

public sealed record AccountRateLimits(
    RateLimitWindow? FiveHour,
    RateLimitWindow? Weekly,
    CreditBalance? Credits,
    long? AvailableResetCount,
    string? PlanType,
    DateTimeOffset FetchedAt);

public sealed record DailyTokenUsage(DateOnly Date, long Tokens)
{
    public string DisplayDate => Date.ToString("yyyy-MM-dd");
    public string DisplayTokens => TokenFormatter.Format(Tokens);
}

public sealed record AccountTokenUsage(
    long? LifetimeTokens,
    long? PeakDailyTokens,
    long? LongestRunningTurnSeconds,
    long? CurrentStreakDays,
    long? LongestStreakDays,
    IReadOnlyList<DailyTokenUsage> DailyUsage,
    DateTimeOffset FetchedAt)
{
    public DailyTokenUsage? LatestDay => DailyUsage.Count == 0
        ? null
        : DailyUsage[^1];
}

/// <summary>A per-model bucket from <c>additional_rate_limits</c> (e.g. "GPT-5.3-Codex-Spark").</summary>
public sealed record CodexModelLimit(
    string Name,
    RateLimitWindow Window,
    bool LimitReached);

/// <summary>
/// Credit details. <c>Approx*Messages</c> is the server's [low, high] estimate of how many
/// messages the remaining balance buys.
/// </summary>
public sealed record CodexCreditDetail(
    string? Balance,
    bool HasCredits,
    bool Unlimited,
    bool OverageLimitReached,
    IReadOnlyList<long> ApproxLocalMessages,
    IReadOnlyList<long> ApproxCloudMessages);

public sealed record CodexProfileStats(
    string? Username,
    string? DisplayName,
    long? TotalThreads,
    double? FastModeUsagePercent,
    long? TotalSkillsUsed,
    long? UniqueSkillsUsed,
    string? MostUsedReasoningEffort,
    double? MostUsedReasoningEffortPercent);

/// <summary>
/// Everything the account endpoints expose beyond the two headline windows.
/// <paramref name="Source"/> records whether it came from the direct API or the local app-server.
/// </summary>
public sealed record CodexAccountDetail(
    string? Email,
    string? PlanType,
    bool? RateLimitAllowed,
    bool? LimitReached,
    string? LimitTitle,
    string? LimitDescription,
    IReadOnlyList<CodexModelLimit> ModelLimits,
    CodexCreditDetail? Credits,
    CodexProfileStats? Profile,
    bool? SpendLimitReached,
    string Source);

public sealed record CodexUsageSnapshot(
    AccountRateLimits RateLimits,
    AccountTokenUsage? TokenUsage,
    string? TokenUsageError,
    DateTimeOffset FetchedAt,
    CodexAccountDetail? Detail = null);

public static class TokenFormatter
{
    /// <summary>
    /// Compact token count. CJK languages group by myriads (万/억 = 10^4, 亿 = 10^8); English uses
    /// the K/M/B scale instead.
    /// </summary>
    public static string Format(long? value)
    {
        if (value is null)
        {
            return "--";
        }

        var absolute = Math.Abs((double)value.Value);
        return Services.Loc.Current switch
        {
            Services.AppLanguage.English => absolute switch
            {
                >= 1_000_000_000 => $"{value.Value / 1_000_000_000d:0.#}B",
                >= 1_000_000 => $"{value.Value / 1_000_000d:0.#}M",
                >= 1_000 => $"{value.Value / 1_000d:0.#}K",
                _ => value.Value.ToString("N0"),
            },
            Services.AppLanguage.Korean => absolute switch
            {
                >= 100_000_000 => $"{Math.Round(value.Value / 100_000_000d, MidpointRounding.AwayFromZero):0}억",
                >= 10_000 => $"{Math.Round(value.Value / 10_000d, MidpointRounding.AwayFromZero):0}만",
                _ => value.Value.ToString("N0"),
            },
            _ => absolute switch
            {
                >= 100_000_000 => $"{Math.Round(value.Value / 100_000_000d, MidpointRounding.AwayFromZero):0}亿",
                >= 10_000 => $"{Math.Round(value.Value / 10_000d, MidpointRounding.AwayFromZero):0}万",
                _ => value.Value.ToString("N0"),
            },
        };
    }
}
