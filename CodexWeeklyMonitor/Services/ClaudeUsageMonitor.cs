using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using CodexWeeklyMonitor.Models;
using Timer = System.Threading.Timer;

namespace CodexWeeklyMonitor.Services;

public sealed class ClaudeUsageMonitor : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan QuotaInterval = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan StatusMaxAge = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan SessionMaxAge = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, byte> _pendingPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, FileState> _observedFiles =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<ClaudeTokenRecord>> _recordsByFile =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _recordsGate = new();
    private readonly string _claudeHome;
    private readonly string _projectsDirectory;
    private readonly IReadOnlyList<string> _transcriptDirectories;
    private readonly IReadOnlyList<string> _desktopStateDirectories;
    private readonly string _bridgeDirectory;
    private readonly string _bridgePath;
    private readonly ClaudeUsageClient? _usageClient;
    // Claude Code polls this same endpoint for its own status line, so the app has to leave room
    // for it. The 5-hour window moves ~0.3%/minute at most, so three-minute spacing loses nothing.
    private readonly PollThrottle _throttle = new(
        minimumInterval: TimeSpan.FromMinutes(3),
        maximumBackoff: TimeSpan.FromMinutes(15));
    private readonly CancellationTokenSource _lifetime = new();

    private readonly List<FileSystemWatcher> _transcriptWatchers = [];
    private FileSystemWatcher? _bridgeWatcher;
    private Timer? _timer;
    private Timer? _quotaTimer;
    private ClaudeStatusUsage? _status;
    private readonly Dictionary<string, ClaudeSessionState> _sessionByFile =
        new(StringComparer.OrdinalIgnoreCase);
    private ClaudeSessionState? _session;
    private ClaudeAccountUsage? _account;
    private string? _accountError;
    private AccountTokenUsage? _tokenUsage;
    private bool _isClaudeAvailable;
    private bool _isBridgeConfigured;
    private int _processing;
    private int _fetchingQuota;
    private bool _publishedInitialSnapshot;
    private bool _forceRefresh;
    private bool _disposed;

    public ClaudeUsageMonitor(string? claudeHome = null, string? bridgeDirectory = null)
        : this(claudeHome, bridgeDirectory, usageClient: null, enableQuotaPolling: true)
    {
    }

    internal ClaudeUsageMonitor(
        string? claudeHome,
        string? bridgeDirectory,
        ClaudeUsageClient? usageClient,
        bool enableQuotaPolling)
    {
        // Honours CLAUDE_CONFIG_DIR, so a CLI user who relocated their config still gets
        // transcripts from the same place the credentials came from.
        _claudeHome = ClaudePaths.ResolveHome(claudeHome);
        _projectsDirectory = Path.Combine(_claudeHome, "projects");
        _transcriptDirectories = ResolveTranscriptDirectories(_projectsDirectory);
        _desktopStateDirectories = ResolveDesktopStateDirectories();
        _bridgeDirectory = string.IsNullOrWhiteSpace(bridgeDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AiTokenMonitor")
            : Path.GetFullPath(bridgeDirectory);
        _bridgePath = Path.Combine(_bridgeDirectory, "claude-status.json");
        _usageClient = usageClient ?? (enableQuotaPolling ? new ClaudeUsageClient() : null);
    }

    public event Action<ClaudeUsageSnapshot>? UsageUpdated;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_timer is not null)
        {
            return;
        }

        QueueAllTranscripts();
        EnsureWatchers();
        _forceRefresh = true;
        _timer = new Timer(ProcessPending, null, TimeSpan.Zero, PollInterval);
        if (_usageClient is not null)
        {
            _quotaTimer = new Timer(_ => _ = FetchQuotaAsync(), null, TimeSpan.Zero, QuotaInterval);
        }
    }

    /// <param name="userInitiated">
    /// Set when the user pressed refresh. That skips the routine spacing but still respects a
    /// rate-limit penalty, so repeated clicking cannot dig the limit deeper.
    /// </param>
    public void Refresh(bool userInitiated = false)
    {
        if (_disposed)
        {
            return;
        }

        QueueAllTranscripts();
        _forceRefresh = true;
        _timer?.Change(TimeSpan.Zero, PollInterval);
        _ = FetchQuotaAsync(userInitiated);
    }

    /// <summary>
    /// Pulls the authoritative quota from Anthropic's usage endpoint. Failures are surfaced in the
    /// snapshot rather than thrown, so a flaky network never blanks out the token statistics.
    /// </summary>
    private async Task FetchQuotaAsync(bool userInitiated = false)
    {
        if (_disposed || _usageClient is null)
        {
            return;
        }

        // The window's refresh timer and this monitor's own timer both land on the same minute, and
        // Claude Code polls the same endpoint. Without this gate the app produced two calls per
        // tick and the endpoint started answering 429.
        if (!_throttle.TryAcquire(DateTimeOffset.UtcNow, userInitiated) ||
            Interlocked.Exchange(ref _fetchingQuota, 1) != 0)
        {
            return;
        }

        try
        {
            var account = await _usageClient.FetchAsync(_lifetime.Token).ConfigureAwait(false);
            _account = account;
            _accountError = null;
            _throttle.ReportSuccess(DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ClaudeUsageException exception)
        {
            _accountError = exception.Message;
            if (exception.Failure is ClaudeUsageFailure.NotLoggedIn or ClaudeUsageFailure.Unauthorized)
            {
                // Stale data would be misleading once the credential itself is rejected.
                _account = null;
            }

            if (exception.Failure == ClaudeUsageFailure.Throttled)
            {
                _throttle.ReportThrottled(DateTimeOffset.UtcNow, exception.RetryAfter);
            }
            else
            {
                _throttle.ReportFailure(DateTimeOffset.UtcNow);
            }

            DiagnosticsLog.Write("ClaudeUsageClient", $"{exception.Failure}: {exception.Message}");
        }
        catch (Exception exception)
        {
            _accountError = "读取 Claude 额度失败。";
            _throttle.ReportFailure(DateTimeOffset.UtcNow);
            DiagnosticsLog.Write(
                "ClaudeUsageClient",
                $"unexpected failure: {exception.GetType().FullName}: {exception.Message}");
        }
        finally
        {
            Volatile.Write(ref _fetchingQuota, 0);
        }

        if (!_disposed)
        {
            PublishSnapshot();
        }
    }

    private void PublishSnapshot()
    {
        _publishedInitialSnapshot = true;
        UsageUpdated?.Invoke(new ClaudeUsageSnapshot(
            _account,
            _accountError,
            GetFreshStatus(DateTimeOffset.Now),
            _tokenUsage,
            _isClaudeAvailable,
            _isBridgeConfigured,
            DateTimeOffset.Now,
            _throttle.RemainingPenalty(DateTimeOffset.UtcNow),
            GetFreshSession(DateTimeOffset.Now)));
    }

    /// <summary>
    /// The status line only writes while Claude Code is running, and the file survives after it
    /// stops. Anything older than <see cref="StatusMaxAge"/> would show a model and context window
    /// from a session that ended long ago, so drop it instead.
    /// </summary>
    private ClaudeStatusUsage? GetFreshStatus(DateTimeOffset now)
    {
        return _status is { } status && now - status.ObservedAt <= StatusMaxAge ? status : null;
    }

    /// <summary>Same staleness rule as the status line: an old turn is not the current session.</summary>
    private ClaudeSessionState? GetFreshSession(DateTimeOffset now)
    {
        return _session is { } session && now - session.ObservedAt <= SessionMaxAge ? session : null;
    }

    private void EnsureWatchers()
    {
        foreach (var directory in _transcriptDirectories)
        {
            if (!Directory.Exists(directory) ||
                _transcriptWatchers.Any(watcher => string.Equals(
                    watcher.Path,
                    directory,
                    StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            try
            {
                var watcher = new FileSystemWatcher(directory, "*.jsonl")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                watcher.Changed += TranscriptWatcher_Changed;
                watcher.Created += TranscriptWatcher_Changed;
                watcher.Deleted += TranscriptWatcher_Changed;
                watcher.Renamed += TranscriptWatcher_Renamed;
                watcher.EnableRaisingEvents = true;
                _transcriptWatchers.Add(watcher);
                QueueAllTranscripts();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Retry on the next refresh; transcript monitoring is optional.
            }
        }

        if (_bridgeWatcher is null)
        {
            try
            {
                Directory.CreateDirectory(_bridgeDirectory);
                _bridgeWatcher = new FileSystemWatcher(_bridgeDirectory, Path.GetFileName(_bridgePath))
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true,
                };
                _bridgeWatcher.Changed += BridgeWatcher_Changed;
                _bridgeWatcher.Created += BridgeWatcher_Changed;
                _bridgeWatcher.Renamed += BridgeWatcher_Renamed;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _bridgeWatcher?.Dispose();
                _bridgeWatcher = null;
            }
        }
    }

    private void QueueAllTranscripts()
    {
        foreach (var directory in _transcriptDirectories)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            try
            {
                foreach (var path in Directory.EnumerateFiles(
                             directory,
                             "*.jsonl",
                             SearchOption.AllDirectories))
                {
                    _pendingPaths.TryAdd(path, 0);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Claude monitoring is optional; retry on the next refresh.
            }
        }
    }

    private void TranscriptWatcher_Changed(object sender, FileSystemEventArgs e)
    {
        if (!_disposed && IsTranscriptFile(e.FullPath))
        {
            _pendingPaths.TryAdd(e.FullPath, 0);
        }
    }

    private void TranscriptWatcher_Renamed(object sender, RenamedEventArgs e)
    {
        if (IsTranscriptFile(e.OldFullPath))
        {
            _pendingPaths.TryAdd(e.OldFullPath, 0);
        }

        TranscriptWatcher_Changed(sender, e);
    }

    private void BridgeWatcher_Changed(object sender, FileSystemEventArgs e)
    {
        _forceRefresh = true;
    }

    private void BridgeWatcher_Renamed(object sender, RenamedEventArgs e)
    {
        BridgeWatcher_Changed(sender, e);
    }

    private void ProcessPending(object? state)
    {
        if (_disposed || Interlocked.Exchange(ref _processing, 1) != 0)
        {
            return;
        }

        try
        {
            EnsureWatchers();
            var changed = false;
            var paths = _pendingPaths.Keys.ToArray();
            foreach (var path in paths)
            {
                _pendingPaths.TryRemove(path, out _);
                if (!File.Exists(path))
                {
                    lock (_recordsGate)
                    {
                        changed |= _recordsByFile.Remove(path);
                        _sessionByFile.Remove(path);
                    }

                    _observedFiles.TryRemove(path, out _);
                    continue;
                }

                if (!TryGetFileState(path, out var readState))
                {
                    _pendingPaths.TryAdd(path, 0);
                    continue;
                }

                // The window's one-minute refresh re-queues every transcript, and a watcher fires
                // several times per append. Re-reading a file whose size and timestamp are
                // unchanged re-materialises its every line as a string for nothing — on a large
                // history that was the entire corpus, every minute, whether or not Claude ran.
                if (_observedFiles.TryGetValue(path, out var observed) && observed == readState)
                {
                    continue;
                }

                try
                {
                    var records = ParseTranscriptFile(path, out var sessionState);
                    lock (_recordsGate)
                    {
                        _recordsByFile[path] = records;
                        if (sessionState is null)
                        {
                            _sessionByFile.Remove(path);
                        }
                        else
                        {
                            _sessionByFile[path] = sessionState;
                        }
                    }

                    // The state captured before the read is what gets remembered: if Claude appended
                    // while the file was being parsed, the next scan sees a difference and retries.
                    _observedFiles[path] = readState;
                    changed = true;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _pendingPaths.TryAdd(path, 0);
                }
            }

            if (changed)
            {
                ClaudeTokenRecord[] allRecords;
                lock (_recordsGate)
                {
                    allRecords = _recordsByFile.Values.SelectMany(records => records).ToArray();
                    // The newest turn across every project is the session the user is in right now.
                    _session = _sessionByFile.Values
                        .OrderByDescending(state => state.ObservedAt)
                        .FirstOrDefault();
                }

                _tokenUsage = allRecords.Length == 0
                    ? null
                    : ClaudeTranscriptParser.BuildAccountUsage(allRecords, DateTimeOffset.Now);
            }

            if (_forceRefresh || File.Exists(_bridgePath))
            {
                changed |= TryReadBridge();
            }

            var available = _account is not null ||
                            ClaudeExecutableResolver.IsAvailable() ||
                            _transcriptDirectories.Any(Directory.Exists) ||
                            _desktopStateDirectories.Any(Directory.Exists) ||
                            File.Exists(_bridgePath);
            // The app no longer installs the status-line bridge; it only consumes one that a CLI
            // user set up earlier, so the file's presence is the whole story.
            var configured = File.Exists(_bridgePath);
            changed |= available != _isClaudeAvailable || configured != _isBridgeConfigured;
            _isClaudeAvailable = available;
            _isBridgeConfigured = configured;
            _forceRefresh = false;

            if (changed || !_publishedInitialSnapshot)
            {
                PublishSnapshot();
            }
        }
        finally
        {
            Volatile.Write(ref _processing, 0);
        }
    }

    private bool TryReadBridge()
    {
        if (!File.Exists(_bridgePath))
        {
            if (_status is null)
            {
                return false;
            }

            _status = null;
            return true;
        }

        try
        {
            using var stream = new FileStream(
                _bridgePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            var status = ClaudeStatusParser.Parse(
                document.RootElement,
                new DateTimeOffset(File.GetLastWriteTimeUtc(_bridgePath), TimeSpan.Zero));
            if (status == _status)
            {
                return false;
            }

            _status = status;
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static bool TryGetFileState(string path, out FileState state)
    {
        state = default;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return false;
            }

            state = new FileState(info.Length, info.LastWriteTimeUtc);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static IReadOnlyList<ClaudeTokenRecord> ParseTranscriptFile(
        string path,
        out ClaudeSessionState? sessionState)
    {
        var fallbackTimestamp = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return ClaudeTranscriptParser.ParseReader(reader, path, fallbackTimestamp, out sessionState);
    }

    private bool IsTranscriptFile(string path)
    {
        return path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) &&
               _transcriptDirectories.Any(directory => path.StartsWith(
                   directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> ResolveTranscriptDirectories(string projectsDirectory)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new[]
        {
            projectsDirectory,
            Path.Combine(appData, "Claude", "projects"),
            Path.Combine(appData, "Claude Code", "projects"),
            Path.Combine(appData, "AnthropicClaude", "projects"),
            Path.Combine(localAppData, "Claude", "projects"),
            Path.Combine(localAppData, "Claude Code", "projects"),
            Path.Combine(localAppData, "AnthropicClaude", "projects"),
        };

        return NormalizeDistinct(candidates);
    }

    private static IReadOnlyList<string> ResolveDesktopStateDirectories()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new[]
        {
            Path.Combine(appData, "Claude"),
            Path.Combine(appData, "Claude Code"),
            Path.Combine(appData, "AnthropicClaude"),
            Path.Combine(localAppData, "Claude"),
            Path.Combine(localAppData, "Claude Code"),
            Path.Combine(localAppData, "AnthropicClaude"),
        };

        return NormalizeDistinct(candidates);
    }

    private static IReadOnlyList<string> NormalizeDistinct(IEnumerable<string> candidates)
    {
        var paths = new List<string>();
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            try
            {
                var path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(candidate));
                if (!paths.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    paths.Add(path);
                }
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                // Ignore malformed environment values.
            }
        }

        return paths;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        _timer?.Dispose();
        _quotaTimer?.Dispose();
        foreach (var watcher in _transcriptWatchers)
        {
            watcher.Dispose();
        }

        _bridgeWatcher?.Dispose();
        _usageClient?.Dispose();
        _lifetime.Dispose();
        _timer = null;
        _quotaTimer = null;
        _transcriptWatchers.Clear();
        _observedFiles.Clear();
        _bridgeWatcher = null;
    }

    private readonly record struct FileState(long Length, DateTime LastWriteTimeUtc);
}
