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

    /// <summary>
    /// Stable buckets emitted by the OAuth endpoint. The overage-included bucket is the Fable
    /// allocation and is billed through usage credits rather than the subscription window.
    /// </summary>
    private static readonly (
        string Key,
        string DisplayName,
        ClaudeLimitBilling Billing,
        string ModelName)[] ScopedBuckets =
    [
        ("seven_day_overage_included", "Fable 5.1 点数额度", ClaudeLimitBilling.UsageCredits, "Fable 5.1"),
        ("seven_day_opus", "Opus 周额度", ClaudeLimitBilling.Subscription, "Opus"),
        ("seven_day_sonnet", "Sonnet 周额度", ClaudeLimitBilling.Subscription, "Sonnet"),
        ("seven_day_cowork", "Cowork 周额度", ClaudeLimitBilling.Subscription, "Cowork"),
        ("seven_day_oauth_apps", "第三方应用周额度", ClaudeLimitBilling.Subscription, "第三方应用"),
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

        var rateLimits = TryGetObject(root, "rate_limits");
        var fiveHour = ParseWindow(rateLimits ?? root, "five_hour", FiveHourWindowMinutes) ??
                       (rateLimits is null ? null : ParseWindow(root, "five_hour", FiveHourWindowMinutes));
        var weekly = ParseWindow(rateLimits ?? root, "seven_day", WeeklyWindowMinutes) ??
                     (rateLimits is null ? null : ParseWindow(root, "seven_day", WeeklyWindowMinutes));
        if (fiveHour is null && weekly is null &&
            !HasAnyQuota(root) &&
            (rateLimits is null || !HasAnyQuota(rateLimits.Value)))
        {
            throw new InvalidDataException("Claude 用量接口没有返回任何额度窗口。");
        }

        var scoped = new List<ClaudeScopedLimit>();
        foreach (var (key, displayName, billing, modelName) in ScopedBuckets)
        {
            var window = ParseWindow(rateLimits ?? root, key, WeeklyWindowMinutes) ??
                         (rateLimits is null ? null : ParseWindow(root, key, WeeklyWindowMinutes));
            if (window is not null)
            {
                scoped.Add(new ClaudeScopedLimit(
                    key,
                    displayName,
                    window.UsedPercent,
                    window.ResetsAt,
                    billing,
                    modelName));
            }
        }

        scoped.AddRange(ParseModelScopedLimits(root, scoped));
        if (rateLimits is { } nestedRateLimits)
        {
            scoped.AddRange(ParseModelScopedLimits(nestedRateLimits, scoped));
        }

        return new ClaudeAccountUsage(
            fiveHour,
            weekly,
            scoped,
            ParseExtraUsage(root) ??
            (rateLimits is { } nested ? ParseExtraUsage(nested) : null),
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
            CanPurchase: TryGetBoolean(spend, "can_purchase_credits") ?? false,
            Used: ReadMoney(spend, "used"),
            Limit: ReadMoney(spend, "limit"),
            UsedPercent: TryGetDouble(spend, "utilization") is { } utilization
                ? ToPercent(utilization)
                : TryGetDouble(spend, "used_percentage") is { } usedPercentage
                    ? ToPercent(usedPercentage)
                    : null,
            IsEnabled: TryGetBoolean(spend, "is_enabled"));

        // Nothing worth showing on an account with no wallet at all.
        return wallet is
        {
            Balance: null,
            AutoReloadEnabled: false,
            CanPurchase: false,
            Used: null,
            Limit: null,
            UsedPercent: null,
            IsEnabled: null,
        }
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

    private static bool HasAnyQuota(JsonElement root)
    {
        if (root.TryGetProperty("limits", out var limits) &&
            limits.ValueKind == JsonValueKind.Array &&
            limits.GetArrayLength() > 0)
        {
            return true;
        }

        if (root.TryGetProperty("model_scoped", out var modelScoped) &&
            modelScoped.ValueKind == JsonValueKind.Array &&
            modelScoped.GetArrayLength() > 0)
        {
            return true;
        }

        return ScopedBuckets.Any(bucket =>
            root.TryGetProperty(bucket.Key, out var value) &&
            value.ValueKind == JsonValueKind.Object);
    }

    private static JsonElement? TryGetObject(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.Object
            ? value
            : null;
    }

    private static RateLimitWindow? ParseWindow(JsonElement root, string name, long windowMinutes)
    {
        if (!root.TryGetProperty(name, out var bucket) || bucket.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var utilization = TryGetDouble(bucket, "utilization") ??
                           TryGetDouble(bucket, "used_percentage") ??
                           TryGetDouble(bucket, "percent");
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
    /// Newer responses expose model buckets in both <c>limits[]</c> and <c>model_scoped[]</c>.
    /// Accept both shapes and de-duplicate a bucket that is present in both projections.
    /// </summary>
    private static IEnumerable<ClaudeScopedLimit> ParseModelScopedLimits(
        JsonElement root,
        IReadOnlyList<ClaudeScopedLimit> alreadyAdded)
    {
        var seen = new HashSet<string>(
            alreadyAdded.Select(LimitIdentity),
            StringComparer.OrdinalIgnoreCase);

        if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
        {
            foreach (var limit in limits.EnumerateArray())
            {
                if (TryParseScopedLimit(limit, fromModelScopedArray: false) is not { } parsed ||
                    !seen.Add(LimitIdentity(parsed)))
                {
                    continue;
                }

                yield return parsed;
            }
        }

        if (root.TryGetProperty("model_scoped", out var modelScoped) &&
            modelScoped.ValueKind == JsonValueKind.Array)
        {
            foreach (var limit in modelScoped.EnumerateArray())
            {
                if (TryParseScopedLimit(limit, fromModelScopedArray: true) is not { } parsed ||
                    !seen.Add(LimitIdentity(parsed)))
                {
                    continue;
                }

                yield return parsed;
            }
        }
    }

    private static ClaudeScopedLimit? TryParseScopedLimit(
        JsonElement limit,
        bool fromModelScopedArray)
    {
        if (limit.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var kind = TryGetString(limit, "kind") ?? "model_scoped";
        if (!fromModelScopedArray && !kind.StartsWith("weekly", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var percent = TryGetDouble(limit, "percent") ?? TryGetDouble(limit, "utilization");
        var modelName = TryGetModelName(limit);
        if (percent is null || string.IsNullOrWhiteSpace(modelName))
        {
            return null;
        }

        var isFable = IsFableBucket(kind, modelName);
        var billing = isFable ? ClaudeLimitBilling.UsageCredits : ClaudeLimitBilling.Subscription;
        return new ClaudeScopedLimit(
            kind,
            BuildScopedDisplayName(modelName, billing),
            ToPercent(percent.Value),
            TryGetTimestamp(limit, "resets_at"),
            billing,
            modelName);
    }

    private static string? TryGetModelName(JsonElement limit)
    {
        if (limit.TryGetProperty("scope", out var scope) &&
            scope.ValueKind == JsonValueKind.Object &&
            scope.TryGetProperty("model", out var model) &&
            model.ValueKind == JsonValueKind.Object)
        {
            return TryGetString(model, "display_name") ?? TryGetString(model, "name");
        }

        if (TryGetObject(limit, "model") is { } modelObject)
        {
            return TryGetString(modelObject, "display_name") ??
                   TryGetString(modelObject, "name") ??
                   TryGetString(modelObject, "id");
        }

        return TryGetString(limit, "display_name") ?? TryGetString(limit, "model");
    }

    private static bool IsFableBucket(string key, string modelName) =>
        key.Equals("seven_day_overage_included", StringComparison.OrdinalIgnoreCase) ||
        modelName.Contains("fable", StringComparison.OrdinalIgnoreCase);

    private static string BuildScopedDisplayName(string modelName, ClaudeLimitBilling billing) =>
        billing == ClaudeLimitBilling.UsageCredits
            ? $"{modelName} 点数额度"
            : $"{modelName} 周额度";

    private static string LimitIdentity(ClaudeScopedLimit limit) =>
        limit.IsFable
            ? "fable"
            : (limit.ModelName ?? limit.Key).Trim();

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
