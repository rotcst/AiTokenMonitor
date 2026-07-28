using CodexWeeklyMonitor.Models;

namespace CodexWeeklyMonitor.Services;

internal sealed record ReconciledTokenUsage(
    AccountTokenUsage Usage,
    long ServerTodayTokens,
    long EffectiveTodayTokens,
    bool LocalRealtimeApplied);

internal static class TokenUsageReconciler
{
    public static ReconciledTokenUsage Reconcile(
        AccountTokenUsage usage,
        TodayTokenUsage? localTodayUsage,
        DateOnly today)
    {
        var serverTodayTokens = usage.DailyUsage
            .Where(item => item.Date == today)
            .Sum(item => item.Tokens);
        var localTodayTokens = localTodayUsage is { Date: var localDate } && localDate == today
            ? Math.Max(0, localTodayUsage.Tokens)
            : 0;
        var effectiveTodayTokens = Math.Max(serverTodayTokens, localTodayTokens);
        var localRealtimeApplied = localTodayTokens > serverTodayTokens;

        if (effectiveTodayTokens <= serverTodayTokens)
        {
            return new ReconciledTokenUsage(
                usage,
                serverTodayTokens,
                effectiveTodayTokens,
                LocalRealtimeApplied: false);
        }

        var dailyUsage = usage.DailyUsage
            .Where(item => item.Date != today)
            .Append(new DailyTokenUsage(today, effectiveTodayTokens))
            .OrderBy(item => item.Date)
            .ToArray();
        var peakDailyTokens = Math.Max(usage.PeakDailyTokens ?? 0, effectiveTodayTokens);

        return new ReconciledTokenUsage(
            usage with
            {
                PeakDailyTokens = peakDailyTokens,
                DailyUsage = dailyUsage,
            },
            serverTodayTokens,
            effectiveTodayTokens,
            localRealtimeApplied);
    }
}
