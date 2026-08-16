using System.Text.Json;
using CodexWeeklyMonitor.Models;

namespace CodexWeeklyMonitor.Services;

internal static class ClaudeTranscriptParser
{
    private const string UsageProperty = "\"usage\"";

    public static IReadOnlyList<ClaudeTokenRecord> ParseText(
        string text,
        string sourceKey,
        DateTimeOffset fallbackTimestamp)
    {
        using var reader = new StringReader(text);
        return ParseReader(reader, sourceKey, fallbackTimestamp);
    }

    public static IReadOnlyList<ClaudeTokenRecord> ParseReader(
        TextReader reader,
        string sourceKey,
        DateTimeOffset fallbackTimestamp)
    {
        return ParseReader(reader, sourceKey, fallbackTimestamp, out _);
    }

    /// <param name="sessionState">
    /// The last assistant turn in the file, which carries the live model and context usage. The
    /// desktop app never runs a custom status line, so this is the only local source for them.
    /// </param>
    public static IReadOnlyList<ClaudeTokenRecord> ParseReader(
        TextReader reader,
        string sourceKey,
        DateTimeOffset fallbackTimestamp,
        out ClaudeSessionState? sessionState)
    {
        var records = new Dictionary<string, ClaudeTokenRecord>(StringComparer.Ordinal);
        var lineNumber = 0;
        var state = sessionState = null;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            // Tool results and pasted attachments are most of a transcript's bytes and carry no
            // usage block, so a substring test keeps JsonDocument off roughly three quarters of the
            // lines. Both readers below require message.usage, so nothing that matters is skipped.
            if (!line.Contains(UsageProperty, StringComparison.Ordinal))
            {
                continue;
            }

            Accumulate(line, sourceKey, lineNumber, fallbackTimestamp, records, ref state);
        }

        sessionState = state;
        return records.Values.ToArray();
    }

    /// <summary>
    /// Folds one already-filtered line into an accumulating per-file view.
    /// </summary>
    /// <remarks>
    /// Shared with the incremental reader so a file parsed in one pass and the same file parsed in
    /// several appended chunks cannot disagree about deduplication or which turn is the newest.
    /// </remarks>
    internal static void Accumulate(
        string line,
        string sourceKey,
        int lineNumber,
        DateTimeOffset fallbackTimestamp,
        Dictionary<string, ClaudeTokenRecord> records,
        ref ClaudeSessionState? sessionState)
    {
        var parsed = ParseUsageLine(line, sourceKey, lineNumber, fallbackTimestamp);
        if (parsed.State is { } state &&
            (sessionState is null || state.ObservedAt >= sessionState.ObservedAt))
        {
            sessionState = state;
        }

        if (parsed.Record is not { } record)
        {
            return;
        }

        if (!records.TryGetValue(record.Key, out var existing) || record.Tokens > existing.Tokens)
        {
            records[record.Key] = record;
        }
    }

    /// <summary>The UTF-8 form of the gate above, for callers that filter before decoding.</summary>
    internal static ReadOnlySpan<byte> UsagePropertyUtf8 => "\"usage\""u8;

    /// <summary>
    /// Reads the live model and context occupancy from one assistant turn. Context is what the
    /// model actually had in front of it: fresh input plus everything read from or written to cache.
    /// </summary>
    internal static ClaudeSessionState? TryParseSessionState(
        string line,
        DateTimeOffset fallbackTimestamp)
    {
        return string.IsNullOrWhiteSpace(line)
            ? null
            : ParseUsageLine(line, sourceKey: string.Empty, lineNumber: 0, fallbackTimestamp).State;
    }

    /// <summary>
    /// Reads the session state and the token record from one line in a single JSON pass. Both need
    /// the same <c>message.usage</c> object, and parsing each line twice doubled the work and the
    /// pooled buffers for no gain.
    /// </summary>
    private static TranscriptLine ParseUsageLine(
        string line,
        string sourceKey,
        int lineNumber,
        DateTimeOffset fallbackTimestamp)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("message", out var message) ||
                message.ValueKind != JsonValueKind.Object ||
                !message.TryGetProperty("usage", out var usage) ||
                usage.ValueKind != JsonValueKind.Object)
            {
                return default;
            }

            var observedAt = TryGetTimestamp(root) ?? fallbackTimestamp;
            return new TranscriptLine(
                ReadSessionState(root, message, usage, observedAt),
                ReadTokenRecord(root, message, usage, sourceKey, lineNumber, observedAt));
        }
        catch (JsonException)
        {
            return default;
        }
    }

    /// <summary>
    /// Context is what the model actually had in front of it: fresh input plus everything read from
    /// or written to cache.
    /// </summary>
    private static ClaudeSessionState? ReadSessionState(
        JsonElement root,
        JsonElement message,
        JsonElement usage,
        DateTimeOffset observedAt)
    {
        var contextTokens = SumSaturated(
        [
            GetNonNegativeInt64(usage, "input_tokens"),
            GetNonNegativeInt64(usage, "cache_creation_input_tokens"),
            GetNonNegativeInt64(usage, "cache_read_input_tokens"),
        ]);
        if (contextTokens <= 0)
        {
            return null;
        }

        return new ClaudeSessionState(
            ModelName: TryGetString(message, "model"),
            ContextTokens: contextTokens,
            Effort: TryGetString(root, "effort"),
            Version: TryGetString(root, "version"),
            ObservedAt: observedAt);
    }

    private static ClaudeTokenRecord? ReadTokenRecord(
        JsonElement root,
        JsonElement message,
        JsonElement usage,
        string sourceKey,
        int lineNumber,
        DateTimeOffset observedAt)
    {
        var tokens = SumSaturated(
        [
            GetNonNegativeInt64(usage, "input_tokens"),
            GetNonNegativeInt64(usage, "output_tokens"),
            GetNonNegativeInt64(usage, "cache_creation_input_tokens"),
            GetNonNegativeInt64(usage, "cache_read_input_tokens"),
        ]);
        if (tokens <= 0)
        {
            return null;
        }

        var messageId = TryGetString(message, "id");
        var uuid = TryGetString(root, "uuid");
        var key = !string.IsNullOrWhiteSpace(messageId)
            ? $"message:{messageId}"
            : !string.IsNullOrWhiteSpace(uuid)
                ? $"uuid:{uuid}"
                : $"{sourceKey}:{lineNumber}";
        return new ClaudeTokenRecord(
            key,
            DateOnly.FromDateTime(observedAt.ToLocalTime().DateTime),
            tokens,
            observedAt);
    }

    private readonly record struct TranscriptLine(
        ClaudeSessionState? State,
        ClaudeTokenRecord? Record);

    public static AccountTokenUsage BuildAccountUsage(
        IEnumerable<ClaudeTokenRecord> sourceRecords,
        DateTimeOffset fetchedAt)
    {
        var records = sourceRecords
            .GroupBy(record => record.Key, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(record => record.Tokens)
                .ThenByDescending(record => record.ObservedAt)
                .First())
            .ToArray();
        var dailyUsage = records
            .GroupBy(record => record.Date)
            .Select(group => new DailyTokenUsage(group.Key, SumSaturated(group.Select(item => item.Tokens))))
            .OrderBy(item => item.Date)
            .ToArray();
        var activeDates = dailyUsage
            .Where(item => item.Tokens > 0)
            .Select(item => item.Date)
            .ToArray();
        var (currentStreak, longestStreak) = CalculateStreaks(activeDates);

        return new AccountTokenUsage(
            LifetimeTokens: SumSaturated(dailyUsage.Select(item => item.Tokens)),
            PeakDailyTokens: dailyUsage.Length == 0 ? null : dailyUsage.Max(item => item.Tokens),
            LongestRunningTurnSeconds: null,
            CurrentStreakDays: currentStreak,
            LongestStreakDays: longestStreak,
            DailyUsage: dailyUsage,
            FetchedAt: fetchedAt);
    }

    private static string? TryGetString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static DateTimeOffset? TryGetTimestamp(JsonElement root)
    {
        var timestamp = TryGetString(root, "timestamp");
        return DateTimeOffset.TryParse(timestamp, out var parsed) ? parsed : null;
    }

    private static long GetNonNegativeInt64(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return Math.Max(0, number);
        }

        return value.ValueKind == JsonValueKind.String &&
               long.TryParse(value.GetString(), out number)
            ? Math.Max(0, number)
            : 0;
    }

    private static long SumSaturated(IEnumerable<long> values)
    {
        var total = 0L;
        foreach (var value in values)
        {
            if (value > long.MaxValue - total)
            {
                return long.MaxValue;
            }

            total += value;
        }

        return total;
    }

    private static (long? Current, long? Longest) CalculateStreaks(IReadOnlyList<DateOnly> dates)
    {
        if (dates.Count == 0)
        {
            return (null, null);
        }

        var longest = 1L;
        var running = 1L;
        for (var index = 1; index < dates.Count; index++)
        {
            if (dates[index].DayNumber == dates[index - 1].DayNumber + 1)
            {
                running++;
                longest = Math.Max(longest, running);
            }
            else
            {
                running = 1;
            }
        }

        return (running, longest);
    }
}
