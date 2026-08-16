using System.Buffers;
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

    /// <summary>
    /// How recent an assistant turn has to be before it counts as "the quota just moved" rather
    /// than history. Parsing a months-old transcript at startup must not look like activity.
    /// </summary>
    internal static readonly TimeSpan ActivityFreshness = TimeSpan.FromMinutes(2);

    /// <summary>Floor between activity-triggered reads, so a rapid exchange is not one call a turn.</summary>
    private static readonly TimeSpan ActivityQuotaInterval = TimeSpan.FromSeconds(20);

    private readonly ConcurrentDictionary<string, byte> _pendingPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TranscriptFile> _transcripts =
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
    private DateTimeOffset _lastHandledTurnAt = DateTimeOffset.MinValue;
    private DateTimeOffset _nextActivityQuotaAt = DateTimeOffset.MinValue;
    private bool _quotaFetchPending;
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
    /// <param name="force">
    /// Set for a read the user asked for, or one a freshly written turn justifies. It skips the
    /// routine spacing but never the rate-limit penalty, so neither repeated clicking nor a busy
    /// session can dig the limit deeper.
    /// </param>
    private async Task FetchQuotaAsync(bool force = false)
    {
        if (_disposed || _usageClient is null)
        {
            return;
        }

        // The window's refresh timer and this monitor's own timer both land on the same minute, and
        // Claude Code polls the same endpoint. Without this gate the app produced two calls per
        // tick and the endpoint started answering 429.
        if (!_throttle.TryAcquire(DateTimeOffset.UtcNow, force) ||
            Interlocked.Exchange(ref _fetchingQuota, 1) != 0)
        {
            return;
        }

        // Every call that actually goes out restarts the activity floor, whatever triggered it, so
        // a routine poll and a turn landing a moment later cannot both reach the endpoint.
        _nextActivityQuotaAt = DateTimeOffset.UtcNow + ActivityQuotaInterval;

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
                        changed |= _transcripts.Remove(path);
                    }

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
                if (_transcripts.TryGetValue(path, out var transcript) && transcript.Matches(readState))
                {
                    continue;
                }

                try
                {
                    lock (_recordsGate)
                    {
                        if (!_transcripts.TryGetValue(path, out transcript))
                        {
                            transcript = new TranscriptFile();
                            _transcripts[path] = transcript;
                        }

                        ReadAppendedRecords(path, transcript, readState);
                    }

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
                    allRecords = _transcripts.Values
                        .SelectMany(file => file.Records.Values)
                        .ToArray();
                    // The newest turn across every project is the session the user is in right now.
                    _session = _transcripts.Values
                        .Select(file => file.Session)
                        .Where(session => session is not null)
                        .OrderByDescending(session => session!.ObservedAt)
                        .FirstOrDefault();
                }

                _tokenUsage = allRecords.Length == 0
                    ? null
                    : ClaudeTranscriptParser.BuildAccountUsage(allRecords, DateTimeOffset.Now);
            }

            TryFetchQuotaForActivity();

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

    /// <summary>
    /// Turns a freshly written assistant turn into an immediate quota read.
    /// </summary>
    /// <remarks>
    /// Codex gets this for free — its app-server pushes <c>account/rateLimits/updated</c>. Claude's
    /// usage endpoint has no such channel, but the transcript watcher is the next best thing: the
    /// turn lands on disk within a second of finishing, and a finished turn is exactly when the
    /// quota moved. Driving the read off that beats shortening the poll in both directions — the
    /// number is seconds old while Claude is in use, and nothing is requested at all while it is
    /// idle, where a shorter poll would have meant strictly more calls for no fresher a number.
    /// </remarks>
    private void TryFetchQuotaForActivity()
    {
        var now = DateTimeOffset.UtcNow;
        if (_session is { } session && IsNewActivity(now, session.ObservedAt, _lastHandledTurnAt))
        {
            _lastHandledTurnAt = session.ObservedAt;
            _quotaFetchPending = true;
        }

        // A turn that arrives inside the floor stays pending rather than being dropped, so the
        // one-second loop picks it up as soon as the floor expires.
        if (!_quotaFetchPending || now < _nextActivityQuotaAt)
        {
            return;
        }

        _quotaFetchPending = false;
        _ = FetchQuotaAsync(force: true);
    }

    internal static bool IsNewActivity(
        DateTimeOffset now,
        DateTimeOffset turnObservedAt,
        DateTimeOffset lastHandledTurnAt)
    {
        return turnObservedAt > lastHandledTurnAt &&
               now - turnObservedAt <= ActivityFreshness;
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

    /// <summary>
    /// Folds everything appended since the last pass into <paramref name="transcript"/>.
    /// </summary>
    /// <remarks>
    /// A Claude transcript is append-only JSONL, so the bytes before the last consumed newline are
    /// settled and never need reading again. Only an append re-reads anything, which is what keeps a
    /// 57 MB session that is being written to right now from costing 57 MB of line strings on every
    /// pass. A file that shrank was replaced rather than appended to, and starts over from zero.
    ///
    /// Byte offsets, not <see cref="StreamReader"/>, because the reader buffers ahead and cannot say
    /// where a line ended. That also allows the usage gate to run on raw UTF-8: a 2.6 MB tool-result
    /// line is rejected without ever becoming a string.
    /// </remarks>
    private static void ReadAppendedRecords(string path, TranscriptFile transcript, FileState observed)
    {
        if (observed.Length < transcript.ParsedOffset)
        {
            transcript.Reset();
        }

        var fallbackTimestamp = new DateTimeOffset(observed.LastWriteTimeUtc, TimeSpan.Zero);
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        var position = transcript.ParsedOffset;
        stream.Seek(position, SeekOrigin.Begin);

        var chunk = ArrayPool<byte>.Shared.Rent(64 * 1024);
        var line = new ArrayBufferWriter<byte>(4 * 1024);
        var session = transcript.Session;
        try
        {
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                var searchFrom = 0;
                while (searchFrom < read)
                {
                    var newline = Array.IndexOf(chunk, (byte)'\n', searchFrom, read - searchFrom);
                    if (newline < 0)
                    {
                        // A partial trailing line: carry it, but do not advance past it. Claude may
                        // still be midway through writing this record.
                        line.Write(chunk.AsSpan(searchFrom, read - searchFrom));
                        break;
                    }

                    line.Write(chunk.AsSpan(searchFrom, newline - searchFrom));
                    transcript.LineNumber++;
                    ConsumeLine(
                        line.WrittenSpan,
                        path,
                        transcript,
                        transcript.LineNumber,
                        fallbackTimestamp,
                        ref session);
                    line.ResetWrittenCount();
                    searchFrom = newline + 1;
                    transcript.ParsedOffset = position + searchFrom;
                }

                position += read;
            }

            if (line.WrittenCount > 0)
            {
                // A final record with no terminating newline. Parse it so a transcript that never
                // ends in one still reports its last turn, but leave ParsedOffset behind it: if this
                // was a half-written record, the next pass re-reads it once Claude finishes. Re-reads
                // are harmless because records deduplicate on message id, and the line number is not
                // committed either, so the fallback key stays stable across those retries.
                ConsumeLine(
                    line.WrittenSpan,
                    path,
                    transcript,
                    transcript.LineNumber + 1,
                    fallbackTimestamp,
                    ref session);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        transcript.Session = session;
        // The state sampled before the read is what gets remembered: if Claude appended while the
        // file was being parsed, the next scan sees a difference and picks up the remainder.
        transcript.Observed = observed;
    }

    private static void ConsumeLine(
        ReadOnlySpan<byte> line,
        string path,
        TranscriptFile transcript,
        int lineNumber,
        DateTimeOffset fallbackTimestamp,
        ref ClaudeSessionState? session)
    {
        if (lineNumber == 1 && line.StartsWith(Utf8Bom))
        {
            line = line[Utf8Bom.Length..];
        }

        if (line.EndsWith("\r"u8))
        {
            line = line[..^1];
        }

        if (line.IndexOf(ClaudeTranscriptParser.UsagePropertyUtf8) < 0)
        {
            return;
        }

        ClaudeTranscriptParser.Accumulate(
            Encoding.UTF8.GetString(line),
            path,
            lineNumber,
            fallbackTimestamp,
            transcript.Records,
            ref session);
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
        lock (_recordsGate)
        {
            _transcripts.Clear();
        }

        _bridgeWatcher = null;
    }

    private readonly record struct FileState(long Length, DateTime LastWriteTimeUtc);

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    /// <summary>One transcript's accumulated view, carried across passes so appends stay cheap.</summary>
    private sealed class TranscriptFile
    {
        public Dictionary<string, ClaudeTokenRecord> Records { get; private set; } =
            new(StringComparer.Ordinal);

        public ClaudeSessionState? Session { get; set; }

        /// <summary>Just past the last complete line consumed; a partial tail is left for next time.</summary>
        public long ParsedOffset { get; set; }

        /// <summary>Only used for the fallback record key, so it has to keep counting across passes.</summary>
        public int LineNumber { get; set; }

        public FileState Observed { get; set; }

        public bool Matches(FileState state) => Observed == state;

        public void Reset()
        {
            Records = new Dictionary<string, ClaudeTokenRecord>(StringComparer.Ordinal);
            Session = null;
            ParsedOffset = 0;
            LineNumber = 0;
            Observed = default;
        }
    }
}
