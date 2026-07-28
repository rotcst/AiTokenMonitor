using System.Globalization;
using System.Text.Json;
using CodexWeeklyMonitor.Models;

namespace CodexWeeklyMonitor.Services;

public static class TokenUsageParser
{
    public static AccountTokenUsage Parse(
        JsonElement result,
        DateTimeOffset? fetchedAt = null)
    {
        if (result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("summary", out var summary) ||
            summary.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Codex 返回的 Token 用量格式无效。");
        }

        var dailyUsage = new List<DailyTokenUsage>();
        if (result.TryGetProperty("dailyUsageBuckets", out var buckets) &&
            buckets.ValueKind == JsonValueKind.Array)
        {
            foreach (var bucket in buckets.EnumerateArray())
            {
                if (bucket.ValueKind != JsonValueKind.Object ||
                    !bucket.TryGetProperty("startDate", out var dateElement) ||
                    dateElement.ValueKind != JsonValueKind.String ||
                    !bucket.TryGetProperty("tokens", out var tokensElement) ||
                    !tokensElement.TryGetInt64(out var tokens) ||
                    !DateOnly.TryParseExact(
                        dateElement.GetString(),
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out var date))
                {
                    continue;
                }

                dailyUsage.Add(new DailyTokenUsage(date, Math.Max(0, tokens)));
            }
        }

        var normalizedDailyUsage = dailyUsage
            .GroupBy(item => item.Date)
            .Select(group => new DailyTokenUsage(group.Key, group.Sum(item => item.Tokens)))
            .OrderBy(item => item.Date)
            .ToArray();

        return new AccountTokenUsage(
            LifetimeTokens: GetOptionalInt64(summary, "lifetimeTokens"),
            PeakDailyTokens: GetOptionalInt64(summary, "peakDailyTokens"),
            LongestRunningTurnSeconds: GetOptionalInt64(summary, "longestRunningTurnSec"),
            CurrentStreakDays: GetOptionalInt64(summary, "currentStreakDays"),
            LongestStreakDays: GetOptionalInt64(summary, "longestStreakDays"),
            DailyUsage: normalizedDailyUsage,
            FetchedAt: fetchedAt ?? DateTimeOffset.Now);
    }

    private static long? GetOptionalInt64(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetInt64(out var parsed)
            ? parsed
            : null;
    }
}
