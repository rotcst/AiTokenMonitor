namespace CodexWeeklyMonitor.Models;

public sealed record WeeklyQuota(
    int UsedPercent,
    DateTimeOffset? ResetsAt,
    long? WindowDurationMinutes,
    string? PlanType,
    string? LimitName,
    DateTimeOffset FetchedAt)
{
    public int RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);
}
