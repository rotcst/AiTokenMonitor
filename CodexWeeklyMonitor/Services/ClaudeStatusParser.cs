using System.Text.Json;
using CodexWeeklyMonitor.Models;

namespace CodexWeeklyMonitor.Services;

internal static class ClaudeStatusParser
{
    public static ClaudeStatusUsage Parse(JsonElement root, DateTimeOffset fallbackObservedAt)
    {
        var observedAt = TryGetDateTimeOffset(root, "observed_at") ?? fallbackObservedAt;
        var rateLimits = TryGetObject(root, "rate_limits");
        var fiveHour = rateLimits is { } limits
            ? ParseWindow(limits, "five_hour", durationMinutes: 300)
            : null;
        var weekly = rateLimits is { } weeklyLimits
            ? ParseWindow(weeklyLimits, "seven_day", durationMinutes: 10_080)
            : null;

        string? modelName = null;
        if (TryGetObject(root, "model") is { } model)
        {
            modelName = TryGetString(model, "display_name") ?? TryGetString(model, "id");
        }

        int? contextUsedPercent = null;
        if (TryGetObject(root, "context_window") is { } context &&
            TryGetDouble(context, "used_percentage") is { } usedPercentage)
        {
            contextUsedPercent = Math.Clamp(
                (int)Math.Round(usedPercentage, MidpointRounding.AwayFromZero),
                0,
                100);
        }

        return new ClaudeStatusUsage(
            fiveHour,
            weekly,
            modelName,
            contextUsedPercent,
            observedAt);
    }

    private static RateLimitWindow? ParseWindow(
        JsonElement rateLimits,
        string propertyName,
        long durationMinutes)
    {
        if (TryGetObject(rateLimits, propertyName) is not { } window ||
            TryGetDouble(window, "used_percentage") is not { } usedPercentage)
        {
            return null;
        }

        DateTimeOffset? resetsAt = null;
        if (TryGetInt64(window, "resets_at") is { } resetSeconds)
        {
            try
            {
                resetsAt = DateTimeOffset.FromUnixTimeSeconds(resetSeconds);
            }
            catch (ArgumentOutOfRangeException)
            {
                // Keep the percentage even if a future Claude version returns an invalid reset value.
            }
        }

        return new RateLimitWindow(
            Math.Clamp((int)Math.Round(usedPercentage, MidpointRounding.AwayFromZero), 0, 100),
            resetsAt,
            durationMinutes);
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

    private static double? TryGetDouble(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String &&
               double.TryParse(value.GetString(), out number)
            ? number
            : null;
    }

    private static long? TryGetInt64(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String &&
               long.TryParse(value.GetString(), out number)
            ? number
            : null;
    }

    private static DateTimeOffset? TryGetDateTimeOffset(JsonElement element, string name)
    {
        var value = TryGetString(element, name);
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }
}
