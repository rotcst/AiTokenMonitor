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

    private readonly ConcurrentDictionary<string, byte> _pendingPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _sessionGroupKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _sessionsDirectory;
    private readonly string _archivedSessionsDirectory;
    private readonly TodayTokenAccumulator _accumulator = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private Timer? _timer;
    private TodayTokenUsage? _lastPublishedUsage;
    private DateOnly _trackedDate;
    private int _processing;
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
        if (_watchers.Count > 0)
        {
            return;
        }

        _trackedDate = DateOnly.FromDateTime(DateTime.Now);
        _accumulator.Reset(_trackedDate);

        QueueSessionFiles(_trackedDate);
        QueueArchivedSessionFiles(_trackedDate);

        var watcherStarted = false;
        watcherStarted |= TryStartWatcher(_sessionsDirectory, includeSubdirectories: true);
        watcherStarted |= TryStartWatcher(_archivedSessionsDirectory, includeSubdirectories: false);
        if (!watcherStarted && _pendingPaths.IsEmpty)
        {
            return;
        }

        _timer = new Timer(ProcessPendingFiles, null, TimeSpan.Zero, DebounceInterval);
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
                _pendingPaths.TryAdd(path, 0);
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
                    _pendingPaths.TryAdd(path, 0);
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
                QueueSessionFiles(today);
                QueueArchivedSessionFiles(today);
            }

            var paths = _pendingPaths.Keys.ToArray();
            foreach (var path in paths)
            {
                _pendingPaths.TryRemove(path, out _);
                if (!IsSessionFileForDate(path, today))
                {
                    continue;
                }

                var usage = TryReadLatestUsage(path);
                if (usage is null)
                {
                    continue;
                }

                var sessionGroupKey = _sessionGroupKeys.GetOrAdd(path, ResolveSessionGroupKey);
                _accumulator.Update(sessionGroupKey, usage);
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

    private static LiveTokenUsage? TryReadLatestUsage(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length == 0)
            {
                return null;
            }

            var bytesToRead = (int)Math.Min(stream.Length, MaximumTailBytes);
            stream.Seek(-bytesToRead, SeekOrigin.End);
            var buffer = new byte[bytesToRead];
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
            return ParseLatestFromText(text, File.GetLastWriteTimeUtc(path));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    internal static LiveTokenUsage? ParseLatestFromText(string text, DateTimeOffset fallbackTimestamp)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        for (var index = lines.Length - 1; index >= 0; index--)
        {
            var line = lines[index].TrimEnd('\r');
            if (!line.Contains("token_count", StringComparison.Ordinal) &&
                !line.Contains("total_token_usage", StringComparison.Ordinal))
            {
                continue;
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
                    continue;
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

                return new LiveTokenUsage(
                    TotalTokens: total.Total,
                    LastTurnTokens: last.Total,
                    InputTokens: last.Input,
                    CachedInputTokens: last.CachedInput,
                    OutputTokens: last.Output,
                    ReasoningOutputTokens: last.Reasoning,
                    ModelContextWindow: contextWindow,
                    ObservedAt: observedAt);
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException)
            {
                // The first line of a tail read can be partial. Continue to an earlier event.
            }
        }

        return null;
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
            watcher.Dispose();
        }

        _timer?.Dispose();
        _pendingPaths.Clear();
        _sessionGroupKeys.Clear();
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
