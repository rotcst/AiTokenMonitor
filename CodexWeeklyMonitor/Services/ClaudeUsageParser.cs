using System.Globalization;
using System.Text.Json;
using CodexWeeklyMonitor.Models;

namespace CodexWeeklyMonitor.Services;

/// <summary>
/// Parses the payload of <c>GET https://api.anthropic.com/api/oauth/usage</c>, the same source
/// Claude Code's <c>/usage</c> command reads. Percentages arrive on a 0-100 scale.
/// </summary>
internal static class ClaudeUsageParser
{
    private const long FiveHourWindowMinutes = 300;
    private const long WeeklyWindowMinutes = 10_080;

    /// <summary>Model/surface scoped buckets, in the order they should be displayed.</summary>
    private static readonly (string Key, string DisplayName)[] ScopedBuckets =
    [
        ("seven_day_opus", "Opus 周额度"),
        ("seven_day_sonnet", "Sonnet 周额度"),
        ("seven_day_cowork", "Cowork 周额度"),
        ("seven_day_oauth_apps", "第三方应用周额度"),
    ];

    public static ClaudeAccountUsage Parse(
        JsonElement root,
        DateTimeOffset fetchedAt,
        string? subscriptionType = null)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Claude 用量接口返回了非预期的数据格式。");
        }

        var fiveHour = ParseWindow(root, "five_hour", FiveHourWindowMinutes);
        var weekly = ParseWindow(root, "seven_day", WeeklyWindowMinutes);
        if (fiveHour is null && weekly is null && !HasAnyLimit(root))
        {
            throw new InvalidDataException("Claude 用量接口没有返回任何额度窗口。");
        }

        var scoped = new List<ClaudeScopedLimit>();
        foreach (var (key, displayName) in ScopedBuckets)
        {
            if (ParseWindow(root, key, WeeklyWindowMinutes) is { } window)
            {
                scoped.Add(new ClaudeScopedLimit(key, displayName, window.UsedPercent, window.ResetsAt));
            }
        }

        scoped.AddRange(ParseModelScopedLimits(root, scoped));

        return new ClaudeAccountUsage(
            fiveHour,
            weekly,
            scoped,
            ParseExtraUsage(root),
            subscriptionType,
            fetchedAt,
            ParseWallet(root));
    }

    /// <summary>
    /// Reads the prepaid balance from <c>spend</c>. Amounts arrive as minor units with an explicit
    /// exponent, and the balance is null on accounts that have never bought credits.
    /// </summary>
    private static ClaudeCreditWallet? ParseWallet(JsonElement root)
    {
        if (!root.TryGetProperty("spend", out var spend) || spend.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var autoReload = spend.TryGetProperty("auto_reload", out var reload) &&
                         reload.ValueKind == JsonValueKind.Object
            ? reload
            : (JsonElement?)null;

        var wallet = new ClaudeCreditWallet(
            Balance: ReadMoney(spend, "balance"),
            Currency: ReadCurrency(spend) ?? "USD",
            AutoReloadEnabled: autoReload is { } enabledNode &&
                               (TryGetBoolean(enabledNode, "enabled") ?? true),
            AutoReloadThreshold: autoReload is { } thresholdNode
                ? ReadMoney(thresholdNode, "threshold") ?? ReadMoney(thresholdNode, "trigger")
                : null,
            AutoReloadAmount: autoReload is { } amountNode
                ? ReadMoney(amountNode, "amount") ?? ReadMoney(amountNode, "reload_amount")
                : null,
            CanPurchase: TryGetBoolean(spend, "can_purchase_credits") ?? false);

        // Nothing worth showing on an account with no wallet at all.
        return wallet is { Balance: null, AutoReloadEnabled: false, CanPurchase: false }
            ? null
            : wallet;
    }

    /// <summary>
    /// Accepts both the money object (<c>{amount_minor, exponent}</c>) and a plain number.
    /// </summary>
    private static decimal? ReadMoney(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            var minor = TryGetDouble(value, "amount_minor") ?? TryGetDouble(value, "amount");
            if (minor is null)
            {
                return null;
            }

            var exponent = TryGetDouble(value, "exponent") ?? 2;
            return (decimal)minor.Value / (decimal)Math.Pow(10, Math.Clamp(exponent, 0, 6));
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? (decimal)number
            : null;
    }

    private static string? ReadCurrency(JsonElement spend)
    {
        foreach (var name in new[] { "balance", "used", "limit" })
        {
            if (spend.TryGetProperty(name, out var node) &&
                node.ValueKind == JsonValueKind.Object &&
                TryGetString(node, "currency") is { } currency)
            {
                return currency;
            }
        }

        return TryGetString(spend, "currency");
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

    private static bool HasAnyLimit(JsonElement root)
    {
        return root.TryGetProperty("limits", out var limits) &&
               limits.ValueKind == JsonValueKind.Array &&
               limits.GetArrayLength() > 0;
    }

    private static RateLimitWindow? ParseWindow(JsonElement root, string name, long windowMinutes)
    {
        if (!root.TryGetProperty(name, out var bucket) || bucket.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var utilization = TryGetDouble(bucket, "utilization") ?? TryGetDouble(bucket, "percent");
        if (utilization is null)
        {
            return null;
        }

        return new RateLimitWindow(
            ToPercent(utilization.Value),
            TryGetTimestamp(bucket, "resets_at"),
            windowMinutes);
    }

    /// <summary>
    /// The <c>limits</c> array carries per-model weekly buckets that have no dedicated top-level
    /// key (their names change as new models ship), so surface them by their scope display name.
    /// </summary>
    private static IEnumerable<ClaudeScopedLimit> ParseModelScopedLimits(
        JsonElement root,
        IReadOnlyList<ClaudeScopedLimit> alreadyAdded)
    {
        if (!root.TryGetProperty("limits", out var limits) || limits.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        var seen = new HashSet<string>(
            alreadyAdded.Select(limit => limit.DisplayName),
            StringComparer.OrdinalIgnoreCase);
        foreach (var limit in limits.EnumerateArray())
        {
            if (limit.ValueKind != JsonValueKind.Object ||
                TryGetString(limit, "kind") is not { } kind ||
                !kind.StartsWith("weekly", StringComparison.OrdinalIgnoreCase) ||
                TryGetDouble(limit, "percent") is not { } percent)
            {
                continue;
            }

            var modelName = limit.TryGetProperty("scope", out var scope) &&
                            scope.ValueKind == JsonValueKind.Object &&
                            scope.TryGetProperty("model", out var model) &&
                            model.ValueKind == JsonValueKind.Object
                ? TryGetString(model, "display_name")
                : null;
            if (string.IsNullOrWhiteSpace(modelName))
            {
                continue;
            }

            var displayName = $"{modelName} 周额度";
            if (!seen.Add(displayName))
            {
                continue;
            }

            yield return new ClaudeScopedLimit(
                kind,
                displayName,
                ToPercent(percent),
                TryGetTimestamp(limit, "resets_at"));
        }
    }

    private static ClaudeExtraUsage? ParseExtraUsage(JsonElement root)
    {
        if (!root.TryGetProperty("extra_usage", out var extra) || extra.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var decimalPlaces = TryGetDouble(extra, "decimal_places") is { } places
            ? (int)Math.Clamp(places, 0, 6)
            : 2;
        var scale = (decimal)Math.Pow(10, decimalPlaces);

        return new ClaudeExtraUsage(
            IsEnabled: extra.TryGetProperty("is_enabled", out var enabled) &&
                       enabled.ValueKind == JsonValueKind.True,
            UsedAmount: TryGetDouble(extra, "used_credits") is { } used ? (decimal)used / scale : null,
            LimitAmount: TryGetDouble(extra, "monthly_limit") is { } limit ? (decimal)limit / scale : null,
            UsedPercent: TryGetDouble(extra, "utilization") is { } utilization
                ? ToPercent(utilization)
                : null,
            Currency: TryGetString(extra, "currency") ?? "USD",
            DisabledReason: TryGetString(extra, "disabled_reason"));
    }

    private static int ToPercent(double value)
    {
        return Math.Clamp((int)Math.Round(value, MidpointRounding.AwayFromZero), 0, 100);
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

    /// <summary>The usage endpoint uses ISO-8601 strings; the status line uses Unix seconds.</summary>
    private static DateTimeOffset? TryGetTimestamp(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return DateTimeOffset.TryParse(
                value.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed)
                ? parsed
                : null;
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
}
