using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CodexWeeklyMonitor.Models;
using CodexWeeklyMonitor.Services;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using ProgressBar = System.Windows.Controls.ProgressBar;
using RadioButton = System.Windows.Controls.RadioButton;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using Point = System.Windows.Point;

namespace CodexWeeklyMonitor;

public partial class MainWindow : Window
{
    private const double CollapsedHeight = 432;
    private const double ExpandPanelHeight = 236;

    private static readonly Brush HealthyBrush = CreateBrush(0x7E, 0xE7, 0x87);
    private static readonly Brush WarningBrush = CreateBrush(0xF0, 0xB9, 0x5C);
    private static readonly Brush DangerBrush = CreateBrush(0xFF, 0x6B, 0x6B);
    private static readonly Brush UnavailableBrush = CreateBrush(0x5B, 0x66, 0x72);

    private readonly CodexUsageProvider _client = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly ITrayIconService _trayIcon;
    private readonly IUpdateService _updateService;
    private readonly LocalSessionTokenMonitor _localTokenMonitor;
    private readonly ClaudeUsageMonitor _claudeMonitor;
    private readonly bool _monitoringEnabled;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _clockTimer;

    private CodexUsageSnapshot? _currentSnapshot;
    private ClaudeUsageSnapshot? _claudeSnapshot;
    private TodayTokenUsage? _todayTokenUsage;
    private AccountTokenUsage? _displayTokenUsage;
    private Exception? _codexError;
    private UsageProvider _activeProvider = UsageProvider.Codex;
    private bool _refreshInProgress;
    private bool _isClosing;
    private bool _initialized;
    private ExpandedPanel _expandedPanel = ExpandedPanel.None;
    private GaugeWindow? _gaugeWindow;
    private Point? _gaugeTopLeft;
    private bool _updateCheckInProgress;
    private bool _manualUpdateResultRequested;

    public MainWindow() : this(null, enableMonitoring: true)
    {
    }

    internal MainWindow(
        ITrayIconService? trayIcon,
        bool enableMonitoring,
        IUpdateService? updateService = null)
    {
        InitializeComponent();

        _trayIcon = trayIcon ?? new TrayIconService();
        _updateService = updateService ?? new GitHubUpdateService();
        AppVersionText.Text = AppVersion.Display;
        UpdateTrayTooltip();
        _localTokenMonitor = new LocalSessionTokenMonitor();
        _claudeMonitor = new ClaudeUsageMonitor();
        _monitoringEnabled = enableMonitoring;

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMinutes(1),
        };
        _refreshTimer.Tick += RefreshTimer_Tick;

        _clockTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(10),
        };
        _clockTimer.Tick += (_, _) => UpdateResetLabels();

        _client.RefreshSuggested += Client_RefreshSuggested;
        _localTokenMonitor.UsageUpdated += LocalTokenMonitor_UsageUpdated;
        _claudeMonitor.UsageUpdated += ClaudeMonitor_UsageUpdated;
        _trayIcon.ShowRequested += TrayIcon_ShowRequested;
        _trayIcon.RefreshRequested += TrayIcon_RefreshRequested;
        _trayIcon.UpdateRequested += TrayIcon_UpdateRequested;
        _trayIcon.ExitRequested += TrayIcon_ExitRequested;
        Loc.Changed += Loc_Changed;
        Loaded += MainWindow_Loaded;
    }

    private void Loc_Changed(object? sender, EventArgs e)
    {
        RunOnDispatcher(OnLanguageChanged);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DwmWindowChrome.ApplyRoundedCorners(
            new System.Windows.Interop.WindowInteropHelper(this).Handle,
            borderColor: 0x00423A2E);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        if (!_monitoringEnabled)
        {
            Topmost = false;
            return;
        }

        RestorePlacement();
        // Normalise the header captions (arrow suffix) for whatever state placement restored.
        SetExpandedPanel(_expandedPanel);
        _localTokenMonitor.Start();
        _claudeMonitor.Start();
        _refreshTimer.Start();
        _clockTimer.Start();
        _ = CheckForUpdatesAfterStartupAsync();
        await RefreshUsageAsync();
    }

    private async Task CheckForUpdatesAfterStartupAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4), _lifetimeCancellation.Token);
            await CheckForUpdatesAsync(userInitiated: false);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // Normal shutdown while the delayed automatic check is pending.
        }
    }

    private async void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        await RefreshUsageAsync();
    }

    private void Client_RefreshSuggested(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => _ = RefreshUsageAsync()));
    }

    private void LocalTokenMonitor_UsageUpdated(TodayTokenUsage usage)
    {
        if (_isClosing)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                _todayTokenUsage = usage;
                UpdateTrayTooltip();
                if (_activeProvider == UsageProvider.Codex)
                {
                    RenderCodex();
                }
            }));
    }

    private void ClaudeMonitor_UsageUpdated(ClaudeUsageSnapshot usage)
    {
        if (_isClosing)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => ApplyClaudeSnapshot(usage)));
    }

    internal void ApplyClaudeSnapshot(ClaudeUsageSnapshot usage)
    {
        if (_isClosing)
        {
            return;
        }

        _claudeSnapshot = usage;
        UpdateTrayTooltip();
        if (_activeProvider == UsageProvider.Claude)
        {
            RenderClaude();
        }

        if (IsGaugeVisible)
        {
            PushGaugeValues();
        }
    }

    private async Task RefreshUsageAsync(bool userInitiated = false)
    {
        _claudeMonitor.Refresh(userInitiated);
        _localTokenMonitor.Refresh();
        if (_refreshInProgress || _isClosing)
        {
            return;
        }

        _refreshInProgress = true;
        RefreshButton.IsEnabled = false;
        if (_activeProvider == UsageProvider.Codex && _currentSnapshot is null)
        {
            ConnectionText.Text = Loc.T("conn.reading");
            StatusDot.Fill = WarningBrush;
        }
        else if (_activeProvider == UsageProvider.Claude && _claudeSnapshot is null)
        {
            ConnectionText.Text = Loc.T("conn.readingClaude");
            StatusDot.Fill = WarningBrush;
        }

        try
        {
            var snapshot = await _client.RefreshAsync(_lifetimeCancellation.Token);
            _codexError = null;
            ApplySnapshot(snapshot);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // Window is closing.
        }
        catch (Exception exception)
        {
            _codexError = exception;
            var staleSnapshot = _currentSnapshot is null
                ? string.Empty
                : $", staleSnapshotAt={_currentSnapshot.FetchedAt:O}, " +
                  $"weeklyUsedPercent={_currentSnapshot.RateLimits.Weekly?.UsedPercent.ToString() ?? "unknown"}";
            DiagnosticsLog.Write(
                "MainWindow",
                $"Codex refresh failed: {exception.GetType().FullName}: {exception.Message}{staleSnapshot}");
            UpdateTrayTooltip();
            ApplyError();
        }
        finally
        {
            _refreshInProgress = false;
            if (!_isClosing)
            {
                RefreshButton.IsEnabled = true;
            }
        }
    }

    private void ApplySnapshot(CodexUsageSnapshot snapshot)
    {
        if (_isClosing)
        {
            return;
        }

        _currentSnapshot = snapshot;
        UpdateTrayTooltip();
        if (_activeProvider == UsageProvider.Codex)
        {
            RenderCodex();
        }

        if (IsGaugeVisible)
        {
            PushGaugeValues();
        }
    }

    private void RenderActiveProvider()
    {
        if (!IsLoaded || _isClosing)
        {
            return;
        }

        if (_activeProvider == UsageProvider.Codex)
        {
            RenderCodex();
        }
        else
        {
            RenderClaude();
        }

        RenderExpandedPanel();
    }

    private void RenderCodex()
    {
        BalanceCaption.Text = Loc.T("card.balance");
        ResetCreditsCaption.Text = Loc.T("card.resetCredits");
        LifetimeTokensCaption.Text = Loc.T("card.lifetimeTokens");
        BalanceText.FontSize = 18;
        ResetCreditsText.FontSize = 18;

        if (_currentSnapshot is not { } snapshot)
        {
            var localOnlyUsage = CreateLocalOnlyUsage(_todayTokenUsage);
            var errorStatus = _codexError is null
                ? Loc.T("status.readingCodexToken")
                : localOnlyUsage is null
                    ? GetFriendlyError(_codexError)
                    : Loc.T("status.errWithLocal", GetFriendlyError(_codexError));
            ApplyRateWindow(null, FiveHourRemainingText, FiveHourUsedText, FiveHourProgress, FiveHourResetText, Loc.T("card.waitingCodex"));
            ApplyRateWindow(null, WeeklyRemainingText, WeeklyUsedText, WeeklyProgress, WeeklyResetText, Loc.T("card.waitingCodex"));
            BalanceText.Text = "--";
            ResetCreditsText.Text = "--";
            ApplyTokenUsageDisplay(localOnlyUsage, errorStatus, errorStatus);
            StatusDot.Fill = _codexError is null ? WarningBrush : DangerBrush;
            ConnectionText.Text = _codexError is null
                ? Loc.T("conn.readingCodex")
                : localOnlyUsage is null
                    ? Loc.T("conn.codexFailed")
                    : Loc.T("conn.codexLocal");
            UpdatedText.Text = _codexError is null ? string.Empty : Loc.T("time.failed", TimeStamp(DateTime.Now));
            RenderExpandedPanel();
            return;
        }

        var isStale = _codexError is not null;
        ApplyRateWindow(
            snapshot.RateLimits.FiveHour,
            FiveHourRemainingText,
            FiveHourUsedText,
            FiveHourProgress,
            FiveHourResetText,
            isStale: isStale);
        ApplyRateWindow(
            snapshot.RateLimits.Weekly,
            WeeklyRemainingText,
            WeeklyUsedText,
            WeeklyProgress,
            WeeklyResetText,
            isStale: isStale);

        BalanceText.Text = FormatBalance(snapshot.RateLimits.Credits);
        ResetCreditsText.Text = snapshot.RateLimits.AvailableResetCount is { } count
            ? Loc.T("unit.times", count)
            : "--";

        AccountTokenUsage? displayUsage = null;
        var tokenStatus = Loc.T("status.codexNoHistory");
        if (snapshot.TokenUsage is { } tokenUsage)
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            var reconciled = TokenUsageReconciler.Reconcile(tokenUsage, _todayTokenUsage, today);
            displayUsage = reconciled.Usage;
            var latest = displayUsage.LatestDay;
            var delayDays = latest is null ? 0 : today.DayNumber - latest.Date.DayNumber;
            tokenStatus = reconciled.LocalRealtimeApplied
                ? Loc.T("status.localRealtime")
                : delayDays > 0 && latest is not null
                    ? Loc.T("status.historyDelay", Loc.MonthDay(latest.Date), delayDays)
                    : Loc.T("status.tokenRefresh");
        }
        else if (string.IsNullOrWhiteSpace(snapshot.TokenUsageError))
        {
            tokenStatus = Loc.T("status.tokenUnavailable");
        }

        ApplyTokenUsageDisplay(displayUsage, tokenStatus, tokenStatus);

        var observedUsage = new[]
            {
                snapshot.RateLimits.FiveHour?.UsedPercent,
                snapshot.RateLimits.Weekly?.UsedPercent,
            }
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .DefaultIfEmpty(0)
            .Max();
        StatusDot.Fill = isStale ? DangerBrush : GetUsageBrush(observedUsage);
        ConnectionText.Text = isStale
            ? Loc.T("conn.stale")
            : snapshot.Detail?.Source is { } source
                ? Loc.T("conn.liveWithSource", Loc.T(source))
                : Loc.T("conn.live");
        UpdatedText.Text = isStale
            ? Loc.T("time.stale", TimeStamp(snapshot.FetchedAt))
            : Loc.T("time.updated", TimeStamp(snapshot.FetchedAt));
        RenderExpandedPanel();
    }

    private void RenderClaude()
    {
        LifetimeTokensCaption.Text = Loc.T("card.localLifetimeTokens");

        var snapshot = _claudeSnapshot;
        var account = snapshot?.Account;
        var status = snapshot?.Status;
        var unavailableText = snapshot?.AccountError is null
            ? Loc.T("card.waitingClaude")
            : Loc.T("card.quotaUnavailable");

        ApplyRateWindow(
            account?.FiveHour,
            FiveHourRemainingText,
            FiveHourUsedText,
            FiveHourProgress,
            FiveHourResetText,
            unavailableText);
        ApplyRateWindow(
            account?.Weekly,
            WeeklyRemainingText,
            WeeklyUsedText,
            WeeklyProgress,
            WeeklyResetText,
            unavailableText);

        ApplyClaudeExtraUsageCard(account?.ExtraUsage, account?.Wallet);
        ApplyClaudeSecondaryCard(account, snapshot);

        var tokenStatus = snapshot switch
        {
            null => Loc.T("conn.readingClaudeQuota"),
            // Throttled but holding real numbers: say when they were taken, not that they are wrong.
            { Account: { } held, IsThrottled: true } => Loc.T(
                "status.claudeThrottledWithData",
                TimeStamp(held.FetchedAt),
                FormatRetry(snapshot.ThrottledFor)),
            { IsThrottled: true } => Loc.T("status.claudeThrottled", FormatRetry(snapshot.ThrottledFor)),
            { Account: not null, AccountError: not null } => Loc.T("status.claudeStale", snapshot.AccountError),
            { Account: not null } => Loc.T("status.claudeOfficial"),
            { AccountError: { } error } => error,
            { IsClaudeAvailable: false } => Loc.T("status.claudeNotDetected"),
            _ => Loc.T("conn.readingClaudeQuota"),
        };
        ApplyTokenUsageDisplay(snapshot?.TokenUsage, tokenStatus, tokenStatus);

        if (account is not null)
        {
            var observedUsage = new[] { account.FiveHour?.UsedPercent, account.Weekly?.UsedPercent }
                .Where(value => value.HasValue)
                .Select(value => value!.Value)
                .DefaultIfEmpty(0)
                .Max();
            StatusDot.Fill = snapshot?.AccountError is null ? GetUsageBrush(observedUsage) : WarningBrush;
            ConnectionText.Text = snapshot?.AccountError is null
                ? Loc.T("conn.claudeLive")
                : Loc.T("conn.claudeUpdateFailed");
            UpdatedText.Text = Loc.T("time.updated", TimeStamp(account.FetchedAt));
        }
        else
        {
            StatusDot.Fill = snapshot?.AccountError is null ? WarningBrush : DangerBrush;
            ConnectionText.Text = snapshot?.AccountError is null
                ? Loc.T("conn.readingClaudeQuota")
                : Loc.T("conn.claudeQuotaFailed");
            UpdatedText.Text = snapshot is null
                ? string.Empty
                : Loc.T("time.check", TimeStamp(snapshot.FetchedAt));
        }

        RenderExpandedPanel();
    }

    /// <summary>Formats the local clock time; a fixed 24-hour format reads the same in every language.</summary>
    private static string TimeStamp(DateTime value) => value.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    private static string TimeStamp(DateTimeOffset value) =>
        value.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    private static string FormatRetry(TimeSpan remaining)
    {
        return remaining.TotalMinutes >= 1
            ? Loc.T("retry.minutes", (int)Math.Ceiling(remaining.TotalMinutes))
            : Loc.T("retry.seconds", Math.Max(1, (int)remaining.TotalSeconds));
    }

    /// <summary>
    /// Mirrors Codex's "充值余额" slot. The prepaid wallet is the closer analogue, so it wins the
    /// slot when the account has one; otherwise this falls back to the period's usage-credit spend.
    /// </summary>
    private void ApplyClaudeExtraUsageCard(ClaudeExtraUsage? extraUsage, ClaudeCreditWallet? wallet)
    {
        if (wallet?.Balance is { } balance)
        {
            BalanceCaption.Text = wallet.AutoReloadEnabled
                ? Loc.T("card.creditBalanceAutoReload")
                : Loc.T("card.creditBalance");
            BalanceText.FontSize = 18;
            BalanceText.Text = FormatMoney(balance, wallet.Currency);
            return;
        }

        BalanceCaption.Text = Loc.T("card.usageCredit");
        if (extraUsage is null)
        {
            BalanceText.FontSize = 18;
            BalanceText.Text = "--";
            return;
        }

        if (!extraUsage.IsEnabled)
        {
            BalanceText.FontSize = 12;
            BalanceText.Text = Loc.T("card.notEnabled");
            return;
        }

        if (extraUsage is { UsedAmount: { } used, LimitAmount: { } limit })
        {
            BalanceText.FontSize = 12;
            BalanceText.Text =
                $"{FormatMoney(used, extraUsage.Currency)} / {FormatMoney(limit, extraUsage.Currency)}";
            return;
        }

        BalanceText.FontSize = 18;
        BalanceText.Text = extraUsage.UsedPercent is { } percent ? $"{percent}%" : Loc.T("card.enabled");
    }

    private static string FormatMoney(decimal amount, string currency)
    {
        var symbol = currency.Equals("USD", StringComparison.OrdinalIgnoreCase) ? "$" : string.Empty;
        return $"{symbol}{amount.ToString("0.##", CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// Prefers a model-scoped weekly bucket, then the status line's context window, then the plan
    /// name - whichever the account actually reports.
    /// </summary>
    private void ApplyClaudeSecondaryCard(ClaudeAccountUsage? account, ClaudeUsageSnapshot? snapshot)
    {
        if (account?.ScopedLimits is { Count: > 0 } scoped)
        {
            var limit = scoped[0];
            ResetCreditsCaption.Text = limit.DisplayName;
            ResetCreditsText.FontSize = 18;
            ResetCreditsText.Text = $"{limit.RemainingPercent}%";
            return;
        }

        if (snapshot?.ContextUsedPercent is { } contextPercent)
        {
            ResetCreditsCaption.Text = Loc.T("card.context");
            ResetCreditsText.FontSize = 18;
            ResetCreditsText.Text = $"{contextPercent}%";
            return;
        }

        ResetCreditsCaption.Text = Loc.T("card.plan");
        ResetCreditsText.FontSize = 14;
        ResetCreditsText.Text = FormatPlanName(account?.SubscriptionType) ?? "--";
    }

    private static string? FormatPlanName(string? subscriptionType)
    {
        if (string.IsNullOrWhiteSpace(subscriptionType))
        {
            return null;
        }

        return subscriptionType.ToLowerInvariant() switch
        {
            "plus" => "Plus",
            "pro" => "Pro",
            "max" => "Max",
            "team" => "Team",
            "enterprise" => "Enterprise",
            "free" => "Free",
            _ => subscriptionType,
        };
    }

    private void ApplyRateWindow(
        RateLimitWindow? window,
        TextBlock remainingText,
        TextBlock usedText,
        ProgressBar progress,
        TextBlock resetText,
        string? unavailableText = null,
        bool isStale = false)
    {
        var opacity = isStale ? 0.55 : 1d;
        remainingText.Opacity = opacity;
        usedText.Opacity = opacity;
        progress.Opacity = opacity;
        resetText.Opacity = opacity;

        if (window is null)
        {
            remainingText.Text = "—";
            usedText.Text = Loc.T("card.notProvided");
            progress.Value = 0;
            progress.Foreground = UnavailableBrush;
            resetText.Text = unavailableText ?? Loc.T("card.noWindow");
            return;
        }

        remainingText.Text = isStale
            ? $"~{window.RemainingPercent}%"
            : $"{window.RemainingPercent}%";
        usedText.Text = Loc.T(isStale ? "card.usedApprox" : "card.used", window.UsedPercent);
        progress.Value = window.UsedPercent;
        progress.Foreground = GetUsageBrush(window.UsedPercent);
        resetText.Text = FormatResetTime(window.ResetsAt);
    }

    private void ApplyTokenUsageDisplay(
        AccountTokenUsage? tokenUsage,
        string unavailableStatus,
        string availableStatus)
    {
        var available = tokenUsage is not null;
        HistoryButton.IsEnabled = available;
        HistoryMenuItem.IsEnabled = available;

        if (tokenUsage is null)
        {
            _displayTokenUsage = null;
            LifetimeTokensText.Text = "--";
            LatestTokensCaption.Text = Loc.T("card.latestDay");
            LatestTokensText.Text = "--";
            SevenDayTokensText.Text = "--";
            HistoryPeriodText.Text = string.Empty;
            HistoryItemsControl.ItemsSource = Array.Empty<HistoryBar>();
            TokenStatusText.Text = unavailableStatus;
            return;
        }

        _displayTokenUsage = tokenUsage;
        LifetimeTokensText.Text = TokenFormatter.Format(tokenUsage.LifetimeTokens);
        var latest = tokenUsage.LatestDay;
        if (latest is null)
        {
            LatestTokensCaption.Text = Loc.T("card.latestDay");
            LatestTokensText.Text = "--";
            SevenDayTokensText.Text = "--";
            HistoryPeriodText.Text = string.Empty;
            HistoryItemsControl.ItemsSource = Array.Empty<HistoryBar>();
            TokenStatusText.Text = Loc.T("status.noDailyToken");
        }
        else
        {
            var bars = BuildHistoryBars(
                tokenUsage,
                _activeProvider == UsageProvider.Claude ? "#D9906E" : "#7EE787");
            LatestTokensCaption.Text = Loc.T("card.dayUsage", Loc.MonthDay(latest.Date));
            LatestTokensText.Text = TokenFormatter.Format(latest.Tokens);
            SevenDayTokensText.Text = TokenFormatter.Format(bars.Sum(item => item.Tokens));
            HistoryPeriodText.Text = $"{Loc.MonthDay(bars[0].Date)} – {Loc.MonthDay(bars[^1].Date)}";
            HistoryItemsControl.ItemsSource = bars;

            TokenStatusText.Text = availableStatus;
        }

    }

    private static IReadOnlyList<HistoryBar> BuildHistoryBars(
        AccountTokenUsage tokenUsage,
        string barColor)
    {
        if (tokenUsage.LatestDay is not { } latest)
        {
            return Array.Empty<HistoryBar>();
        }

        var byDate = tokenUsage.DailyUsage.ToDictionary(item => item.Date, item => item.Tokens);
        var endDate = latest.Date;
        var startDate = endDate.AddDays(-6);
        var points = Enumerable.Range(0, 7)
            .Select(offset =>
            {
                var date = startDate.AddDays(offset);
                return new
                {
                    Date = date,
                    Tokens = byDate.GetValueOrDefault(date),
                };
            })
            .ToArray();
        var maximum = Math.Max(1L, points.Max(item => item.Tokens));

        return points
            .Select(item => new HistoryBar(
                Date: item.Date,
                Tokens: item.Tokens,
                DateLabel: Loc.MonthDay(item.Date),
                TooltipDate: Loc.MonthDay(item.Date),
                TooltipTokens: $"{item.Tokens.ToString("N0", CultureInfo.InvariantCulture)} Token",
                AutomationLabel: $"{Loc.MonthDay(item.Date)} {item.Tokens.ToString("N0", CultureInfo.InvariantCulture)} Token",
                BarColor: barColor,
                BarHeight: item.Tokens == 0
                    ? 2
                    : Math.Max(5, 42d * item.Tokens / maximum),
                Opacity: item.Tokens == 0 ? 0.22 : 0.88))
            .ToArray();
    }

    private void ApplyError()
    {
        if (_activeProvider != UsageProvider.Codex)
        {
            return;
        }

        RenderCodex();
    }

    private void UpdateTrayTooltip()
    {
        var codexIsStale = _currentSnapshot is not null && _codexError is not null;
        var codexWeekly = FormatWeeklyTooltip(_currentSnapshot?.RateLimits.Weekly, codexIsStale);
        var claudeWeekly = FormatWeeklyTooltip(_claudeSnapshot?.Weekly);
        _trayIcon.ToolTipText = $"Codex周剩余:{codexWeekly} | Claude周剩余:{claudeWeekly}";
        _trayIcon.UpdateMenuStatus(
            FormatTrayStatusLine(
                "Codex",
                _currentSnapshot?.Detail?.PlanType,
                _currentSnapshot?.RateLimits.Weekly,
                codexIsStale),
            FormatTrayStatusLine(
                "Claude",
                _claudeSnapshot?.Account?.SubscriptionType,
                _claudeSnapshot?.Weekly));
    }

    /// <summary>Mirrors the Claude tray menu's "plan / usage" block for one provider.</summary>
    internal static string FormatTrayStatusLine(
        string provider,
        string? plan,
        RateLimitWindow? weekly,
        bool isStale = false)
    {
        var head = string.IsNullOrWhiteSpace(plan)
            ? provider
            : $"{provider} · {FormatPlanName(plan)}";
        return weekly is null
            ? Loc.T("tray.weeklyUnknown", head)
            : Loc.T(isStale ? "tray.weeklyUsedStale" : "tray.weeklyUsed", head, weekly.UsedPercent);
    }

    internal static string FormatWeeklyTooltip(RateLimitWindow? window, bool isStale = false)
    {
        return window is null
            ? "--"
            : isStale
                ? $"~{window.RemainingPercent}%"
                : $"{window.RemainingPercent}%";
    }

    private void UpdateResetLabels()
    {
        RateLimitWindow? fiveHour;
        RateLimitWindow? weekly;
        string unavailableText;
        if (_activeProvider == UsageProvider.Codex)
        {
            fiveHour = _currentSnapshot?.RateLimits.FiveHour;
            weekly = _currentSnapshot?.RateLimits.Weekly;
            unavailableText = _currentSnapshot is null ? Loc.T("card.waitingCodex") : Loc.T("card.noWindow");
        }
        else
        {
            fiveHour = _claudeSnapshot?.FiveHour;
            weekly = _claudeSnapshot?.Weekly;
            unavailableText = _claudeSnapshot?.AccountError is null
                ? Loc.T("card.waitingClaude")
                : Loc.T("card.quotaUnavailable");
        }

        FiveHourResetText.Text = fiveHour is null ? unavailableText : FormatResetTime(fiveHour.ResetsAt);
        WeeklyResetText.Text = weekly is null ? unavailableText : FormatResetTime(weekly.ResetsAt);
    }

    internal static string FormatResetTime(DateTimeOffset? resetsAt)
    {
        return resetsAt is null
            ? Loc.T("reset.unknown")
            : Loc.T("window.reset", Loc.MonthDayTime(resetsAt.Value));
    }

    private static string FormatBalance(CreditBalance? credits)
    {
        if (credits is null)
        {
            return "--";
        }

        if (credits.Unlimited)
        {
            return Loc.T("credit.unlimited");
        }

        return string.IsNullOrWhiteSpace(credits.Balance)
            ? credits.HasCredits ? Loc.T("credit.available") : "0"
            : credits.Balance;
    }

    private static Brush GetUsageBrush(int usedPercent)
    {
        return usedPercent switch
        {
            >= 85 => DangerBrush,
            >= 65 => WarningBrush,
            _ => HealthyBrush,
        };
    }

    private static string GetFriendlyError(Exception exception)
    {
        if (exception is FileNotFoundException)
        {
            return Loc.T("friendly.codexNotFound");
        }

        var message = exception.Message;
        if (message.Contains("login", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("not logged", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("authentication required", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("401", StringComparison.OrdinalIgnoreCase) ||
            message.Contains(Loc.T("friendly.codexLogin"), StringComparison.OrdinalIgnoreCase))
        {
            return Loc.T("friendly.codexLogin");
        }

        if (exception is UnauthorizedAccessException or Win32Exception)
        {
            return Loc.T("friendly.codexStart");
        }

        return Loc.T("friendly.usageUnavailable");
    }

    private static AccountTokenUsage? CreateLocalOnlyUsage(TodayTokenUsage? todayUsage)
    {
        if (todayUsage is not { Tokens: > 0 })
        {
            return null;
        }

        return new AccountTokenUsage(
            LifetimeTokens: null,
            PeakDailyTokens: todayUsage.Tokens,
            LongestRunningTurnSeconds: null,
            CurrentStreakDays: 1,
            LongestStreakDays: 1,
            DailyUsage: [new DailyTokenUsage(todayUsage.Date, todayUsage.Tokens)],
            FetchedAt: todayUsage.UpdatedAt);
    }

    private void RestorePlacement()
    {
        // Now that the card owns a taskbar button, WPF honours the show-state the launcher passed
        // in STARTUPINFO, which can start it iconic. The monitor should always come up visible.
        WindowState = WindowState.Normal;

        var workArea = SystemParameters.WorkArea;
        var placement = WindowPlacementStore.Load();
        if (placement is { GaugeLeft: { } gaugeLeft, GaugeTop: { } gaugeTop } &&
            double.IsFinite(gaugeLeft) && double.IsFinite(gaugeTop))
        {
            _gaugeTopLeft = new Point(gaugeLeft, gaugeTop);
        }
        if (placement?.DetailsExpanded == true)
        {
            SetExpandedPanel(ExpandedPanel.Details);
        }

        if (placement is not null && double.IsFinite(placement.Left) && double.IsFinite(placement.Top))
        {
            Left = Math.Clamp(placement.Left, workArea.Left, Math.Max(workArea.Left, workArea.Right - Width));
            Top = Math.Clamp(placement.Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - Height));
            Topmost = placement.Topmost;
        }
        else
        {
            Left = workArea.Right - Width - 20;
            Top = workArea.Top + 20;
            Topmost = true;
        }

        TopmostMenuItem.IsChecked = Topmost;
    }

    private void Card_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || IsInsideButton(e.OriginalSource as DependencyObject))
        {
            return;
        }

        // Double-click anywhere on the card (except buttons) collapses to the gauge orb.
        if (e.ClickCount == 2)
        {
            e.Handled = true;
            EnterGaugeMode();
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The mouse button may be released between the event and DragMove.
        }
    }

    /// <summary>
    /// Collapses the full card to the floating gauge orb. The window keeps running and polling; only
    /// its visible form changes, so live data still flows to the orb.
    /// </summary>
    internal void EnterGaugeMode()
    {
        if (_isClosing)
        {
            return;
        }

        SavePlacement();

        if (_gaugeWindow is null)
        {
            _gaugeWindow = new GaugeWindow { Topmost = Topmost };
            _gaugeWindow.RestoreRequested += (_, _) => RunOnDispatcher(ExitGaugeMode);
            _gaugeWindow.UpdateRequested += GaugeWindow_UpdateRequested;
            _gaugeWindow.TopmostChangedRequested += GaugeWindow_TopmostChangedRequested;
        }

        PushGaugeValues();

        // The card and orb own independent positions. The card is only used as the first-run
        // default; every later switch restores the orb's own last position.
        if (_gaugeTopLeft is { } savedGauge)
        {
            _gaugeWindow.PlaceOrbAt(savedGauge.X, savedGauge.Y);
        }
        else if (double.IsFinite(Left) && double.IsFinite(Top))
        {
            _gaugeWindow.PlaceOrbAt(Left, Top);
            _gaugeTopLeft = new Point(Left, Top);
        }

        _gaugeWindow.Show();
        _gaugeWindow.Activate();
        ShowInTaskbar = false;
        Hide();
    }

    internal void ExitGaugeMode()
    {
        if (_isClosing)
        {
            return;
        }

        if (_gaugeWindow is not null)
        {
            CaptureGaugePlacement();
            _gaugeWindow.Hide();
        }

        SavePlacement();
        Show();
        EnsureRestoredWindow();
        ClampToWorkArea();
    }

    private bool IsGaugeVisible => _gaugeWindow is { IsVisible: true };

    internal GaugeWindow? GaugeWindowForTesting => _gaugeWindow;

    /// <summary>Feeds the orb each provider's remaining quota (the water level), 5-hour window if present.</summary>
    private void PushGaugeValues()
    {
        _gaugeWindow?.SetValues(
            GaugeRemainingPercent(_currentSnapshot?.RateLimits.FiveHour, _currentSnapshot?.RateLimits.Weekly),
            GaugeRemainingPercent(_claudeSnapshot?.FiveHour, _claudeSnapshot?.Weekly));
    }

    /// <summary>The window the orb reflects: the 5-hour one if present, else weekly (Codex's current shape).</summary>
    internal static int? GaugeUsedPercent(RateLimitWindow? fiveHour, RateLimitWindow? weekly) =>
        (fiveHour ?? weekly)?.UsedPercent;

    /// <summary>
    /// Remaining quota for the orb's water line: the inverse of <see cref="GaugeUsedPercent"/>, so a
    /// full tank is 100% (fresh) and a dry tank is 0% (exhausted). Null when there is no window at all.
    /// </summary>
    internal static int? GaugeRemainingPercent(RateLimitWindow? fiveHour, RateLimitWindow? weekly) =>
        GaugeUsedPercent(fiveHour, weekly) is { } used ? 100 - Math.Clamp(used, 0, 100) : null;

    private static bool IsInsideButton(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is ButtonBase)
            {
                return true;
            }

            element = VisualTreeHelper.GetParent(element);
        }

        return false;
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshUsageAsync(userInitiated: true);
    }

    private async void RefreshMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await RefreshUsageAsync(userInitiated: true);
    }

    private async void CheckUpdatesMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await CheckForUpdatesAsync(userInitiated: true);
    }

    private async Task CheckForUpdatesAsync(bool userInitiated)
    {
        if (_isClosing || _updateCheckInProgress)
        {
            if (userInitiated && !_isClosing)
            {
                _manualUpdateResultRequested = true;
            }

            return;
        }

        _updateCheckInProgress = true;
        try
        {
            var result = await _updateService.CheckForUpdateAsync(_lifetimeCancellation.Token);
            if (result.IsUpdateAvailable)
            {
                UpdateDialog.ShowAvailable(
                    IsVisible ? this : null,
                    result.Release,
                    _updateService,
                    Topmost);
            }
            else if (userInitiated || _manualUpdateResultRequested)
            {
                UpdateDialog.ShowInformation(
                    IsVisible ? this : null,
                    Loc.T("update.upToDate", AppVersion.Display),
                    Topmost);
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // Closing the app cancels both automatic and user-initiated checks.
        }
        catch (Exception exception)
        {
            if (userInitiated || _manualUpdateResultRequested)
            {
                var message = exception is UpdateServiceException updateException
                    ? Loc.T(updateException.ResourceKey)
                    : Loc.T("update.checkFailed");
                UpdateDialog.ShowInformation(IsVisible ? this : null, message, Topmost);
            }
        }
        finally
        {
            _updateCheckInProgress = false;
            _manualUpdateResultRequested = false;
        }
    }

    private void ProviderTab_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string provider })
        {
            return;
        }

        _activeProvider = provider.Equals("Claude", StringComparison.OrdinalIgnoreCase)
            ? UsageProvider.Claude
            : UsageProvider.Codex;
        RenderActiveProvider();
    }

    /// <summary>
    /// Retitles the two panel entries to whatever they would actually do right now, so the menu
    /// never offers to expand something that is already open.
    /// </summary>
    private void CardContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        DetailsMenuItem.Header = Loc.T(_expandedPanel == ExpandedPanel.Details
            ? "menu.collapseDetails"
            : "menu.expandDetails");
        HistoryMenuItem.Header = Loc.T(_expandedPanel == ExpandedPanel.History
            ? "menu.collapseHistory"
            : "menu.expandHistory");
        TopmostMenuItem.IsChecked = Topmost;

        if (sender is ContextMenu menu)
        {
            EnsureLanguageMenu(menu);
        }
    }

    private void DetailsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        SetExpandedPanel(_expandedPanel == ExpandedPanel.Details
            ? ExpandedPanel.None
            : ExpandedPanel.Details);
    }

    private void DetailsButton_Click(object sender, RoutedEventArgs e)
    {
        SetExpandedPanel(_expandedPanel == ExpandedPanel.Details
            ? ExpandedPanel.None
            : ExpandedPanel.Details);
    }

    private void HistoryButton_Click(object sender, RoutedEventArgs e)
    {
        SetExpandedPanel(_expandedPanel == ExpandedPanel.History
            ? ExpandedPanel.None
            : ExpandedPanel.History);
    }

    /// <summary>
    /// Details and history share one expansion slot, so opening either collapses the other and the
    /// window only ever grows by a single panel's height.
    /// </summary>
    private void SetExpandedPanel(ExpandedPanel panel)
    {
        if (panel == ExpandedPanel.History && !HistoryButton.IsEnabled)
        {
            panel = ExpandedPanel.None;
        }

        _expandedPanel = panel;
        var expanded = panel != ExpandedPanel.None;
        ExpandRow.Height = expanded ? new GridLength(ExpandPanelHeight) : new GridLength(0);
        DetailsPanel.Visibility = panel == ExpandedPanel.Details ? Visibility.Visible : Visibility.Collapsed;
        HistoryPanel.Visibility = panel == ExpandedPanel.History ? Visibility.Visible : Visibility.Collapsed;
        DetailsButtonText.Text = Loc.T(
            "header.detailsArrow",
            panel == ExpandedPanel.Details ? "▴" : "▾");
        HistoryButtonText.Text = Loc.T(
            "header.historyArrow",
            panel == ExpandedPanel.History ? "▴" : "▾");

        // The window has a fixed height, so grow it by exactly the row the panel occupies and keep
        // the card anchored where the user put it.
        var height = expanded ? CollapsedHeight + ExpandPanelHeight : CollapsedHeight;
        MinHeight = height;
        Height = height;
        ClampToWorkArea();
        RenderExpandedPanel();
    }

    private void ClampToWorkArea()
    {
        var workArea = SystemParameters.WorkArea;
        if (double.IsFinite(Top) && Top + Height > workArea.Bottom)
        {
            Top = Math.Max(workArea.Top, workArea.Bottom - Height);
        }
    }

    private void RenderExpandedPanel()
    {
        switch (_expandedPanel)
        {
            case ExpandedPanel.Details:
                DetailsItemsControl.ItemsSource = _activeProvider == UsageProvider.Codex
                    ? UsageDetailBuilder.BuildCodex(
                        _currentSnapshot,
                        _codexError is null ? null : GetFriendlyError(_codexError))
                    : UsageDetailBuilder.BuildClaude(_claudeSnapshot);
                break;
            case ExpandedPanel.History:
                RenderHistoryList();
                break;
        }
    }

    private void RenderHistoryList()
    {
        var providerName = _activeProvider == UsageProvider.Claude ? "Claude" : "Codex";
        HistoryTitleText.Text = Loc.T("history.title", providerName);

        if (_displayTokenUsage is not { } usage || usage.DailyUsage.Count == 0)
        {
            HistorySubtitleText.Text = Loc.T("history.noDaily");
            HistoryListItemsControl.ItemsSource = Array.Empty<HistoryRow>();
            return;
        }

        HistorySubtitleText.Text = Loc.T(
            "history.subtitle",
            usage.DailyUsage.Count,
            usage.LatestDay!.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var color = _activeProvider == UsageProvider.Claude ? "#D9906E" : "#7EE787";
        var maximum = Math.Max(1L, usage.DailyUsage.Max(item => item.Tokens));

        // Newest first: the recent days are what the user came to look at.
        HistoryListItemsControl.ItemsSource = usage.DailyUsage
            .OrderByDescending(item => item.Date)
            .Select(item => new HistoryRow(
                DateLabel: item.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                TokensLabel: item.Tokens.ToString("N0", CultureInfo.InvariantCulture),
                AutomationLabel: $"{item.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} {item.Tokens.ToString("N0", CultureInfo.InvariantCulture)} Token",
                BarWidth: Math.Max(2d, 150d * item.Tokens / maximum),
                BarColor: color))
            .ToArray();
    }

    /// <summary>
    /// Builds (once) and refreshes the language sub-menu shared by the card and tray menus. Each
    /// language is a checkable item; picking one calls <see cref="Loc.SetLanguage"/>, which fires
    /// the app-wide re-render.
    /// </summary>
    private void EnsureLanguageMenu(ContextMenu menu)
    {
        var languageItem = menu.Items.OfType<MenuItem>()
            .FirstOrDefault(item => (item.Tag as string) == "language");
        if (languageItem is null)
        {
            languageItem = new MenuItem { Tag = "language" };
            languageItem.SetResourceReference(StyleProperty, "ModernMenuItemStyle");
            foreach (var language in Loc.All)
            {
                var captured = language;
                var child = new MenuItem
                {
                    Header = Loc.DisplayName(language),
                    IsCheckable = true,
                    Tag = language,
                };
                child.SetResourceReference(StyleProperty, "ModernMenuItemStyle");
                child.Click += (_, _) => Loc.SetLanguage(captured);
                languageItem.Items.Add(child);
            }

            // Sits just above the trailing exit/hide actions.
            var insertAt = Math.Max(0, menu.Items.Count - 1);
            menu.Items.Insert(insertAt, languageItem);
        }

        languageItem.Header = Loc.T("menu.language");
        foreach (var child in languageItem.Items.OfType<MenuItem>())
        {
            child.IsChecked = child.Tag is AppLanguage language && language == Loc.Current;
        }
    }

    /// <summary>Re-runs every imperative label after a language change; bound XAML updates itself.</summary>
    private void OnLanguageChanged()
    {
        if (_isClosing)
        {
            return;
        }

        UpdateTrayTooltip();
        SetExpandedPanel(_expandedPanel);
        RenderActiveProvider();
    }

    private void HistoryMenuItem_Click(object sender, RoutedEventArgs e)
    {
        SetExpandedPanel(_expandedPanel == ExpandedPanel.History
            ? ExpandedPanel.None
            : ExpandedPanel.History);
    }

    private void TopmostMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Topmost = TopmostMenuItem.IsChecked;
        if (_gaugeWindow is not null)
        {
            _gaugeWindow.Topmost = Topmost;
        }
    }

    private void GaugeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        EnterGaugeMode();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        HideToTray();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        MinimizeToTaskbar();
    }

    private void MinimizeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        MinimizeToTaskbar();
    }

    private void HideMenuItem_Click(object sender, RoutedEventArgs e)
    {
        HideToTray();
    }

    /// <summary>Minimises like an ordinary window; the taskbar button stays put.</summary>
    private void MinimizeToTaskbar()
    {
        if (_isClosing)
        {
            return;
        }

        SavePlacement();
        WindowState = WindowState.Minimized;
    }

    /// <summary>
    /// Hides completely: the tray icon becomes the only way back, so the taskbar button goes away
    /// with the window. This is the one state that drops it.
    /// </summary>
    private void HideToTray()
    {
        if (_isClosing)
        {
            return;
        }

        SavePlacement();
        WindowState = WindowState.Normal;
        ShowInTaskbar = false;
        Hide();
    }

    private void SavePlacement()
    {
        // Left/Top are meaningless once minimised, so capture before changing the state.
        if (_monitoringEnabled && WindowState == WindowState.Normal)
        {
            CaptureGaugePlacement();
            WindowPlacementStore.Save(
                CreateWindowPlacement(Left, Top));
        }
    }

    private WindowPlacement CreateWindowPlacement(double mainLeft, double mainTop) => new(
        mainLeft,
        mainTop,
        Topmost,
        _expandedPanel == ExpandedPanel.Details,
        _gaugeTopLeft?.X,
        _gaugeTopLeft?.Y);

    private void CaptureGaugePlacement()
    {
        if (_gaugeWindow is not { } gauge)
        {
            return;
        }

        var position = gauge.GetOrbTopLeft();
        if (double.IsFinite(position.X) && double.IsFinite(position.Y))
        {
            _gaugeTopLeft = position;
        }
    }


    private void RestoreFromTray()
    {
        if (_isClosing)
        {
            return;
        }

        // If the orb is up, restoring means leaving gauge mode back to the full card.
        if (IsGaugeVisible)
        {
            ExitGaugeMode();
            return;
        }

        Show();
        EnsureRestoredWindow();
        Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(EnsureRestoredWindow));
    }

    private void EnsureRestoredWindow()
    {
        if (_isClosing)
        {
            return;
        }

        if (!IsVisible)
        {
            Show();
        }

        // Coming back from the tray restores the taskbar button too.
        ShowInTaskbar = true;

        WindowState = WindowState.Normal;
        Width = Math.Max(Width, MinWidth);
        Height = Math.Max(Height, MinHeight);
        InvalidateMeasure();
        InvalidateArrange();
        UpdateLayout();
        Activate();
    }

    private void TrayIcon_ShowRequested(object? sender, EventArgs e)
    {
        RunOnDispatcher(RestoreFromTray);
    }

    internal void RestoreFromExternalLaunch()
    {
        RestoreFromTray();
    }

    private void TrayIcon_RefreshRequested(object? sender, EventArgs e)
    {
        RunOnDispatcher(() => _ = RefreshUsageAsync(userInitiated: true));
    }

    private void TrayIcon_UpdateRequested(object? sender, EventArgs e)
    {
        RunOnDispatcher(() => _ = CheckForUpdatesAsync(userInitiated: true));
    }

    private void GaugeWindow_UpdateRequested(object? sender, EventArgs e)
    {
        RunOnDispatcher(() => _ = CheckForUpdatesAsync(userInitiated: true));
    }

    private void GaugeWindow_TopmostChangedRequested(bool isTopmost)
    {
        RunOnDispatcher(() =>
        {
            Topmost = isTopmost;
            TopmostMenuItem.IsChecked = isTopmost;
            SavePlacement();
        });
    }

    private void TrayIcon_ExitRequested(object? sender, EventArgs e)
    {
        RunOnDispatcher(Close);
    }

    private void RunOnDispatcher(Action action)
    {
        if (Dispatcher.CheckAccess())
        {
            action();
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Normal, action);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _isClosing = true;
        _refreshTimer.Stop();
        _clockTimer.Stop();
        _lifetimeCancellation.Cancel();
        if (_monitoringEnabled)
        {
            CaptureGaugePlacement();
            WindowPlacementStore.Save(CreateWindowPlacement(Left, Top));
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _gaugeWindow?.Close();
        _gaugeWindow = null;
        _client.RefreshSuggested -= Client_RefreshSuggested;
        _localTokenMonitor.UsageUpdated -= LocalTokenMonitor_UsageUpdated;
        _claudeMonitor.UsageUpdated -= ClaudeMonitor_UsageUpdated;
        _trayIcon.ShowRequested -= TrayIcon_ShowRequested;
        _trayIcon.RefreshRequested -= TrayIcon_RefreshRequested;
        _trayIcon.UpdateRequested -= TrayIcon_UpdateRequested;
        _trayIcon.ExitRequested -= TrayIcon_ExitRequested;
        Loc.Changed -= Loc_Changed;
        _client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _localTokenMonitor.Dispose();
        _claudeMonitor.Dispose();
        _trayIcon.Dispose();
        _updateService.Dispose();
        _lifetimeCancellation.Dispose();
        base.OnClosed(e);
    }

    private static Brush CreateBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private sealed record HistoryBar(
        DateOnly Date,
        long Tokens,
        string DateLabel,
        string TooltipDate,
        string TooltipTokens,
        string AutomationLabel,
        string BarColor,
        double BarHeight,
        double Opacity);

    private sealed record HistoryRow(
        string DateLabel,
        string TokensLabel,
        string AutomationLabel,
        double BarWidth,
        string BarColor);

    private enum UsageProvider
    {
        Codex,
        Claude,
    }

    private enum ExpandedPanel
    {
        None,
        Details,
        History,
    }
}
