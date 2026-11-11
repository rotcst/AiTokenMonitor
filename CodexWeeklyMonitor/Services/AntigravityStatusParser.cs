using System.Globalization;
using System.Text.Json;
using CodexWeeklyMonitor.Models;

namespace CodexWeeklyMonitor.Services;

internal static class AntigravityStatusParser
{
    public static AntigravityStatusUsage Parse(JsonElement root, DateTimeOffset fallbackObservedAt)
    {
        var observedAt = TryGetDateTimeOffset(root, "observed_at") ??
                         TryGetDateTimeOffset(root, "observedAt") ??
                         TryGetDateTimeOffset(root, "timestamp") ??
                         fallbackObservedAt;

        var rateLimits = TryGetObject(root, "rate_limits") ?? TryGetObject(root, "rateLimits");
        var fiveHour = rateLimits is { } limits
            ? ParseWindow(limits, "five_hour", 300) ?? ParseWindow(limits, "fiveHour", 300)
            : null;
        var weekly = rateLimits is { } weeklyLimits
            ? ParseWindow(weeklyLimits, "seven_day", 10_080) ??
              ParseWindow(weeklyLimits, "weekly", 10_080) ??
              ParseWindow(weeklyLimits, "sevenDay", 10_080)
            : null;

        var modelName = TryGetString(root, "modelName") ??
                        TryGetString(root, "model_name") ??
                        TryGetString(root, "model");
        if (string.IsNullOrWhiteSpace(modelName) && TryGetObject(root, "model") is { } modelObj)
        {
            modelName = TryGetString(modelObj, "display_name") ??
                        TryGetString(modelObj, "displayName") ??
                        TryGetString(modelObj, "name") ??
                        TryGetString(modelObj, "id");
        }

        var conversationId = TryGetString(root, "conversationId") ??
                             TryGetString(root, "conversation_id");
        var transcriptPath = TryGetString(root, "transcriptPath") ??
                             TryGetString(root, "transcript_path");
        var planType = TryGetString(root, "plan") ??
                       TryGetString(root, "planType") ??
                       TryGetString(root, "plan_type");

        int? contextUsedPercent = null;
        if (TryGetObject(root, "context_window") is { } context &&
            (TryGetDouble(context, "used_percentage") ?? TryGetDouble(context, "usedPercent")) is { } usedPct)
        {
            contextUsedPercent = ToPercent(usedPct);
        }
        else if ((TryGetDouble(root, "contextUsedPercent") ?? TryGetDouble(root, "context_used_percent")) is { } directPct)
        {
            contextUsedPercent = ToPercent(directPct);
        }

        var modelQuotas = rateLimits is { } limitsObj && ParseModelQuotas(limitsObj) is { Count: > 0 } nestedQuotas
            ? nestedQuotas
            : ParseModelQuotas(root);

        return new AntigravityStatusUsage(
            FiveHour: fiveHour,
            Weekly: weekly,
            ModelName: modelName,
            ConversationId: conversationId,
            ContextUsedPercent: contextUsedPercent,
            PlanType: planType,
            ObservedAt: observedAt,
            ModelQuotas: modelQuotas,
            TranscriptPath: transcriptPath);
    }

    private static IReadOnlyList<AntigravityModelQuota> ParseModelQuotas(JsonElement container)
    {
        if (container.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var result = new List<AntigravityModelQuota>();
        var array = TryGetArray(container, "model_scoped") ??
                    TryGetArray(container, "modelScoped") ??
                    TryGetArray(container, "models");
        if (array is not { } items)
        {
            return result;
        }

        foreach (var entry in items.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var modelName = TryGetModelName(entry);
            var utilization = TryGetDouble(entry, "used_percentage") ??
                              TryGetDouble(entry, "usedPercent") ??
                              TryGetDouble(entry, "utilization");
            if (string.IsNullOrWhiteSpace(modelName) || utilization is null)
            {
                continue;
            }

            var displayName = AntigravitySessionState.NormalizeModelDisplayName(modelName) ?? modelName;
            if (result.Any(existing => string.Equals(
                    existing.DisplayName,
                    displayName,
                    StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var fiveHour = TryGetObject(entry, "five_hour") is { } f5h
                ? ParseWindowDirect(f5h, 300)
                : (TryGetObject(entry, "fiveHour") is { } f5hC ? ParseWindowDirect(f5hC, 300) : null);

            var weekly = TryGetObject(entry, "weekly") is { } fWk
                ? ParseWindowDirect(fWk, 10_080)
                : (TryGetObject(entry, "seven_day") is { } f7d
                    ? ParseWindowDirect(f7d, 10_080)
                    : (TryGetObject(entry, "sevenDay") is { } f7dC ? ParseWindowDirect(f7dC, 10_080) : null));

            var resetsAt = TryGetTimestampAny(entry, "resets_at", "reset_at", "resetsAt");
            var used = ToPercent(utilization.Value);

            if (fiveHour is null && weekly is null)
            {
                if (resetsAt is { } rt && rt - DateTimeOffset.Now > TimeSpan.FromHours(12))
                {
                    weekly = new RateLimitWindow(used, resetsAt, 10_080);
                }
                else
                {
                    fiveHour = new RateLimitWindow(used, resetsAt, 300);
                }
            }

            result.Add(new AntigravityModelQuota(
                Key: $"model:{modelName}",
                ModelName: modelName,
                DisplayName: displayName,
                UsedPercent: used,
                ResetsAt: resetsAt ?? fiveHour?.ResetsAt ?? weekly?.ResetsAt,
                FiveHourWindow: fiveHour,
                WeeklyWindow: weekly));
        }

        return result;
    }

    private static RateLimitWindow? ParseWindowDirect(JsonElement window, long durationMinutes)
    {
        var usedPercentage = TryGetDouble(window, "used_percentage") ??
                             TryGetDouble(window, "usedPercent") ??
                             TryGetDouble(window, "utilization") ??
                             TryGetDouble(window, "percent");
        if (usedPercentage is null)
        {
            return null;
        }

        return new RateLimitWindow(
            ToPercent(usedPercentage.Value),
            TryGetTimestampAny(window, "resets_at", "reset_at", "resetsAt"),
            durationMinutes);
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
                             TryGetDouble(window, "usedPercent") ??
                             TryGetDouble(window, "utilization") ??
                             TryGetDouble(window, "percent");
        if (usedPercentage is null)
        {
            return null;
        }

        return new RateLimitWindow(
            ToPercent(usedPercentage.Value),
            TryGetTimestampAny(window, "resets_at", "reset_at", "resetsAt"),
            durationMinutes);
    }

    private static string? TryGetModelName(JsonElement element)
    {
        if (TryGetString(element, "display_name") is { } direct)
        {
            return direct;
        }

        if (TryGetString(element, "displayName") is { } directCamel)
        {
            return directCamel;
        }

        if (TryGetObject(element, "model") is { } model)
        {
            return TryGetString(model, "display_name") ??
                   TryGetString(model, "displayName") ??
                   TryGetString(model, "name") ??
                   TryGetString(model, "id");
        }

        return TryGetString(element, "model") ??
               TryGetString(element, "modelName") ??
               TryGetString(element, "name");
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
                return null;
            }
        }

        return null;
    }

    private static int ToPercent(double value) =>
        Math.Clamp((int)Math.Round(value, MidpointRounding.AwayFromZero), 0, 100);
}
