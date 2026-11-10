namespace CodexWeeklyMonitor.Models;

/// <summary>
/// Per-model token and invocation statistics aggregated from Antigravity conversation transcripts
/// (and optional quota windows supplied by a hook/status bridge).
/// </summary>
public sealed record AntigravityModelUsage(
    string ModelName,
    string DisplayName,
    long TotalTokens,
    long InputTokens,
    long CacheReadTokens,
    long OutputTokens,
    long TodayTokens,
    long SevenDayTokens,
    int TurnCount,
    int SharePercent,
    DateTimeOffset LastUsedAt,
    RateLimitWindow? QuotaWindow = null);

/// <summary>
/// Per-model quota window reported by an optional Antigravity status/hook bridge.
/// </summary>
public sealed record AntigravityModelQuota(
    string Key,
    string ModelName,
    string DisplayName,
    int UsedPercent,
    DateTimeOffset? ResetsAt)
{
    public int RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);
}

/// <summary>
/// Supplementary data that an Antigravity hook or status bridge can write to
/// <c>%LOCALAPPDATA%\AiTokenMonitor\antigravity-status.json</c>.
/// </summary>
public sealed record AntigravityStatusUsage(
    RateLimitWindow? FiveHour,
    RateLimitWindow? Weekly,
    string? ModelName,
    string? ConversationId,
    int? ContextUsedPercent,
    string? PlanType,
    DateTimeOffset ObservedAt,
    IReadOnlyList<AntigravityModelQuota>? ModelQuotas = null,
    string? TranscriptPath = null);

/// <summary>
/// Live session state extracted from the newest Antigravity conversation transcript.
/// </summary>
public sealed record AntigravitySessionState(
    string? ConversationId,
    string? ModelName,
    long ContextTokens,
    long OutputTokens,
    int TurnCount,
    DateTimeOffset ObservedAt)
{
    /// <summary>
    /// Gemini models in Antigravity typically run with a 1M context window; smaller non-Gemini or
    /// lightweight models may use 200K. Default to 1M for Gemini or when context exceeds 200K.
    /// </summary>
    public long ContextWindow =>
        ContextTokens > 200_000 ||
        (ModelName?.Contains("gemini", StringComparison.OrdinalIgnoreCase) ?? true)
            ? 1_000_000
            : 200_000;

    public int ContextUsedPercent =>
        (int)Math.Clamp(Math.Round(ContextTokens * 100d / ContextWindow), 0, 100);

    public string? DisplayModelName => NormalizeModelDisplayName(ModelName);

    public static string? NormalizeModelDisplayName(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return null;
        }

        var trimmed = rawName.Trim();
        if (trimmed.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("inherit", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return "Auto";
        }

        // Preserve human-readable names like "Gemini 4 Argon (High)" or "Claude Sonnet 4.5".
        if (trimmed.Contains(' '))
        {
            return trimmed;
        }

        // Normalize slug IDs such as "gemini-2.5-pro" -> "Gemini 2.5 Pro".
        var parts = trimmed.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Length <= 1
                ? part.ToUpperInvariant()
                : char.ToUpperInvariant(part[0]) + part[1..])
            .ToArray();
        return string.Join(' ', parts);
    }
}

public sealed record AntigravityUsageSnapshot(
    IReadOnlyList<AntigravityModelUsage> Models,
    AccountTokenUsage? TokenUsage,
    AntigravitySessionState? Session,
    AntigravityStatusUsage? Status,
    int TotalConversations,
    int TotalTurns,
    bool IsAntigravityAvailable,
    bool IsBridgeConfigured,
    DateTimeOffset FetchedAt)
{
    public string? ActiveModelName =>
        Session?.DisplayModelName ??
        AntigravitySessionState.NormalizeModelDisplayName(Status?.ModelName) ??
        Models.FirstOrDefault()?.DisplayName;

    public int? ContextUsedPercent => Session?.ContextUsedPercent ?? Status?.ContextUsedPercent;

    public RateLimitWindow? FiveHour => Status?.FiveHour;

    public RateLimitWindow? Weekly => Status?.Weekly;
}

internal sealed record AntigravityTokenRecord(
    string Key,
    string ConversationId,
    string ModelName,
    DateOnly Date,
    long InputTokens,
    long CacheReadTokens,
    long OutputTokens,
    long TotalTokens,
    DateTimeOffset ObservedAt);
