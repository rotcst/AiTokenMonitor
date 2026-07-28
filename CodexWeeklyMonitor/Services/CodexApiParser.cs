using System.Globalization;
using System.Text.Json;
using CodexWeeklyMonitor.Models;

namespace CodexWeeklyMonitor.Services;

/// <summary>
/// Parses the ChatGPT backend payloads (<c>wham/usage</c>, <c>wham/profiles/me</c>,
/// <c>wham/rate-limit-reset-credits</c>). These use snake_case and a different window shape than
/// the app-server protocol that <see cref="QuotaParser"/> handles.
/// </summary>
internal static class CodexApiParser
{
    private const long FiveHourSeconds = 5 * 60 * 60;
    private const long FiveHourLowerBoundSeconds = 4 * 60 * 60;
    private const long FiveHourUpperBoundSeconds = 6 * 60 * 60;
    private const long WeekSeconds = 7 * 24 * 60 * 60;
    private const long WeeklyLowerBoundSeconds = 6 * 24 * 60 * 60;
    private const long WeeklyUpperBoundSeconds = 8 * 24 * 60 * 60;

    public static (AccountRateLimits Limits, CodexAccountDetail Detail) ParseUsage(
        JsonElement root,
        DateTimeOffset fetchedAt,
        long? availableResetCount)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Codex 用量接口返回了非预期的数据格式。");
        }

        var rateLimit = TryGetObject(root, "rate_limit");
        var windows = rateLimit is { } limit ? ReadWindows(limit) : [];
        var fiveHour = SelectWindow(windows, FiveHourLowerBoundSeconds, FiveHourUpperBoundSeconds, FiveHourSeconds);
        var weekly = SelectWindow(windows, WeeklyLowerBoundSeconds, WeeklyUpperBoundSeconds, WeekSeconds);
        if (fiveHour is null && weekly is null)
        {
            throw new InvalidDataException("Codex 用量接口没有返回可识别的额度窗口。");
        }

        var credits = ParseCredits(root);
        var limits = new AccountRateLimits(
            FiveHour: fiveHour,
            Weekly: weekly,
            Credits: credits is null
                ? null
                : new CreditBalance(credits.Balance, credits.HasCredits, credits.Unlimited),
            AvailableResetCount: availableResetCount,
            PlanType: TryGetString(root, "plan_type"),
            FetchedAt: fetchedAt);

        var upsell = TryGetObject(root, "rate_limit_upsell");
        var detail = new CodexAccountDetail(
            Email: TryGetString(root, "email"),
            PlanType: TryGetString(root, "plan_type"),
            RateLimitAllowed: rateLimit is { } r1 ? TryGetBoolean(r1, "allowed") : null,
            LimitReached: rateLimit is { } r2 ? TryGetBoolean(r2, "limit_reached") : null,
            LimitTitle: upsell is { } u1 ? TryGetString(u1, "title") : null,
            LimitDescription: upsell is { } u2 ? FormatUpsellDescription(u2) : null,
            ModelLimits: ParseModelLimits(root),
            Credits: credits,
            Profile: null,
            SpendLimitReached: TryGetObject(root, "spend_control") is { } spend
                ? TryGetBoolean(spend, "reached")
                : null,
            Source: "source.official");

        return (limits, detail);
    }

    /// <summary>Substitutes the <c>{time}</c> placeholder the server leaves in the upsell copy.</summary>
    private static string? FormatUpsellDescription(JsonElement upsell)
    {
        var description = TryGetString(upsell, "description");
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        if (FromUnixSeconds(TryGetInt64(upsell, "reset_at")) is { } resetAt)
        {
            description = description.Replace(
                "{time}",
                Loc.MonthDayTime(resetAt),
                StringComparison.Ordinal);
        }

        return description;
    }

    private static IReadOnlyList<CodexModelLimit> ParseModelLimits(JsonElement root)
    {
        if (!root.TryGetProperty("additional_rate_limits", out var additional) ||
            additional.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<CodexModelLimit>();
        foreach (var entry in additional.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object ||
                TryGetObject(entry, "rate_limit") is not { } rateLimit)
            {
                continue;
            }

            var windows = ReadWindows(rateLimit);
            if (windows.Count == 0)
            {
                continue;
            }

            var name = TryGetString(entry, "limit_name") ??
                       TryGetString(entry, "metered_feature") ??
                       "附加额度";
            results.Add(new CodexModelLimit(
                name,
                windows[0],
                TryGetBoolean(rateLimit, "limit_reached") ?? false));
        }

        return results;
    }

    private static CodexCreditDetail? ParseCredits(JsonElement root)
    {
        if (TryGetObject(root, "credits") is not { } credits)
        {
            return null;
        }

        return new CodexCreditDetail(
            Balance: TryGetString(credits, "balance") ??
                     TryGetDouble(credits, "balance")?.ToString("0.##", CultureInfo.InvariantCulture),
            HasCredits: TryGetBoolean(credits, "has_credits") ?? false,
            Unlimited: TryGetBoolean(credits, "unlimited") ?? false,
            OverageLimitReached: TryGetBoolean(credits, "overage_limit_reached") ?? false,
            ApproxLocalMessages: ReadInt64Array(credits, "approx_local_messages"),
            ApproxCloudMessages: ReadInt64Array(credits, "approx_cloud_messages"));
    }

    public static (AccountTokenUsage Usage, CodexProfileStats Stats) ParseProfile(
        JsonElement root,
        DateTimeOffset fetchedAt)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            TryGetObject(root, "stats") is not { } stats)
        {
            throw new InvalidDataException("Codex 账号统计接口返回了非预期的数据格式。");
        }

        var daily = new List<DailyTokenUsage>();
        if (stats.TryGetProperty("daily_usage_buckets", out var buckets) &&
            buckets.ValueKind == JsonValueKind.Array)
        {
            var byDate = new Dictionary<DateOnly, long>();
            foreach (var bucket in buckets.EnumerateArray())
            {
                if (bucket.ValueKind != JsonValueKind.Object ||
                    TryGetString(bucket, "start_date") is not { } startDate ||
                    !DateOnly.TryParse(startDate, CultureInfo.InvariantCulture, out var date))
                {
                    continue;
                }

                var tokens = Math.Max(0, TryGetInt64(bucket, "tokens") ?? 0);
                byDate[date] = byDate.GetValueOrDefault(date) + tokens;
            }

            daily.AddRange(byDate
                .OrderBy(pair => pair.Key)
                .Select(pair => new DailyTokenUsage(pair.Key, pair.Value)));
        }

        var usage = new AccountTokenUsage(
            LifetimeTokens: TryGetInt64(stats, "lifetime_tokens"),
            PeakDailyTokens: TryGetInt64(stats, "peak_daily_tokens"),
            LongestRunningTurnSeconds: TryGetInt64(stats, "longest_running_turn_sec"),
            CurrentStreakDays: TryGetInt64(stats, "current_streak_days"),
            LongestStreakDays: TryGetInt64(stats, "longest_streak_days"),
            DailyUsage: daily,
            FetchedAt: fetchedAt);

        var profile = TryGetObject(root, "profile");
        var profileStats = new CodexProfileStats(
            Username: profile is { } p1 ? TryGetString(p1, "username") : null,
            DisplayName: profile is { } p2 ? TryGetString(p2, "display_name") : null,
            TotalThreads: TryGetInt64(stats, "total_threads"),
            FastModeUsagePercent: TryGetDouble(stats, "fast_mode_usage_percentage"),
            TotalSkillsUsed: TryGetInt64(stats, "total_skills_used"),
            UniqueSkillsUsed: TryGetInt64(stats, "unique_skills_used"),
            MostUsedReasoningEffort: TryGetString(stats, "most_used_reasoning_effort"),
            MostUsedReasoningEffortPercent: TryGetDouble(stats, "most_used_reasoning_effort_percentage"));

        return (usage, profileStats);
    }

    public static long? ParseResetCredits(JsonElement root)
    {
        return root.ValueKind == JsonValueKind.Object
            ? TryGetInt64(root, "available_count") is { } count ? Math.Max(0, count) : null
            : null;
    }

    private static List<RateLimitWindow> ReadWindows(JsonElement rateLimit)
    {
        var windows = new List<RateLimitWindow>(capacity: 2);
        foreach (var name in new[] { "primary_window", "secondary_window" })
        {
            if (TryGetObject(rateLimit, name) is not { } window ||
                TryGetDouble(window, "used_percent") is not { } usedPercent)
            {
                continue;
            }

            var durationSeconds = TryGetInt64(window, "limit_window_seconds");
            windows.Add(new RateLimitWindow(
                UsedPercent: Math.Clamp((int)Math.Round(usedPercent, MidpointRounding.AwayFromZero), 0, 100),
                ResetsAt: ResolveResetAt(window),
                DurationMinutes: durationSeconds is { } seconds ? seconds / 60 : null));
        }

        return windows;
    }

    /// <summary>
    /// Prefers the absolute <c>reset_at</c>; falls back to <c>reset_after_seconds</c> so a window
    /// still shows a reset time on responses that omit the timestamp.
    /// </summary>
    private static DateTimeOffset? ResolveResetAt(JsonElement window)
    {
        if (FromUnixSeconds(TryGetInt64(window, "reset_at")) is { } resetAt)
        {
            return resetAt;
        }

        return TryGetInt64(window, "reset_after_seconds") is { } after and >= 0
            ? DateTimeOffset.UtcNow.AddSeconds(after)
            : null;
    }

    private static RateLimitWindow? SelectWindow(
        IReadOnlyList<RateLimitWindow> windows,
        long lowerBoundSeconds,
        long upperBoundSeconds,
        long targetSeconds)
    {
        return windows
            .Where(window => window.DurationMinutes is { } minutes &&
                             minutes * 60 >= lowerBoundSeconds &&
                             minutes * 60 <= upperBoundSeconds)
            .OrderBy(window => Math.Abs((window.DurationMinutes!.Value * 60) - targetSeconds))
            .FirstOrDefault();
    }

    private static IReadOnlyList<long> ReadInt64Array(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var values = new List<long>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var value))
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static JsonElement? TryGetObject(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.Object
            ? value
            : null;
    }

    private static string? TryGetString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool? TryGetBoolean(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static long? TryGetInt64(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetInt64(out var number)
            ? number
            : null;
    }

    private static double? TryGetDouble(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetDouble(out var number)
            ? number
            : null;
    }

    private static DateTimeOffset? FromUnixSeconds(long? value)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(value.Value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
