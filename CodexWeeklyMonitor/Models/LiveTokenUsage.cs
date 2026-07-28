namespace CodexWeeklyMonitor.Models;

public sealed record LiveTokenUsage(
    long TotalTokens,
    long LastTurnTokens,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    long ReasoningOutputTokens,
    long? ModelContextWindow,
    DateTimeOffset ObservedAt);

public sealed record TodayTokenUsage(
    DateOnly Date,
    long Tokens,
    int SessionCount,
    DateTimeOffset UpdatedAt);
