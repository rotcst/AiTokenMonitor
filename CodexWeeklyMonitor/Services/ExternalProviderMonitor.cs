using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using CodexWeeklyMonitor.Models;

namespace CodexWeeklyMonitor.Services;

/// <summary>
/// Discovers credentials, polls official balance/key endpoints, reads optional status bridges,
/// and aggregates local session token usage for Grok (xAI), DeepSeek, Kimi (Moonshot),
/// Qwen (DashScope), GLM (Zhipu), and SiliconFlow.
/// </summary>
public sealed class ExternalProviderMonitor : IDisposable
{
    public static readonly IReadOnlyList<ExternalProviderSpec> AllSpecs =
    [
        new ExternalProviderSpec(
            Id: ExternalProviderId.Grok,
            Key: "grok",
            DisplayName: "Grok",
            BarColor: "#E2E8F0",
            ApiKeyEnvVars: ["XAI_API_KEY", "GROK_API_KEY"],
            BaseUrlDomains: ["api.x.ai", "x.ai"],
            ModelPrefixes: ["grok-"],
            DefaultModels:
            [
                ("grok-4", "Grok 4"),
                ("grok-3", "Grok 3"),
                ("grok-3-mini", "Grok 3 Mini"),
                ("grok-code-fast-1", "Grok Code Fast"),
            ]),
        new ExternalProviderSpec(
            Id: ExternalProviderId.DeepSeek,
            Key: "deepseek",
            DisplayName: "DeepSeek",
            BarColor: "#4D6BFE",
            ApiKeyEnvVars: ["DEEPSEEK_API_KEY"],
            BaseUrlDomains: ["api.deepseek.com", "deepseek.com"],
            ModelPrefixes: ["deepseek-"],
            DefaultModels:
            [
                ("deepseek-chat", "DeepSeek V3"),
                ("deepseek-reasoner", "DeepSeek R1"),
            ]),
        new ExternalProviderSpec(
            Id: ExternalProviderId.Kimi,
            Key: "kimi",
            DisplayName: "Kimi",
            BarColor: "#A78BFA",
            ApiKeyEnvVars: ["MOONSHOT_API_KEY", "KIMI_API_KEY"],
            BaseUrlDomains: ["api.moonshot.cn", "api.moonshot.ai", "api.kimi.com", "moonshot.cn"],
            ModelPrefixes: ["kimi-", "moonshot-"],
            DefaultModels:
            [
                ("kimi-k2.5", "Kimi K2.5"),
                ("kimi-k2-thinking", "Kimi K2 Thinking"),
                ("moonshot-v1-128k", "Moonshot V1"),
            ]),
        new ExternalProviderSpec(
            Id: ExternalProviderId.Qwen,
            Key: "qwen",
            DisplayName: "Qwen",
            BarColor: "#818CF8",
            ApiKeyEnvVars: ["DASHSCOPE_API_KEY", "QWEN_API_KEY"],
            BaseUrlDomains: ["dashscope.aliyuncs.com", "aliyuncs.com"],
            ModelPrefixes: ["qwen-", "qwen2", "qwen3", "qwq-"],
            DefaultModels:
            [
                ("qwen3-max", "Qwen3 Max"),
                ("qwen-plus", "Qwen Plus"),
                ("qwen3-coder-plus", "Qwen3 Coder Plus"),
                ("qwq-plus", "QwQ Plus"),
            ]),
        new ExternalProviderSpec(
            Id: ExternalProviderId.Glm,
            Key: "glm",
            DisplayName: "GLM",
            BarColor: "#38BDF8",
            ApiKeyEnvVars: ["ZHIPU_API_KEY", "ZHIPUAI_API_KEY", "GLM_API_KEY"],
            BaseUrlDomains: ["open.bigmodel.cn", "bigmodel.cn"],
            ModelPrefixes: ["glm-", "chatglm-", "codegeex-"],
            DefaultModels:
            [
                ("glm-5", "GLM-5"),
                ("glm-4-plus", "GLM-4 Plus"),
                ("glm-4-air", "GLM-4 Air"),
                ("glm-4-flash", "GLM-4 Flash"),
            ]),
        new ExternalProviderSpec(
            Id: ExternalProviderId.SiliconFlow,
            Key: "siliconflow",
            DisplayName: "SiliconFlow",
            BarColor: "#F472B6",
            ApiKeyEnvVars: ["SILICONFLOW_API_KEY"],
            BaseUrlDomains: ["api.siliconflow.cn", "api.siliconflow.com", "siliconflow.cn"],
            ModelPrefixes: ["pro/deepseek-ai/", "deepseek-ai/", "qwen/", "thudm/"],
            DefaultModels:
            [
                ("deepseek-ai/DeepSeek-V3", "DeepSeek V3 (SF)"),
                ("deepseek-ai/DeepSeek-R1", "DeepSeek R1 (SF)"),
                ("Qwen/Qwen3-235B-A22B", "Qwen3 235B (SF)"),
            ]),
    ];

    private static readonly IReadOnlyDictionary<ExternalProviderId, ExternalProviderSpec> SpecsById =
        AllSpecs.ToDictionary(spec => spec.Id);

    private readonly string _bridgeDirectory;
    private readonly string _claudeHome;
    private readonly string _kimiHome;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly bool _enableNetworkPolling;
    private readonly object _gate = new();
    private readonly Dictionary<ExternalProviderId, ExternalProviderSnapshot> _snapshots = new();
    private bool _disposed;

    public ExternalProviderMonitor(
        string? bridgeDirectory = null,
        string? claudeHome = null,
        string? kimiHome = null,
        HttpClient? httpClient = null,
        bool enableNetworkPolling = true)
    {
        _bridgeDirectory = string.IsNullOrWhiteSpace(bridgeDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AiTokenMonitor")
            : bridgeDirectory;
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _claudeHome = ClaudePaths.ResolveHome(claudeHome);
        _kimiHome = string.IsNullOrWhiteSpace(kimiHome)
            ? Path.Combine(userProfile, ".kimi")
            : kimiHome;
        _enableNetworkPolling = enableNetworkPolling;
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    public event Action<ExternalProviderSnapshot>? UsageUpdated;

    public static ExternalProviderSpec GetSpec(ExternalProviderId id) => SpecsById[id];

    public ExternalProviderSnapshot? GetSnapshot(ExternalProviderId id)
    {
        lock (_gate)
        {
            return _snapshots.GetValueOrDefault(id);
        }
    }

    public void Start()
    {
        _ = RefreshAllAsync();
    }

    public void Refresh()
    {
        _ = RefreshAllAsync();
    }

    public async Task RefreshAllAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }

        var localRecordsByProvider = ScanLocalTranscripts();
        var configMap = LoadProvidersConfig(_bridgeDirectory);

        foreach (var spec in AllSpecs)
        {
            if (_disposed || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                var localData = localRecordsByProvider.TryGetValue(spec.Id, out var found)
                    ? found
                    : (Records: (IReadOnlyList<AntigravityTokenRecord>)Array.Empty<AntigravityTokenRecord>(), Session: (AntigravitySessionState?)null);
                var snapshot = await BuildSnapshotAsync(
                    spec,
                    localData,
                    configMap.GetValueOrDefault(spec.Key),
                    cancellationToken);
                lock (_gate)
                {
                    _snapshots[spec.Id] = snapshot;
                }

                UsageUpdated?.Invoke(snapshot);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                DiagnosticsLog.Write(
                    "ExternalProviderMonitor",
                    $"{spec.DisplayName} refresh failed: {exception.GetType().Name}: {exception.Message}");
            }
        }
    }

    private async Task<ExternalProviderSnapshot> BuildSnapshotAsync(
        ExternalProviderSpec spec,
        (IReadOnlyList<AntigravityTokenRecord> Records, AntigravitySessionState? Session) localData,
        ProviderConfigEntry? configEntry,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        var apiKey = ResolveApiKey(spec, configEntry, _claudeHome, _kimiHome);
        var hasApiKey = !string.IsNullOrWhiteSpace(apiKey);

        var bridgePath = Path.Combine(_bridgeDirectory, $"{spec.Key}-status.json");
        var isBridgeConfigured = File.Exists(bridgePath);
        AntigravityStatusUsage? bridgeStatus = null;
        ExternalProviderBalance? bridgeBalance = null;
        if (isBridgeConfigured)
        {
            try
            {
                using var stream = new FileStream(
                    bridgePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var document = JsonDocument.Parse(stream);
                (bridgeStatus, bridgeBalance) = ParseBridgeStatus(
                    document.RootElement,
                    new DateTimeOffset(File.GetLastWriteTimeUtc(bridgePath), TimeSpan.Zero),
                    configEntry?.MonthlyBudget);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                // Ignore transient partial bridge writes.
            }
        }

        ExternalProviderBalance? remoteBalance = null;
        string? accountName = null;
        string? planType = bridgeStatus?.PlanType ?? configEntry?.PlanType;
        string? apiError = null;

        if (_enableNetworkPolling && hasApiKey)
        {
            var remoteResult = await FetchRemoteStatusAsync(
                spec,
                apiKey!,
                configEntry?.MonthlyBudget,
                cancellationToken);
            remoteBalance = remoteResult.Balance;
            accountName = remoteResult.AccountName;
            planType ??= remoteResult.PlanType;
            apiError = remoteResult.Error;
        }

        var effectiveBalance = remoteBalance ?? bridgeBalance;
        var quotas = bridgeStatus?.ModelQuotas ?? configEntry?.ModelQuotas;

        var models = AntigravityTranscriptParser.BuildModelUsage(
            localData.Records,
            today,
            quotas);

        // When no local turns or explicit model_scoped bridge exist yet, surface the provider's
        // flagship models whenever the provider is configured (or has balance/budget info) so the
        // user sees all model cards in the top area.
        var budgetWindow = effectiveBalance?.RemainingBudgetPercent is { } remainingBudget
            ? new RateLimitWindow(Math.Clamp(100 - remainingBudget, 0, 100), null, null)
            : bridgeStatus?.Weekly ?? bridgeStatus?.FiveHour;

        if (models.Count == 0 && (hasApiKey || isBridgeConfigured || effectiveBalance is not null))
        {
            models = spec.DefaultModels
                .Select(item => new AntigravityModelUsage(
                    ModelName: item.ModelId,
                    DisplayName: item.DisplayName,
                    TotalTokens: 0,
                    InputTokens: 0,
                    CacheReadTokens: 0,
                    OutputTokens: 0,
                    TodayTokens: 0,
                    SevenDayTokens: 0,
                    TurnCount: 0,
                    SharePercent: 0,
                    LastUsedAt: DateTimeOffset.MinValue,
                    QuotaWindow: budgetWindow))
                .ToArray();
        }
        else if (budgetWindow is not null)
        {
            models = models
                .Select(model => model.QuotaWindow is not null
                    ? model
                    : model with { QuotaWindow = budgetWindow })
                .ToArray();
        }

        var tokenUsage = localData.Records.Count > 0
            ? AntigravityTranscriptParser.BuildAccountUsage(localData.Records, now)
            : null;

        var isAvailable = hasApiKey ||
                          isBridgeConfigured ||
                          localData.Records.Count > 0 ||
                          effectiveBalance is not null;

        return new ExternalProviderSnapshot(
            Provider: spec.Id,
            Models: models,
            Balance: effectiveBalance,
            FiveHour: bridgeStatus?.FiveHour,
            Weekly: bridgeStatus?.Weekly ?? budgetWindow,
            TokenUsage: tokenUsage,
            Session: localData.Session,
            PlanType: planType,
            AccountName: accountName,
            Error: apiError,
            IsAvailable: isAvailable,
            HasApiKey: hasApiKey,
            IsBridgeConfigured: isBridgeConfigured,
            FetchedAt: now);
    }

    internal static (AntigravityStatusUsage Status, ExternalProviderBalance? Balance) ParseBridgeStatus(
        JsonElement root,
        DateTimeOffset fallbackTimestamp,
        decimal? fallbackMonthlyBudget = null)
    {
        var status = AntigravityStatusParser.Parse(root, fallbackTimestamp);
        ExternalProviderBalance? balance = null;

        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("balance", out var balanceElement) &&
                balanceElement.ValueKind == JsonValueKind.Object)
            {
                balance = ParseGenericBalanceObject(balanceElement, fallbackMonthlyBudget);
            }
            else if (root.TryGetProperty("balance_infos", out _))
            {
                balance = ParseDeepSeekBalance(root, fallbackMonthlyBudget);
            }
            else if (root.TryGetProperty("available_balance", out _) ||
                     (root.TryGetProperty("data", out var dataElement) &&
                      dataElement.ValueKind == JsonValueKind.Object &&
                      dataElement.TryGetProperty("available_balance", out _)))
            {
                balance = ParseKimiBalance(root, fallbackMonthlyBudget);
            }
        }

        return (status, balance);
    }

    public static ExternalProviderBalance? ParseDeepSeekBalance(
        JsonElement root,
        decimal? monthlyBudget = null)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var isAvailable = !root.TryGetProperty("is_available", out var availProp) ||
                          availProp.ValueKind != JsonValueKind.False;

        if (!root.TryGetProperty("balance_infos", out var infos) ||
            infos.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        ExternalProviderBalance? first = null;
        foreach (var item in infos.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var currency = TryGetString(item, "currency") ?? "CNY";
            var total = TryGetDecimal(item, "total_balance");
            if (total is null)
            {
                continue;
            }

            var granted = TryGetDecimal(item, "granted_balance");
            var toppedUp = TryGetDecimal(item, "topped_up_balance");
            var parsed = new ExternalProviderBalance(
                Currency: currency,
                TotalBalance: total.Value,
                GrantedBalance: granted,
                ToppedUpBalance: toppedUp,
                MonthlyBudget: monthlyBudget,
                IsAvailable: isAvailable);

            first ??= parsed;
            if (total.Value > 0)
            {
                return parsed;
            }
        }

        return first;
    }

    public static ExternalProviderBalance? ParseKimiBalance(
        JsonElement root,
        decimal? monthlyBudget = null)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var target = root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            ? data
            : root;

        var available = TryGetDecimal(target, "available_balance");
        if (available is null)
        {
            return null;
        }

        var voucher = TryGetDecimal(target, "voucher_balance");
        var cash = TryGetDecimal(target, "cash_balance");
        return new ExternalProviderBalance(
            Currency: TryGetString(target, "currency") ?? "CNY",
            TotalBalance: available.Value,
            GrantedBalance: voucher,
            ToppedUpBalance: cash,
            MonthlyBudget: monthlyBudget,
            IsAvailable: available.Value > 0);
    }

    public static (ExternalProviderBalance? Balance, string? AccountName, string? Status) ParseSiliconFlowUserInfo(
        JsonElement root,
        decimal? monthlyBudget = null)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return (null, null, null);
        }

        var target = root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            ? data
            : root;

        var total = TryGetDecimal(target, "totalBalance") ?? TryGetDecimal(target, "balance");
        var granted = TryGetDecimal(target, "balance");
        var charge = TryGetDecimal(target, "chargeBalance");
        var name = TryGetString(target, "name") ?? TryGetString(target, "email");
        var status = TryGetString(target, "status");

        var balance = total is null
            ? null
            : new ExternalProviderBalance(
                Currency: "CNY",
                TotalBalance: total.Value,
                GrantedBalance: granted,
                ToppedUpBalance: charge,
                MonthlyBudget: monthlyBudget,
                IsAvailable: !string.Equals(status, "blocked", StringComparison.OrdinalIgnoreCase));

        return (balance, name, status);
    }

    public static (string? KeyName, string? PlanType, bool IsActive) ParseGrokApiKeyInfo(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return (null, null, false);
        }

        var name = TryGetString(root, "name") ?? TryGetString(root, "redacted_api_key");
        var blocked = (root.TryGetProperty("api_key_blocked", out var b1) && b1.ValueKind == JsonValueKind.True) ||
                      (root.TryGetProperty("api_key_disabled", out var b2) && b2.ValueKind == JsonValueKind.True) ||
                      (root.TryGetProperty("team_blocked", out var b3) && b3.ValueKind == JsonValueKind.True);
        var plan = TryGetString(root, "team_id") is { Length: > 0 }
            ? "xAI API"
            : null;
        return (name, plan, !blocked);
    }

    private async Task<(ExternalProviderBalance? Balance, string? AccountName, string? PlanType, string? Error)>
        FetchRemoteStatusAsync(
            ExternalProviderSpec spec,
            string apiKey,
            decimal? monthlyBudget,
            CancellationToken cancellationToken)
    {
        try
        {
            switch (spec.Id)
            {
                case ExternalProviderId.DeepSeek:
                {
                    using var request = CreateBearerRequest("https://api.deepseek.com/user/balance", apiKey);
                    using var response = await _httpClient.SendAsync(request, cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        return (null, null, null, $"HTTP {(int)response.StatusCode}");
                    }

                    using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                    return (ParseDeepSeekBalance(doc.RootElement, monthlyBudget), null, "API", null);
                }

                case ExternalProviderId.Kimi:
                {
                    using var request = CreateBearerRequest("https://api.moonshot.cn/v1/users/me/balance", apiKey);
                    using var response = await _httpClient.SendAsync(request, cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        return (null, null, null, $"HTTP {(int)response.StatusCode}");
                    }

                    using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                    return (ParseKimiBalance(doc.RootElement, monthlyBudget), null, "API", null);
                }

                case ExternalProviderId.SiliconFlow:
                {
                    using var request = CreateBearerRequest("https://api.siliconflow.cn/v1/user/info", apiKey);
                    using var response = await _httpClient.SendAsync(request, cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        return (null, null, null, $"HTTP {(int)response.StatusCode}");
                    }

                    using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                    var (balance, name, _) = ParseSiliconFlowUserInfo(doc.RootElement, monthlyBudget);
                    return (balance, name, "API", null);
                }

                case ExternalProviderId.Grok:
                {
                    using var request = CreateBearerRequest("https://api.x.ai/v1/api-key", apiKey);
                    using var response = await _httpClient.SendAsync(request, cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        return (null, null, null, $"HTTP {(int)response.StatusCode}");
                    }

                    using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                    var (keyName, plan, _) = ParseGrokApiKeyInfo(doc.RootElement);
                    return (null, keyName, plan ?? "xAI API", null);
                }

                default:
                    return (null, null, "API", null);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return (null, null, null, exception.Message);
        }
    }

    private static HttpRequestMessage CreateBearerRequest(string url, string apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private Dictionary<ExternalProviderId, (IReadOnlyList<AntigravityTokenRecord> Records, AntigravitySessionState? Session)>
        ScanLocalTranscripts()
    {
        var recordsByProvider = AllSpecs.ToDictionary(
            spec => spec.Id,
            _ => new List<AntigravityTokenRecord>());
        var latestSessionByProvider = new Dictionary<ExternalProviderId, AntigravitySessionState>();

        // 1. Scan Claude Code projects directory for third-party model sessions (DeepSeek, Kimi, GLM, Qwen, Grok, SiliconFlow).
        var claudeProjects = Path.Combine(_claudeHome, "projects");
        if (Directory.Exists(claudeProjects))
        {
            ScanJsonlDirectory(claudeProjects, recordsByProvider, latestSessionByProvider);
        }

        // 2. Scan Kimi CLI directory (~/.kimi) if present.
        if (Directory.Exists(_kimiHome))
        {
            ScanJsonlDirectory(_kimiHome, recordsByProvider, latestSessionByProvider, ExternalProviderId.Kimi);
        }

        // 3. Scan %LOCALAPPDATA%\AiTokenMonitor\sessions\<provider> if present.
        var customSessionsRoot = Path.Combine(_bridgeDirectory, "sessions");
        if (Directory.Exists(customSessionsRoot))
        {
            foreach (var spec in AllSpecs)
            {
                var providerDir = Path.Combine(customSessionsRoot, spec.Key);
                if (Directory.Exists(providerDir))
                {
                    ScanJsonlDirectory(providerDir, recordsByProvider, latestSessionByProvider, spec.Id);
                }
            }
        }

        var result = new Dictionary<ExternalProviderId, (IReadOnlyList<AntigravityTokenRecord>, AntigravitySessionState?)>();
        foreach (var spec in AllSpecs)
        {
            var list = recordsByProvider[spec.Id];
            result[spec.Id] = (list, latestSessionByProvider.GetValueOrDefault(spec.Id));
        }

        return result;
    }

    private static void ScanJsonlDirectory(
        string directory,
        Dictionary<ExternalProviderId, List<AntigravityTokenRecord>> recordsByProvider,
        Dictionary<ExternalProviderId, AntigravitySessionState> latestSessionByProvider,
        ExternalProviderId? defaultProvider = null)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.AllDirectories))
            {
                try
                {
                    ParseThirdPartySessionFile(file, recordsByProvider, latestSessionByProvider, defaultProvider);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Skip locked files.
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Ignore inaccessible directories.
        }
    }

    internal static void ParseThirdPartySessionFile(
        string filePath,
        Dictionary<ExternalProviderId, List<AntigravityTokenRecord>> recordsByProvider,
        Dictionary<ExternalProviderId, AntigravitySessionState> latestSessionByProvider,
        ExternalProviderId? defaultProvider = null)
    {
        var fallbackTime = File.Exists(filePath)
            ? new DateTimeOffset(File.GetLastWriteTimeUtc(filePath), TimeSpan.Zero)
            : DateTimeOffset.UtcNow;
        using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lineIndex = 0;
        while (reader.ReadLine() is { } line)
        {
            lineIndex++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var model = TryExtractModelName(root);
                var providerId = MatchProviderByModel(model) ?? defaultProvider;
                if (providerId is null)
                {
                    continue;
                }

                if (!TryExtractTokenUsage(root, out var input, out var cacheRead, out var output))
                {
                    continue;
                }

                var total = input + cacheRead + output;
                if (total <= 0)
                {
                    continue;
                }

                var timestamp = TryExtractTimestamp(root) ?? fallbackTime;
                var recordId = TryGetString(root, "uuid") ??
                               (root.TryGetProperty("message", out var msgObj) && msgObj.ValueKind == JsonValueKind.Object
                                   ? TryGetString(msgObj, "id")
                                   : null) ??
                               $"{filePath}:{lineIndex}";
                var normalizedModel = NormalizeProviderModelName(providerId.Value, model);

                recordsByProvider[providerId.Value].Add(new AntigravityTokenRecord(
                    Key: $"{filePath}#{recordId}",
                    ConversationId: Path.GetFileNameWithoutExtension(filePath),
                    ModelName: normalizedModel,
                    Date: DateOnly.FromDateTime(timestamp.LocalDateTime),
                    InputTokens: input,
                    CacheReadTokens: cacheRead,
                    OutputTokens: output,
                    TotalTokens: total,
                    ObservedAt: timestamp));

                var contextTokens = input + cacheRead;
                if (!latestSessionByProvider.TryGetValue(providerId.Value, out var existingSession) ||
                    timestamp >= existingSession.ObservedAt)
                {
                    latestSessionByProvider[providerId.Value] = new AntigravitySessionState(
                        ConversationId: Path.GetFileNameWithoutExtension(filePath),
                        ModelName: normalizedModel,
                        ContextTokens: contextTokens,
                        OutputTokens: output,
                        TurnCount: recordsByProvider[providerId.Value].Count,
                        ObservedAt: timestamp);
                }
            }
            catch (JsonException)
            {
                // Ignore malformed JSONL lines.
            }
        }
    }

    public static ExternalProviderId? MatchProviderByModel(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return null;
        }

        var trimmed = modelName.Trim();
        foreach (var spec in AllSpecs)
        {
            foreach (var prefix in spec.ModelPrefixes)
            {
                if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return spec.Id;
                }
            }
        }

        return null;
    }

    public static string NormalizeProviderModelName(ExternalProviderId provider, string? rawModel)
    {
        var spec = GetSpec(provider);
        if (string.IsNullOrWhiteSpace(rawModel))
        {
            return spec.DefaultModels[0].DisplayName;
        }

        var trimmed = rawModel.Trim();
        foreach (var (modelId, displayName) in spec.DefaultModels)
        {
            if (trimmed.Equals(modelId, StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals(displayName, StringComparison.OrdinalIgnoreCase))
            {
                return displayName;
            }
        }

        return AntigravitySessionState.NormalizeModelDisplayName(trimmed) ?? trimmed;
    }

    private static string? ResolveApiKey(
        ExternalProviderSpec spec,
        ProviderConfigEntry? configEntry,
        string claudeHome,
        string kimiHome)
    {
        if (!string.IsNullOrWhiteSpace(configEntry?.ApiKey))
        {
            return configEntry.ApiKey.Trim();
        }

        foreach (var envName in spec.ApiKeyEnvVars)
        {
            var value = Environment.GetEnvironmentVariable(envName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        // Check ~/.claude/settings.json if the user configured Claude Code against a third-party base URL
        var claudeSettings = Path.Combine(claudeHome, "settings.json");
        if (File.Exists(claudeSettings))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(claudeSettings));
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("env", out var envObj) &&
                    envObj.ValueKind == JsonValueKind.Object)
                {
                    foreach (var envName in spec.ApiKeyEnvVars)
                    {
                        if (TryGetString(envObj, envName) is { Length: > 0 } directKey)
                        {
                            return directKey;
                        }
                    }

                    var baseUrl = TryGetString(envObj, "ANTHROPIC_BASE_URL");
                    var authToken = TryGetString(envObj, "ANTHROPIC_AUTH_TOKEN") ??
                                    TryGetString(envObj, "ANTHROPIC_API_KEY");
                    if (!string.IsNullOrWhiteSpace(baseUrl) &&
                        !string.IsNullOrWhiteSpace(authToken) &&
                        spec.BaseUrlDomains.Any(domain => baseUrl.Contains(domain, StringComparison.OrdinalIgnoreCase)))
                    {
                        return authToken;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                // Ignore unreadable settings.json.
            }
        }

        // Check ~/.kimi/config.json or ~/.kimi/credentials.json for Kimi CLI
        if (spec.Id == ExternalProviderId.Kimi && Directory.Exists(kimiHome))
        {
            foreach (var fileName in new[] { "config.json", "credentials.json" })
            {
                var path = Path.Combine(kimiHome, fileName);
                if (!File.Exists(path))
                {
                    continue;
                }

                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    var key = TryGetString(doc.RootElement, "api_key") ??
                              TryGetString(doc.RootElement, "apiKey") ??
                              TryGetString(doc.RootElement, "access_token");
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        return key;
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
                {
                    // Ignore unreadable Kimi CLI config.
                }
            }
        }

        return null;
    }

    private static Dictionary<string, ProviderConfigEntry> LoadProvidersConfig(string bridgeDirectory)
    {
        var result = new Dictionary<string, ProviderConfigEntry>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(bridgeDirectory, "providers.json");
        if (!File.Exists(path))
        {
            return result;
        }

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    result[prop.Name] = new ProviderConfigEntry(prop.Value.GetString(), null, null, null);
                }
                else if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    var apiKey = TryGetString(prop.Value, "api_key") ?? TryGetString(prop.Value, "apiKey");
                    var budget = TryGetDecimal(prop.Value, "monthly_budget") ?? TryGetDecimal(prop.Value, "monthlyBudget");
                    var plan = TryGetString(prop.Value, "plan_type") ?? TryGetString(prop.Value, "planType");
                    var parsedStatus = AntigravityStatusParser.Parse(prop.Value, DateTimeOffset.UtcNow);
                    result[prop.Name] = new ProviderConfigEntry(apiKey, budget, plan, parsedStatus.ModelQuotas);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // Ignore malformed providers.json.
        }

        return result;
    }

    private static ExternalProviderBalance? ParseGenericBalanceObject(
        JsonElement element,
        decimal? fallbackMonthlyBudget)
    {
        var total = TryGetDecimal(element, "total_balance") ??
                    TryGetDecimal(element, "available_balance") ??
                    TryGetDecimal(element, "balance");
        if (total is null)
        {
            return null;
        }

        var currency = TryGetString(element, "currency") ?? "CNY";
        var granted = TryGetDecimal(element, "granted_balance") ?? TryGetDecimal(element, "voucher_balance");
        var toppedUp = TryGetDecimal(element, "topped_up_balance") ?? TryGetDecimal(element, "cash_balance");
        var budget = TryGetDecimal(element, "monthly_budget") ?? fallbackMonthlyBudget;

        return new ExternalProviderBalance(
            Currency: currency,
            TotalBalance: total.Value,
            GrantedBalance: granted,
            ToppedUpBalance: toppedUp,
            MonthlyBudget: budget,
            IsAvailable: total.Value > 0);
    }

    private static string? TryExtractModelName(JsonElement root)
    {
        if (TryGetString(root, "model") is { Length: > 0 } directModel)
        {
            return directModel;
        }

        if (root.TryGetProperty("message", out var message) &&
            message.ValueKind == JsonValueKind.Object &&
            TryGetString(message, "model") is { Length: > 0 } messageModel)
        {
            return messageModel;
        }

        return null;
    }

    private static bool TryExtractTokenUsage(
        JsonElement root,
        out long inputTokens,
        out long cacheReadTokens,
        out long outputTokens)
    {
        inputTokens = 0;
        cacheReadTokens = 0;
        outputTokens = 0;

        var usage = root;
        if (root.TryGetProperty("message", out var message) &&
            message.ValueKind == JsonValueKind.Object &&
            message.TryGetProperty("usage", out var msgUsage) &&
            msgUsage.ValueKind == JsonValueKind.Object)
        {
            usage = msgUsage;
        }
        else if (root.TryGetProperty("usage", out var directUsage) &&
                 directUsage.ValueKind == JsonValueKind.Object)
        {
            usage = directUsage;
        }

        var input = TryGetInt64(usage, "input_tokens") ?? TryGetInt64(usage, "prompt_tokens") ?? 0;
        var cacheCreation = TryGetInt64(usage, "cache_creation_input_tokens") ?? 0;
        var cacheRead = TryGetInt64(usage, "cache_read_input_tokens") ??
                        TryGetInt64(usage, "prompt_cache_hit_tokens") ?? 0;
        var output = TryGetInt64(usage, "output_tokens") ?? TryGetInt64(usage, "completion_tokens") ?? 0;

        inputTokens = Math.Max(0, input + cacheCreation);
        cacheReadTokens = Math.Max(0, cacheRead);
        outputTokens = Math.Max(0, output);
        return inputTokens > 0 || cacheReadTokens > 0 || outputTokens > 0;
    }

    private static DateTimeOffset? TryExtractTimestamp(JsonElement root)
    {
        foreach (var name in new[] { "timestamp", "created_at", "time" })
        {
            if (TryGetString(root, name) is { } text &&
                DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var prop) &&
            prop.ValueKind == JsonValueKind.String)
        {
            var value = prop.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        return null;
    }

    private static decimal? TryGetDecimal(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var prop))
        {
            return null;
        }

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDecimal(out var number))
        {
            return number;
        }

        if (prop.ValueKind == JsonValueKind.String &&
            decimal.TryParse(prop.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static long? TryGetInt64(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var prop))
        {
            return null;
        }

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var number))
        {
            return number;
        }

        if (prop.ValueKind == JsonValueKind.String &&
            long.TryParse(prop.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private sealed record ProviderConfigEntry(
        string? ApiKey,
        decimal? MonthlyBudget,
        string? PlanType,
        IReadOnlyList<AntigravityModelQuota>? ModelQuotas);
}
