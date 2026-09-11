using System.Globalization;
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

        var scoped = ParseScopedLimits(rateLimits);
        var extraUsage = ParseExtraUsage(rateLimits) ?? ParseExtraUsage(root);

        return new ClaudeStatusUsage(
            fiveHour,
            weekly,
            modelName,
            contextUsedPercent,
            observedAt,
            scoped,
            extraUsage);
    }

    private static IReadOnlyList<ClaudeScopedLimit> ParseScopedLimits(JsonElement? rateLimits)
    {
        if (rateLimits is not { ValueKind: JsonValueKind.Object } limits)
        {
            return [];
        }

        var result = new List<ClaudeScopedLimit>();
        foreach (var (key, modelName, billing, displayName) in new[]
                 {
                     ("seven_day_overage_included", "Fable 5.1", ClaudeLimitBilling.UsageCredits, "Fable 5.1 点数额度"),
                     ("seven_day_opus", "Opus", ClaudeLimitBilling.Subscription, "Opus 周额度"),
                     ("seven_day_sonnet", "Sonnet", ClaudeLimitBilling.Subscription, "Sonnet 周额度"),
                     ("seven_day_cowork", "Cowork", ClaudeLimitBilling.Subscription, "Cowork 周额度"),
                     ("seven_day_oauth_apps", "第三方应用", ClaudeLimitBilling.Subscription, "第三方应用周额度"),
                 })
        {
            if (ParseWindow(limits, key, durationMinutes: 10_080) is { } window)
            {
                result.Add(new ClaudeScopedLimit(
                    key,
                    displayName,
                    window.UsedPercent,
                    window.ResetsAt,
                    billing,
                    modelName));
            }
        }

        if (TryGetArray(limits, "model_scoped") is { } modelScoped)
        {
            foreach (var entry in modelScoped.EnumerateArray())
            {
                if (TryParseModelScoped(entry) is not { } parsed ||
                    result.Any(existing => string.Equals(
                        existing.IsFable ? "fable" : existing.ModelName ?? existing.Key,
                        parsed.IsFable ? "fable" : parsed.ModelName ?? parsed.Key,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                result.Add(parsed);
            }
        }

        return result;
    }

    private static ClaudeScopedLimit? TryParseModelScoped(JsonElement entry)
    {
        var utilization = TryGetDouble(entry, "utilization") ?? TryGetDouble(entry, "used_percentage");
        if (entry.ValueKind != JsonValueKind.Object ||
            TryGetModelName(entry) is not { } modelName ||
            utilization is null)
        {
            return null;
        }

        var isFable = modelName.Contains("fable", StringComparison.OrdinalIgnoreCase);
        var billing = isFable ? ClaudeLimitBilling.UsageCredits : ClaudeLimitBilling.Subscription;
        return new ClaudeScopedLimit(
            $"model_scoped:{modelName}",
            isFable ? "Fable 5.1 点数额度" : $"{modelName} 周额度",
            ToPercent(utilization.Value),
            TryGetTimestampAny(entry, "resets_at", "reset_at"),
            billing,
            modelName);
    }

    private static ClaudeExtraUsage? ParseExtraUsage(JsonElement? rateLimits)
    {
        if (rateLimits is not { ValueKind: JsonValueKind.Object } limits ||
            limits.TryGetProperty("extra_usage", out var extra) is false ||
            extra.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var decimalPlaces = TryGetDouble(extra, "decimal_places") is { } places
            ? (int)Math.Clamp(places, 0, 6)
            : 2;
        var scale = (decimal)Math.Pow(10, decimalPlaces);

        return new ClaudeExtraUsage(
            IsEnabled: TryGetBoolean(extra, "is_enabled") ?? false,
            UsedAmount: TryGetDouble(extra, "used_credits") is { } used ? (decimal)used / scale : null,
            LimitAmount: TryGetDouble(extra, "monthly_limit") is { } limit ? (decimal)limit / scale : null,
            UsedPercent: TryGetDouble(extra, "utilization") is { } utilization
                ? ToPercent(utilization)
                : null,
            Currency: TryGetString(extra, "currency") ?? "USD",
            DisabledReason: TryGetString(extra, "disabled_reason"));
    }

    private static RateLimitWindow? ParseWindow(
        JsonElement rateLimits,
        string propertyName,
        long durationMinutes)
    {
        if (TryGetObject(rateLimits, propertyName) is not { } window)
        {
            return null;
        }

        var usedPercentage = TryGetDouble(window, "used_percentage") ??
                             TryGetDouble(window, "utilization") ??
                             TryGetDouble(window, "percent");
        if (usedPercentage is null)
        {
            return null;
        }

        return new RateLimitWindow(
            Math.Clamp((int)Math.Round(usedPercentage.Value, MidpointRounding.AwayFromZero), 0, 100),
            TryGetTimestampAny(window, "resets_at", "reset_at"),
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

    private static JsonElement? TryGetArray(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.Array
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
               double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            ? number
            : null;
    }

    private static DateTimeOffset? TryGetDateTimeOffset(JsonElement element, string name)
    {
        var value = TryGetString(element, name);
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }

    private static string? TryGetModelName(JsonElement element)
    {
        if (TryGetString(element, "display_name") is { } direct)
        {
            return direct;
        }

        if (TryGetObject(element, "model") is { } model)
        {
            return TryGetString(model, "display_name") ??
                   TryGetString(model, "name") ??
                   TryGetString(model, "id");
        }

        return TryGetString(element, "model") ?? TryGetString(element, "name");
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

    private static DateTimeOffset? TryGetTimestamp(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(
                value.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return parsed;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var seconds))
        {
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds);
            }
            catch (ArgumentOutOfRangeException)
            {
                // Keep the percentage when a future bridge returns an invalid reset value.
            }
        }

        return null;
    }

    private static DateTimeOffset? TryGetTimestampAny(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetTimestamp(element, name) is { } timestamp)
            {
                return timestamp;
            }
        }

        return null;
    }

    private static int ToPercent(double value) =>
        Math.Clamp((int)Math.Round(value, MidpointRounding.AwayFromZero), 0, 100);
}
