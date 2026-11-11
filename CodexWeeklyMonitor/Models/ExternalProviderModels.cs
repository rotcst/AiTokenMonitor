namespace CodexWeeklyMonitor.Models;

/// <summary>
/// Identifiers for extended AI providers beyond Codex, Claude, and Antigravity.
/// </summary>
public enum ExternalProviderId
{
    Grok,
    DeepSeek,
    Kimi,
    Qwen,
    Glm,
    SiliconFlow,
}

/// <summary>
/// Static descriptor for an extended AI provider, including its display name, theme color,
/// environment variables, model prefixes, and official API endpoint metadata.
/// </summary>
public sealed record ExternalProviderSpec(
    ExternalProviderId Id,
    string Key,
    string DisplayName,
    string BarColor,
    IReadOnlyList<string> ApiKeyEnvVars,
    IReadOnlyList<string> BaseUrlDomains,
    IReadOnlyList<string> ModelPrefixes,
    IReadOnlyList<(string ModelId, string DisplayName)> DefaultModels);

/// <summary>
/// Currency balance and credit breakdown returned by an official provider balance API
/// (such as DeepSeek <c>/user/balance</c>, Moonshot/Kimi <c>/v1/users/me/balance</c>, or
/// SiliconFlow <c>/v1/user/info</c>) or a local status bridge.
/// </summary>
public sealed record ExternalProviderBalance(
    string Currency,
    decimal TotalBalance,
    decimal? GrantedBalance = null,
    decimal? ToppedUpBalance = null,
    decimal? MonthlyBudget = null,
    bool IsAvailable = true)
{
    public int? RemainingBudgetPercent =>
        MonthlyBudget is { } budget && budget > 0
            ? (int)Math.Clamp(Math.Round((double)(TotalBalance / budget * 100m), MidpointRounding.AwayFromZero), 0, 100)
            : null;
}

/// <summary>
/// Unified usage and quota snapshot for an extended AI provider.
/// </summary>
public sealed record ExternalProviderSnapshot(
    ExternalProviderId Provider,
    IReadOnlyList<AntigravityModelUsage> Models,
    ExternalProviderBalance? Balance,
    RateLimitWindow? FiveHour,
    RateLimitWindow? Weekly,
    AccountTokenUsage? TokenUsage,
    AntigravitySessionState? Session,
    string? PlanType,
    string? AccountName,
    string? Error,
    bool IsAvailable,
    bool HasApiKey,
    bool IsBridgeConfigured,
    DateTimeOffset FetchedAt)
{
    public string? ActiveModelName =>
        Session?.DisplayModelName ??
        Models.FirstOrDefault()?.DisplayName;

    public int? ContextUsedPercent => Session?.ContextUsedPercent;
}
