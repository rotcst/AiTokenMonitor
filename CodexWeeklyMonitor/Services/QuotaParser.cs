using System.Text.Json;
using CodexWeeklyMonitor.Models;

namespace CodexWeeklyMonitor.Services;

public static class QuotaParser
{
    private const long FiveHourMinutes = 5 * 60;
    private const long FiveHourLowerBoundMinutes = 4 * 60;
    private const long FiveHourUpperBoundMinutes = 6 * 60;
    private const long WeekMinutes = 7 * 24 * 60;
    private const long WeeklyLowerBoundMinutes = 6 * 24 * 60;
    private const long WeeklyUpperBoundMinutes = 8 * 24 * 60;

    public static WeeklyQuota ParseRateLimitResult(
        JsonElement result,
        DateTimeOffset? fetchedAt = null)
    {
        var rateLimits = ParseAccountRateLimits(result, fetchedAt);
        if (rateLimits.Weekly is not { } weekly)
        {
            throw new InvalidDataException("当前账号响应中没有 7 天额度窗口。");
        }

        return new WeeklyQuota(
            UsedPercent: weekly.UsedPercent,
            ResetsAt: weekly.ResetsAt,
            WindowDurationMinutes: weekly.DurationMinutes,
            PlanType: rateLimits.PlanType,
            LimitName: null,
            FetchedAt: rateLimits.FetchedAt);
    }

    public static AccountRateLimits ParseAccountRateLimits(
        JsonElement result,
        DateTimeOffset? fetchedAt = null)
    {
        if (result.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Codex 返回的额度数据格式无效。");
        }

        JsonElement snapshot;
        if (result.TryGetProperty("rateLimitsByLimitId", out var buckets) &&
            buckets.ValueKind == JsonValueKind.Object &&
            buckets.TryGetProperty("codex", out var codexBucket) &&
            codexBucket.ValueKind == JsonValueKind.Object)
        {
            snapshot = codexBucket;
        }
        else if (result.TryGetProperty("rateLimits", out var legacySnapshot) &&
                 legacySnapshot.ValueKind == JsonValueKind.Object)
        {
            snapshot = legacySnapshot;
        }
        else
        {
            throw new InvalidDataException("Codex 没有返回可用的主额度桶。");
        }

        var windows = ReadWindows(snapshot);
        var fiveHour = SelectWindow(
            windows,
            FiveHourLowerBoundMinutes,
            FiveHourUpperBoundMinutes,
            FiveHourMinutes);
        var weekly = SelectWindow(
            windows,
            WeeklyLowerBoundMinutes,
            WeeklyUpperBoundMinutes,
            WeekMinutes);

        if (fiveHour is null && weekly is null)
        {
            throw new InvalidDataException("Codex 没有返回可识别的 5 小时或 7 天额度窗口。");
        }

        CreditBalance? credits = null;
        if (snapshot.TryGetProperty("credits", out var creditsElement) &&
            creditsElement.ValueKind == JsonValueKind.Object)
        {
            credits = new CreditBalance(
                Balance: GetOptionalString(creditsElement, "balance"),
                HasCredits: GetOptionalBoolean(creditsElement, "hasCredits") ?? false,
                Unlimited: GetOptionalBoolean(creditsElement, "unlimited") ?? false);
        }

        long? availableResetCount = null;
        if (result.TryGetProperty("rateLimitResetCredits", out var resetCredits) &&
            resetCredits.ValueKind == JsonValueKind.Object &&
            resetCredits.TryGetProperty("availableCount", out var availableCountElement) &&
            availableCountElement.TryGetInt64(out var availableCount))
        {
            availableResetCount = Math.Max(0, availableCount);
        }

        return new AccountRateLimits(
            FiveHour: ToRateLimitWindow(fiveHour),
            Weekly: ToRateLimitWindow(weekly),
            Credits: credits,
            AvailableResetCount: availableResetCount,
            PlanType: GetOptionalString(snapshot, "planType"),
            FetchedAt: fetchedAt ?? DateTimeOffset.Now);
    }

    public static WeeklyQuota ParseSnapshot(
        JsonElement snapshot,
        DateTimeOffset? fetchedAt = null)
    {
        if (snapshot.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Codex 额度快照格式无效。");
        }

        var windows = ReadWindows(snapshot);

        var weeklyWindow = windows
            .Where(window => window.DurationMinutes is >= WeeklyLowerBoundMinutes and <= WeeklyUpperBoundMinutes)
            .OrderBy(window => Math.Abs(window.DurationMinutes!.Value - WeekMinutes))
            .FirstOrDefault();

        if (weeklyWindow is null)
        {
            // Older protocol versions can omit duration while still exposing the weekly
            // window as secondary. Do not ever relabel a known short window as weekly.
            weeklyWindow = windows.FirstOrDefault(window =>
                window.Name == "secondary" && window.DurationMinutes is null);
        }

        if (weeklyWindow is null && windows.Count == 1 && windows[0].DurationMinutes is null)
        {
            weeklyWindow = windows[0];
        }

        if (weeklyWindow is null)
        {
            throw new InvalidDataException("当前账号响应中没有 7 天额度窗口。");
        }

        return new WeeklyQuota(
            UsedPercent: Math.Clamp(weeklyWindow.UsedPercent, 0, 100),
            ResetsAt: FromUnixSeconds(weeklyWindow.ResetsAtUnixSeconds),
            WindowDurationMinutes: weeklyWindow.DurationMinutes,
            PlanType: GetOptionalString(snapshot, "planType"),
            LimitName: GetOptionalString(snapshot, "limitName"),
            FetchedAt: fetchedAt ?? DateTimeOffset.Now);
    }

    private static List<WindowCandidate> ReadWindows(JsonElement snapshot)
    {
        var windows = new List<WindowCandidate>(capacity: 2);
        AddWindow(snapshot, "primary", windows);
        AddWindow(snapshot, "secondary", windows);
        return windows;
    }

    private static WindowCandidate? SelectWindow(
        IEnumerable<WindowCandidate> windows,
        long lowerBound,
        long upperBound,
        long targetDuration)
    {
        return windows
            .Where(window => window.DurationMinutes is not null &&
                             window.DurationMinutes.Value >= lowerBound &&
                             window.DurationMinutes.Value <= upperBound)
            .OrderBy(window => Math.Abs(window.DurationMinutes!.Value - targetDuration))
            .FirstOrDefault();
    }

    private static RateLimitWindow? ToRateLimitWindow(WindowCandidate? candidate)
    {
        return candidate is null
            ? null
            : new RateLimitWindow(
                UsedPercent: Math.Clamp(candidate.UsedPercent, 0, 100),
                ResetsAt: FromUnixSeconds(candidate.ResetsAtUnixSeconds),
                DurationMinutes: candidate.DurationMinutes);
    }

    private static void AddWindow(
        JsonElement snapshot,
        string propertyName,
        ICollection<WindowCandidate> destination)
    {
        if (!snapshot.TryGetProperty(propertyName, out var window) ||
            window.ValueKind != JsonValueKind.Object ||
            !window.TryGetProperty("usedPercent", out var usedElement) ||
            !usedElement.TryGetInt32(out var usedPercent))
        {
            return;
        }

        long? duration = null;
        if (window.TryGetProperty("windowDurationMins", out var durationElement) &&
            durationElement.ValueKind == JsonValueKind.Number &&
            durationElement.TryGetInt64(out var durationValue))
        {
            duration = durationValue;
        }

        long? resetsAt = null;
        if (window.TryGetProperty("resetsAt", out var resetElement) &&
            resetElement.ValueKind == JsonValueKind.Number &&
            resetElement.TryGetInt64(out var resetValue))
        {
            resetsAt = resetValue;
        }

        destination.Add(new WindowCandidate(propertyName, usedPercent, duration, resetsAt));
    }

    private static string? GetOptionalString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool? GetOptionalBoolean(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
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

    private static DateTimeOffset? FromUnixSeconds(long? value)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(value.Value).ToLocalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private sealed record WindowCandidate(
        string Name,
        int UsedPercent,
        long? DurationMinutes,
        long? ResetsAtUnixSeconds);
}
