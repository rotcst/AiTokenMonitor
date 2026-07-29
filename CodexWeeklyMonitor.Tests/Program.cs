using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using CodexWeeklyMonitor;
using CodexWeeklyMonitor.Models;
using CodexWeeklyMonitor.Services;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using ContextMenu = System.Windows.Controls.ContextMenu;
using FontFamily = System.Windows.Media.FontFamily;
using Forms = System.Windows.Forms;
using MenuItem = System.Windows.Controls.MenuItem;
using Orientation = System.Windows.Controls.Orientation;
using RadioButton = System.Windows.Controls.RadioButton;
using ScrollBar = System.Windows.Controls.Primitives.ScrollBar;
using ToolTip = System.Windows.Controls.ToolTip;

var failed = 0;
using var staTestRunner = new StaTestRunner();

// Pin the UI language so string assertions are deterministic regardless of the OS locale or a
// previously persisted choice.
CodexWeeklyMonitor.Services.Loc.SetLanguage(CodexWeeklyMonitor.Services.AppLanguage.Chinese);

Run("当前协议：主额度桶的 primary 为周窗口", () =>
{
    using var document = JsonDocument.Parse("""
        {
          "rateLimits": {
            "limitId": "codex",
            "planType": "pro",
            "primary": {
              "usedPercent": 26,
              "windowDurationMins": 10080,
              "resetsAt": 1785280637
            },
            "secondary": null
          },
          "rateLimitsByLimitId": {
            "codex": {
              "limitId": "codex",
              "planType": "pro",
              "primary": {
                "usedPercent": 26,
                "windowDurationMins": 10080,
                "resetsAt": 1785280637
              }
            },
            "codex_other": {
              "limitId": "codex_other",
              "primary": {
                "usedPercent": 91,
                "windowDurationMins": 10080
              }
            }
          }
        }
        """);

    var quota = QuotaParser.ParseRateLimitResult(document.RootElement);
    Equal(26, quota.UsedPercent);
    Equal(74, quota.RemainingPercent);
    Equal(10080L, quota.WindowDurationMinutes);
    Equal("pro", quota.PlanType);
});

Run("兼容旧协议：secondary 为周窗口", () =>
{
    using var document = JsonDocument.Parse("""
        {
          "rateLimits": {
            "limitId": "codex",
            "primary": {
              "usedPercent": 80,
              "windowDurationMins": 300
            },
            "secondary": {
              "usedPercent": 42,
              "windowDurationMins": 10080,
              "resetsAt": 1785280637
            }
          }
        }
        """);

    var quota = QuotaParser.ParseRateLimitResult(document.RootElement);
    Equal(42, quota.UsedPercent);
    Equal(58, quota.RemainingPercent);
});

Run("不会把 5 小时窗口误报成周额度", () =>
{
    using var document = JsonDocument.Parse("""
        {
          "rateLimits": {
            "primary": {
              "usedPercent": 67,
              "windowDurationMins": 300
            }
          }
        }
        """);

    try
    {
        _ = QuotaParser.ParseRateLimitResult(document.RootElement);
        throw new Exception("预期解析器拒绝缺少周窗口的数据。");
    }
    catch (InvalidDataException)
    {
        // Expected.
    }
});

Run("异常百分比会被限制在 0-100", () =>
{
    using var document = JsonDocument.Parse("""
        {
          "rateLimits": {
            "primary": {
              "usedPercent": 140,
              "windowDurationMins": 10080
            }
          }
        }
        """);

    var quota = QuotaParser.ParseRateLimitResult(document.RootElement);
    Equal(100, quota.UsedPercent);
    Equal(0, quota.RemainingPercent);
});

Run("解析 5 小时、周额度、余额与可用重置次数", () =>
{
    using var document = JsonDocument.Parse("""
        {
          "rateLimits": {
            "planType": "pro",
            "primary": {
              "usedPercent": 35,
              "windowDurationMins": 300,
              "resetsAt": 1785280000
            },
            "secondary": {
              "usedPercent": 61,
              "windowDurationMins": 10080,
              "resetsAt": 1785290000
            },
            "credits": {
              "hasCredits": true,
              "unlimited": false,
              "balance": "12.50"
            }
          },
          "rateLimitResetCredits": {
            "availableCount": 2,
            "credits": []
          }
        }
        """);

    var limits = QuotaParser.ParseAccountRateLimits(document.RootElement);
    Equal(35, limits.FiveHour!.UsedPercent);
    Equal(65, limits.FiveHour.RemainingPercent);
    Equal(61, limits.Weekly!.UsedPercent);
    Equal("12.50", limits.Credits!.Balance);
    Equal<long?>(2, limits.AvailableResetCount);
});

Run("解析并归一化逐日 Token 历史", () =>
{
    using var document = JsonDocument.Parse("""
        {
          "summary": {
            "lifetimeTokens": 30199171066,
            "peakDailyTokens": 3600140432,
            "longestRunningTurnSec": 65190,
            "currentStreakDays": 1,
            "longestStreakDays": 30
          },
          "dailyUsageBuckets": [
            { "startDate": "2026-07-20", "tokens": 72000000 },
            { "startDate": "2026-07-18", "tokens": 3000000000 },
            { "startDate": "2026-07-20", "tokens": 24529 },
            { "startDate": "invalid", "tokens": 99 }
          ]
        }
        """);

    var usage = TokenUsageParser.Parse(document.RootElement);
    Equal<long?>(30199171066, usage.LifetimeTokens);
    Equal(2, usage.DailyUsage.Count);
    Equal(new DateOnly(2026, 7, 18), usage.DailyUsage[0].Date);
    Equal(72024529L, usage.LatestDay!.Tokens);
    Equal("302亿", TokenFormatter.Format(usage.LifetimeTokens));
    Equal("720万", TokenFormatter.Format(7_204_529));
});

Run("解析本机会话的实时 Token 事件", () =>
{
    var usage = LocalSessionTokenMonitor.ParseLatestFromText(
        """
        {"timestamp":"2026-07-22T06:19:50Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":1486341697,"cached_input_tokens":1470000000,"output_tokens":201228,"reasoning_output_tokens":90663,"total_tokens":1486542925},"last_token_usage":{"input_tokens":157910,"cached_input_tokens":152320,"output_tokens":286,"reasoning_output_tokens":63,"total_tokens":158196},"model_context_window":258400}}}
        """,
        DateTimeOffset.UnixEpoch) ?? throw new Exception("未解析到实时 Token 数据。");

    Equal(1_486_542_925L, usage.TotalTokens);
    Equal(158_196L, usage.LastTurnTokens);
    Equal(157_910L, usage.InputTokens);
    Equal(152_320L, usage.CachedInputTokens);
    Equal(286L, usage.OutputTokens);
    Equal(63L, usage.ReasoningOutputTokens);
    Equal<long?>(258_400L, usage.ModelContextWindow);

    var groupKey = LocalSessionTokenMonitor.ParseSessionGroupKeyFromText(
        """
        {"timestamp":"2026-07-22T06:48:30Z","type":"session_meta","payload":{"session_id":"logical-session","id":"fork-file","forked_from_id":"parent-file","dynamic_tools":[{"tools":[{"inputSchema":{"type":"object"}}]}]}}
        """);
    Equal("logical-session", groupKey);

    var desktopForkGroupKey = LocalSessionTokenMonitor.ParseSessionGroupKeyFromText(
        """
        {"timestamp":"2026-07-22T06:48:30Z","type":"session_meta","payload":{"session_id":"child-file","id":"child-file","forked_from_id":"parent-session","source":"vscode"}}
        """);
    Equal("parent-session", desktopForkGroupKey);
});

Run("Codex 本机统计同时读取 CLI 当日会话和桌面端归档会话", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "AiTokenMonitorTests", Guid.NewGuid().ToString("N"));
    var codexHome = Path.Combine(root, ".codex");
    var today = DateOnly.FromDateTime(DateTime.Now);
    var sessionDirectory = Path.Combine(
        codexHome,
        "sessions",
        today.ToString("yyyy", CultureInfo.InvariantCulture),
        today.ToString("MM", CultureInfo.InvariantCulture),
        today.ToString("dd", CultureInfo.InvariantCulture));
    var archiveDirectory = Path.Combine(codexHome, "archived_sessions");
    Directory.CreateDirectory(sessionDirectory);
    Directory.CreateDirectory(archiveDirectory);

    try
    {
        File.WriteAllText(
            Path.Combine(sessionDirectory, $"rollout-{today:yyyy-MM-dd}T09-00-00-cli-session.jsonl"),
            CreateCodexRollout("cli-session", null, "cli", 1_000));
        File.WriteAllText(
            Path.Combine(archiveDirectory, $"rollout-{today:yyyy-MM-dd}T10-00-00-desktop-session.jsonl"),
            CreateCodexRollout("desktop-session", null, "vscode", 2_000));
        File.WriteAllText(
            Path.Combine(archiveDirectory, $"rollout-{today:yyyy-MM-dd}T10-05-00-desktop-fork.jsonl"),
            CreateCodexRollout("desktop-fork", "desktop-session", "vscode", 1_500));

        using var signal = new ManualResetEventSlim();
        TodayTokenUsage? observed = null;
        using var monitor = new LocalSessionTokenMonitor(codexHome);
        monitor.UsageUpdated += usage =>
        {
            observed = usage;
            if (usage.Tokens == 3_000 && usage.SessionCount == 2)
            {
                signal.Set();
            }
        };
        monitor.Start();

        if (!signal.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new Exception($"Codex 本机统计没有读取到桌面归档会话，实际 {observed?.Tokens}。");
        }
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
});

Run("Codex 本机统计会补扫启动后新建的会话目录", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "AiTokenMonitorTests", Guid.NewGuid().ToString("N"));
    var codexHome = Path.Combine(root, ".codex");
    var today = DateOnly.FromDateTime(DateTime.Now);
    var sessionDirectory = Path.Combine(
        codexHome,
        "sessions",
        today.ToString("yyyy", CultureInfo.InvariantCulture),
        today.ToString("MM", CultureInfo.InvariantCulture),
        today.ToString("dd", CultureInfo.InvariantCulture));

    try
    {
        using var signal = new ManualResetEventSlim();
        TodayTokenUsage? observed = null;
        using var monitor = new LocalSessionTokenMonitor(codexHome);
        monitor.UsageUpdated += usage =>
        {
            observed = usage;
            if (usage.Tokens == 4_200)
            {
                signal.Set();
            }
        };
        monitor.Start();

        Directory.CreateDirectory(sessionDirectory);
        File.WriteAllText(
            Path.Combine(sessionDirectory, $"rollout-{today:yyyy-MM-dd}T11-00-00-late-session.jsonl"),
            CreateCodexRollout("late-session", null, "cli", 4_200));

        if (!signal.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new Exception($"Codex 本机统计没有补扫到启动后创建的会话，实际 {observed?.Tokens}。");
        }
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
});

Run("Codex 跨午夜会话只计入当天新增 Token", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "AiTokenMonitorTests", Guid.NewGuid().ToString("N"));
    var codexHome = Path.Combine(root, ".codex");
    var today = DateOnly.FromDateTime(DateTime.Now);
    var yesterday = today.AddDays(-1);
    var sessionDirectory = Path.Combine(
        codexHome,
        "sessions",
        yesterday.ToString("yyyy", CultureInfo.InvariantCulture),
        yesterday.ToString("MM", CultureInfo.InvariantCulture),
        yesterday.ToString("dd", CultureInfo.InvariantCulture));
    Directory.CreateDirectory(sessionDirectory);

    try
    {
        var yesterdayTime = new DateTimeOffset(
            yesterday.ToDateTime(new TimeOnly(23, 50)),
            TimeZoneInfo.Local.GetUtcOffset(yesterday.ToDateTime(new TimeOnly(23, 50))));
        var todayTime = new DateTimeOffset(
            today.ToDateTime(new TimeOnly(0, 10)),
            TimeZoneInfo.Local.GetUtcOffset(today.ToDateTime(new TimeOnly(0, 10))));
        var meta = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["timestamp"] = yesterdayTime.ToUniversalTime().ToString("O"),
            ["type"] = "session_meta",
            ["payload"] = new Dictionary<string, object?>
            {
                ["session_id"] = "overnight-session",
                ["id"] = "overnight-session",
                ["source"] = "cli",
            },
        });
        var path = Path.Combine(
            sessionDirectory,
            $"rollout-{yesterday:yyyy-MM-dd}T23-50-00-overnight-session.jsonl");
        File.WriteAllText(
            path,
            meta + Environment.NewLine +
            CreateCodexTokenEvent(yesterdayTime, 10_000, 10_000) + Environment.NewLine +
            CreateCodexTokenEvent(todayTime, 10_600, 600) + Environment.NewLine);
        File.SetLastWriteTime(path, DateTime.Now);

        using var signal = new ManualResetEventSlim();
        TodayTokenUsage? observed = null;
        using var monitor = new LocalSessionTokenMonitor(codexHome);
        monitor.UsageUpdated += usage =>
        {
            observed = usage;
            if (usage.Tokens == 600)
            {
                signal.Set();
            }
        };
        monitor.Start();

        if (!signal.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new Exception($"跨午夜会话应只计入 600 Token，实际 {observed?.Tokens}。");
        }
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
});

Run("今日 Token 跨会话单调累加并在换日后归零", () =>
{
    var date = new DateOnly(2026, 7, 22);
    var accumulator = new LocalSessionTokenMonitor.TodayTokenAccumulator();
    accumulator.Reset(date);

    accumulator.Update("logical-session-a", CreateLiveUsage(1_500_000_000, date));
    Equal(1_500_000_000L, accumulator.Snapshot().Tokens);

    accumulator.Update("logical-session-b", CreateLiveUsage(100_000_000, date));
    Equal(1_600_000_000L, accumulator.Snapshot().Tokens);
    Equal(2, accumulator.Snapshot().SessionCount);

    accumulator.Update("logical-session-a", CreateLiveUsage(1_400_000_000, date));
    Equal(1_600_000_000L, accumulator.Snapshot().Tokens);

    accumulator.Update("logical-session-a", CreateLiveUsage(1_550_000_000, date));
    Equal(1_650_000_000L, accumulator.Snapshot().Tokens);

    accumulator.Reset(date.AddDays(1));
    Equal(0L, accumulator.Snapshot().Tokens);
    Equal(0, accumulator.Snapshot().SessionCount);
});

Run("当天服务端历史与本机实时用量采用较新较大的值", () =>
{
    var today = new DateOnly(2026, 7, 22);
    var serverUsage = new AccountTokenUsage(
        LifetimeTokens: 30_700_000_000,
        PeakDailyTokens: 3_600_000_000,
        LongestRunningTurnSeconds: null,
        CurrentStreakDays: null,
        LongestStreakDays: null,
        DailyUsage:
        [
            new DailyTokenUsage(today.AddDays(-1), 600_000_000),
            new DailyTokenUsage(today, 500_000_000),
        ],
        FetchedAt: DateTimeOffset.Now);
    var localUsage = new TodayTokenUsage(
        today,
        Tokens: 1_600_000_000,
        SessionCount: 3,
        UpdatedAt: DateTimeOffset.Now);

    var localAhead = TokenUsageReconciler.Reconcile(serverUsage, localUsage, today);
    Equal(500_000_000L, localAhead.ServerTodayTokens);
    Equal(1_600_000_000L, localAhead.EffectiveTodayTokens);
    Equal(true, localAhead.LocalRealtimeApplied);
    Equal(1_600_000_000L, localAhead.Usage.LatestDay!.Tokens);
    Equal<long?>(31_800_000_000L, localAhead.Usage.LifetimeTokens);
    Equal<long?>(3_600_000_000L, localAhead.Usage.PeakDailyTokens);

    var serverAheadUsage = serverUsage with
    {
        DailyUsage =
        [
            new DailyTokenUsage(today.AddDays(-1), 600_000_000),
            new DailyTokenUsage(today, 1_700_000_000),
        ],
    };
    var serverAhead = TokenUsageReconciler.Reconcile(serverAheadUsage, localUsage, today);
    Equal(1_700_000_000L, serverAhead.EffectiveTodayTokens);
    Equal(false, serverAhead.LocalRealtimeApplied);
    Equal(1_700_000_000L, serverAhead.Usage.LatestDay!.Tokens);
});

Run("解析 Codex 官方用量接口的周额度、分模型额度、余额与限额提示", () =>
{
    using var document = JsonDocument.Parse("""
        {
          "email": "someone@example.com",
          "plan_type": "pro",
          "rate_limit": {
            "allowed": false,
            "limit_reached": true,
            "primary_window": {
              "used_percent": 100,
              "limit_window_seconds": 604800,
              "reset_after_seconds": 459689,
              "reset_at": 1785645333
            },
            "secondary_window": null
          },
          "additional_rate_limits": [
            {
              "limit_name": "GPT-5.3-Codex-Spark",
              "metered_feature": "codex_bengalfox",
              "rate_limit": {
                "allowed": true,
                "limit_reached": false,
                "primary_window": {
                  "used_percent": 12,
                  "limit_window_seconds": 604800,
                  "reset_at": 1785790445
                }
              }
            }
          ],
          "credits": {
            "has_credits": false,
            "unlimited": false,
            "overage_limit_reached": false,
            "balance": "0",
            "approx_local_messages": [0, 0],
            "approx_cloud_messages": [3, 7]
          },
          "spend_control": { "reached": false },
          "rate_limit_upsell": {
            "title": "You're out of Codex messages",
            "description": "Your rate limit resets on {time}.",
            "reset_at": 1785645333
          }
        }
        """);

    var (limits, detail) = CodexApiParser.ParseUsage(document.RootElement, DateTimeOffset.UnixEpoch, 2);
    Equal<RateLimitWindow?>(null, limits.FiveHour);
    Equal(100, limits.Weekly!.UsedPercent);
    Equal(0, limits.Weekly.RemainingPercent);
    Equal(10_080L, limits.Weekly.DurationMinutes!.Value);
    Equal("pro", limits.PlanType);
    Equal<long?>(2, limits.AvailableResetCount);

    Equal("someone@example.com", detail.Email);
    Equal<bool?>(false, detail.RateLimitAllowed);
    Equal<bool?>(true, detail.LimitReached);
    Equal(1, detail.ModelLimits.Count);
    Equal("GPT-5.3-Codex-Spark", detail.ModelLimits[0].Name);
    Equal(12, detail.ModelLimits[0].Window.UsedPercent);
    Equal("0", detail.Credits!.Balance);
    Equal("3 – 7 条", string.Join("", detail.Credits.ApproxCloudMessages.Count >= 2
        ? $"{detail.Credits.ApproxCloudMessages[0]} – {detail.Credits.ApproxCloudMessages[1]} 条"
        : ""));

    // The server leaves a {time} placeholder for the client to fill in.
    Equal(true, detail.LimitDescription!.Contains("月", StringComparison.Ordinal));
    Equal(false, detail.LimitDescription.Contains("{time}", StringComparison.Ordinal));
});

Run("不会把 Codex 的 5 小时窗口误报成周额度", () =>
{
    using var document = JsonDocument.Parse("""
        {
          "rate_limit": {
            "primary_window": { "used_percent": 67, "limit_window_seconds": 18000 }
          }
        }
        """);

    var (limits, _) = CodexApiParser.ParseUsage(document.RootElement, DateTimeOffset.UnixEpoch, null);
    Equal(67, limits.FiveHour!.UsedPercent);
    Equal<RateLimitWindow?>(null, limits.Weekly);
});

Run("解析 Codex 账号统计的 Token 历史与使用画像", () =>
{
    using var document = JsonDocument.Parse("""
        {
          "profile": { "username": "someone", "display_name": "S" },
          "stats": {
            "lifetime_tokens": 32831840132,
            "peak_daily_tokens": 3600140432,
            "current_streak_days": 1,
            "longest_streak_days": 30,
            "total_threads": 215,
            "longest_running_turn_sec": 65190,
            "fast_mode_usage_percentage": 20.73,
            "total_skills_used": 76,
            "unique_skills_used": 14,
            "most_used_reasoning_effort": "max",
            "most_used_reasoning_effort_percentage": 58.68,
            "daily_usage_buckets": [
              { "start_date": "2026-07-20", "tokens": 72000000 },
              { "start_date": "2026-07-18", "tokens": 3000000000 },
              { "start_date": "2026-07-20", "tokens": 24529 },
              { "start_date": "bad", "tokens": 99 }
            ]
          }
        }
        """);

    var (usage, stats) = CodexApiParser.ParseProfile(document.RootElement, DateTimeOffset.UnixEpoch);
    Equal<long?>(32831840132, usage.LifetimeTokens);
    Equal<long?>(65190, usage.LongestRunningTurnSeconds);
    Equal(2, usage.DailyUsage.Count);
    Equal(new DateOnly(2026, 7, 18), usage.DailyUsage[0].Date);
    Equal(72_024_529L, usage.LatestDay!.Tokens);

    Equal("S", stats.DisplayName);
    Equal<long?>(215, stats.TotalThreads);
    Equal<long?>(14, stats.UniqueSkillsUsed);
    Equal("max", stats.MostUsedReasoningEffort);
});

Run("CLAUDE_CONFIG_DIR 指向的目录同时用于凭据和会话记录", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "AiTokenMonitorTests", Guid.NewGuid().ToString("N"));
    var configDir = Path.Combine(root, "custom-claude");
    var projectDirectory = Path.Combine(configDir, "projects", "sample");
    Directory.CreateDirectory(projectDirectory);

    // A terminal CLI user can relocate the whole config directory; both halves must follow it.
    File.WriteAllText(
        Path.Combine(configDir, ".credentials.json"),
        """
        {"claudeAiOauth":{"accessToken":"sk-ant-oat-cli","expiresAt":4102444800000,"subscriptionType":"max"}}
        """);
    var now = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture);
    File.WriteAllText(
        Path.Combine(projectDirectory, "session.jsonl"),
        """
        {"type":"assistant","uuid":"cli-1","timestamp":"@","message":{"id":"m-cli","model":"claude-sonnet-5","usage":{"input_tokens":100,"output_tokens":20,"cache_read_input_tokens":50}}}
        """.Replace("@", now));

    var previous = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
    try
    {
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", configDir);
        Equal(configDir, ClaudePaths.ResolveHome());

        var token = ClaudeCredentialStore.ResolveAll().FirstOrDefault(t => t.Source.Contains(configDir))
            ?? throw new Exception("没有从 CLAUDE_CONFIG_DIR 读取到 CLI 凭据。");
        Equal("sk-ant-oat-cli", token.AccessToken);

        using var signal = new ManualResetEventSlim();
        ClaudeUsageSnapshot? observed = null;
        using var monitor = new ClaudeUsageMonitor(
            claudeHome: null,
            bridgeDirectory: Path.Combine(root, "bridge"),
            usageClient: null,
            enableQuotaPolling: false);
        monitor.UsageUpdated += snapshot =>
        {
            observed = snapshot;
            if (snapshot.TokenUsage is not null && snapshot.Session is not null)
            {
                signal.Set();
            }
        };
        monitor.Start();
        if (!signal.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new Exception("监控器没有读取 CLAUDE_CONFIG_DIR 下的会话记录。");
        }

        Equal<long?>(170L, observed!.TokenUsage!.LifetimeTokens);
        Equal("Sonnet 5", observed.ModelName);
    }
    finally
    {
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", previous);
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
});

Run("CODEX_HOME 指向的目录用于 Codex 凭据", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "AiTokenMonitorTests", Guid.NewGuid().ToString("N"));
    var codexHome = Path.Combine(root, "custom-codex");
    Directory.CreateDirectory(codexHome);

    var payload = Convert.ToBase64String(
        System.Text.Encoding.UTF8.GetBytes("""{"exp":4102444800}"""))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    File.WriteAllText(
        Path.Combine(codexHome, "auth.json"),
        """
        {"auth_mode":"chatgpt","tokens":{"access_token":"header.@.sig","account_id":"acct-cli"}}
        """.Replace("@", payload));

    var previous = Environment.GetEnvironmentVariable("CODEX_HOME");
    try
    {
        Environment.SetEnvironmentVariable("CODEX_HOME", codexHome);
        var credentials = CodexAuthStore.Resolve()
            ?? throw new Exception("没有从 CODEX_HOME 读取到 Codex 凭据。");
        Equal("acct-cli", credentials.AccountId);
        Equal(false, credentials.IsExpired);
    }
    finally
    {
        Environment.SetEnvironmentVariable("CODEX_HOME", previous);
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
});

Run("从 auth.json 读取 Codex 凭据并判断过期", () =>
{
    // A JWT whose payload carries only an exp far in the past.
    var payload = Convert.ToBase64String(
        System.Text.Encoding.UTF8.GetBytes("""{"exp":1000000000}"""))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    var token = $"header.{payload}.signature";

    using var document = JsonDocument.Parse($$"""
        {
          "auth_mode": "chatgpt",
          "tokens": { "access_token": "{{token}}", "account_id": "acct-1" }
        }
        """);

    var credentials = CodexAuthStore.Parse(document.RootElement, "fixture")
        ?? throw new Exception("未解析出 Codex 凭据。");
    Equal("acct-1", credentials.AccountId);
    Equal("chatgpt", credentials.AuthMode);
    Equal(true, credentials.IsExpired);
    Equal(DateTimeOffset.FromUnixTimeSeconds(1000000000), credentials.ExpiresAt);
});

Run("限流后按 Retry-After 退避，成功后恢复正常间隔", () =>
{
    var start = DateTimeOffset.UnixEpoch;
    var throttle = new PollThrottle(TimeSpan.FromSeconds(55), TimeSpan.FromMinutes(15));

    Equal(true, throttle.TryAcquire(start));
    // A second caller on the same tick is what produced the duplicate 429s.
    Equal(false, throttle.TryAcquire(start));
    Equal(false, throttle.TryAcquire(start.AddSeconds(54)));

    throttle.ReportThrottled(start.AddSeconds(1), TimeSpan.FromMinutes(5));
    Equal(false, throttle.TryAcquire(start.AddMinutes(4)));
    Equal(true, throttle.TryAcquire(start.AddMinutes(6)));

    // Without a hint the penalty doubles, and is capped.
    var doubling = new PollThrottle(TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(2));
    doubling.ReportThrottled(start, null);
    Equal(TimeSpan.FromSeconds(60), doubling.CurrentBackoff);
    doubling.ReportThrottled(start, null);
    Equal(TimeSpan.FromSeconds(120), doubling.CurrentBackoff);
    doubling.ReportThrottled(start, null);
    Equal(TimeSpan.FromMinutes(2), doubling.CurrentBackoff);

    doubling.ReportSuccess(start);
    Equal(TimeSpan.Zero, doubling.CurrentBackoff);
});

Run("解析 Claude 官方用量接口的 5 小时、周额度、模型分桶和用量额度", () =>
{
    var usage = ClaudeUsageClient.ParsePayload(
        """
        {
          "five_hour": { "utilization": 17.0, "resets_at": "2026-07-28T00:20:00.733916+00:00" },
          "seven_day": { "utilization": 63.4, "resets_at": "2026-08-02T16:00:00.733941+00:00" },
          "seven_day_opus": { "utilization": 41.2, "resets_at": "2026-08-02T16:00:00+00:00" },
          "seven_day_sonnet": null,
          "extra_usage": {
            "is_enabled": true,
            "monthly_limit": 5000,
            "used_credits": 450.0,
            "utilization": 9.0,
            "currency": "USD",
            "decimal_places": 2,
            "disabled_reason": null
          },
          "limits": [
            { "kind": "session", "group": "session", "percent": 17, "resets_at": null, "scope": null },
            { "kind": "weekly_all", "group": "weekly", "percent": 63, "resets_at": null, "scope": null },
            {
              "kind": "weekly_scoped",
              "group": "weekly",
              "percent": 8,
              "resets_at": "2026-08-02T16:00:00+00:00",
              "scope": { "model": { "display_name": "Fable 5" } }
            }
          ]
        }
        """,
        DateTimeOffset.UnixEpoch,
        "pro");

    Equal(17, usage.FiveHour!.UsedPercent);
    Equal(83, usage.FiveHour.RemainingPercent);
    Equal(63, usage.Weekly!.UsedPercent);
    Equal(
        DateTimeOffset.Parse("2026-08-02T16:00:00.733941Z").ToUniversalTime(),
        usage.Weekly.ResetsAt!.Value.ToUniversalTime());
    Equal("pro", usage.SubscriptionType);

    Equal(2, usage.ScopedLimits.Count);
    Equal("Opus 周额度", usage.ScopedLimits[0].DisplayName);
    Equal(41, usage.ScopedLimits[0].UsedPercent);
    Equal("Fable 5 周额度", usage.ScopedLimits[1].DisplayName);
    Equal(8, usage.ScopedLimits[1].UsedPercent);

    Equal(true, usage.ExtraUsage!.IsEnabled);
    Equal<decimal?>(4.5m, usage.ExtraUsage.UsedAmount);
    Equal<decimal?>(50m, usage.ExtraUsage.LimitAmount);
    Equal<int?>(9, usage.ExtraUsage.UsedPercent);
});

Run("解析 Claude 预付点数余额与自动充值", () =>
{
    var usage = ClaudeUsageClient.ParsePayload(
        """
        {
          "five_hour": { "utilization": 10, "resets_at": null },
          "extra_usage": { "is_enabled": true, "monthly_limit": 5000, "used_credits": 450, "utilization": 9, "currency": "USD", "decimal_places": 2 },
          "spend": {
            "used": { "amount_minor": 450, "currency": "USD", "exponent": 2 },
            "limit": { "amount_minor": 5000, "currency": "USD", "exponent": 2 },
            "balance": { "amount_minor": 9551, "currency": "USD", "exponent": 2 },
            "auto_reload": { "enabled": true, "threshold": { "amount_minor": 1000, "exponent": 2 }, "amount": { "amount_minor": 5000, "exponent": 2 } },
            "can_purchase_credits": true
          }
        }
        """,
        DateTimeOffset.UnixEpoch,
        null);

    // The wallet is the billing page's "现在余额"; extra_usage is this period's spend.
    Equal<decimal?>(95.51m, usage.Wallet!.Balance);
    Equal("USD", usage.Wallet.Currency);
    Equal(true, usage.Wallet.AutoReloadEnabled);
    Equal<decimal?>(10m, usage.Wallet.AutoReloadThreshold);
    Equal<decimal?>(50m, usage.Wallet.AutoReloadAmount);
    Equal(true, usage.Wallet.CanPurchase);
    Equal<decimal?>(4.5m, usage.ExtraUsage!.UsedAmount);
});

Run("没有钱包的账号不会显示空的点数余额", () =>
{
    var usage = ClaudeUsageClient.ParsePayload(
        """
        {
          "five_hour": { "utilization": 10, "resets_at": null },
          "spend": { "balance": null, "auto_reload": null, "can_purchase_credits": false }
        }
        """,
        DateTimeOffset.UnixEpoch,
        null);

    Equal<ClaudeCreditWallet?>(null, usage.Wallet);
});

Run("手动刷新可以跳过常规间隔，但不能跳过限流惩罚", () =>
{
    var start = DateTimeOffset.UnixEpoch;
    var throttle = new PollThrottle(TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(15));

    Equal(true, throttle.TryAcquire(start));
    Equal(false, throttle.TryAcquire(start.AddSeconds(5)));
    // A user pressing refresh should not have to wait out the routine spacing.
    Equal(true, throttle.TryAcquire(start.AddSeconds(5), force: true));

    throttle.ReportThrottled(start.AddSeconds(6), TimeSpan.FromMinutes(10));
    Equal(false, throttle.TryAcquire(start.AddMinutes(1), force: true));
    Equal(TimeSpan.FromMinutes(10), throttle.RemainingPenalty(start.AddSeconds(6)));
    Equal(true, throttle.TryAcquire(start.AddMinutes(11), force: true));
});

Run("Claude 用量接口返回空额度时报错而不是显示 0", () =>
{
    try
    {
        _ = ClaudeUsageClient.ParsePayload(
            """{"five_hour":null,"seven_day":null,"limits":[]}""",
            DateTimeOffset.UnixEpoch,
            null);
        throw new Exception("预期解析器拒绝没有任何额度窗口的数据。");
    }
    catch (ClaudeUsageException exception)
    {
        Equal(ClaudeUsageFailure.Protocol, exception.Failure);
    }
});

Run("从 CLI 凭据和桌面端令牌缓存解析 Claude 访问令牌", () =>
{
    using var cliDocument = JsonDocument.Parse("""
        {
          "claudeAiOauth": {
            "accessToken": "sk-ant-oat-cli",
            "refreshToken": "sk-ant-ort-cli",
            "expiresAt": 4102444800000,
            "subscriptionType": "max"
          }
        }
        """);
    var cliToken = ClaudeCredentialStore.ParseCliCredentials(cliDocument.RootElement, "fixture")
        ?? throw new Exception("未从 CLI 凭据解析出访问令牌。");
    Equal("sk-ant-oat-cli", cliToken.AccessToken);
    Equal("max", cliToken.SubscriptionType);
    Equal(false, cliToken.IsExpired);

    var desktopTokens = ClaudeCredentialStore.ParseDesktopTokenCache(
        """
        {
          "acct:device:https://mcp.example.com:tools": { "token": "not-anthropic" },
          "acct:device:https://api.anthropic.com:user:inference user:profile": {
            "token": "sk-ant-oat-desktop",
            "expiresAt": 1000,
            "subscriptionType": "pro"
          }
        }
        """,
        "fixture");
    Equal(1, desktopTokens.Count);
    Equal("sk-ant-oat-desktop", desktopTokens[0].AccessToken);
    Equal("pro", desktopTokens[0].SubscriptionType);
    Equal(true, desktopTokens[0].IsExpired);
});

Run("自动发现并解密 Microsoft Store Claude 桌面凭据", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "AiTokenMonitorTests", Guid.NewGuid().ToString("N"));
    var localRoot = Path.Combine(root, "Local");
    var roamingRoot = Path.Combine(root, "Roaming");
    var claudeHome = Path.Combine(root, "ClaudeHome");
    var desktopDirectory = Path.Combine(
        localRoot,
        "Packages",
        "Claude_test",
        "LocalCache",
        "Roaming",
        "Claude");
    Directory.CreateDirectory(desktopDirectory);
    Directory.CreateDirectory(claudeHome);

    try
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var protectedKey = ProtectedData.Protect(key, optionalEntropy: null, DataProtectionScope.CurrentUser);
        var keyBlob = new byte[5 + protectedKey.Length];
        Encoding.ASCII.GetBytes("DPAPI").CopyTo(keyBlob, 0);
        protectedKey.CopyTo(keyBlob, 5);
        File.WriteAllText(
            Path.Combine(desktopDirectory, "Local State"),
            JsonSerializer.Serialize(new
            {
                os_crypt = new { encrypted_key = Convert.ToBase64String(keyBlob) },
            }));

        const string expectedToken = "sk-ant-oat-msix-fixture";
        var plaintext = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["acct:device:https://api.anthropic.com:user:inference user:profile"] = new
            {
                token = expectedToken,
                expiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(),
                subscriptionType = "max",
            },
        });
        File.WriteAllText(
            Path.Combine(desktopDirectory, "config.json"),
            JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["oauth:tokenCacheV2"] = EncryptDesktopTokenCache(plaintext, key),
            }));

        var discovered = ClaudeCredentialStore.ResolveDesktopDirectories(
            localApplicationData: localRoot,
            roamingApplicationData: roamingRoot);
        if (!discovered.Contains(desktopDirectory, StringComparer.OrdinalIgnoreCase))
        {
            throw new Exception("没有发现 Microsoft Store Claude 的 LocalCache\\Roaming 凭据目录。");
        }

        var resolved = ClaudeCredentialStore.ResolveAll(
            claudeHome,
            desktopDirectory: null,
            localApplicationData: localRoot,
            roamingApplicationData: roamingRoot);
        var token = resolved.SingleOrDefault(item => item.AccessToken == expectedToken)
            ?? throw new Exception("没有从 Microsoft Store Claude 凭据目录解密出访问令牌。");
        Equal("max", token.SubscriptionType);
        Equal(false, token.IsExpired);
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
});

Run("陈旧的 Claude 令牌返回 401 时自动改用下一个凭据", () =>
{
    var requested = new List<string>();
    var handler = new StubHttpMessageHandler(request =>
    {
        var token = request.Headers.Authorization!.Parameter!;
        requested.Add(token);
        return token == "good"
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"five_hour":{"utilization":5,"resets_at":null}}"""),
            }
            : new HttpResponseMessage(HttpStatusCode.Unauthorized);
    });

    using var client = new ClaudeUsageClient(
        () =>
        [
            new ClaudeOAuthToken("stale", null, null, "cache-v1"),
            new ClaudeOAuthToken("good", null, "pro", "cache-v2"),
        ],
        handler);

    var usage = client.FetchAsync(CancellationToken.None).GetAwaiter().GetResult();
    Equal(5, usage.FiveHour!.UsedPercent);
    Equal("pro", usage.SubscriptionType);
    Equal(2, requested.Count);

    // The working credential is remembered, so later refreshes skip the dead one.
    requested.Clear();
    _ = client.FetchAsync(CancellationToken.None).GetAwaiter().GetResult();
    Equal(1, requested.Count);
    Equal("good", requested[0]);
});

Run("所有 Claude 凭据都失效时报告需要重新登录", () =>
{
    using var client = new ClaudeUsageClient(
        () => [new ClaudeOAuthToken("stale", null, null, "cache-v1")],
        new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));

    try
    {
        _ = client.FetchAsync(CancellationToken.None).GetAwaiter().GetResult();
        throw new Exception("预期在所有凭据失效时抛出异常。");
    }
    catch (ClaudeUsageException exception)
    {
        Equal(ClaudeUsageFailure.Unauthorized, exception.Failure);
    }
});

Run("解析 Claude 官方状态栏的 5 小时、7 天额度和上下文", () =>
{
    using var document = JsonDocument.Parse("""
        {
          "observed_at": "2026-07-22T10:15:30Z",
          "model": { "id": "claude-opus-4-8", "display_name": "Opus" },
          "context_window": { "used_percentage": 37.4 },
          "rate_limits": {
            "five_hour": { "used_percentage": 23.5, "resets_at": 1784721600 },
            "seven_day": { "used_percentage": 41.2, "resets_at": 1785153600 }
          }
        }
        """);

    var status = ClaudeStatusParser.Parse(document.RootElement, DateTimeOffset.UnixEpoch);
    Equal(24, status.FiveHour!.UsedPercent);
    Equal(76, status.FiveHour.RemainingPercent);
    Equal(41, status.Weekly!.UsedPercent);
    Equal("Opus", status.ModelName);
    Equal<int?>(37, status.ContextUsedPercent);
    Equal(DateTimeOffset.Parse("2026-07-22T10:15:30Z"), status.ObservedAt);
});

Run("解析 Claude 本机会话 Token 并按消息去重生成历史", () =>
{
    var records = ClaudeTranscriptParser.ParseText(
        """
        {"type":"assistant","uuid":"a1","timestamp":"2026-07-21T12:00:00+09:00","message":{"id":"msg_1","usage":{"input_tokens":100,"output_tokens":20,"cache_creation_input_tokens":30,"cache_read_input_tokens":50}}}
        {"type":"assistant","uuid":"a1-copy","timestamp":"2026-07-21T12:00:01+09:00","message":{"id":"msg_1","usage":{"input_tokens":100,"output_tokens":20,"cache_creation_input_tokens":30,"cache_read_input_tokens":50}}}
        {"type":"assistant","uuid":"a2","timestamp":"2026-07-22T12:00:00+09:00","message":{"id":"msg_2","usage":{"input_tokens":500,"output_tokens":100,"cache_creation_input_tokens":200,"cache_read_input_tokens":300}}}
        """,
        "fixture.jsonl",
        DateTimeOffset.UnixEpoch);
    Equal(2, records.Count);

    var usage = ClaudeTranscriptParser.BuildAccountUsage(records, DateTimeOffset.Now);
    Equal<long?>(1_300L, usage.LifetimeTokens);
    Equal(2, usage.DailyUsage.Count);
    Equal(200L, usage.DailyUsage[0].Tokens);
    Equal(1_100L, usage.DailyUsage[1].Tokens);
    Equal<long?>(2L, usage.CurrentStreakDays);
    Equal<long?>(2L, usage.LongestStreakDays);
});

Run("从会话记录读取当前模型与上下文占用", () =>
{
    var state = ClaudeTranscriptParser.TryParseSessionState(
        """
        {"type":"assistant","effort":"high","version":"2.1.219","timestamp":"2026-07-28T06:30:00+09:00","message":{"model":"claude-opus-5","usage":{"input_tokens":2,"cache_creation_input_tokens":1065,"cache_read_input_tokens":431052,"output_tokens":858}}}
        """,
        DateTimeOffset.UnixEpoch) ?? throw new Exception("未解析出会话状态。");

    // Context is what the model saw: fresh input plus cache writes and reads. Output is not context.
    Equal(432_119L, state.ContextTokens);
    Equal(1_000_000L, state.ContextWindow);
    Equal(43, state.ContextUsedPercent);
    Equal("Opus 5", state.DisplayModelName);
    Equal("high", state.Effort);
    Equal("2.1.219", state.Version);

    var small = ClaudeTranscriptParser.TryParseSessionState(
        """
        {"type":"assistant","timestamp":"2026-07-28T06:30:00+09:00","message":{"model":"claude-haiku-4-5-20251001","usage":{"input_tokens":10,"cache_read_input_tokens":49990}}}
        """,
        DateTimeOffset.UnixEpoch)!;
    Equal(200_000L, small.ContextWindow);
    Equal(25, small.ContextUsedPercent);
    // The trailing date stamp is not part of the display name.
    Equal("Haiku 4 5", small.DisplayModelName);
});

Run("Claude 监控器从会话记录得出模型与上下文，不依赖状态栏桥接", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "AiTokenMonitorTests", Guid.NewGuid().ToString("N"));
    var claudeHome = Path.Combine(root, ".claude");
    var projectDirectory = Path.Combine(claudeHome, "projects", "sample");
    var bridgeDirectory = Path.Combine(root, "bridge");
    Directory.CreateDirectory(projectDirectory);
    Directory.CreateDirectory(bridgeDirectory);

    // No claude-status.json at all: this is what the desktop app actually leaves behind.
    var now = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture);
    File.WriteAllText(
        Path.Combine(projectDirectory, "session.jsonl"),
        """
        {"type":"assistant","uuid":"a1","effort":"high","version":"2.1.219","timestamp":"@","message":{"id":"m1","model":"claude-opus-5","usage":{"input_tokens":2,"cache_creation_input_tokens":1065,"cache_read_input_tokens":431052,"output_tokens":858}}}
        """.Replace("@", now));

    try
    {
        using var signal = new ManualResetEventSlim();
        ClaudeUsageSnapshot? observed = null;
        using var monitor = new ClaudeUsageMonitor(
            claudeHome,
            bridgeDirectory,
            usageClient: null,
            enableQuotaPolling: false);
        monitor.UsageUpdated += snapshot =>
        {
            observed = snapshot;
            if (snapshot.Session is not null)
            {
                signal.Set();
            }
        };
        monitor.Start();
        if (!signal.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new Exception("Claude 监控器没有从会话记录得出当前会话状态。");
        }

        Equal("Opus 5", observed!.ModelName);
        Equal<int?>(43, observed.ContextUsedPercent);
        Equal<ClaudeStatusUsage?>(null, observed.Status);
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
});

Run("Claude 监控器整合状态栏补充信息与本机会话历史", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "AiTokenMonitorTests", Guid.NewGuid().ToString("N"));
    var claudeHome = Path.Combine(root, ".claude");
    var projectDirectory = Path.Combine(claudeHome, "projects", "sample");
    var bridgeDirectory = Path.Combine(root, "bridge");
    Directory.CreateDirectory(projectDirectory);
    Directory.CreateDirectory(bridgeDirectory);
    File.WriteAllText(
        Path.Combine(projectDirectory, "session.jsonl"),
        """
        {"type":"assistant","uuid":"live-a1","timestamp":"2026-07-22T12:00:00+09:00","message":{"id":"msg_live_1","usage":{"input_tokens":500,"output_tokens":100,"cache_creation_input_tokens":200,"cache_read_input_tokens":300}}}
        """);
    // The monitor drops status-line data older than ClaudeUsageMonitor.StatusMaxAge, so stamp it now.
    var observedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    File.WriteAllText(
        Path.Combine(bridgeDirectory, "claude-status.json"),
        """
        {"observed_at":"@","model":{"display_name":"Opus"},"context_window":{"used_percentage":37},"rate_limits":{"five_hour":{"used_percentage":23.5,"resets_at":1784721600},"seven_day":{"used_percentage":41.2,"resets_at":1785153600}}}
        """.Replace("@", observedAt));

    try
    {
        using var signal = new ManualResetEventSlim();
        ClaudeUsageSnapshot? observed = null;
        using var monitor = new ClaudeUsageMonitor(
            claudeHome,
            bridgeDirectory,
            usageClient: null,
            enableQuotaPolling: false);
        monitor.UsageUpdated += snapshot =>
        {
            observed = snapshot;
            if (snapshot.Status is not null && snapshot.TokenUsage is not null)
            {
                signal.Set();
            }
        };
        monitor.Start();
        if (!signal.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new Exception("Claude 监控器没有在超时前发布完整快照。");
        }

        Equal("Opus", observed!.Status!.ModelName);
        Equal(24, observed.Status.FiveHour!.UsedPercent);
        Equal<long?>(1_100L, observed.TokenUsage!.LifetimeTokens);
        Equal(true, observed.IsClaudeAvailable);
        Equal(true, observed.IsBridgeConfigured);

        // Quota never comes from the status line any more.
        Equal<RateLimitWindow?>(null, observed.FiveHour);
        Equal<RateLimitWindow?>(null, observed.Weekly);
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
});

Run("Claude 监控器丢弃过期的状态栏快照", () =>
{
    var root = Path.Combine(Path.GetTempPath(), "AiTokenMonitorTests", Guid.NewGuid().ToString("N"));
    var claudeHome = Path.Combine(root, ".claude");
    var bridgeDirectory = Path.Combine(root, "bridge");
    Directory.CreateDirectory(Path.Combine(claudeHome, "projects"));
    Directory.CreateDirectory(bridgeDirectory);

    // Claude Code stopped long ago; the file it left behind must not keep showing a live model.
    var staleAt = DateTimeOffset.UtcNow
        .Subtract(ClaudeUsageMonitor.StatusMaxAge)
        .AddMinutes(-1)
        .ToString("O", CultureInfo.InvariantCulture);
    File.WriteAllText(
        Path.Combine(bridgeDirectory, "claude-status.json"),
        """
        {"observed_at":"@","model":{"display_name":"Opus"},"context_window":{"used_percentage":37}}
        """.Replace("@", staleAt));

    try
    {
        using var signal = new ManualResetEventSlim();
        ClaudeUsageSnapshot? observed = null;
        using var monitor = new ClaudeUsageMonitor(
            claudeHome,
            bridgeDirectory,
            usageClient: null,
            enableQuotaPolling: false);
        monitor.UsageUpdated += snapshot =>
        {
            observed = snapshot;
            signal.Set();
        };
        monitor.Start();
        if (!signal.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new Exception("Claude 监控器没有发布快照。");
        }

        Equal<ClaudeStatusUsage?>(null, observed!.Status);
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
});

Run("窗口位置未初始化时不写入也不抛出", () =>
{
    // Regression: serializing Infinity threw and took the whole process down on shutdown.
    WindowPlacementStore.Save(new WindowPlacement(double.PositiveInfinity, double.NaN, true));
});

Run("重置时间显示本地月日和时分", () =>
{
    var localDate = new DateTime(2026, 7, 29, 14, 30, 0, DateTimeKind.Unspecified);
    var resetAt = new DateTimeOffset(localDate, TimeZoneInfo.Local.GetUtcOffset(localDate));
    Equal("7月29日 14:30 重置", MainWindow.FormatResetTime(resetAt));
});

Run("仪表盘水位取剩余额度,5 小时优先否则周额度", () =>
{
    var fiveHour = new RateLimitWindow(44, DateTimeOffset.Now, 300);
    var weekly = new RateLimitWindow(89, DateTimeOffset.Now, 10_080);

    // The reflected window: 5-hour wins when present, else weekly (Codex's current shape).
    Equal<int?>(44, MainWindow.GaugeUsedPercent(fiveHour, weekly));
    Equal<int?>(89, MainWindow.GaugeUsedPercent(null, weekly));
    Equal<int?>(null, MainWindow.GaugeUsedPercent(null, null));

    // Water line = remaining, i.e. 100 - used, so a full tank reads 100% and a dry tank 0%.
    Equal<int?>(56, MainWindow.GaugeRemainingPercent(fiveHour, weekly));
    Equal<int?>(11, MainWindow.GaugeRemainingPercent(null, weekly));
    Equal<int?>(null, MainWindow.GaugeRemainingPercent(null, null));
    // Fully consumed -> empty tank; untouched -> full tank.
    Equal<int?>(0, MainWindow.GaugeRemainingPercent(new RateLimitWindow(100, DateTimeOffset.Now, 300), null));
    Equal<int?>(100, MainWindow.GaugeRemainingPercent(new RateLimitWindow(0, DateTimeOffset.Now, 300), null));
});

RunSta("液面球渲染各种剩余取值不抛异常", () =>
{
    var gauge = new CodexWeeklyMonitor.Controls.GaugeControl();
    Equal(116d, gauge.Width);
    Equal(116d, gauge.Height);
    Equal(116d, CodexWeeklyMonitor.Controls.GaugeControl.Diameter);
    gauge.Update(97, 68);   // remaining percentages
    gauge.Update(null, null);
    gauge.Update(0, 100);   // dry tank / full tank
    gauge.Update(150, -5);  // clamped, must not throw
    gauge.Measure(new System.Windows.Size(160, 160));
    gauge.Arrange(new System.Windows.Rect(0, 0, 160, 160));
});

RunSta("液面球缩小后阴影仍不会被窗口裁剪", () =>
{
    var gaugeWindow = new GaugeWindow();
    try
    {
        gaugeWindow.SetValues(13, 65);
        gaugeWindow.Show();
        gaugeWindow.UpdateLayout();
        Equal(118d, GaugeWindow.OrbDiameter);
        Equal(GaugeWindow.ShadowCanvasWidth, gaugeWindow.Width);
        Equal(GaugeWindow.ShadowCanvasHeight, gaugeWindow.Height);
        Equal(GaugeWindow.ShadowCanvasWidth, gaugeWindow.ActualWidth);
        Equal(GaugeWindow.ShadowCanvasHeight, gaugeWindow.ActualHeight);

        // The orb shrank from the old 150, so the ball reads as an accessory.
        if (GaugeWindow.OrbDiameter >= 130d)
        {
            throw new Exception("仪表盘悬浮球没有按要求缩小。");
        }

        if (GaugeWindow.OrbOffsetX < 18d ||
            GaugeWindow.ShadowCanvasWidth - GaugeWindow.OrbOffsetX - GaugeWindow.OrbDiameter < 18d ||
            GaugeWindow.ShadowCanvasHeight - GaugeWindow.OrbOffsetY - GaugeWindow.OrbDiameter < 30d)
        {
            throw new Exception("仪表盘阴影画布未保留足够的透明扩散空间。");
        }

        var ambient = (System.Windows.Shapes.Ellipse)gaugeWindow.FindName("AmbientShadow");
        var contact = (System.Windows.Shapes.Ellipse)gaugeWindow.FindName("ContactShadow");
        var ambientEffect = (System.Windows.Media.Effects.DropShadowEffect)ambient.Effect;
        var contactEffect = (System.Windows.Media.Effects.DropShadowEffect)contact.Effect;
        if (ambientEffect.Opacity >= 0.3 ||
            contactEffect.Opacity >= ambientEffect.Opacity ||
            ambientEffect.BlurRadius <= contactEffect.BlurRadius ||
            contactEffect.ShadowDepth <= ambientEffect.ShadowDepth)
        {
            throw new Exception("仪表盘没有形成柔和环境影与紧凑接触影的自然层次。");
        }

        gaugeWindow.PlaceOrbAt(420d, 260d);
        var topLeft = gaugeWindow.GetOrbTopLeft();
        Equal(420d, topLeft.X);
        Equal(260d, topLeft.Y);
    }
    finally
    {
        gaugeWindow.Close();
    }
});

Run("界面语言可切换且默认跟随系统", () =>
{
    var original = CodexWeeklyMonitor.Services.Loc.Current;
    try
    {
        CodexWeeklyMonitor.Services.Loc.SetLanguage(CodexWeeklyMonitor.Services.AppLanguage.English);
        Equal("Weekly limit", CodexWeeklyMonitor.Services.Loc.T("card.weekly"));
        Equal("42% used", CodexWeeklyMonitor.Services.Loc.T("card.used", 42));

        CodexWeeklyMonitor.Services.Loc.SetLanguage(CodexWeeklyMonitor.Services.AppLanguage.Korean);
        Equal("주간 한도", CodexWeeklyMonitor.Services.Loc.T("card.weekly"));

        CodexWeeklyMonitor.Services.Loc.SetLanguage(CodexWeeklyMonitor.Services.AppLanguage.Chinese);
        Equal("周额度", CodexWeeklyMonitor.Services.Loc.T("card.weekly"));

        // Missing key falls back to the key itself rather than a blank.
        Equal("no.such.key", CodexWeeklyMonitor.Services.Loc.T("no.such.key"));
    }
    finally
    {
        CodexWeeklyMonitor.Services.Loc.SetLanguage(original);
    }
});

Run("TokenFormatter 按语言使用万/억或 K/M/B", () =>
{
    var original = CodexWeeklyMonitor.Services.Loc.Current;
    try
    {
        CodexWeeklyMonitor.Services.Loc.SetLanguage(CodexWeeklyMonitor.Services.AppLanguage.Chinese);
        Equal("328亿", TokenFormatter.Format(32_831_840_132));
        Equal("720万", TokenFormatter.Format(7_204_529));

        CodexWeeklyMonitor.Services.Loc.SetLanguage(CodexWeeklyMonitor.Services.AppLanguage.Korean);
        Equal("328억", TokenFormatter.Format(32_831_840_132));
        Equal("720만", TokenFormatter.Format(7_204_529));

        CodexWeeklyMonitor.Services.Loc.SetLanguage(CodexWeeklyMonitor.Services.AppLanguage.English);
        Equal("32.8B", TokenFormatter.Format(32_831_840_132));
        Equal("7.2M", TokenFormatter.Format(7_204_529));
    }
    finally
    {
        CodexWeeklyMonitor.Services.Loc.SetLanguage(original);
    }
});

Run("重复启动会通知已运行实例显示主窗口", () =>
{
    var suffix = Guid.NewGuid().ToString("N");
    using var primary = new SingleInstanceCoordinator(
        $"Local\\AiTokenMonitor.Tests.Mutex.{suffix}",
        $"Local\\AiTokenMonitor.Tests.Activate.{suffix}");
    Equal(true, primary.IsPrimary);

    using var activated = new ManualResetEventSlim();
    primary.ActivationRequested += (_, _) => activated.Set();
    primary.StartListening();

    var secondaryWasPrimary = true;
    Exception? secondaryFailure = null;
    var secondaryThread = new Thread(() =>
    {
        try
        {
            using var secondary = new SingleInstanceCoordinator(
                $"Local\\AiTokenMonitor.Tests.Mutex.{suffix}",
                $"Local\\AiTokenMonitor.Tests.Activate.{suffix}");
            secondaryWasPrimary = secondary.IsPrimary;
            secondary.SignalPrimary();
        }
        catch (Exception exception)
        {
            secondaryFailure = exception;
        }
    });
    secondaryThread.Start();
    secondaryThread.Join();
    if (secondaryFailure is not null)
    {
        throw secondaryFailure;
    }

    Equal(false, secondaryWasPrimary);
    if (!activated.Wait(TimeSpan.FromSeconds(2)))
    {
        throw new Exception("第二次启动没有通知主实例恢复窗口。");
    }
});

RunSta("原生托盘菜单在窗口失焦后自动关闭", () =>
{
    var opened = false;
    var closed = false;
    var timedOut = false;
    using var trayIcon = new TrayIconService();
    using var hostForm = new Forms.Form
    {
        FormBorderStyle = Forms.FormBorderStyle.FixedToolWindow,
        ShowInTaskbar = false,
        Size = new System.Drawing.Size(180, 100),
        StartPosition = Forms.FormStartPosition.Manual,
        Location = new System.Drawing.Point(-2_000, -2_000),
    };
    using var otherForm = new Forms.Form
    {
        FormBorderStyle = Forms.FormBorderStyle.FixedToolWindow,
        ShowInTaskbar = false,
        Size = new System.Drawing.Size(180, 100),
        StartPosition = Forms.FormStartPosition.Manual,
        Location = new System.Drawing.Point(-2_000, -2_000),
    };
    using var activateOtherWindow = new Forms.Timer { Interval = 100 };
    using var watchdog = new Forms.Timer { Interval = 2_000 };

    trayIcon.MenuForTesting.Closed += (_, _) =>
    {
        closed = true;
        hostForm.BeginInvoke(new Action(hostForm.Close));
    };
    activateOtherWindow.Tick += (_, _) =>
    {
        activateOtherWindow.Stop();
        otherForm.Activate();
        otherForm.Focus();
    };
    watchdog.Tick += (_, _) =>
    {
        watchdog.Stop();
        timedOut = true;
        trayIcon.MenuForTesting.Close();
        hostForm.Close();
    };
    hostForm.Shown += (_, _) =>
    {
        otherForm.Show();
        hostForm.Activate();
        trayIcon.MenuForTesting.Show(hostForm, new System.Drawing.Point(8, 8));
        opened = trayIcon.MenuForTesting.Visible;
        activateOtherWindow.Start();
        watchdog.Start();
    };

    Forms.Application.Run(hostForm);
    Equal(true, opened);
    Equal(false, timedOut);
    Equal(true, closed);
    Equal(false, trayIcon.MenuForTesting.Visible);
    Equal(
        TrayIconService.MenuHoverColorForTesting.ToArgb(),
        TrayIconService.ResolveMenuItemBackgroundForTesting(isSelected: true, isSubmenuOpen: false).ToArgb());
    if (TrayIconService.MenuHoverColorForTesting.ToArgb() == System.Drawing.SystemColors.Highlight.ToArgb())
    {
        throw new Exception("托盘 hover 仍使用系统蓝色高亮。");
    }
});

RunSta("窗口交互、托盘隐藏恢复和现代滚动条可用", () =>
{
    var application = Application.Current ?? new Application
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown,
    };
    application.Resources["AppFont"] = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI, Segoe UI");
    application.Resources.MergedDictionaries.Add(new ResourceDictionary
    {
        Source = new Uri(
            "/AiTokenMonitor;component/Themes/ModernMenuStyles.xaml",
            UriKind.Relative),
    });


    var trayIcon = new FakeTrayIconService();
    var mainWindow = new MainWindow(trayIcon, enableMonitoring: false);
    var mainWindowClosed = false;
    mainWindow.Closed += (_, _) => mainWindowClosed = true;

    try
    {
        mainWindow.Show();
        Equal("AI TOKEN 用量监控", mainWindow.Title);
        var card = mainWindow.FindName("Card") as Border
            ?? throw new Exception("未找到主窗口卡片。");
        var mainContextMenu = card.ContextMenu
            ?? throw new Exception("未找到主窗口右键菜单。");
        mainContextMenu.ApplyTemplate();
        if (mainContextMenu.Template.FindName("ModernMenuChrome", mainContextMenu) is not Border)
        {
            throw new Exception("主窗口右键菜单没有加载现代面板样式。");
        }

        if (mainContextMenu.StaysOpen)
        {
            throw new Exception("主窗口右键菜单没有启用外部点击自动关闭。");
        }

        // The status-line bridge installer is gone; 详情 took that slot.
        if (mainWindow.FindName("ConfigureClaudeMenuItem") is not null)
        {
            throw new Exception("右键菜单仍保留已废弃的“配置 Claude 监控”。");
        }

        var detailsMenuItem = mainWindow.FindName("DetailsMenuItem") as MenuItem
            ?? throw new Exception("右键菜单缺少展开详情。");
        var historyMenuItem = mainWindow.FindName("HistoryMenuItem") as MenuItem
            ?? throw new Exception("右键菜单缺少展开历史。");

        // The menu describes what the entry would do right now, not a fixed label.
        mainContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent, mainContextMenu));
        Equal("展开详情", (string)detailsMenuItem.Header);
        Equal("展开逐日 Token 历史", (string)historyMenuItem.Header);

        detailsMenuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, detailsMenuItem));
        mainContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent, mainContextMenu));
        Equal("收起详情", (string)detailsMenuItem.Header);

        // Toggling from the menu closes it again, same as the header button.
        detailsMenuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, detailsMenuItem));
        mainContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent, mainContextMenu));
        Equal("展开详情", (string)detailsMenuItem.Header);

        // Regression: the previous item template had no PART_Popup, so the language header was
        // visible but none of its children could open or receive a click.
        var languageMenu = mainContextMenu.Items.OfType<MenuItem>()
            .SingleOrDefault(item => (item.Tag as string) == "language")
            ?? throw new Exception("主窗口右键菜单缺少语言切换项。");
        languageMenu.ApplyTemplate();
        if (languageMenu.Template.FindName("PART_Popup", languageMenu) is not Popup)
        {
            throw new Exception("语言菜单模板没有提供子菜单弹出层。");
        }

        var englishLanguageItem = languageMenu.Items.OfType<MenuItem>()
            .Single(item => item.Tag is AppLanguage.English);
        englishLanguageItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, englishLanguageItem));
        Equal(AppLanguage.English, Loc.Current);
        Equal("AI Token Monitor", mainWindow.Title);
        Loc.SetLanguage(AppLanguage.Chinese);

        // The tray must stay a native ContextMenuStrip attached to NotifyIcon. This is the
        // mechanism that makes a click in another application close it reliably.
        using (var nativeTray = new TrayIconService())
        {
            var trayMenu = nativeTray.MenuForTesting;
            if (!nativeTray.MenuAttachedForTesting || !trayMenu.AutoClose)
            {
                throw new Exception("托盘菜单没有启用原生外部点击自动关闭机制。");
            }

            if (trayMenu.MinimumSize.Width > 148)
            {
                throw new Exception($"托盘菜单最小宽度过大：{trayMenu.MinimumSize.Width}px。");
            }

            nativeTray.UpdateMenuStatus("Codex status", "Claude status");
            var trayItems = trayMenu.Items.OfType<Forms.ToolStripMenuItem>().ToArray();
            if (!trayItems.Any(item => item.Text == "Claude status"))
            {
                throw new Exception("托盘菜单没有显示 Claude 状态行。");
            }

            var refreshRequested = false;
            nativeTray.RefreshRequested += (_, _) => refreshRequested = true;
            trayItems.Single(item => item.Text == "立即刷新").PerformClick();
            Equal(true, refreshRequested);

            var nativeLanguageMenu = trayItems.Single(item => item.Text == "语言 / Language");
            if (!ReferenceEquals(nativeLanguageMenu.DropDown.Renderer, trayMenu.Renderer))
            {
                throw new Exception("托盘语言子菜单没有使用统一的现代渲染器。");
            }

            var nativeEnglishItem = nativeLanguageMenu.DropDownItems
                .OfType<Forms.ToolStripMenuItem>()
                .Single(item => item.Tag is AppLanguage.English);
            nativeEnglishItem.PerformClick();
            Equal(AppLanguage.English, Loc.Current);
            Equal("Show window", trayItems[0].Text);
            Equal(true, nativeEnglishItem.Checked);
            Loc.SetLanguage(AppLanguage.Chinese);
        }

        var menuBackgroundBrush = mainWindow.TryFindResource("ModernMenuBackgroundBrush") as System.Windows.Media.Brush
            ?? throw new Exception("未加载菜单配色。");
        var menuBackgroundColor = menuBackgroundBrush switch
        {
            SolidColorBrush solid => solid.Color,
            System.Windows.Media.GradientBrush gradient => gradient.GradientStops[0].Color,
            _ => throw new Exception("菜单背景类型未知。"),
        };
        var menuText = mainWindow.TryFindResource("ModernMenuTextBrush") as SolidColorBrush
            ?? throw new Exception("未加载菜单文字配色。");
        var menuHoverText = mainWindow.TryFindResource("ModernMenuHoverTextBrush") as SolidColorBrush
            ?? throw new Exception("未加载菜单悬停文字配色。");
        // Dark card, muted resting text, near-white hover text — the reference palette.
        if (menuBackgroundColor.R > 0x60 || menuText.Color.R > 0xA0 || menuHoverText.Color.R < 0xE0)
        {
            throw new Exception("菜单配色不是深色主题。");
        }

        var topmostMenuItem = mainWindow.FindName("TopmostMenuItem") as MenuItem
            ?? throw new Exception("未找到始终置顶菜单项。");
        topmostMenuItem.ApplyTemplate();
        if (topmostMenuItem.Template.FindName("ModernMenuItemChrome", topmostMenuItem) is not Border)
        {
            throw new Exception("主窗口菜单项没有加载现代悬浮样式。");
        }

        if (mainWindow.FindName("TodayTokensText") is not null)
        {
            throw new Exception("重复的今日 TOKEN 横条仍然存在。");
        }

        var lifetimeTokensText = (TextBlock)mainWindow.FindName("LifetimeTokensText");
        var latestTokensText = (TextBlock)mainWindow.FindName("LatestTokensText");
        var sevenDayTokensText = (TextBlock)mainWindow.FindName("SevenDayTokensText");
        Equal(lifetimeTokensText.FontSize, latestTokensText.FontSize);
        Equal(lifetimeTokensText.FontSize, sevenDayTokensText.FontSize);
        Equal(lifetimeTokensText.Margin.Top, latestTokensText.Margin.Top);
        Equal(lifetimeTokensText.Margin.Top, sevenDayTokensText.Margin.Top);

        var codexTab = mainWindow.FindName("CodexProviderTab") as RadioButton
            ?? throw new Exception("未找到 Codex 切换标签。");
        var claudeTab = mainWindow.FindName("ClaudeProviderTab") as RadioButton
            ?? throw new Exception("未找到 Claude 切换标签。");
        mainWindow.ApplyClaudeSnapshot(new ClaudeUsageSnapshot(
            new ClaudeAccountUsage(
                new RateLimitWindow(24, DateTimeOffset.Now.AddHours(2), 300),
                new RateLimitWindow(41, DateTimeOffset.Now.AddDays(4), 10_080),
                ScopedLimits: [],
                ExtraUsage: new ClaudeExtraUsage(
                    IsEnabled: true,
                    UsedAmount: 4.5m,
                    LimitAmount: 50m,
                    UsedPercent: 9,
                    Currency: "USD",
                    DisabledReason: null),
                SubscriptionType: "pro",
                FetchedAt: DateTimeOffset.Now),
            AccountError: null,
            new ClaudeStatusUsage(
                null,
                null,
                "Opus",
                37,
                DateTimeOffset.Now),
            new AccountTokenUsage(
                LifetimeTokens: 1_300,
                PeakDailyTokens: 1_100,
                LongestRunningTurnSeconds: null,
                CurrentStreakDays: 2,
                LongestStreakDays: 2,
                DailyUsage:
                [
                    new DailyTokenUsage(new DateOnly(2026, 7, 21), 200),
                    new DailyTokenUsage(new DateOnly(2026, 7, 22), 1_100),
                ],
                FetchedAt: DateTimeOffset.Now),
            IsClaudeAvailable: true,
            IsBridgeConfigured: true,
            FetchedAt: DateTimeOffset.Now));
        claudeTab.IsChecked = true;
        mainWindow.UpdateLayout();
        var lifetimeCaption = mainWindow.FindName("LifetimeTokensCaption") as TextBlock
            ?? throw new Exception("未找到累计 Token 标签。");
        Equal("本机累计 TOKEN", lifetimeCaption.Text);
        Equal("76%", ((TextBlock)mainWindow.FindName("FiveHourRemainingText")).Text);
        Equal("59%", ((TextBlock)mainWindow.FindName("WeeklyRemainingText")).Text);
        Equal(true, trayIcon.ToolTipText.Contains("Claude周剩余:59%", StringComparison.Ordinal));
        Equal(true, trayIcon.ClaudeMenuStatus.Contains("Claude", StringComparison.Ordinal));
        Equal("用量额度", ((TextBlock)mainWindow.FindName("BalanceCaption")).Text);
        Equal("$4.5 / $50", ((TextBlock)mainWindow.FindName("BalanceText")).Text);
        Equal("上下文占用", ((TextBlock)mainWindow.FindName("ResetCreditsCaption")).Text);
        Equal("37%", ((TextBlock)mainWindow.FindName("ResetCreditsText")).Text);
        Equal(
            "额度来自 Claude 官方接口 · Token 为本机统计",
            ((TextBlock)mainWindow.FindName("TokenStatusText")).Text);
        Equal("1,300", ((TextBlock)mainWindow.FindName("LifetimeTokensText")).Text);
        claudeTab.ApplyTemplate();
        if (claudeTab.Template.FindName("TabChrome", claudeTab) is not Border)
        {
            throw new Exception("提供商切换标签没有加载现代圆角模板。");
        }

        // 详情 and 历史 share one expansion slot and must never be open at the same time.
        var detailsButton = mainWindow.FindName("DetailsButton") as Button
            ?? throw new Exception("未找到详情按钮。");
        var historyButton = mainWindow.FindName("HistoryButton") as Button
            ?? throw new Exception("未找到历史按钮。");

        // All five header actions share one base style: same height, spacing and hover chrome.
        var headerButtons = new[]
        {
            detailsButton,
            historyButton,
            (Button)mainWindow.FindName("RefreshButton"),
            (Button)mainWindow.FindName("MinimizeButton"),
            (Button)mainWindow.FindName("CloseToTrayButton"),
        };
        foreach (var button in headerButtons)
        {
            Equal(26d, button.Height);
            Equal(new Thickness(3, 0, 0, 0), button.Margin);
            Equal(new Thickness(0), button.Padding);
            button.ApplyTemplate();
            if (button.Template.FindName("Chrome", button) is not Border)
            {
                throw new Exception($"{button.Name} 没有使用统一的按钮模板。");
            }
        }

        // Labelled buttons match each other; icon buttons are square.
        Equal(detailsButton.Width, historyButton.Width);
        Equal(26d, headerButtons[2].Width);
        Equal(headerButtons[2].Width, headerButtons[3].Width);
        Equal(headerButtons[3].Width, headerButtons[4].Width);
        var detailsPanel = mainWindow.FindName("DetailsPanel") as Border
            ?? throw new Exception("未找到详情面板。");
        var historyPanel = mainWindow.FindName("HistoryPanel") as Border
            ?? throw new Exception("未找到历史面板。");
        var collapsedHeight = mainWindow.Height;

        Equal(Visibility.Collapsed, detailsPanel.Visibility);
        Equal(Visibility.Collapsed, historyPanel.Visibility);

        detailsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, detailsButton));
        Equal(Visibility.Visible, detailsPanel.Visibility);
        Equal(Visibility.Collapsed, historyPanel.Visibility);
        var expandedHeight = mainWindow.Height;
        if (expandedHeight <= collapsedHeight)
        {
            throw new Exception("展开详情没有把窗口加高。");
        }

        historyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, historyButton));
        Equal(Visibility.Collapsed, detailsPanel.Visibility);
        Equal(Visibility.Visible, historyPanel.Visibility);

        // With history open the menu offers to collapse it, and details go back to "expand".
        mainContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent, mainContextMenu));
        Equal("收起逐日 Token 历史", (string)historyMenuItem.Header);
        Equal("展开详情", (string)detailsMenuItem.Header);
        // Only one panel is ever open, so the window height must not stack.
        Equal(expandedHeight, mainWindow.Height);

        var historyList = mainWindow.FindName("HistoryListItemsControl") as ItemsControl
            ?? throw new Exception("未找到内联历史列表。");
        mainWindow.UpdateLayout();
        var historyRows = historyList.ItemsSource?.Cast<object>().Count() ?? 0;
        if (historyRows != 2)
        {
            throw new Exception($"内联历史应显示 2 天，实际 {historyRows} 天。");
        }

        var historyScrollBar = FindVisualChildren<ScrollBar>(historyPanel)
            .FirstOrDefault(scrollBar => scrollBar.Orientation == Orientation.Vertical)
            ?? throw new Exception("历史面板没有创建纵向滚动条。");
        historyScrollBar.ApplyTemplate();
        if (historyScrollBar.Template.FindName("ModernRail", historyScrollBar) is not Border)
        {
            throw new Exception("历史面板的滚动条未使用现代模板。");
        }

        historyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, historyButton));
        Equal(Visibility.Collapsed, historyPanel.Visibility);
        Equal(collapsedHeight, mainWindow.Height);

        codexTab.IsChecked = true;
        var historyItems = mainWindow.FindName("HistoryItemsControl") as ItemsControl
            ?? throw new Exception("未找到主窗口逐日柱状图。");
        historyItems.ItemsSource = new[]
        {
            new
            {
                BarHeight = 32d,
                Opacity = 0.88d,
                DateLabel = "7/22",
                TooltipDate = "7月22日",
                TooltipTokens = "1,600,000,000 Token",
                AutomationLabel = "7月22日，1,600,000,000 Token",
                BarColor = "#7EE787",
            },
        };
        mainWindow.UpdateLayout();

        var toolTipOwners = FindVisualChildren<FrameworkElement>(mainWindow)
            .Where(element => element.ToolTip is not null)
            .ToArray();
        if (toolTipOwners.Length != 1 || toolTipOwners[0].ToolTip is not ToolTip barToolTip)
        {
            throw new Exception("主窗口应当只为逐日柱子提供悬浮提示。");
        }

        barToolTip.ApplyTemplate();
        if (barToolTip.Template.FindName("TooltipChrome", barToolTip) is not Border tooltipChrome)
        {
            throw new Exception("柱状图悬浮提示没有加载现代圆角模板。");
        }

        if (barToolTip.HasDropShadow ||
            barToolTip.Background is not SolidColorBrush { Color.A: 0 } ||
            tooltipChrome.Effect is not null)
        {
            throw new Exception("柱状图悬浮提示仍存在圆角外的矩形黑底或阴影层。");
        }

        barToolTip.PlacementTarget = toolTipOwners[0];
        barToolTip.IsOpen = true;
        barToolTip.UpdateLayout();
        var toolTipTexts = FindVisualChildren<TextBlock>(barToolTip)
            .Select(text => text.Text)
            .ToArray();
        barToolTip.IsOpen = false;
        if (!toolTipTexts.Contains("7月22日") || !toolTipTexts.Contains("1,600,000,000 Token"))
        {
            throw new Exception("柱状图悬浮提示没有显示日期和精确 Token 数。");
        }

        // — minimises to the taskbar (needs the taskbar button switched on to be restorable).
        var minimizeButton = mainWindow.FindName("MinimizeButton") as Button
            ?? throw new Exception("未找到最小化按钮。");
        minimizeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, minimizeButton));
        if (mainWindow.WindowState != WindowState.Minimized)
        {
            throw new Exception("最小化按钮没有把窗口最小化到任务栏。");
        }

        if (!mainWindow.ShowInTaskbar)
        {
            throw new Exception("最小化后窗口没有出现在任务栏，将无法恢复。");
        }

        trayIcon.RequestShow();
        if (!mainWindow.IsVisible || mainWindow.WindowState != WindowState.Normal)
        {
            throw new Exception("托盘操作没有恢复主窗口。");
        }

        // Restoring from a minimise must keep the taskbar button; only the tray hides it.
        if (!mainWindow.ShowInTaskbar)
        {
            throw new Exception("从最小化恢复后不应丢失任务栏按钮。");
        }

        // × hides to the tray entirely: no taskbar button, process still alive.
        var closeButton = mainWindow.FindName("CloseToTrayButton") as Button
            ?? throw new Exception("未找到隐藏到托盘按钮。");
        closeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, closeButton));
        if (mainWindow.IsVisible || mainWindowClosed)
        {
            throw new Exception("主窗口 × 没有保持进程并隐藏到托盘。");
        }

        if (mainWindow.ShowInTaskbar)
        {
            throw new Exception("隐藏到托盘后不应保留任务栏按钮。");
        }

        // Double-clicking the EXE while this instance is hidden signals this same restore path.
        mainWindow.RestoreFromExternalLaunch();
        if (!mainWindow.IsVisible || !mainWindow.ShowInTaskbar)
        {
            throw new Exception("重复启动没有恢复主窗口和任务栏按钮。");
        }

        // Quitting is a tray-only action now.
        if (mainContextMenu.Items.OfType<MenuItem>().Any(item => (item.Header as string) == "退出程序"))
        {
            throw new Exception("窗口右键菜单不应再提供退出程序。");
        }

        if (!mainContextMenu.Items.OfType<MenuItem>().Any(item => (item.Header as string) == "最小化到任务栏"))
        {
            throw new Exception("窗口右键菜单缺少最小化到任务栏。");
        }

        trayIcon.RequestShow();
        trayIcon.RequestExit();
        if (!mainWindowClosed || !trayIcon.Disposed)
        {
            throw new Exception("托盘退出没有完整关闭程序或释放图标。");
        }

        using var nativeTrayIcon = new TrayIconService();
        if (!nativeTrayIcon.Visible)
        {
            throw new Exception("真实系统托盘图标未成功初始化。");
        }

        nativeTrayIcon.Visible = false;
    }
    finally
    {
        if (!mainWindowClosed)
        {
            mainWindow.Close();
        }
    }
});

Console.WriteLine(failed == 0
    ? "全部测试通过。"
    : $"{failed} 个测试失败。");
return failed == 0 ? 0 : 1;

void Run(string name, Action test)
{
    try
    {
        test();
        Console.WriteLine($"PASS  {name}");
    }
    catch (Exception exception)
    {
        failed++;
        Console.Error.WriteLine($"FAIL  {name}: {exception.Message}");
    }
}

void RunSta(string name, Action test)
{
    var failure = staTestRunner.Execute(test);

    if (failure is null)
    {
        Console.WriteLine($"PASS  {name}");
        return;
    }

    failed++;
    Console.Error.WriteLine($"FAIL  {name}: {failure}");
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new Exception($"期望 {expected}，实际 {actual}。");
    }
}

static LiveTokenUsage CreateLiveUsage(long totalTokens, DateOnly date)
{
    return new LiveTokenUsage(
        TotalTokens: totalTokens,
        LastTurnTokens: 0,
        InputTokens: 0,
        CachedInputTokens: 0,
        OutputTokens: 0,
        ReasoningOutputTokens: 0,
        ModelContextWindow: null,
        ObservedAt: new DateTimeOffset(date.ToDateTime(new TimeOnly(12, 0)), TimeZoneInfo.Local.GetUtcOffset(date.ToDateTime(new TimeOnly(12, 0)))));
}

static string CreateCodexRollout(
    string sessionId,
    string? forkedFromId,
    string source,
    long totalTokens)
{
    var payload = new Dictionary<string, object?>
    {
        ["session_id"] = sessionId,
        ["id"] = sessionId,
        ["source"] = source,
    };
    if (!string.IsNullOrWhiteSpace(forkedFromId))
    {
        payload["forked_from_id"] = forkedFromId;
    }

    var meta = JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["timestamp"] = DateTimeOffset.Now.ToUniversalTime().ToString("O"),
        ["type"] = "session_meta",
        ["payload"] = payload,
    });
    return meta + Environment.NewLine +
           CreateCodexTokenEvent(DateTimeOffset.Now, totalTokens, totalTokens) +
           Environment.NewLine;
}

static string CreateCodexTokenEvent(DateTimeOffset timestamp, long totalTokens, long lastTurnTokens)
{
    static Dictionary<string, long> Usage(long tokens) => new()
    {
        ["input_tokens"] = tokens,
        ["cached_input_tokens"] = 0,
        ["output_tokens"] = 0,
        ["reasoning_output_tokens"] = 0,
        ["total_tokens"] = tokens,
    };

    return JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["timestamp"] = timestamp.ToUniversalTime().ToString("O"),
        ["type"] = "event_msg",
        ["payload"] = new Dictionary<string, object?>
        {
            ["type"] = "token_count",
            ["info"] = new Dictionary<string, object?>
            {
                ["total_token_usage"] = Usage(totalTokens),
                ["last_token_usage"] = Usage(lastTurnTokens),
                ["model_context_window"] = 200_000,
            },
        },
    });
}

static string EncryptDesktopTokenCache(string plaintext, byte[] key)
{
    var nonce = RandomNumberGenerator.GetBytes(12);
    var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
    var ciphertext = new byte[plaintextBytes.Length];
    var tag = new byte[16];
    using (var aes = new AesGcm(key, tag.Length))
    {
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);
    }

    var blob = new byte[3 + nonce.Length + ciphertext.Length + tag.Length];
    Encoding.ASCII.GetBytes("v10").CopyTo(blob, 0);
    nonce.CopyTo(blob, 3);
    ciphertext.CopyTo(blob, 3 + nonce.Length);
    tag.CopyTo(blob, 3 + nonce.Length + ciphertext.Length);
    return Convert.ToBase64String(blob);
}

static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent)
    where T : DependencyObject
{
    for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
    {
        var child = VisualTreeHelper.GetChild(parent, index);
        if (child is T match)
        {
            yield return match;
        }

        foreach (var descendant in FindVisualChildren<T>(child))
        {
            yield return descendant;
        }
    }
}

sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(responder(request));
    }
}

sealed class FakeTrayIconService : ITrayIconService
{
    public event EventHandler? ShowRequested;

    public event EventHandler? RefreshRequested;

    public event EventHandler? ExitRequested;

    public bool Visible { get; set; } = true;

    public string ToolTipText { get; set; } = string.Empty;

    public string CodexMenuStatus { get; private set; } = string.Empty;

    public string ClaudeMenuStatus { get; private set; } = string.Empty;


    public bool Disposed { get; private set; }

    public void RequestShow() => ShowRequested?.Invoke(this, EventArgs.Empty);

    public void RequestRefresh() => RefreshRequested?.Invoke(this, EventArgs.Empty);

    public void RequestExit() => ExitRequested?.Invoke(this, EventArgs.Empty);

    public void UpdateMenuStatus(string codexStatus, string claudeStatus)
    {
        CodexMenuStatus = codexStatus;
        ClaudeMenuStatus = claudeStatus;
    }

    public void Dispose()
    {
        Visible = false;
        Disposed = true;
    }
}

sealed class StaTestRunner : IDisposable
{
    private readonly ManualResetEventSlim _ready = new();
    private readonly Thread _thread;
    private Dispatcher? _dispatcher;

    public StaTestRunner()
    {
        _thread = new Thread(() =>
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            _ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "AiTokenMonitor UI tests",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
    }

    public Exception? Execute(Action action)
    {
        Exception? failure = null;
        _dispatcher!.Invoke(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        return failure;
    }

    public void Dispose()
    {
        if (_dispatcher is not null && !_dispatcher.HasShutdownStarted)
        {
            _dispatcher.InvokeShutdown();
        }

        _thread.Join(TimeSpan.FromSeconds(5));
        _ready.Dispose();
    }
}
