using System.Globalization;
using CodexWeeklyMonitor.Models;

namespace CodexWeeklyMonitor.Services;

/// <summary>
/// Flattens a provider snapshot into label/value rows for the detail panel, so every field the
/// upstream APIs return is visible somewhere in the UI rather than only the two headline windows.
/// </summary>
public static class UsageDetailBuilder
{
    public static IReadOnlyList<UsageDetailSection> BuildCodex(
        CodexUsageSnapshot? snapshot,
        string? error)
    {
        if (snapshot is null)
        {
            return Single("Codex", Loc.T("lbl.status"), error ?? Loc.T("detail.reading"));
        }

        var sections = new List<UsageDetailSection>();
        var detail = snapshot.Detail;

        var account = new List<UsageDetailItem>();
        Add(account, Loc.T("lbl.source"), detail?.Source is { } src ? Loc.T(src) : null);
        Add(account, Loc.T("lbl.account"), detail?.Email);
        Add(account, Loc.T("lbl.nickname"), FormatProfileName(detail?.Profile));
        Add(account, Loc.T("lbl.plan"), detail?.PlanType ?? snapshot.RateLimits.PlanType);
        Add(account, Loc.T("lbl.available"), detail?.RateLimitAllowed switch
        {
            true => Loc.T("val.yes"),
            false => Loc.T("val.exhausted"),
            null => null,
        });
        Add(account, Loc.T("lbl.updateTime"), Timestamp(snapshot.FetchedAt));
        AddSection(sections, Loc.T("sec.account"), account);

        var quota = new List<UsageDetailItem>();
        AddWindow(quota, Loc.T("card.fiveHour"), snapshot.RateLimits.FiveHour);
        AddWindow(quota, Loc.T("card.weekly"), snapshot.RateLimits.Weekly);
        foreach (var model in detail?.ModelLimits ?? [])
        {
            AddWindow(quota, model.Name, model.Window, model.LimitReached ? Loc.T("val.exhaustedShort") : null);
        }

        Add(quota, Loc.T("lbl.limitTitle"), detail?.LimitTitle);
        Add(quota, Loc.T("lbl.limitDesc"), detail?.LimitDescription);
        Add(quota, Loc.T("lbl.spendCap"), detail?.SpendLimitReached switch
        {
            true => Loc.T("val.capReached"),
            false => Loc.T("val.capNotReached"),
            null => null,
        });
        AddSection(sections, Loc.T("sec.quota"), quota);

        var credits = new List<UsageDetailItem>();
        if (detail?.Credits is { } creditDetail)
        {
            Add(credits, Loc.T("lbl.balance"), creditDetail.Unlimited ? Loc.T("credit.unlimited") : creditDetail.Balance);
            Add(credits, Loc.T("lbl.hasBalance"), creditDetail.HasCredits ? Loc.T("val.yes") : Loc.T("val.no"));
            Add(credits, Loc.T("lbl.overageCap"), creditDetail.OverageLimitReached ? Loc.T("val.capReached") : Loc.T("val.capNotReached"));
            Add(credits, Loc.T("lbl.approxLocalMsg"), FormatRange(creditDetail.ApproxLocalMessages));
            Add(credits, Loc.T("lbl.approxCloudMsg"), FormatRange(creditDetail.ApproxCloudMessages));
        }
        else if (snapshot.RateLimits.Credits is { } legacy)
        {
            Add(credits, Loc.T("lbl.balance"), legacy.Unlimited ? Loc.T("credit.unlimited") : legacy.Balance);
            Add(credits, Loc.T("lbl.hasBalance"), legacy.HasCredits ? Loc.T("val.yes") : Loc.T("val.no"));
        }

        Add(credits, Loc.T("lbl.resetCredits"), snapshot.RateLimits.AvailableResetCount is { } count
            ? Loc.T("unit.times", count)
            : null);
        AddSection(sections, Loc.T("sec.balance"), credits);

        var usage = new List<UsageDetailItem>();
        if (snapshot.TokenUsage is { } tokenUsage)
        {
            Add(usage, Loc.T("lbl.lifetimeTokens"), FormatExactTokens(tokenUsage.LifetimeTokens));
            Add(usage, Loc.T("lbl.peakDaily"), FormatExactTokens(tokenUsage.PeakDailyTokens));
            Add(usage, Loc.T("lbl.longestTurn"), FormatDuration(tokenUsage.LongestRunningTurnSeconds));
            Add(usage, Loc.T("lbl.currentStreak"), FormatDays(tokenUsage.CurrentStreakDays));
            Add(usage, Loc.T("lbl.longestStreak"), FormatDays(tokenUsage.LongestStreakDays));
            Add(usage, Loc.T("lbl.historyDays"), Loc.T("unit.days", tokenUsage.DailyUsage.Count));
            Add(usage, Loc.T("lbl.historyUntil"), tokenUsage.LatestDay?.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        if (detail?.Profile is { } profile)
        {
            Add(usage, Loc.T("lbl.totalThreads"), profile.TotalThreads is { } threads ? Loc.T("unit.count", threads) : null);
            Add(usage, Loc.T("lbl.fastMode"), FormatPercent(profile.FastModeUsagePercent));
            Add(usage, Loc.T("lbl.skillsUsed"), profile.TotalSkillsUsed is { } total ? Loc.T("unit.times", total) : null);
            Add(usage, Loc.T("lbl.uniqueSkills"), profile.UniqueSkillsUsed is { } unique ? Loc.T("unit.kinds", unique) : null);
            Add(usage, Loc.T("lbl.mostEffort"), FormatEffort(profile));
        }

        Add(usage, Loc.T("lbl.statsNote"), snapshot.TokenUsageError);
        AddSection(sections, Loc.T("sec.usageStats"), usage);

        return sections.Count == 0 ? Single("Codex", Loc.T("lbl.status"), Loc.T("detail.noData")) : sections;
    }

    public static IReadOnlyList<UsageDetailSection> BuildClaude(ClaudeUsageSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return Single("Claude", Loc.T("lbl.status"), Loc.T("detail.reading"));
        }

        var sections = new List<UsageDetailSection>();
        var account = snapshot.Account;

        var identity = new List<UsageDetailItem>();
        Add(identity, Loc.T("lbl.source"), account is null ? null : Loc.T("source.officialUsage"));
        Add(identity, Loc.T("lbl.plan"), FormatPlan(account?.SubscriptionType));
        Add(identity, Loc.T("lbl.updateTime"), account is null ? null : Timestamp(account.FetchedAt));
        Add(identity, Loc.T("lbl.status"), snapshot.AccountError ?? (account is null ? Loc.T("detail.reading") : Loc.T("detail.normal")));
        AddSection(sections, Loc.T("sec.account"), identity);

        var quota = new List<UsageDetailItem>();
        AddWindow(quota, Loc.T("card.fiveHour"), account?.FiveHour);
        AddWindow(quota, Loc.T("card.weekly"), account?.Weekly);
        foreach (var scoped in account?.ScopedLimits ?? [])
        {
            AddWindow(
                quota,
                scoped.DisplayName,
                new RateLimitWindow(scoped.UsedPercent, scoped.ResetsAt, null));
        }

        AddSection(sections, Loc.T("sec.quota"), quota);

        var wallet = new List<UsageDetailItem>();
        if (account?.Wallet is { } purse)
        {
            Add(wallet, Loc.T("lbl.currentBalance"), FormatMoney(purse.Balance, purse.Currency));
            Add(wallet, Loc.T("lbl.autoReload"), purse.AutoReloadEnabled ? Loc.T("val.on") : Loc.T("val.off"));
            Add(wallet, Loc.T("lbl.reloadTrigger"), FormatMoney(purse.AutoReloadThreshold, purse.Currency));
            Add(wallet, Loc.T("lbl.reloadAmount"), FormatMoney(purse.AutoReloadAmount, purse.Currency));
            Add(wallet, Loc.T("lbl.canPurchase"), purse.CanPurchase ? Loc.T("val.yes") : Loc.T("val.no"));
        }

        AddSection(sections, Loc.T("sec.wallet"), wallet);

        var credits = new List<UsageDetailItem>();
        if (account?.ExtraUsage is { } extra)
        {
            Add(credits, Loc.T("lbl.isEnabled"), extra.IsEnabled ? Loc.T("val.enabledShort") : Loc.T("val.notEnabledShort"));
            Add(credits, Loc.T("lbl.periodUsed"), FormatMoney(extra.UsedAmount, extra.Currency));
            Add(credits, Loc.T("lbl.periodCap"), FormatMoney(extra.LimitAmount, extra.Currency));
            Add(credits, Loc.T("lbl.usedPercent"), extra.UsedPercent is { } percent ? $"{percent}%" : null);
            Add(credits, Loc.T("lbl.disabledReason"), extra.DisabledReason);
        }

        AddSection(sections, Loc.T("sec.usageCredit"), credits);

        var session = new List<UsageDetailItem>();
        Add(session, Loc.T("lbl.currentModel"), snapshot.ModelName);
        if (snapshot.Session is { } live)
        {
            Add(
                session,
                Loc.T("lbl.context"),
                $"{live.ContextUsedPercent}% · {live.ContextTokens:N0} / {live.ContextWindow:N0}");
            Add(session, Loc.T("lbl.effort"), live.Effort);
            Add(session, Loc.T("lbl.ccVersion"), live.Version);
            Add(session, Loc.T("lbl.lastTurn"), Timestamp(live.ObservedAt));
            Add(session, Loc.T("lbl.source"), Loc.T("source.localSession"));
        }
        else if (snapshot.Status?.ContextUsedPercent is { } context)
        {
            Add(session, Loc.T("lbl.context"), $"{context}%");
            Add(session, Loc.T("lbl.source"), Loc.T("source.statusBridge"));
        }
        else
        {
            Add(session, Loc.T("lbl.status"), Loc.T("val.noRecentSession"));
        }

        AddSection(sections, Loc.T("sec.session"), session);

        var usage = new List<UsageDetailItem>();
        if (snapshot.TokenUsage is { } tokenUsage)
        {
            Add(usage, Loc.T("lbl.localLifetimeTokens"), FormatExactTokens(tokenUsage.LifetimeTokens));
            Add(usage, Loc.T("lbl.peakDaily"), FormatExactTokens(tokenUsage.PeakDailyTokens));
            Add(usage, Loc.T("lbl.currentStreak"), FormatDays(tokenUsage.CurrentStreakDays));
            Add(usage, Loc.T("lbl.longestStreak"), FormatDays(tokenUsage.LongestStreakDays));
            Add(usage, Loc.T("lbl.historyDays"), Loc.T("unit.days", tokenUsage.DailyUsage.Count));
            Add(usage, Loc.T("lbl.statsScope"), Loc.T("val.statsScopeClaude"));
        }

        AddSection(sections, Loc.T("sec.usageStats"), usage);

        return sections.Count == 0 ? Single("Claude", Loc.T("lbl.status"), Loc.T("detail.noData")) : sections;
    }

    private static string Timestamp(DateTimeOffset value) =>
        value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static void AddWindow(
        ICollection<UsageDetailItem> items,
        string label,
        RateLimitWindow? window,
        string? note = null)
    {
        if (window is null)
        {
            return;
        }

        var parts = new List<string> { Loc.T("window.usedRemaining", window.UsedPercent, window.RemainingPercent) };
        if (window.ResetsAt is { } resetsAt)
        {
            parts.Add(Loc.T("window.reset", Loc.MonthDayTime(resetsAt)));
            parts.Add(FormatCountdown(resetsAt));
        }

        if (!string.IsNullOrWhiteSpace(note))
        {
            parts.Add(note);
        }

        items.Add(new UsageDetailItem(label, string.Join(" · ", parts)));
    }

    internal static string FormatCountdown(DateTimeOffset resetsAt)
    {
        var remaining = resetsAt - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero)
        {
            return Loc.T("countdown.imminent");
        }

        if (remaining.TotalDays >= 1)
        {
            return Loc.T("countdown.daysHours", (int)remaining.TotalDays, remaining.Hours);
        }

        return remaining.TotalHours >= 1
            ? Loc.T("countdown.hoursMinutes", (int)remaining.TotalHours, remaining.Minutes)
            : Loc.T("countdown.minutes", Math.Max(1, (int)remaining.TotalMinutes));
    }

    private static string? FormatProfileName(CodexProfileStats? profile)
    {
        if (profile is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(profile.DisplayName))
        {
            return string.IsNullOrWhiteSpace(profile.Username) ? null : $"@{profile.Username}";
        }

        return string.IsNullOrWhiteSpace(profile.Username)
            ? profile.DisplayName
            : $"{profile.DisplayName} (@{profile.Username})";
    }

    private static string? FormatEffort(CodexProfileStats profile)
    {
        if (string.IsNullOrWhiteSpace(profile.MostUsedReasoningEffort))
        {
            return null;
        }

        return profile.MostUsedReasoningEffortPercent is { } percent
            ? $"{profile.MostUsedReasoningEffort} ({percent:0.#}%)"
            : profile.MostUsedReasoningEffort;
    }

    private static string? FormatRange(IReadOnlyList<long> range)
    {
        if (range.Count == 0)
        {
            return null;
        }

        return range.Count >= 2 && range[0] != range[1]
            ? Loc.T("range.pair", range[0], range[1])
            : Loc.T("range.single", range[0]);
    }

    private static string? FormatMoney(decimal? amount, string currency)
    {
        if (amount is null)
        {
            return null;
        }

        var symbol = currency.Equals("USD", StringComparison.OrdinalIgnoreCase) ? "$" : string.Empty;
        return $"{symbol}{amount.Value.ToString("0.##", CultureInfo.InvariantCulture)}";
    }

    private static string? FormatPlan(string? subscriptionType)
    {
        return string.IsNullOrWhiteSpace(subscriptionType) ? null : subscriptionType;
    }

    private static string? FormatPercent(double? value)
    {
        return value is null ? null : $"{value.Value.ToString("0.#", CultureInfo.InvariantCulture)}%";
    }

    private static string? FormatDays(long? days)
    {
        return days is null ? null : Loc.T("unit.days", days);
    }

    private static string? FormatExactTokens(long? tokens)
    {
        return tokens is null
            ? null
            : $"{TokenFormatter.Format(tokens)} ({tokens.Value.ToString("N0", CultureInfo.InvariantCulture)})";
    }

    private static string? FormatDuration(long? seconds)
    {
        if (seconds is null or <= 0)
        {
            return null;
        }

        var span = TimeSpan.FromSeconds(seconds.Value);
        if (span.TotalHours >= 1)
        {
            return Loc.T("duration.hoursMin", (int)span.TotalHours, span.Minutes);
        }

        return span.TotalMinutes >= 1
            ? Loc.T("duration.minSec", (int)span.TotalMinutes, span.Seconds)
            : Loc.T("duration.sec", span.Seconds);
    }

    private static void Add(ICollection<UsageDetailItem> items, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            items.Add(new UsageDetailItem(label, value));
        }
    }

    private static void AddSection(
        ICollection<UsageDetailSection> sections,
        string title,
        IReadOnlyList<UsageDetailItem> items)
    {
        if (items.Count > 0)
        {
            sections.Add(new UsageDetailSection(title, items));
        }
    }

    private static IReadOnlyList<UsageDetailSection> Single(string title, string label, string value)
    {
        return [new UsageDetailSection(title, [new UsageDetailItem(label, value)])];
    }
}
