using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CodexWeeklyMonitor.Models;
using Timer = System.Threading.Timer;

namespace CodexWeeklyMonitor.Services;

public sealed class LocalSessionTokenMonitor : IDisposable
{
    private const int MaximumTailBytes = 512 * 1024;
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan DirectoryRecoveryInterval = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<string, byte> _pendingPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _sessionGroupKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, FileState> _observedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _dailyBaselines = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _sessionsDirectory;
    private readonly string _archivedSessionsDirectory;
    private readonly TodayTokenAccumulator _accumulator = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private Timer? _timer;
    private TodayTokenUsage? _lastPublishedUsage;
    private DateOnly _trackedDate;
    private DateTimeOffset _nextDirectoryRecoveryAt;
    private int _processing;
    private int _fullRecoveryRequested;
    private bool _disposed;

    public LocalSessionTokenMonitor(string? codexHome = null)
    {
        var resolvedHome = string.IsNullOrWhiteSpace(codexHome)
            ? Environment.GetEnvironmentVariable("CODEX_HOME")
            : codexHome;
        if (string.IsNullOrWhiteSpace(resolvedHome))
        {
            resolvedHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex");
        }

        _sessionsDirectory = Path.Combine(resolvedHome, "sessions");
        _archivedSessionsDirectory = Path.Combine(resolvedHome, "archived_sessions");
    }

    public event Action<TodayTokenUsage>? UsageUpdated;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_timer is not null)
        {
            return;
        }

        _trackedDate = DateOnly.FromDateTime(DateTime.Now);
        _accumulator.Reset(_trackedDate);

        QueueRecentlyModifiedFiles(_trackedDate);
        QueueCurrentDateFiles(_trackedDate);
        EnsureWatchers();

        _nextDirectoryRecoveryAt = DateTimeOffset.MinValue;
        _timer = new Timer(ProcessPendingFiles, null, TimeSpan.Zero, DebounceInterval);
    }

    public void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        Interlocked.Exchange(ref _fullRecoveryRequested, 1);
        _timer?.Change(TimeSpan.Zero, DebounceInterval);
    }

    private void EnsureWatchers()
    {
        if (!_watchers.Any(watcher => string.Equals(
                watcher.Path,
                _sessionsDirectory,
                StringComparison.OrdinalIgnoreCase)))
        {
            TryStartWatcher(_sessionsDirectory, includeSubdirectories: true);
        }

        if (!_watchers.Any(watcher => string.Equals(
                watcher.Path,
                _archivedSessionsDirectory,
                StringComparison.OrdinalIgnoreCase)))
        {
            TryStartWatcher(_archivedSessionsDirectory, includeSubdirectories: false);
        }
    }

    private bool TryStartWatcher(string directory, bool includeSubdirectories)
    {
        if (!Directory.Exists(directory))
        {
            return false;
        }

        FileSystemWatcher watcher;
        try
        {
            watcher = new FileSystemWatcher(directory, "*.jsonl")
            {
                IncludeSubdirectories = includeSubdirectories,
                NotifyFilter = NotifyFilters.CreationTime |
                               NotifyFilters.FileName |
                               NotifyFilters.LastWrite |
                               NotifyFilters.Size,
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        watcher.Changed += Watcher_Changed;
        watcher.Created += Watcher_Changed;
        watcher.Renamed += Watcher_Renamed;
        watcher.Error += Watcher_Error;
        watcher.EnableRaisingEvents = true;
        _watchers.Add(watcher);
        return true;
    }

    private void QueueSessionFiles(DateOnly date)
    {
        var directory = GetSessionDirectory(date);
        if (!Directory.Exists(directory))
        {
            return;
        }

        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.TopDirectoryOnly))
            {
                QueueIfChanged(path);
            }
        }
        catch (IOException)
        {
            // A Codex process may be rotating session files while they are enumerated.
        }
        catch (UnauthorizedAccessException)
        {
            // Local session monitoring is optional; account usage remains available.
        }
    }

    private void QueueArchivedSessionFiles(DateOnly date)
    {
        if (!Directory.Exists(_archivedSessionsDirectory))
        {
            return;
        }

        try
        {
            foreach (var path in Directory.EnumerateFiles(
                         _archivedSessionsDirectory,
                         "*.jsonl",
                         SearchOption.TopDirectoryOnly))
            {
                if (IsArchivedSessionFileForDate(path, date))
                {
                    QueueIfChanged(path);
                }
            }
        }
        catch (IOException)
        {
            // Codex Desktop may be moving completed sessions into the archive.
        }
        catch (UnauthorizedAccessException)
        {
            // Local session monitoring is optional; account usage remains available.
        }
    }

    private void QueueCurrentDateFiles(DateOnly date)
    {
        QueueSessionFiles(date);
        QueueArchivedSessionFiles(date);
    }

    private void QueueRecentlyModifiedFiles(DateOnly date)
    {
        var localMidnight = date.ToDateTime(TimeOnly.MinValue);
        QueueRecentlyModifiedFiles(_sessionsDirectory, SearchOption.AllDirectories, localMidnight);
        QueueRecentlyModifiedFiles(_archivedSessionsDirectory, SearchOption.TopDirectoryOnly, localMidnight);
    }

    private void QueueRecentlyModifiedFiles(string directory, SearchOption searchOption, DateTime localMidnight)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.jsonl", searchOption))
            {
                if (File.GetLastWriteTime(path) >= localMidnight)
                {
                    QueueIfChanged(path);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A recovery scan is best effort; watcher events and the next refresh can fill gaps.
        }
    }

    private void QueueIfChanged(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return;
            }

            var current = new FileState(info.Length, info.LastWriteTimeUtc);
            if (!_observedFiles.TryGetValue(path, out var observed) || observed != current)
            {
                _pendingPaths.TryAdd(path, 0);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _pendingPaths.TryAdd(path, 0);
        }
    }

    private void Watcher_Changed(object sender, FileSystemEventArgs e)
    {
        if (!_disposed && e.FullPath.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            _pendingPaths.TryAdd(e.FullPath, 0);
        }
    }

    private void Watcher_Renamed(object sender, RenamedEventArgs e)
    {
        Watcher_Changed(sender, e);
    }

    private void Watcher_Error(object sender, ErrorEventArgs e)
    {
        if (!_disposed)
        {
            // FileSystemWatcher reports internal-buffer overflow here. A full modified-today scan
            // repairs any events that were dropped while Codex was writing many parallel sessions.
            Interlocked.Exchange(ref _fullRecoveryRequested, 1);
        }
    }

    private void ProcessPendingFiles(object? state)
    {
        if (_disposed || Interlocked.Exchange(ref _processing, 1) != 0)
        {
            return;
        }

        try
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            if (today != _trackedDate)
            {
                _trackedDate = today;
                _accumulator.Reset(today);
                _lastPublishedUsage = null;
                _sessionGroupKeys.Clear();
                _observedFiles.Clear();
                _dailyBaselines.Clear();
                QueueRecentlyModifiedFiles(today);
                QueueCurrentDateFiles(today);
            }

            EnsureWatchers();
            var now = DateTimeOffset.Now;
            if (now >= _nextDirectoryRecoveryAt)
            {
                // Metadata-only rescan of today's directories closes the small race between a
                // partial JSONL write and its final FileSystemWatcher notification.
                QueueCurrentDateFiles(today);
                _nextDirectoryRecoveryAt = now + DirectoryRecoveryInterval;
            }

            if (Interlocked.Exchange(ref _fullRecoveryRequested, 0) != 0)
            {
                QueueRecentlyModifiedFiles(today);
            }

            var paths = _pendingPaths.Keys.ToArray();
            foreach (var path in paths)
            {
                _pendingPaths.TryRemove(path, out _);
                if (!TryGetFileState(path, out var readState))
                {
                    _observedFiles.TryRemove(path, out _);
                    continue;
                }

                if (!TryReadLatestUsage(path, out var usage))
                {
                    _pendingPaths.TryAdd(path, 0);
                    continue;
                }

                // Remember the state captured before the read. If Codex appended while the tail
                // was being parsed, the next metadata scan will see a different state and retry.
                _observedFiles[path] = readState;
                if (usage is null ||
                    DateOnly.FromDateTime(usage.ObservedAt.LocalDateTime) != today ||
                    !TryConvertToDailyUsage(path, today, usage, out var dailyUsage))
                {
                    continue;
                }

                var sessionGroupKey = _sessionGroupKeys.GetOrAdd(path, ResolveSessionGroupKey);
                _accumulator.Update(sessionGroupKey, dailyUsage);
            }

            var aggregate = _accumulator.Snapshot();
            if (_lastPublishedUsage is null ||
                aggregate.Date != _lastPublishedUsage.Date ||
                aggregate.Tokens != _lastPublishedUsage.Tokens ||
                aggregate.SessionCount != _lastPublishedUsage.SessionCount)
            {
                _lastPublishedUsage = aggregate;
                UsageUpdated?.Invoke(aggregate);
            }
        }
        finally
        {
            Volatile.Write(ref _processing, 0);
        }
    }

    private bool IsSessionFileForDate(string path, DateOnly date)
    {
        return IsDailySessionFileForDate(path, date) ||
               IsArchivedSessionFileForDate(path, date);
    }

    private bool IsDailySessionFileForDate(string path, DateOnly date)
    {
        var directory = Path.GetDirectoryName(path);
        return string.Equals(
            directory?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            GetSessionDirectory(date).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private bool IsArchivedSessionFileForDate(string path, DateOnly date)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.Equals(
                directory?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                _archivedSessionsDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        const string prefix = "rollout-";
        var fileName = Path.GetFileName(path);
        if (fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            fileName.Length >= prefix.Length + 10 &&
            DateOnly.TryParseExact(
                fileName.Substring(prefix.Length, 10),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var fileDate))
        {
            return fileDate == date;
        }

        return DateOnly.FromDateTime(File.GetLastWriteTime(path)) == date;
    }

    private string GetSessionDirectory(DateOnly date)
    {
        return Path.Combine(
            _sessionsDirectory,
            date.ToString("yyyy", CultureInfo.InvariantCulture),
            date.ToString("MM", CultureInfo.InvariantCulture),
            date.ToString("dd", CultureInfo.InvariantCulture));
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

    private bool TryConvertToDailyUsage(
        string path,
        DateOnly date,
        LiveTokenUsage latest,
        out LiveTokenUsage dailyUsage)
    {
        dailyUsage = latest;
        if (!_dailyBaselines.TryGetValue(path, out var baseline))
        {
            if (!TryReadDailyBaseline(path, date, out baseline))
            {
                return false;
            }

            _dailyBaselines[path] = baseline;
        }

        var dailyTotal = Math.Max(0, latest.TotalTokens - baseline);
        dailyUsage = latest with { TotalTokens = dailyTotal };
        return true;
    }

    private bool TryReadDailyBaseline(string path, DateOnly date, out long baseline)
    {
        baseline = 0;
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length == 0)
            {
                return false;
            }

            var startOffset = IsSessionFileForDate(path, date)
                ? 0
                : FindDateSearchOffset(stream, StartOfLocalDateUtc(date));
            stream.Seek(startOffset, SeekOrigin.Begin);
            using var reader = new StreamReader(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: startOffset == 0,
                bufferSize: 16 * 1024,
                leaveOpen: false);
            if (startOffset > 0)
            {
                // The search offset can land in the middle of a JSONL record.
                reader.ReadLine();
            }

            while (reader.ReadLine() is { } line)
            {
                if (!TryParseUsageLine(line.AsMemory(), File.GetLastWriteTimeUtc(path), out var usage))
                {
                    continue;
                }

                var observedDate = DateOnly.FromDateTime(usage.ObservedAt.LocalDateTime);
                if (observedDate < date)
                {
                    continue;
                }

                if (observedDate > date)
                {
                    return false;
                }

                // App-server's total_token_usage is cumulative. Removing the first event's
                // last_token_usage gives the exact carry-in from before this local day, including
                // resumed sessions and forked transcripts that already contain prior context.
                baseline = Math.Max(0, usage.TotalTokens - usage.LastTurnTokens);
                return true;
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }

        return false;
    }

    private static DateTimeOffset StartOfLocalDateUtc(DateOnly date)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUniversalTime();
    }

    private static long FindDateSearchOffset(FileStream stream, DateTimeOffset targetUtc)
    {
        const long searchPadding = 2L * 1024 * 1024;
        var low = 0L;
        var high = stream.Length;
        while (high - low > MaximumTailBytes)
        {
            var middle = low + ((high - low) / 2);
            if (!TryReadTimestampAtOrAfter(stream, middle, out var timestamp))
            {
                high = middle;
                continue;
            }

            if (timestamp < targetUtc)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        // Back up beyond the remaining binary-search window so the first token event of the day
        // cannot be skipped even when the probe landed inside a large response_item record.
        return Math.Max(0, low - searchPadding);
    }

    private static bool TryReadTimestampAtOrAfter(
        FileStream stream,
        long offset,
        out DateTimeOffset timestamp)
    {
        timestamp = default;
        stream.Seek(offset, SeekOrigin.Begin);
        if (offset > 0)
        {
            var foundLineEnd = false;
            var scanBuffer = new byte[16 * 1024];
            while (stream.Position < stream.Length)
            {
                var blockStart = stream.Position;
                var read = stream.Read(scanBuffer, 0, scanBuffer.Length);
                var lineEnd = Array.IndexOf(scanBuffer, (byte)'\n', 0, read);
                if (lineEnd >= 0)
                {
                    stream.Position = blockStart + lineEnd + 1;
                    foundLineEnd = true;
                    break;
                }

                if (read == 0)
                {
                    break;
                }
            }

            if (!foundLineEnd)
            {
                return false;
            }
        }

        var prefixBuffer = new byte[512];
        var prefixLength = stream.Read(prefixBuffer, 0, prefixBuffer.Length);
        if (prefixLength == 0)
        {
            return false;
        }

        var prefix = Encoding.UTF8.GetString(prefixBuffer, 0, prefixLength);
        const string marker = "\"timestamp\":\"";
        var markerIndex = prefix.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return false;
        }

        var valueStart = markerIndex + marker.Length;
        var valueEnd = prefix.IndexOf('"', valueStart);
        return valueEnd > valueStart &&
               DateTimeOffset.TryParse(
                   prefix[valueStart..valueEnd],
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal,
                   out timestamp);
    }

    private static string ResolveSessionGroupKey(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 4096,
                leaveOpen: false);
            var firstLine = reader.ReadLine();
            var key = string.IsNullOrWhiteSpace(firstLine)
                ? null
                : ParseSessionGroupKeyFromText(firstLine);
            return string.IsNullOrWhiteSpace(key)
                ? Path.GetFileNameWithoutExtension(path)
                : key;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return Path.GetFileNameWithoutExtension(path);
        }
    }

    internal static string? ParseSessionGroupKeyFromText(string sessionMetaLine)
    {
        using var document = JsonDocument.Parse(
            sessionMetaLine,
            new JsonDocumentOptions
            {
                MaxDepth = 512,
            });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (root.TryGetProperty("payload", out var payload) &&
            payload.ValueKind == JsonValueKind.Object &&
            PickSessionGroupKey(ReadSessionIdentity(payload)) is { } payloadKey)
        {
            return payloadKey;
        }

        return PickSessionGroupKey(ReadSessionIdentity(root));
    }

    private static SessionIdentity ReadSessionIdentity(JsonElement element)
    {
        return new SessionIdentity(
            ForkedFromId: GetString(element, "forked_from_id") ?? GetString(element, "forkedFromId"),
            SessionId: GetString(element, "session_id") ?? GetString(element, "sessionId"),
            Id: GetString(element, "id"));
    }

    private static string? PickSessionGroupKey(SessionIdentity identity)
    {
        if (!string.IsNullOrWhiteSpace(identity.ForkedFromId) &&
            (string.IsNullOrWhiteSpace(identity.SessionId) ||
             string.Equals(identity.SessionId, identity.Id, StringComparison.OrdinalIgnoreCase)))
        {
            return identity.ForkedFromId;
        }

        return identity.SessionId ?? identity.ForkedFromId ?? identity.Id;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) &&
               value.ValueKind == JsonValueKind.String &&
               !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;
    }

    private static bool TryReadLatestUsage(string path, out LiveTokenUsage? usage)
    {
        usage = null;
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length == 0)
            {
                return true;
            }

            var bytesToRead = (int)Math.Min(stream.Length, MaximumTailBytes);
            stream.Seek(-bytesToRead, SeekOrigin.End);
            // Pooled: this runs for every active session file every couple of seconds, and a fresh
            // half-megabyte array each time went straight to the large object heap.
            var buffer = ArrayPool<byte>.Shared.Rent(bytesToRead);
            try
            {
                var read = 0;
                while (read < bytesToRead)
                {
                    var count = stream.Read(buffer, read, bytesToRead - read);
                    if (count == 0)
                    {
                        break;
                    }

                    read += count;
                }

                var text = Encoding.UTF8.GetString(buffer, 0, read);
                usage = ParseLatestFromText(text, File.GetLastWriteTimeUtc(path));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Walks the tail backwards to the newest usage record.
    /// </summary>
    /// <remarks>
    /// Slices rather than splits: <c>Split</c> materialised the whole half-megabyte tail a second
    /// time as an array of substrings, on every scan of every active session file, and only the one
    /// matching line is ever needed.
    /// </remarks>
    internal static LiveTokenUsage? ParseLatestFromText(string text, DateTimeOffset fallbackTimestamp)
    {
        var end = text.Length;
        while (end > 0)
        {
            var start = text.LastIndexOf('\n', end - 1) + 1;
            var line = text.AsMemory(start, end - start).TrimEnd('\r');
            if (!line.IsEmpty && TryParseUsageLine(line, fallbackTimestamp, out var usage))
            {
                return usage;
            }

            end = start - 1;
        }

        return null;
    }

    private static bool TryParseUsageLine(
        ReadOnlyMemory<char> line,
        DateTimeOffset fallbackTimestamp,
        out LiveTokenUsage usage)
    {
        usage = default!;
        if (!line.Span.Contains("token_count", StringComparison.Ordinal) &&
            !line.Span.Contains("total_token_usage", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("payload", out var payload) ||
                !payload.TryGetProperty("info", out var info) ||
                info.ValueKind != JsonValueKind.Object ||
                !TryGetUsage(info, "total_token_usage", out var total) ||
                !TryGetUsage(info, "last_token_usage", out var last))
            {
                return false;
            }

            var observedAt = fallbackTimestamp;
            if (root.TryGetProperty("timestamp", out var timestampElement) &&
                DateTimeOffset.TryParse(
                    timestampElement.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var parsedTimestamp))
            {
                observedAt = parsedTimestamp;
            }

            long? contextWindow = null;
            if (info.TryGetProperty("model_context_window", out var contextElement) &&
                contextElement.TryGetInt64(out var parsedContextWindow))
            {
                contextWindow = parsedContextWindow;
            }

            usage = new LiveTokenUsage(
                TotalTokens: total.Total,
                LastTurnTokens: last.Total,
                InputTokens: last.Input,
                CachedInputTokens: last.CachedInput,
                OutputTokens: last.Output,
                ReasoningOutputTokens: last.Reasoning,
                ModelContextWindow: contextWindow,
                ObservedAt: observedAt);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            // A tail read can start or end in the middle of a JSONL record.
            return false;
        }
    }

    private static bool TryGetUsage(JsonElement info, string name, out UsageBreakdown usage)
    {
        usage = default;
        if (!info.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        usage = new UsageBreakdown(
            Total: GetInt64(element, "total_tokens"),
            Input: GetInt64(element, "input_tokens"),
            CachedInput: GetInt64(element, "cached_input_tokens"),
            Output: GetInt64(element, "output_tokens"),
            Reasoning: GetInt64(element, "reasoning_output_tokens"));
        return true;
    }

    private static long GetInt64(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.TryGetInt64(out var parsed)
            ? parsed
            : 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= Watcher_Changed;
            watcher.Created -= Watcher_Changed;
            watcher.Renamed -= Watcher_Renamed;
            watcher.Error -= Watcher_Error;
            watcher.Dispose();
        }

        _timer?.Dispose();
        _pendingPaths.Clear();
        _sessionGroupKeys.Clear();
        _observedFiles.Clear();
        _dailyBaselines.Clear();
        _watchers.Clear();
    }

    private readonly record struct SessionIdentity(
        string? ForkedFromId,
        string? SessionId,
        string? Id);

    private readonly record struct UsageBreakdown(
        long Total,
        long Input,
        long CachedInput,
        long Output,
        long Reasoning);

    private readonly record struct FileState(long Length, DateTime LastWriteTimeUtc);

    internal sealed class TodayTokenAccumulator
    {
        private readonly Dictionary<string, LiveTokenUsage> _usageBySession =
            new(StringComparer.OrdinalIgnoreCase);
        private DateOnly _date;
        private DateTimeOffset _updatedAt;

        public void Reset(DateOnly date)
        {
            _date = date;
            _updatedAt = DateTimeOffset.Now;
            _usageBySession.Clear();
        }

        public void Update(string sessionPath, LiveTokenUsage usage)
        {
            if (_usageBySession.TryGetValue(sessionPath, out var existing) &&
                existing.TotalTokens >= usage.TotalTokens)
            {
                return;
            }

            _usageBySession[sessionPath] = usage;
            if (usage.ObservedAt > _updatedAt)
            {
                _updatedAt = usage.ObservedAt;
            }
        }

        public TodayTokenUsage Snapshot()
        {
            long total = 0;
            foreach (var usage in _usageBySession.Values)
            {
                total = usage.TotalTokens > long.MaxValue - total
                    ? long.MaxValue
                    : total + usage.TotalTokens;
            }

            return new TodayTokenUsage(
                Date: _date,
                Tokens: total,
                SessionCount: _usageBySession.Count,
                UpdatedAt: _updatedAt);
        }
    }
}
