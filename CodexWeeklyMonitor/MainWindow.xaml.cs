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
    private const double CollapsedHeight = 470;
    private const double ExpandPanelHeight = 236;
    private const double QuotaRowHeight = 116;
    private const double QuotaRowGap = 10;
    private const double ExtraQuotaRowStep = QuotaRowHeight + QuotaRowGap;

    private static readonly Brush WarningBrush = QuotaColorScale.BrushForRemaining(50);
    private static readonly Brush DangerBrush = QuotaColorScale.BrushForRemaining(0);
    private static readonly Brush UnavailableBrush = CreateBrush(0x5B, 0x66, 0x72);
    private static readonly Brush AntigravityBarBrush = CreateBrush(0x58, 0xA6, 0xFF);

    /// <summary>How often a long-running instance re-checks GitHub for a newer release.</summary>
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(6);

    /// <summary>
    /// How long a version the user already declined stays quiet. Without it, a release the user
    /// answered "Later" to would ask again on every check for as long as the app keeps running.
    /// </summary>
    private static readonly TimeSpan UpdatePromptCooldown = TimeSpan.FromHours(24);

    private readonly CodexUsageProvider _client = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly ITrayIconService _trayIcon;
    private readonly IUpdateService _updateService;
    private readonly LocalSessionTokenMonitor _localTokenMonitor;
    private readonly ClaudeUsageMonitor _claudeMonitor;
    private readonly AntigravityUsageMonitor _antigravityMonitor;
    private readonly ExternalProviderMonitor _externalMonitor;
    private readonly bool _monitoringEnabled;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _updateTimer;

    private CodexUsageSnapshot? _currentSnapshot;
    private ClaudeUsageSnapshot? _claudeSnapshot;
    private AntigravityUsageSnapshot? _antigravitySnapshot;
    private readonly Dictionary<ExternalProviderId, ExternalProviderSnapshot> _externalSnapshots = new();
    private TodayTokenUsage? _todayTokenUsage;
    private AccountTokenUsage? _displayTokenUsage;
    private Exception? _codexError;
    private UsageProvider _activeProvider = UsageProvider.Codex;
    private bool _refreshInProgress;
    private bool _isClosing;
    private bool _initialized;
    private ExpandedPanel _expandedPanel = ExpandedPanel.None;
    private GaugeWindow? _gaugeWindow;
    private string _leftGaugeProvider = "Codex";
    private string _rightGaugeProvider = "Claude";
    private Point? _gaugeTopLeft;
    private bool _updateCheckInProgress;
    private bool _manualUpdateResultRequested;
    private Version? _promptedVersion;
    private DateTimeOffset _promptedAt;
    private int _extraQuotaRows;
    private readonly List<(TextBlock ResetText, RateLimitWindow? Window)> _extraQuotaResetTargets = [];
    private QuotaPeriod _antigravityQuotaPeriod = QuotaPeriod.FiveHour;
    private Brush? _cardHoverBorderBrush;
    private bool _hasCustomWidth;
    private bool _hasCustomHeight;
    private double _customWidth;
    private double _customHeight;

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint WM_SETICON = 0x0080;
    private const int ICON_SMALL = 0;
    private const int ICON_BIG = 1;

    public MainWindow() : this(null, enableMonitoring: true)
    {
    }

    internal MainWindow(
        ITrayIconService? trayIcon,
        bool enableMonitoring,
        IUpdateService? updateService = null)
    {
        InitializeComponent();
        _ = new TopmostWindowController(this);

        _trayIcon = trayIcon ?? new TrayIconService();
        _updateService = updateService ?? new GitHubUpdateService();
        AppVersionText.Text = AppVersion.Display;
        UpdateTrayTooltip();
        _localTokenMonitor = new LocalSessionTokenMonitor();
        _claudeMonitor = new ClaudeUsageMonitor();
        _antigravityMonitor = new AntigravityUsageMonitor();
        _externalMonitor = new ExternalProviderMonitor(enableNetworkPolling: enableMonitoring);
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

        // Checking once at startup was enough while the app was launched by hand. It now starts
        // with Windows and can sit in the tray for weeks, so anything released in between would
        // never be noticed without a periodic re-check.
        _updateTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = UpdateCheckInterval,
        };
        _updateTimer.Tick += async (_, _) => await CheckForUpdatesAsync(userInitiated: false);

        _client.RefreshSuggested += Client_RefreshSuggested;
        _localTokenMonitor.UsageUpdated += LocalTokenMonitor_UsageUpdated;
        _claudeMonitor.UsageUpdated += ClaudeMonitor_UsageUpdated;
        _antigravityMonitor.UsageUpdated += AntigravityMonitor_UsageUpdated;
        _externalMonitor.UsageUpdated += ExternalMonitor_UsageUpdated;
        _trayIcon.ShowRequested += TrayIcon_ShowRequested;
        _trayIcon.RefreshRequested += TrayIcon_RefreshRequested;
        _trayIcon.UpdateRequested += TrayIcon_UpdateRequested;
        _trayIcon.ExitRequested += TrayIcon_ExitRequested;
        _trayIcon.StartupToggleRequested += ApplyStartupPreference;
        Loc.Changed += Loc_Changed;
        Loaded += MainWindow_Loaded;
    }

    private void Loc_Changed(object? sender, EventArgs e)
    {
        RunOnDispatcher(OnLanguageChanged);
    }

    /// <summary>
    /// Starts monitoring at sign-in and shows the orb using the saved position and providers.
    /// </summary>
    /// <remarks>
    /// The main window is never shown, not shown-then-hidden. The first attempt showed it fully
    /// transparent so that <see cref="FrameworkElement.Loaded"/> would fire, which was wrong twice
    /// over: <see cref="Window.Opacity"/> is only honoured on a window with
    /// <see cref="Window.AllowsTransparency"/> set, and clearing
    /// <see cref="Window.ShowInTaskbar"/> to hide it afterwards makes WPF destroy and recreate the
    /// HWND, so the first real appearance came up unpainted. Nothing in initialisation needs a
    /// visible window, so it simply runs without one.
    /// </remarks>
    internal void StartAtSignIn()
    {
        _ = InitializeAsync();
        ShowGauge(activate: false);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        DwmWindowChrome.ApplyRoundedCorners(
            hwnd,
            borderColor: 0x00423A2E);
        ApplyWindowNativeIcons(hwnd);
    }

    private static void ApplyWindowNativeIcons(IntPtr hwnd)
    {
        try
        {
            var iconUri = new Uri("pack://application:,,,/Assets/app.ico");
            using var stream = System.Windows.Application.GetResourceStream(iconUri)?.Stream;
            if (stream is not null)
            {
                using var icon = new System.Drawing.Icon(stream);
                SendMessage(hwnd, WM_SETICON, new IntPtr(ICON_SMALL), icon.Handle);
                SendMessage(hwnd, WM_SETICON, new IntPtr(ICON_BIG), icon.Handle);
            }
        }
        catch
        {
            // Non-critical if icon extraction fails
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await InitializeAsync();
    }

    /// <summary>
    /// Brings up placement, monitors, and timers. Driven by <see cref="FrameworkElement.Loaded"/>
    /// for a normal launch and called directly when the app starts into orb mode, so it must not
    /// depend on the window being visible - nothing here does.
    /// </summary>
    private async Task InitializeAsync()
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

        var placement = WindowPlacementStore.Load();
        RestorePlacement(placement);
        // Normalise the header captions (arrow suffix) for whatever state placement restored.
        SetExpandedPanel(_expandedPanel);
        _localTokenMonitor.Start();
        _claudeMonitor.Start();
        _antigravityMonitor.Start();
        _externalMonitor.Start();
        _refreshTimer.Start();
        _clockTimer.Start();
        _updateTimer.Start();
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

    private void AntigravityMonitor_UsageUpdated(AntigravityUsageSnapshot usage)
    {
        if (_isClosing)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => ApplyAntigravitySnapshot(usage)));
    }

    internal void ApplyAntigravitySnapshot(AntigravityUsageSnapshot usage)
    {
        if (_isClosing)
        {
            return;
        }

        _antigravitySnapshot = usage;
        if (_activeProvider == UsageProvider.Antigravity)
        {
            RenderAntigravity();
        }

        if (IsGaugeVisible)
        {
            PushGaugeValues();
        }
    }

    private void ExternalMonitor_UsageUpdated(ExternalProviderSnapshot usage)
    {
        if (_isClosing)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => ApplyExternalProviderSnapshot(usage)));
    }

    internal void ApplyExternalProviderSnapshot(ExternalProviderSnapshot usage)
    {
        if (_isClosing)
        {
            return;
        }

        _externalSnapshots[usage.Provider] = usage;
        if (TryMapExternalProvider(_activeProvider) == usage.Provider)
        {
            RenderExternalProvider(usage.Provider);
        }
        if (IsGaugeVisible)
        {
            PushGaugeValues();
        }
    }

    private static ExternalProviderId? TryMapExternalProvider(UsageProvider provider) => provider switch
    {
        UsageProvider.Grok => ExternalProviderId.Grok,
        UsageProvider.DeepSeek => ExternalProviderId.DeepSeek,
        UsageProvider.Kimi => ExternalProviderId.Kimi,
        UsageProvider.Qwen => ExternalProviderId.Qwen,
        UsageProvider.Glm => ExternalProviderId.Glm,
        UsageProvider.SiliconFlow => ExternalProviderId.SiliconFlow,
        _ => null,
    };

    private async Task RefreshUsageAsync(bool userInitiated = false)
    {
        _claudeMonitor.Refresh(userInitiated);
        _antigravityMonitor.Refresh();
        _externalMonitor.Refresh();
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
        else if (_activeProvider == UsageProvider.Antigravity && _antigravitySnapshot is null)
        {
            ConnectionText.Text = Loc.T("conn.readingAntigravity");
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

        switch (_activeProvider)
        {
            case UsageProvider.Claude:
                RenderClaude();
                break;
            case UsageProvider.Antigravity:
                RenderAntigravity();
                break;
            default:
                if (TryMapExternalProvider(_activeProvider) is { } externalId)
                {
                    RenderExternalProvider(externalId);
                }
                else
                {
                    RenderCodex();
                }

                break;
        }

        RenderExpandedPanel();
    }

    private void SetQuotaVisibility(bool showFiveHour, bool showWeekly, bool showLuna)
    {
        FiveHourQuotaCard.Visibility = showFiveHour ? Visibility.Visible : Visibility.Collapsed;
        WeeklyQuotaCard.Visibility = showWeekly ? Visibility.Visible : Visibility.Collapsed;
        LunaQuotaCard.Visibility = showLuna ? Visibility.Visible : Visibility.Collapsed;

        FiveHourColumn.Width = showFiveHour
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0);
        WeeklyColumn.Width = showWeekly
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0);
        LunaColumn.Width = showLuna
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0);

        // Keep a separator only when it sits between two visible cards. If Weekly is absent but
        // Luna is present, the first gap naturally separates the remaining Five-hour and Luna cards.
        FiveHourGapColumn.Width = showFiveHour && (showWeekly || showLuna)
            ? new GridLength(10)
            : new GridLength(0);
        LunaGapColumn.Width = showWeekly && showLuna
            ? new GridLength(10)
            : new GridLength(0);
    }

    private void SetCodexQuotaVisibility(AccountRateLimits? limits)
    {
        // Until the first snapshot arrives the account's available windows are unknown, so keep
        // loading placeholders. Once a snapshot is present, only buckets actually returned by
        // the account/API occupy space; a Pro response with no 5-hour bucket does not leave a
        // misleading "—" card behind.
        var showLoadingPlaceholders = limits is null;
        SetQuotaVisibility(
            showLoadingPlaceholders || limits?.FiveHour is not null,
            showLoadingPlaceholders || limits?.Weekly is not null,
            showLoadingPlaceholders || limits?.LunaReserve is not null);
    }

    private void SetClaudeQuotaVisibility(ClaudeUsageSnapshot? snapshot)
    {
        // Claude has no Luna bucket. Reuse the third card for the optional Fable point bucket or
        // the first scoped model bucket, and spill any additional scoped model buckets to extra rows.
        var account = snapshot?.Account;
        var showLoadingPlaceholders = account is null;
        var hasThirdCard = snapshot?.FableLimit is not null ||
                           (snapshot?.ScopedLimits.Any(limit => !limit.IsFable) ?? false);
        SetQuotaVisibility(
            showLoadingPlaceholders || account?.FiveHour is not null,
            showLoadingPlaceholders || account?.Weekly is not null,
            showLuna: hasThirdCard);
    }

    private void RenderCodex()
    {
        ClearExtraQuotaCards();
        SetLunaCardCaption(FiveHourCaption, "card.fiveHour");
        SetLunaCardCaption(WeeklyCaption, "card.weekly");
        SetLunaCardCaption(LunaCaption, "card.lunaReserve");
        BalanceCaption.Text = Loc.T("card.balance");
        ResetCreditsCaption.Text = Loc.T("card.resetCredits");
        LifetimeTokensCaption.Text = Loc.T("card.lifetimeTokens");
        BalanceText.FontSize = 18;
        ResetCreditsText.FontSize = 18;

        if (_currentSnapshot is not { } snapshot)
        {
            SetCodexQuotaVisibility(null);
            var localOnlyUsage = CreateLocalOnlyUsage(_todayTokenUsage);
            var errorStatus = _codexError is null
                ? Loc.T("status.readingCodexToken")
                : localOnlyUsage is null
                    ? GetFriendlyError(_codexError)
                    : Loc.T("status.errWithLocal", GetFriendlyError(_codexError));
            ApplyRateWindow(null, FiveHourRemainingText, FiveHourUsedText, FiveHourProgress, FiveHourResetText);
            ApplyRateWindow(null, WeeklyRemainingText, WeeklyUsedText, WeeklyProgress, WeeklyResetText);
            ApplyRateWindow(null, LunaRemainingText, LunaUsedText, LunaProgress, LunaResetText);
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

        SetCodexQuotaVisibility(snapshot.RateLimits);
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
        ApplyRateWindow(
            snapshot.RateLimits.LunaReserve,
            LunaRemainingText,
            LunaUsedText,
            LunaProgress,
            LunaResetText,
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
                snapshot.RateLimits.LunaReserve?.UsedPercent,
            }
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .DefaultIfEmpty(0)
            .Max();
        StatusDot.Fill = isStale ? DangerBrush : GetQuotaBrush(100 - observedUsage);
        ConnectionText.Text = isStale
            ? Loc.T("conn.stale")
            : Loc.T("conn.codexLive");
        UpdatedText.Text = isStale
            ? Loc.T("time.stale", TimeStamp(snapshot.FetchedAt))
            : Loc.T("time.updated", TimeStamp(snapshot.FetchedAt));
        RenderExpandedPanel();
    }

    private void RenderClaude()
    {
        ClearExtraQuotaCards();
        SetLunaCardCaption(FiveHourCaption, "card.fiveHour");
        SetLunaCardCaption(WeeklyCaption, "card.weekly");
        LifetimeTokensCaption.Text = Loc.T("card.localLifetimeTokens");

        var snapshot = _claudeSnapshot;
        var account = snapshot?.Account;
        var status = snapshot?.Status;
        SetClaudeQuotaVisibility(snapshot);
        var fable = snapshot?.FableLimit;
        var nonFableLimits = snapshot?.ScopedLimits.Where(limit => !limit.IsFable).ToArray() ?? [];
        var thirdLimit = fable ?? nonFableLimits.FirstOrDefault();
        if (fable is not null)
        {
            SetLunaCardCaption(LunaCaption, "card.fableCredits");
        }
        else if (thirdLimit is not null)
        {
            SetCardCaptionText(LunaCaption, LocalizeClaudeScopedLabel(thirdLimit));
        }
        else
        {
            SetLunaCardCaption(LunaCaption, "card.lunaReserve");
        }

        ApplyRateWindow(
            account?.FiveHour,
            FiveHourRemainingText,
            FiveHourUsedText,
            FiveHourProgress,
            FiveHourResetText);
        ApplyRateWindow(
            account?.Weekly,
            WeeklyRemainingText,
            WeeklyUsedText,
            WeeklyProgress,
            WeeklyResetText);
        ApplyRateWindow(
            thirdLimit is null
                ? null
                : new RateLimitWindow(thirdLimit.UsedPercent, thirdLimit.ResetsAt, 10_080),
            LunaRemainingText,
            LunaUsedText,
            LunaProgress,
            LunaResetText);

        var extraClaudeLimits = fable is not null
            ? nonFableLimits
            : nonFableLimits.Skip(1).ToArray();
        if (extraClaudeLimits.Length > 0)
        {
            RenderExtraClaudeCards(extraClaudeLimits);
        }

        ApplyClaudeExtraUsageCard(snapshot?.ExtraUsage, account?.Wallet);
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
                .Concat(snapshot!.ScopedLimits.Select(limit => (int?)limit.UsedPercent))
                .Where(value => value.HasValue)
                .Select(value => value!.Value)
                .DefaultIfEmpty(0)
                .Max();
            StatusDot.Fill = snapshot?.AccountError is null ? GetQuotaBrush(100 - observedUsage) : WarningBrush;
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

    private void RenderAntigravity()
    {
        LifetimeTokensCaption.Text = Loc.T("card.localLifetimeTokens");

        var snapshot = _antigravitySnapshot;
        var models = GetOrderedAntigravityModels(snapshot);
        if (models.Count > 0)
        {
            SetQuotaVisibility(
                showFiveHour: models.Count >= 1,
                showWeekly: models.Count >= 2,
                showLuna: models.Count >= 3);

            ApplyAntigravityModelCard(
                models[0],
                FiveHourCaption,
                FiveHourRemainingText,
                FiveHourUsedText,
                FiveHourProgress,
                FiveHourResetText);

            if (models.Count >= 2)
            {
                ApplyAntigravityModelCard(
                    models[1],
                    WeeklyCaption,
                    WeeklyRemainingText,
                    WeeklyUsedText,
                    WeeklyProgress,
                    WeeklyResetText);
            }

            if (models.Count >= 3)
            {
                ApplyAntigravityModelCard(
                    models[2],
                    LunaCaption,
                    LunaRemainingText,
                    LunaUsedText,
                    LunaProgress,
                    LunaResetText);
            }

            if (models.Count > 3)
            {
                RenderExtraAntigravityCards(models.Skip(3).ToArray());
            }
            else
            {
                ClearExtraQuotaCards();
            }
        }
        else
        {
            ClearExtraQuotaCards();
            var status = snapshot?.Status;
            SetQuotaVisibility(
                showFiveHour: true,
                showWeekly: status?.Weekly is not null || status?.FiveHour is null,
                showLuna: false);
            SetLunaCardCaption(FiveHourCaption, "card.fiveHour");
            SetLunaCardCaption(WeeklyCaption, "card.weekly");
            SetLunaCardCaption(LunaCaption, "card.lunaReserve");

            ApplyRateWindow(
                status?.FiveHour,
                FiveHourRemainingText,
                FiveHourUsedText,
                FiveHourProgress,
                FiveHourResetText);
            ApplyRateWindow(
                status?.Weekly,
                WeeklyRemainingText,
                WeeklyUsedText,
                WeeklyProgress,
                WeeklyResetText);
            ApplyRateWindow(
                null,
                LunaRemainingText,
                LunaUsedText,
                LunaProgress,
                LunaResetText);
        }

        BalanceCaption.Text = Loc.T("lbl.currentModel");
        var activeModel = snapshot?.ActiveModelName;
        if (string.IsNullOrWhiteSpace(activeModel))
        {
            BalanceText.FontSize = 18;
            BalanceText.Text = "--";
        }
        else
        {
            BalanceText.FontSize = 13;
            BalanceText.Text = activeModel;
        }

        if (snapshot?.ContextUsedPercent is { } contextPercent)
        {
            ResetCreditsCaption.Text = Loc.T("card.context");
            ResetCreditsText.FontSize = 18;
            ResetCreditsText.Text = $"{contextPercent}%";
        }
        else
        {
            ResetCreditsCaption.Text = Loc.T("card.modelCount");
            ResetCreditsText.FontSize = 18;
            ResetCreditsText.Text = models.Count > 0
                ? Loc.T("unit.kinds", models.Count)
                : "--";
        }

        var tokenStatus = snapshot switch
        {
            null => Loc.T("conn.readingAntigravity"),
            { Status.ModelQuotas.Count: > 0 } => Loc.T("status.antigravityQuota"),
            { Models.Count: > 0 } => Loc.T("status.antigravityLocal", models.Count),
            { IsAntigravityAvailable: false } => Loc.T("status.antigravityNotDetected"),
            _ => Loc.T("status.antigravityNoHistory"),
        };
        ApplyTokenUsageDisplay(snapshot?.TokenUsage, tokenStatus, tokenStatus);

        if (snapshot is { IsAntigravityAvailable: true })
        {
            var observedUsage = new[]
                {
                    snapshot.Status?.FiveHour?.UsedPercent,
                    snapshot.Status?.Weekly?.UsedPercent,
                }
                .Concat(models.Select(model => model.QuotaWindow?.UsedPercent))
                .Where(value => value.HasValue)
                .Select(value => value!.Value)
                .DefaultIfEmpty(0)
                .Max();
            StatusDot.Fill = GetQuotaBrush(100 - observedUsage);
            ConnectionText.Text = Loc.T("conn.antigravityLive");
            UpdatedText.Text = Loc.T("time.updated", TimeStamp(snapshot.FetchedAt));
        }
        else if (snapshot is { IsAntigravityAvailable: false })
        {
            StatusDot.Fill = WarningBrush;
            ConnectionText.Text = Loc.T("status.antigravityNotDetected");
            UpdatedText.Text = Loc.T("time.check", TimeStamp(snapshot.FetchedAt));
        }
        else
        {
            StatusDot.Fill = WarningBrush;
            ConnectionText.Text = Loc.T("conn.readingAntigravity");
            UpdatedText.Text = string.Empty;
        }

        RenderExpandedPanel();
    }

    private void RenderExternalProvider(ExternalProviderId providerId)
    {
        var spec = ExternalProviderMonitor.GetSpec(providerId);
        LifetimeTokensCaption.Text = Loc.T("card.localLifetimeTokens");

        var snapshot = _externalSnapshots.GetValueOrDefault(providerId);
        var models = (snapshot?.Models ?? Array.Empty<AntigravityModelUsage>())
            .OrderByDescending(model => model.QuotaWindow is not null)
            .ToArray();

        if (models.Length > 0)
        {
            SetQuotaVisibility(
                showFiveHour: models.Length >= 1,
                showWeekly: models.Length >= 2,
                showLuna: models.Length >= 3);

            ApplyAntigravityModelCard(
                models[0],
                FiveHourCaption,
                FiveHourRemainingText,
                FiveHourUsedText,
                FiveHourProgress,
                FiveHourResetText);

            if (models.Length >= 2)
            {
                ApplyAntigravityModelCard(
                    models[1],
                    WeeklyCaption,
                    WeeklyRemainingText,
                    WeeklyUsedText,
                    WeeklyProgress,
                    WeeklyResetText);
            }

            if (models.Length >= 3)
            {
                ApplyAntigravityModelCard(
                    models[2],
                    LunaCaption,
                    LunaRemainingText,
                    LunaUsedText,
                    LunaProgress,
                    LunaResetText);
            }

            if (models.Length > 3)
            {
                RenderExtraAntigravityCards(models.Skip(3).ToArray());
            }
            else
            {
                ClearExtraQuotaCards();
            }
        }
        else
        {
            ClearExtraQuotaCards();
            var defaultModels = spec.DefaultModels;
            SetQuotaVisibility(
                showFiveHour: defaultModels.Count >= 1,
                showWeekly: defaultModels.Count >= 2,
                showLuna: defaultModels.Count >= 3);

            if (defaultModels.Count >= 1)
            {
                SetCardCaptionText(FiveHourCaption, defaultModels[0].DisplayName);
                ApplyRateWindow(snapshot?.FiveHour, FiveHourRemainingText, FiveHourUsedText, FiveHourProgress, FiveHourResetText);
            }

            if (defaultModels.Count >= 2)
            {
                SetCardCaptionText(WeeklyCaption, defaultModels[1].DisplayName);
                ApplyRateWindow(snapshot?.Weekly, WeeklyRemainingText, WeeklyUsedText, WeeklyProgress, WeeklyResetText);
            }

            if (defaultModels.Count >= 3)
            {
                SetCardCaptionText(LunaCaption, defaultModels[2].DisplayName);
                ApplyRateWindow(null, LunaRemainingText, LunaUsedText, LunaProgress, LunaResetText);
            }
        }

        if (snapshot?.Balance is { } balance)
        {
            BalanceCaption.Text = Loc.T("lbl.currentBalance");
            BalanceText.FontSize = 16;
            BalanceText.Text = FormatMoney(balance.TotalBalance, balance.Currency);
        }
        else
        {
            BalanceCaption.Text = Loc.T("lbl.currentModel");
            var activeModel = snapshot?.ActiveModelName;
            if (string.IsNullOrWhiteSpace(activeModel))
            {
                BalanceText.FontSize = 18;
                BalanceText.Text = "--";
            }
            else
            {
                BalanceText.FontSize = 13;
                BalanceText.Text = activeModel;
            }
        }

        if (snapshot?.ContextUsedPercent is { } contextPercent)
        {
            ResetCreditsCaption.Text = Loc.T("card.context");
            ResetCreditsText.FontSize = 18;
            ResetCreditsText.Text = $"{contextPercent}%";
        }
        else if (!string.IsNullOrWhiteSpace(snapshot?.PlanType))
        {
            ResetCreditsCaption.Text = Loc.T("card.plan");
            ResetCreditsText.FontSize = 14;
            ResetCreditsText.Text = snapshot.PlanType;
        }
        else
        {
            ResetCreditsCaption.Text = Loc.T("card.modelCount");
            ResetCreditsText.FontSize = 18;
            var count = models.Length > 0 ? models.Length : spec.DefaultModels.Count;
            ResetCreditsText.Text = Loc.T("unit.kinds", count);
        }

        var tokenStatus = snapshot switch
        {
            null => Loc.T("conn.readingProvider", spec.DisplayName),
            { Balance: not null } => Loc.T("status.providerBalanceAndToken", spec.DisplayName),
            { IsAvailable: true } => Loc.T("status.providerLocal", spec.DisplayName),
            _ => Loc.T("status.providerNotConfigured", spec.DisplayName, spec.ApiKeyEnvVars[0]),
        };
        ApplyTokenUsageDisplay(snapshot?.TokenUsage, tokenStatus, tokenStatus);

        if (snapshot is { IsAvailable: true })
        {
            var observedUsage = new[]
                {
                    snapshot.FiveHour?.UsedPercent,
                    snapshot.Weekly?.UsedPercent,
                }
                .Concat(models.Select(model => model.QuotaWindow?.UsedPercent))
                .Where(value => value.HasValue)
                .Select(value => value!.Value)
                .DefaultIfEmpty(0)
                .Max();
            StatusDot.Fill = snapshot.Error is null ? GetQuotaBrush(100 - observedUsage) : WarningBrush;
            ConnectionText.Text = Loc.T("conn.providerLive", spec.DisplayName.ToUpperInvariant());
            UpdatedText.Text = Loc.T("time.updated", TimeStamp(snapshot.FetchedAt));
        }
        else if (snapshot is not null)
        {
            StatusDot.Fill = WarningBrush;
            ConnectionText.Text = Loc.T("status.providerNotConfigured", spec.DisplayName, spec.ApiKeyEnvVars[0]);
            UpdatedText.Text = Loc.T("time.check", TimeStamp(snapshot.FetchedAt));
        }
        else
        {
            StatusDot.Fill = WarningBrush;
            ConnectionText.Text = Loc.T("conn.readingProvider", spec.DisplayName);
            UpdatedText.Text = string.Empty;
        }

        RenderExpandedPanel();
    }

    private static IReadOnlyList<AntigravityModelUsage> GetOrderedAntigravityModels(AntigravityUsageSnapshot? snapshot)
    {
        var baseModels = snapshot?.Models is { Count: > 0 } snapshotModels
            ? snapshotModels
            : AntigravityTranscriptParser.BuildModelUsage(
                Array.Empty<AntigravityTokenRecord>(),
                DateOnly.FromDateTime(DateTime.Now),
                snapshot?.Status?.ModelQuotas);
        return baseModels
            .OrderByDescending(model => model.QuotaWindow is not null)
            .ToArray();
    }

    private void ClearExtraQuotaCards()
    {
        _extraQuotaResetTargets.Clear();
        ExtraQuotaRowsPanel.Children.Clear();
        ExtraQuotaRowsPanel.Visibility = Visibility.Collapsed;
        if (_extraQuotaRows != 0)
        {
            _extraQuotaRows = 0;
            UpdateWindowHeight();
        }
    }

    private void RenderExtraClaudeCards(IReadOnlyList<ClaudeScopedLimit> extraLimits)
    {
        var items = extraLimits
            .Select(limit => new AntigravityModelUsage(
                ModelName: limit.ModelName ?? limit.Key,
                DisplayName: LocalizeClaudeScopedLabel(limit),
                TotalTokens: 0,
                InputTokens: 0,
                CacheReadTokens: 0,
                OutputTokens: 0,
                TodayTokens: 0,
                SevenDayTokens: 0,
                TurnCount: 0,
                SharePercent: 0,
                LastUsedAt: DateTimeOffset.MinValue,
                QuotaWindow: new RateLimitWindow(limit.UsedPercent, limit.ResetsAt, 10_080)))
            .ToArray();
        RenderExtraAntigravityCards(items);
    }

    private void RenderExtraAntigravityCards(IReadOnlyList<AntigravityModelUsage> extraModels)
    {
        _extraQuotaResetTargets.Clear();
        ExtraQuotaRowsPanel.Children.Clear();

        if (extraModels.Count == 0)
        {
            ExtraQuotaRowsPanel.Visibility = Visibility.Collapsed;
            _extraQuotaRows = 0;
            UpdateWindowHeight();
            return;
        }

        ExtraQuotaRowsPanel.Visibility = Visibility.Visible;
        var rowCount = (extraModels.Count + 2) / 3;
        for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            var slice = extraModels.Skip(rowIndex * 3).Take(3).ToArray();
            var rowGrid = new Grid
            {
                Height = QuotaRowHeight,
                Margin = new Thickness(0, QuotaRowGap, 0, 0),
            };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = slice.Length >= 2 ? new GridLength(10) : new GridLength(0),
            });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = slice.Length >= 2 ? new GridLength(1, GridUnitType.Star) : new GridLength(0),
            });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = slice.Length >= 3 ? new GridLength(10) : new GridLength(0),
            });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = slice.Length >= 3 ? new GridLength(1, GridUnitType.Star) : new GridLength(0),
            });

            for (var cardIndex = 0; cardIndex < slice.Length; cardIndex++)
            {
                var (card, caption, remainingText, usedText, progress, resetText) = CreateExtraQuotaCard();
                Grid.SetColumn(card, cardIndex * 2);
                ApplyAntigravityModelCard(slice[cardIndex], caption, remainingText, usedText, progress, resetText);
                if (slice[cardIndex].GetQuotaWindow(_antigravityQuotaPeriod) is { } window)
                {
                    _extraQuotaResetTargets.Add((resetText, window));
                }

                rowGrid.Children.Add(card);
            }

            ExtraQuotaRowsPanel.Children.Add(rowGrid);
        }

        _extraQuotaRows = rowCount;
        UpdateWindowHeight();
    }

    private (
        Border Card,
        TextBlock Caption,
        TextBlock RemainingText,
        TextBlock UsedText,
        ProgressBar Progress,
        TextBlock ResetText) CreateExtraQuotaCard()
    {
        var cardBrush = (Brush)FindResource("CardBrush");
        var cardBorderBrush = (Brush)FindResource("CardBorderBrush");
        var mutedTextBrush = (Brush)FindResource("MutedTextBrush");
        var subtleTextBrush = (Brush)FindResource("SubtleTextBrush");
        var progressStyle = (Style)FindResource("RoundedProgressStyle");

        var caption = new TextBlock
        {
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Foreground = mutedTextBrush,
        };
        var remainingText = new TextBlock
        {
            Text = "--%",
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = CreateBrush(0xF5, 0xF7, 0xFA),
        };
        Grid.SetRow(remainingText, 1);

        var headerGrid = new Grid();
        headerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        headerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
        headerGrid.Children.Add(caption);
        headerGrid.Children.Add(remainingText);

        var progress = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Style = progressStyle,
        };
        Grid.SetRow(progress, 2);

        var usedText = new TextBlock
        {
            Text = "已用 --%",
            FontSize = 9,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = subtleTextBrush,
        };
        var resetText = new TextBlock
        {
            Text = Loc.T("card.waitingData"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            FontSize = 9,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Right,
            Foreground = subtleTextBrush,
        };
        Grid.SetColumn(resetText, 1);

        var footerGrid = new Grid { ClipToBounds = true };
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footerGrid.Children.Add(usedText);
        footerGrid.Children.Add(resetText);
        Grid.SetRow(footerGrid, 4);

        var innerGrid = new Grid();
        innerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        innerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
        innerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(5) });
        innerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
        innerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(15) });
        innerGrid.Children.Add(headerGrid);
        innerGrid.Children.Add(progress);
        innerGrid.Children.Add(footerGrid);

        var card = new Border
        {
            Padding = new Thickness(11, 9, 11, 9),
            Background = cardBrush,
            BorderBrush = cardBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = innerGrid,
            Cursor = System.Windows.Input.Cursors.Hand,
            Tag = "QuotaCard",
        };
        card.PreviewMouseLeftButtonUp += QuotaCard_MouseLeftButtonUp;
        card.MouseLeftButtonUp += QuotaCard_MouseLeftButtonUp;
        card.MouseEnter += QuotaCard_MouseEnter;
        card.MouseLeave += QuotaCard_MouseLeave;

        return (card, caption, remainingText, usedText, progress, resetText);
    }

    private void ApplyAntigravityModelCard(
        AntigravityModelUsage model,
        TextBlock caption,
        TextBlock remainingText,
        TextBlock usedText,
        ProgressBar progress,
        TextBlock resetText)
    {
        SetCardCaptionText(caption, model.DisplayName);
        var window = model.GetQuotaWindow(_antigravityQuotaPeriod);
        if (window is not null)
        {
            ApplyRateWindow(window, remainingText, usedText, progress, resetText);

            var isWeekly = window.DurationMinutes >= 1440 ||
                           (window.ResetsAt is { } r && r - DateTimeOffset.Now > TimeSpan.FromHours(12));
            var periodTag = isWeekly ? Loc.T("lbl.quotaWeeklyShort") : Loc.T("lbl.quotaFiveHourShort");
            usedText.Text = Loc.T("card.usedWithPeriod", window.UsedPercent, periodTag);

            var switchTarget = _antigravityQuotaPeriod == QuotaPeriod.FiveHour
                ? Loc.T("lbl.quotaWeeklyTag")
                : Loc.T("lbl.quotaFiveHourTag");
            var tip = Loc.T("tip.switchQuotaPeriod", switchTarget);
            caption.ToolTip = tip;
            remainingText.ToolTip = tip;
            usedText.ToolTip = tip;
            resetText.ToolTip = tip;
            if (caption.Parent is Grid headerGrid && headerGrid.Parent is Grid innerGrid && innerGrid.Parent is Border cardBorder)
            {
                cardBorder.ToolTip = tip;
            }
            return;
        }

        remainingText.Opacity = 1d;
        usedText.Opacity = 1d;
        progress.Opacity = 1d;
        resetText.Opacity = 1d;
        remainingText.Text = TokenFormatter.Format(model.TotalTokens);
        usedText.Text = Loc.T("card.modelShare", model.SharePercent);
        progress.Value = model.SharePercent;
        progress.Foreground = AntigravityBarBrush;
        resetText.Text = Loc.T("unit.times", model.TurnCount);
    }

    private void QuotaCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_activeProvider == UsageProvider.Antigravity)
        {
            _antigravityQuotaPeriod = _antigravityQuotaPeriod == QuotaPeriod.FiveHour
                ? QuotaPeriod.Weekly
                : QuotaPeriod.FiveHour;
            SavePlacement();
            RenderAntigravity();
            e.Handled = true;
        }
        else if (TryMapExternalProvider(_activeProvider) is { } externalId)
        {
            _antigravityQuotaPeriod = _antigravityQuotaPeriod == QuotaPeriod.FiveHour
                ? QuotaPeriod.Weekly
                : QuotaPeriod.FiveHour;
            SavePlacement();
            RenderExternalProvider(externalId);
            e.Handled = true;
        }
    }

    private void QuotaCard_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is Border border)
        {
            _cardHoverBorderBrush ??= new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69));
            border.BorderBrush = _cardHoverBorderBrush;
        }
    }

    private void QuotaCard_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is Border border)
        {
            border.BorderBrush = (Brush)FindResource("CardBorderBrush");
        }
    }

    private static void SetCardCaptionText(TextBlock caption, string text)
    {
        caption.Text = text;
        System.Windows.Automation.AutomationProperties.SetName(caption, text);
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
        var symbol = currency.Equals("USD", StringComparison.OrdinalIgnoreCase)
            ? "$"
            : currency.Equals("CNY", StringComparison.OrdinalIgnoreCase) ||
              currency.Equals("RMB", StringComparison.OrdinalIgnoreCase)
                ? "¥"
                : string.Empty;
        return $"{symbol}{amount.ToString("0.##", CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// Shows another subscription model bucket in the secondary slot after Fable has its own card,
    /// then falls back to the status line's context window or the plan name.
    /// </summary>
    private void ApplyClaudeSecondaryCard(ClaudeAccountUsage? account, ClaudeUsageSnapshot? snapshot)
    {
        var scoped = snapshot?.ScopedLimits.FirstOrDefault(limit => !limit.IsFable);
        if (scoped is { } limit)
        {
            ResetCreditsCaption.Text = LocalizeClaudeScopedLabel(limit);
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

    private static string LocalizeClaudeScopedLabel(ClaudeScopedLimit scoped)
    {
        if (scoped.IsFable)
        {
            return Loc.T("card.fableCredits");
        }

        if (scoped.UsesUsageCredits)
        {
            return scoped.DisplayName;
        }

        var modelName = scoped.Key switch
        {
            "seven_day_opus" => "Opus",
            "seven_day_sonnet" => "Sonnet",
            "seven_day_cowork" => "Cowork",
            "seven_day_oauth_apps" => Loc.Current switch
            {
                AppLanguage.English => "Third-party apps",
                AppLanguage.Korean => "타사 앱",
                _ => "第三方应用",
            },
            _ => scoped.ModelName,
        };

        return string.IsNullOrWhiteSpace(modelName)
            ? scoped.DisplayName
            : Loc.T("card.modelWeekly", modelName);
    }

    private static void SetLunaCardCaption(TextBlock caption, string resourceKey)
    {
        var text = Loc.T(resourceKey);
        caption.Text = text;
        System.Windows.Automation.AutomationProperties.SetName(caption, text);
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
            resetText.Text = Loc.T("card.notProvided");
            return;
        }

        remainingText.Text = isStale
            ? $"~{window.RemainingPercent}%"
            : $"{window.RemainingPercent}%";
        usedText.Text = Loc.T(isStale ? "card.usedApprox" : "card.used", window.UsedPercent);
        // The bar represents the quota that remains available: full at 100% remaining and
        // empty when the window is exhausted. Keep the color tied to usage so warnings still
        // become more prominent as the account approaches its limit.
        progress.Value = window.RemainingPercent;
        progress.Foreground = GetQuotaBrush(window.RemainingPercent);
        resetText.Text = FormatResetCardTime(window.ResetsAt);
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
                GetProviderBarColor());
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
            : Loc.T(
                isStale ? "tray.weeklyRemainingStale" : "tray.weeklyRemaining",
                head,
                weekly.RemainingPercent);
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
        if (TryMapExternalProvider(_activeProvider) is { } externalId)
        {
            var extSnapshot = _externalSnapshots.GetValueOrDefault(externalId);
            var extModels = extSnapshot?.Models ?? Array.Empty<AntigravityModelUsage>();
            if (extModels.Count > 0)
            {
                if (extModels[0].GetQuotaWindow(_antigravityQuotaPeriod) is { } firstWindow)
                {
                    SetCompactResetLabel(FiveHourResetText, firstWindow);
                }
                if (extModels.Count >= 2 && extModels[1].GetQuotaWindow(_antigravityQuotaPeriod) is { } secondWindow)
                {
                    SetCompactResetLabel(WeeklyResetText, secondWindow);
                }
                if (extModels.Count >= 3 && extModels[2].GetQuotaWindow(_antigravityQuotaPeriod) is { } thirdWindow)
                {
                    SetCompactResetLabel(LunaResetText, thirdWindow);
                }
                foreach (var (resetText, window) in _extraQuotaResetTargets)
                {
                    SetCompactResetLabel(resetText, window);
                }
            }
            else if (extSnapshot is { FiveHour: not null } or { Weekly: not null })
            {
                SetCompactResetLabel(FiveHourResetText, extSnapshot.FiveHour);
                SetCompactResetLabel(WeeklyResetText, extSnapshot.Weekly);
                SetCompactResetLabel(LunaResetText, null);
            }

            return;
        }

        if (_activeProvider == UsageProvider.Antigravity)
        {
            var models = GetOrderedAntigravityModels(_antigravitySnapshot);
            if (models.Count > 0)
            {
                if (models[0].GetQuotaWindow(_antigravityQuotaPeriod) is { } firstWindow)
                {
                    SetCompactResetLabel(FiveHourResetText, firstWindow);
                }
                if (models.Count >= 2 && models[1].GetQuotaWindow(_antigravityQuotaPeriod) is { } secondWindow)
                {
                    SetCompactResetLabel(WeeklyResetText, secondWindow);
                }
                if (models.Count >= 3 && models[2].GetQuotaWindow(_antigravityQuotaPeriod) is { } thirdWindow)
                {
                    SetCompactResetLabel(LunaResetText, thirdWindow);
                }
                foreach (var (resetText, window) in _extraQuotaResetTargets)
                {
                    SetCompactResetLabel(resetText, window);
                }
            }
            else
            {
                SetCompactResetLabel(FiveHourResetText, _antigravitySnapshot?.Status?.FiveHour);
                SetCompactResetLabel(WeeklyResetText, _antigravitySnapshot?.Status?.Weekly);
                SetCompactResetLabel(LunaResetText, null);
            }

            return;
        }

        RateLimitWindow? fiveHour;
        RateLimitWindow? weekly;
        RateLimitWindow? lunaReserve;
        if (_activeProvider == UsageProvider.Codex)
        {
            fiveHour = _currentSnapshot?.RateLimits.FiveHour;
            weekly = _currentSnapshot?.RateLimits.Weekly;
            lunaReserve = _currentSnapshot?.RateLimits.LunaReserve;
        }
        else
        {
            fiveHour = _claudeSnapshot?.FiveHour;
            weekly = _claudeSnapshot?.Weekly;
            var third = _claudeSnapshot?.FableLimit ??
                        _claudeSnapshot?.ScopedLimits.FirstOrDefault(limit => !limit.IsFable);
            lunaReserve = third is null
                ? null
                : new RateLimitWindow(third.UsedPercent, third.ResetsAt, 10_080);
        }

        SetCompactResetLabel(FiveHourResetText, fiveHour);
        SetCompactResetLabel(WeeklyResetText, weekly);
        SetCompactResetLabel(LunaResetText, lunaReserve);
        foreach (var (resetText, window) in _extraQuotaResetTargets)
        {
            SetCompactResetLabel(resetText, window);
        }
    }

    private static void SetCompactResetLabel(
        TextBlock target,
        RateLimitWindow? window)
    {
        target.Text = window is null ? Loc.T("card.notProvided") : FormatResetCardTime(window.ResetsAt);
    }

    internal static string FormatResetCardTime(DateTimeOffset? resetsAt)
    {
        return resetsAt is null
            ? "--"
            : resetsAt.Value.ToLocalTime().ToString("M/d HH:mm", CultureInfo.InvariantCulture);
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

    private static Brush GetQuotaBrush(int remainingPercent) =>
        QuotaColorScale.BrushForRemaining(remainingPercent);

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

    private void RestorePlacement(WindowPlacement? placement)
    {
        // Now that the card owns a taskbar button, WPF honours the show-state the launcher passed
        // in STARTUPINFO, which can start it iconic. The monitor should always come up visible.
        WindowState = WindowState.Normal;
        _leftGaugeProvider = placement?.LeftGaugeProvider ?? "Codex";
        _rightGaugeProvider = placement?.RightGaugeProvider ?? "Claude";

        if (placement?.AntigravityQuotaPeriod is { } pStr &&
            Enum.TryParse<QuotaPeriod>(pStr, true, out var p))
        {
            _antigravityQuotaPeriod = p;
        }

        if (placement is { CustomWidth: { } customW, CustomHeight: { } customH } &&
            double.IsFinite(customW) && double.IsFinite(customH) && customW > 0 && customH > 0)
        {
            Width = Math.Max(MinWidth, customW);
            Height = Math.Max(MinHeight, customH);
            _hasCustomWidth = true;
            _hasCustomHeight = true;
            _customWidth = Width;
            _customHeight = Height;
        }

        var workArea = SystemParameters.WorkArea;
        if (placement is { GaugeLeft: { } gaugeLeft, GaugeTop: { } gaugeTop } &&
            double.IsFinite(gaugeLeft) && double.IsFinite(gaugeTop))
        {
            // Clamped on the way in as well as on the way out, so a coordinate left over from a
            // display layout that no longer exists is corrected once and then saved corrected.
            _gaugeTopLeft = ClampOrbToWorkArea(new Point(gaugeLeft, gaugeTop));
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
        if (e.LeftButton != MouseButtonState.Pressed || IsInsideInteractiveElement(e.OriginalSource as DependencyObject))
        {
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

        ShowGauge(activate: true);
        SavePlacement();
    }

    private void ShowGauge(bool activate)
    {
        if (_gaugeWindow is null)
        {
            _gaugeWindow = new GaugeWindow { Topmost = Topmost };
            _gaugeWindow.SetProviders(_leftGaugeProvider, _rightGaugeProvider);
            _gaugeWindow.ProvidersChanged += (_, _) =>
            {
                _leftGaugeProvider = _gaugeWindow.LeftProviderName;
                _rightGaugeProvider = _gaugeWindow.RightProviderName;
                SavePlacement();
            };
            _gaugeWindow.RestoreRequested += (_, _) => RunOnDispatcher(ExitGaugeMode);
            _gaugeWindow.RefreshRequested += GaugeWindow_RefreshRequested;
            _gaugeWindow.UpdateRequested += GaugeWindow_UpdateRequested;
            _gaugeWindow.TopmostChangedRequested += GaugeWindow_TopmostChangedRequested;
            _gaugeWindow.StartupToggleRequested += ApplyStartupPreference;
        }

        PushGaugeValues();

        // The card and orb own independent positions. The card is only used as the first-run
        // default; every later switch restores the orb's own last position.
        _gaugeTopLeft = ClampOrbToWorkArea(
            _gaugeTopLeft ?? (double.IsFinite(Left) && double.IsFinite(Top) ? new Point(Left, Top) : null));
        if (_gaugeTopLeft is { } orbTopLeft)
        {
            _gaugeWindow.PlaceOrbAt(orbTopLeft.X, orbTopLeft.Y);
        }

        _gaugeWindow.ShowActivated = activate;
        _gaugeWindow.Show();
        if (activate)
        {
            _gaugeWindow.Activate();
        }
        ShowInTaskbar = false;
        if (IsVisible)
        {
            Hide();
        }
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

    /// <summary>
    /// Keeps the orb somewhere the user can actually see it.
    /// </summary>
    /// <remarks>
    /// The card has always been clamped when its placement is restored; the orb never was, so a
    /// coordinate saved under a different monitor layout or display scale - which is exactly what a
    /// reboot can change - parked it outside the desktop. The switch worked, the window was shown,
    /// and nothing appeared: indistinguishable from the feature being broken.
    /// </remarks>
    internal static Point? ClampOrbToWorkArea(Point? orbTopLeft)
    {
        if (orbTopLeft is not { } position)
        {
            return null;
        }

        // Left exactly where it is if it still lands somewhere on the desktop, second monitor
        // included. WorkArea describes the primary display only, so clamping unconditionally would
        // drag an orb the user parked on their other screen back onto their first one.
        if (IsOnVirtualScreen(position))
        {
            return position;
        }

        // Measured against the whole orb window, not the visible disc: PlaceOrbAt offsets the window
        // up and left by the shadow padding, so clamping the disc alone still let the window hang
        // off the edge by exactly that padding.
        var workArea = SystemParameters.WorkArea;
        var minLeft = workArea.Left + GaugeWindow.OrbOffsetX;
        var minTop = workArea.Top + GaugeWindow.OrbOffsetY;
        var maxLeft = Math.Max(minLeft, workArea.Right - GaugeWindow.ShadowCanvasWidth + GaugeWindow.OrbOffsetX);
        var maxTop = Math.Max(minTop, workArea.Bottom - GaugeWindow.ShadowCanvasHeight + GaugeWindow.OrbOffsetY);
        return new Point(
            double.IsFinite(position.X) ? Math.Clamp(position.X, minLeft, maxLeft) : minLeft,
            double.IsFinite(position.Y) ? Math.Clamp(position.Y, minTop, maxTop) : minTop);
    }

    /// <summary>
    /// Whether the whole orb window would sit inside the desktop as it is currently arranged.
    /// </summary>
    /// <remarks>
    /// The virtual screen spans every monitor, and WPF reports it in the same device-independent
    /// units as <see cref="Window.Left"/>, so no DPI conversion is involved. A position saved under
    /// a monitor that has since been unplugged, or under a different display scale, falls outside
    /// it and is the case worth rescuing.
    /// </remarks>
    private static bool IsOnVirtualScreen(Point orbTopLeft)
    {
        if (!double.IsFinite(orbTopLeft.X) || !double.IsFinite(orbTopLeft.Y))
        {
            return false;
        }

        var left = orbTopLeft.X - GaugeWindow.OrbOffsetX;
        var top = orbTopLeft.Y - GaugeWindow.OrbOffsetY;
        return left >= SystemParameters.VirtualScreenLeft &&
               top >= SystemParameters.VirtualScreenTop &&
               left + GaugeWindow.ShadowCanvasWidth <=
                   SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
               top + GaugeWindow.ShadowCanvasHeight <=
                   SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
    }

    private bool IsGaugeVisible => _gaugeWindow is { IsVisible: true };

    internal GaugeWindow? GaugeWindowForTesting => _gaugeWindow;

    /// <summary>Feeds all selectable quota windows to each side of the orb.</summary>
    private void PushGaugeValues()
    {
        _gaugeWindow?.SetAntigravity(_antigravitySnapshot?.Status);
        _gaugeWindow?.SetExternalSnapshots(_externalSnapshots);
        var claudeFable = _claudeSnapshot?.FableLimit;
        _gaugeWindow?.SetWindows(
            _currentSnapshot?.RateLimits.FiveHour,
            _currentSnapshot?.RateLimits.Weekly,
            _currentSnapshot?.RateLimits.LunaReserve,
            _claudeSnapshot?.FiveHour,
            _claudeSnapshot?.Weekly,
            claudeFable is null
                ? null
                : new RateLimitWindow(claudeFable.UsedPercent, claudeFable.ResetsAt, 10_080));
    }

    private bool IsInsideInteractiveElement(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is ButtonBase or System.Windows.Controls.Primitives.ScrollBar or System.Windows.Controls.Primitives.Thumb)
            {
                return true;
            }

            if (element is FrameworkElement fe)
            {
                if (fe == FiveHourQuotaCard || fe == WeeklyQuotaCard || fe == LunaQuotaCard || Equals(fe.Tag, "QuotaCard"))
                {
                    if (_activeProvider == UsageProvider.Antigravity || TryMapExternalProvider(_activeProvider) is not null)
                    {
                        return true;
                    }
                }

                if (fe.Cursor == System.Windows.Input.Cursors.Hand)
                {
                    return true;
                }
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
                if (userInitiated || ShouldPromptFor(result.Release.Version))
                {
                    _promptedVersion = result.Release.Version;
                    _promptedAt = DateTimeOffset.UtcNow;
                    UpdateDialog.ShowAvailable(
                        IsVisible ? this : null,
                        result.Release,
                        _updateService,
                        Topmost);
                }
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

    /// <summary>
    /// A newly released version always interrupts; one the user already saw waits out the cooldown.
    /// </summary>
    /// <remarks>
    /// An explicit check from a menu bypasses this entirely — asking for the result and being told
    /// nothing because of a cooldown would read as a broken button.
    /// </remarks>
    internal static bool ShouldPromptForUpdate(
        Version available,
        Version? promptedVersion,
        DateTimeOffset promptedAt,
        DateTimeOffset now)
    {
        return promptedVersion is null ||
               available != promptedVersion ||
               now - promptedAt >= UpdatePromptCooldown;
    }

    private bool ShouldPromptFor(Version available) =>
        ShouldPromptForUpdate(available, _promptedVersion, _promptedAt, DateTimeOffset.UtcNow);

    private void ProviderTab_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string provider } button)
        {
            return;
        }

        _activeProvider = provider switch
        {
            _ when provider.Equals("Claude", StringComparison.OrdinalIgnoreCase) => UsageProvider.Claude,
            _ when provider.Equals("Antigravity", StringComparison.OrdinalIgnoreCase) => UsageProvider.Antigravity,
            _ when provider.Equals("Grok", StringComparison.OrdinalIgnoreCase) => UsageProvider.Grok,
            _ when provider.Equals("DeepSeek", StringComparison.OrdinalIgnoreCase) => UsageProvider.DeepSeek,
            _ when provider.Equals("Kimi", StringComparison.OrdinalIgnoreCase) => UsageProvider.Kimi,
            _ when provider.Equals("Qwen", StringComparison.OrdinalIgnoreCase) => UsageProvider.Qwen,
            _ when provider.Equals("Glm", StringComparison.OrdinalIgnoreCase) => UsageProvider.Glm,
            _ when provider.Equals("SiliconFlow", StringComparison.OrdinalIgnoreCase) => UsageProvider.SiliconFlow,
            _ => UsageProvider.Codex,
        };
        button.BringIntoView();
        RenderActiveProvider();
    }

    private void TabScrollLeftButton_Click(object sender, RoutedEventArgs e)
    {
        ProviderTabScrollViewer.ScrollToHorizontalOffset(
            Math.Max(0, ProviderTabScrollViewer.HorizontalOffset - 140));
    }

    private void TabScrollRightButton_Click(object sender, RoutedEventArgs e)
    {
        ProviderTabScrollViewer.ScrollToHorizontalOffset(
            ProviderTabScrollViewer.HorizontalOffset + 140);
    }

    private void ProviderTabScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0)
        {
            return;
        }

        ProviderTabScrollViewer.ScrollToHorizontalOffset(
            Math.Max(0, ProviderTabScrollViewer.HorizontalOffset - e.Delta * 0.6));
        e.Handled = true;
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
        // Read from the registry rather than a cached flag: the entry can also be cleared from
        // Task Manager's Startup tab or the Settings app while this window sits in the tray.
        StartupMenuItem.IsEnabled = StartupRegistration.IsSupported;
        StartupMenuItem.IsChecked = StartupRegistration.IsEnabled();

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
        UpdateWindowHeight();
        RenderExpandedPanel();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_initialized && !_isClosing && WindowState == WindowState.Normal)
        {
            if (sizeInfo.WidthChanged && Math.Abs(sizeInfo.NewSize.Width - sizeInfo.PreviousSize.Width) > 1)
            {
                _hasCustomWidth = true;
                _customWidth = sizeInfo.NewSize.Width;
            }
            if (sizeInfo.HeightChanged && Math.Abs(sizeInfo.NewSize.Height - sizeInfo.PreviousSize.Height) > 1)
            {
                var expected = (_expandedPanel != ExpandedPanel.None ? CollapsedHeight + ExpandPanelHeight : CollapsedHeight) +
                               (_extraQuotaRows * ExtraQuotaRowStep);
                if (Math.Abs(sizeInfo.NewSize.Height - expected) > 2)
                {
                    _hasCustomHeight = true;
                    _customHeight = sizeInfo.NewSize.Height;
                }
            }
        }
    }

    private void UpdateWindowHeight()
    {
        var expanded = _expandedPanel != ExpandedPanel.None;
        var requiredHeight = (expanded ? CollapsedHeight + ExpandPanelHeight : CollapsedHeight) +
                             (_extraQuotaRows * ExtraQuotaRowStep);
        MinHeight = requiredHeight;
        if (!_hasCustomHeight || Height < requiredHeight)
        {
            Height = requiredHeight;
        }
        else if (_hasCustomHeight && _customHeight >= requiredHeight)
        {
            Height = _customHeight;
        }
        ClampToWorkArea();
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
                if (TryMapExternalProvider(_activeProvider) is { } extId)
                {
                    DetailsItemsControl.ItemsSource = UsageDetailBuilder.BuildExternalProvider(
                        _externalSnapshots.GetValueOrDefault(extId),
                        extId);
                    break;
                }

                DetailsItemsControl.ItemsSource = _activeProvider switch
                {
                    UsageProvider.Claude => UsageDetailBuilder.BuildClaude(_claudeSnapshot),
                    UsageProvider.Antigravity => UsageDetailBuilder.BuildAntigravity(_antigravitySnapshot),
                    _ => UsageDetailBuilder.BuildCodex(
                        _currentSnapshot,
                        _codexError is null ? null : GetFriendlyError(_codexError)),
                };
                break;
            case ExpandedPanel.History:
                RenderHistoryList();
                break;
        }
    }

    private void RenderHistoryList()
    {
        var providerName = TryMapExternalProvider(_activeProvider) is { } extId
            ? ExternalProviderMonitor.GetSpec(extId).DisplayName
            : _activeProvider switch
            {
                UsageProvider.Claude => "Claude",
                UsageProvider.Antigravity => "Antigravity",
                _ => "Codex",
            };
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
        var color = GetProviderBarColor();
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

            // The update action is deliberately last; language sits immediately above it.
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
        if (IsGaugeVisible)
        {
            PushGaugeValues();
        }
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
        SavePlacement();
    }

    private void StartupMenuItem_Click(object sender, RoutedEventArgs e)
    {
        StartupMenuItem.IsChecked = ApplyStartupPreference(StartupMenuItem.IsChecked);
    }

    /// <summary>
    /// Applies the requested auto-start state and reports what actually stuck, so a menu whose
    /// write was refused snaps back instead of claiming a setting the registry never took.
    /// </summary>
    /// <remarks>
    /// The failure notice is deferred to the dispatcher because the tray's WinForms menu calls this
    /// from inside its own message loop, and a modal dialog opened there would sit behind a menu
    /// that cannot close.
    /// </remarks>
    private bool ApplyStartupPreference(bool enabled)
    {
        if (StartupRegistration.TrySetEnabled(enabled))
        {
            return enabled;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!_isClosing)
            {
                UpdateDialog.ShowInformation(
                    IsVisible ? this : _gaugeWindow,
                    Loc.T("startup.failed"),
                    Topmost,
                    Loc.T("menu.startup"));
            }
        }));
        return StartupRegistration.IsEnabled();
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
        _gaugeTopLeft?.Y,
        IsGaugeVisible,
        _leftGaugeProvider,
        _rightGaugeProvider,
        _hasCustomWidth ? Width : null,
        _hasCustomHeight ? Height : null,
        _antigravityQuotaPeriod.ToString());

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

    private void GaugeWindow_RefreshRequested(object? sender, EventArgs e)
    {
        RunOnDispatcher(() => _ = RefreshUsageAsync(userInitiated: true));
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
        _updateTimer.Stop();
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
        _antigravityMonitor.UsageUpdated -= AntigravityMonitor_UsageUpdated;
        _externalMonitor.UsageUpdated -= ExternalMonitor_UsageUpdated;
        _trayIcon.ShowRequested -= TrayIcon_ShowRequested;
        _trayIcon.RefreshRequested -= TrayIcon_RefreshRequested;
        _trayIcon.UpdateRequested -= TrayIcon_UpdateRequested;
        _trayIcon.ExitRequested -= TrayIcon_ExitRequested;
        _trayIcon.StartupToggleRequested -= ApplyStartupPreference;
        Loc.Changed -= Loc_Changed;
        _client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _localTokenMonitor.Dispose();
        _claudeMonitor.Dispose();
        _antigravityMonitor.Dispose();
        _externalMonitor.Dispose();
        _trayIcon.Dispose();
        _updateService.Dispose();
        _lifetimeCancellation.Dispose();
        base.OnClosed(e);
    }

    private string GetProviderBarColor() =>
        TryMapExternalProvider(_activeProvider) is { } extId
            ? ExternalProviderMonitor.GetSpec(extId).BarColor
            : _activeProvider switch
            {
                UsageProvider.Claude => "#D9906E",
                UsageProvider.Antigravity => "#58A6FF",
                _ => "#7EE787",
            };

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
        Antigravity,
        Grok,
        DeepSeek,
        Kimi,
        Qwen,
        Glm,
        SiliconFlow,
    }

    private enum ExpandedPanel
    {
        None,
        Details,
        History,
    }
}
