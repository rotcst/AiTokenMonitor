using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using CodexWeeklyMonitor.Models;
using Timer = System.Threading.Timer;

namespace CodexWeeklyMonitor.Services;

public sealed class AntigravityUsageMonitor : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan StatusMaxAge = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan SessionMaxAge = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, byte> _pendingPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TranscriptFile> _transcripts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _recordsGate = new();
    private readonly string _antigravityHome;
    private readonly IReadOnlyList<string> _brainDirectories;
    private readonly string _bridgeDirectory;
    private readonly string _bridgePath;
    private readonly List<FileSystemWatcher> _transcriptWatchers = [];

    private FileSystemWatcher? _bridgeWatcher;
    private Timer? _timer;
    private AntigravityStatusUsage? _status;
    private AntigravitySessionState? _session;
    private AccountTokenUsage? _tokenUsage;
    private IReadOnlyList<AntigravityModelUsage> _models = [];
    private int _totalConversations;
    private int _totalTurns;
    private bool _isAntigravityAvailable;
    private bool _isBridgeConfigured;
    private int _processing;
    private bool _publishedInitialSnapshot;
    private bool _forceRefresh;
    private bool _disposed;

    public AntigravityUsageMonitor(string? antigravityHome = null, string? bridgeDirectory = null)
    {
        _antigravityHome = AntigravityPaths.ResolveHome(antigravityHome);
        _brainDirectories = AntigravityPaths.ResolveBrainDirectories(antigravityHome);
        _bridgeDirectory = string.IsNullOrWhiteSpace(bridgeDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AiTokenMonitor")
            : Path.GetFullPath(bridgeDirectory);
        _bridgePath = Path.Combine(_bridgeDirectory, "antigravity-status.json");
    }

    public event Action<AntigravityUsageSnapshot>? UsageUpdated;

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
    }

    public void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        QueueAllTranscripts();
        _forceRefresh = true;
        if (_timer is null)
        {
            ProcessPending(null);
            return;
        }

        _timer.Change(TimeSpan.Zero, PollInterval);
    }

    private void PublishSnapshot()
    {
        _publishedInitialSnapshot = true;
        var now = DateTimeOffset.Now;
        UsageUpdated?.Invoke(new AntigravityUsageSnapshot(
            Models: _models,
            TokenUsage: _tokenUsage,
            Session: GetFreshSession(now),
            Status: GetFreshStatus(now),
            TotalConversations: _totalConversations,
            TotalTurns: _totalTurns,
            IsAntigravityAvailable: _isAntigravityAvailable,
            IsBridgeConfigured: _isBridgeConfigured,
            FetchedAt: now));
    }

    private AntigravityStatusUsage? GetFreshStatus(DateTimeOffset now)
    {
        return _status is { } status && now - status.ObservedAt <= StatusMaxAge ? status : null;
    }

    private AntigravitySessionState? GetFreshSession(DateTimeOffset now)
    {
        return _session is { } session && now - session.ObservedAt <= SessionMaxAge ? session : null;
    }

    private void EnsureWatchers()
    {
        foreach (var directory in _brainDirectories)
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
                var watcher = new FileSystemWatcher(directory, "transcript.jsonl")
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
        foreach (var directory in _brainDirectories)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            try
            {
                foreach (var path in Directory.EnumerateFiles(
                             directory,
                             "transcript.jsonl",
                             SearchOption.AllDirectories))
                {
                    if (IsCanonicalTranscriptFile(path))
                    {
                        _pendingPaths.TryAdd(path, 0);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Antigravity monitoring is optional; retry on the next refresh.
            }
        }

        if (_status?.TranscriptPath is { } hookedTranscript &&
            !string.IsNullOrWhiteSpace(hookedTranscript) &&
            File.Exists(hookedTranscript))
        {
            _pendingPaths.TryAdd(hookedTranscript, 0);
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

            if (_forceRefresh || File.Exists(_bridgePath))
            {
                if (TryReadBridge())
                {
                    changed = true;
                    if (_status?.TranscriptPath is { } hookedPath &&
                        !string.IsNullOrWhiteSpace(hookedPath) &&
                        File.Exists(hookedPath))
                    {
                        _pendingPaths.TryAdd(hookedPath, 0);
                    }
                }
            }

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

                        ReadAppendedRecords(path, transcript, readState, _status);
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
                RebuildAggregates();
            }

            var available = _totalTurns > 0 ||
                            _status is not null ||
                            Directory.Exists(_antigravityHome) ||
                            _brainDirectories.Any(Directory.Exists) ||
                            File.Exists(_bridgePath);
            var configured = File.Exists(_bridgePath);
            changed |= available != _isAntigravityAvailable || configured != _isBridgeConfigured;
            _isAntigravityAvailable = available;
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

    private void RebuildAggregates()
    {
        AntigravityTokenRecord[] allRecords;
        int conversationCount;
        lock (_recordsGate)
        {
            allRecords = _transcripts.Values
                .SelectMany(file => file.Records.Values)
                .ToArray();
            conversationCount = _transcripts.Values.Count(file => file.Records.Count > 0);
            _session = _transcripts.Values
                .Select(file => file.Session)
                .Where(session => session is not null)
                .OrderByDescending(session => session!.ObservedAt)
                .FirstOrDefault();
        }

        // If a hook bridge supplied a live modelName for the active conversation and the transcript
        // itself had no explicit Model Selection tag ("Auto"), attribute those records to the hook's model.
        if (_status is { ModelName: { Length: > 0 } bridgedModel } &&
            !bridgedModel.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            var targetConversation = _status.ConversationId;
            for (var index = 0; index < allRecords.Length; index++)
            {
                var record = allRecords[index];
                if (record.ModelName.Equals("Auto", StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrWhiteSpace(targetConversation) ||
                     string.Equals(record.ConversationId, targetConversation, StringComparison.OrdinalIgnoreCase)))
                {
                    allRecords[index] = record with { ModelName = bridgedModel };
                }
            }

            if (_session is { ModelName: "Auto" } currentSession &&
                (string.IsNullOrWhiteSpace(targetConversation) ||
                 string.Equals(currentSession.ConversationId, targetConversation, StringComparison.OrdinalIgnoreCase)))
            {
                _session = currentSession with { ModelName = bridgedModel };
            }
        }

        var now = DateTimeOffset.Now;
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        var freshStatus = GetFreshStatus(now);
        _totalConversations = conversationCount;
        _totalTurns = allRecords.Length;
        _tokenUsage = allRecords.Length == 0
            ? null
            : AntigravityTranscriptParser.BuildAccountUsage(allRecords, now);
        _models = AntigravityTranscriptParser.BuildModelUsage(
            allRecords,
            today,
            freshStatus?.ModelQuotas);
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
            var status = AntigravityStatusParser.Parse(
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

    private static void ReadAppendedRecords(
        string path,
        TranscriptFile transcript,
        FileState observed,
        AntigravityStatusUsage? status)
    {
        if (observed.Length < transcript.ParsedOffset)
        {
            transcript.Reset();
        }

        if (string.IsNullOrWhiteSpace(transcript.ActiveModel) &&
            status?.ModelName is { Length: > 0 } bridgedModel &&
            !bridgedModel.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            var convId = AntigravityTranscriptParser.ResolveConversationIdFromPath(path);
            if (string.IsNullOrWhiteSpace(status.ConversationId) ||
                string.Equals(convId, status.ConversationId, StringComparison.OrdinalIgnoreCase))
            {
                transcript.ActiveModel = bridgedModel;
            }
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
        var activeModel = transcript.ActiveModel;
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
                        ref activeModel,
                        ref session);
                    line.ResetWrittenCount();
                    searchFrom = newline + 1;
                    transcript.ParsedOffset = position + searchFrom;
                }

                position += read;
            }

            if (line.WrittenCount > 0)
            {
                var trailingModel = activeModel;
                ConsumeLine(
                    line.WrittenSpan,
                    path,
                    transcript,
                    transcript.LineNumber + 1,
                    fallbackTimestamp,
                    ref trailingModel,
                    ref session);
                activeModel = trailingModel;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        transcript.ActiveModel = activeModel;
        transcript.Session = session;
        transcript.Observed = observed;
    }

    private static void ConsumeLine(
        ReadOnlySpan<byte> line,
        string path,
        TranscriptFile transcript,
        int lineNumber,
        DateTimeOffset fallbackTimestamp,
        ref string? activeModel,
        ref AntigravitySessionState? session)
    {
        if (lineNumber == 1 && line.StartsWith(Utf8Bom))
        {
            line = line[Utf8Bom.Length..];
        }

        if (line.EndsWith("\r"u8))
        {
            line = line[..^1];
        }

        if (!AntigravityTranscriptParser.MayContainRelevantDataUtf8(line))
        {
            return;
        }

        AntigravityTranscriptParser.Accumulate(
            Encoding.UTF8.GetString(line),
            path,
            lineNumber,
            fallbackTimestamp,
            transcript.Records,
            ref activeModel,
            ref session);
    }

    private static bool IsCanonicalTranscriptFile(string path)
    {
        if (!Path.GetFileName(path).Equals("transcript.jsonl", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return !normalized.Contains(
            $"{Path.DirectorySeparatorChar}chunks{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase);
    }

    private bool IsTranscriptFile(string path)
    {
        if (!IsCanonicalTranscriptFile(path))
        {
            return false;
        }

        return _brainDirectories.Any(directory => path.StartsWith(
            directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer?.Dispose();
        foreach (var watcher in _transcriptWatchers)
        {
            watcher.Dispose();
        }

        _bridgeWatcher?.Dispose();
        _timer = null;
        _transcriptWatchers.Clear();
        lock (_recordsGate)
        {
            _transcripts.Clear();
        }

        _bridgeWatcher = null;
    }

    private readonly record struct FileState(long Length, DateTime LastWriteTimeUtc);

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    private sealed class TranscriptFile
    {
        public Dictionary<string, AntigravityTokenRecord> Records { get; private set; } =
            new(StringComparer.Ordinal);

        public AntigravitySessionState? Session { get; set; }

        public string? ActiveModel { get; set; }

        public long ParsedOffset { get; set; }

        public int LineNumber { get; set; }

        public FileState Observed { get; set; }

        public bool Matches(FileState state) => Observed == state;

        public void Reset()
        {
            Records.Clear();
            Session = null;
            ActiveModel = null;
            ParsedOffset = 0;
            LineNumber = 0;
            Observed = default;
        }
    }
}
