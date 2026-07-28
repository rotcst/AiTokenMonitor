using System.Text.Json;
using CodexWeeklyMonitor.Models;

namespace CodexWeeklyMonitor.Services;

internal static class ClaudeTranscriptParser
{
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
        sessionState = null;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (TryParseSessionState(line, fallbackTimestamp) is { } state &&
                (sessionState is null || state.ObservedAt >= sessionState.ObservedAt))
            {
                sessionState = state;
            }

            if (TryParseLine(line, sourceKey, lineNumber, fallbackTimestamp) is not { } record)
            {
                continue;
            }

            if (!records.TryGetValue(record.Key, out var existing) || record.Tokens > existing.Tokens)
            {
                records[record.Key] = record;
            }
        }

        return records.Values.ToArray();
    }

    /// <summary>
    /// Reads the live model and context occupancy from one assistant turn. Context is what the
    /// model actually had in front of it: fresh input plus everything read from or written to cache.
    /// </summary>
    internal static ClaudeSessionState? TryParseSessionState(
        string line,
        DateTimeOffset fallbackTimestamp)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

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
                return null;
            }

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
                ObservedAt: TryGetTimestamp(root) ?? fallbackTimestamp);
        }
        catch (JsonException)
        {
            return null;
        }
    }

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

    private static ClaudeTokenRecord? TryParseLine(
        string line,
        string sourceKey,
        int lineNumber,
        DateTimeOffset fallbackTimestamp)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("message", out var message) ||
                message.ValueKind != JsonValueKind.Object ||
                !message.TryGetProperty("usage", out var usage) ||
                usage.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

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

            var observedAt = TryGetTimestamp(root) ?? fallbackTimestamp;
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
        catch (JsonException)
        {
            return null;
        }
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
