using System.Globalization;
using System.Text.Json;
using CodexWeeklyMonitor.Models;

namespace CodexWeeklyMonitor.Services;

internal static class AntigravityTranscriptParser
{
    public static IReadOnlyList<AntigravityTokenRecord> ParseText(
        string text,
        string sourceKey,
        DateTimeOffset fallbackTimestamp,
        out AntigravitySessionState? sessionState,
        string? defaultModelName = null)
    {
        using var reader = new StringReader(text);
        return ParseReader(reader, sourceKey, fallbackTimestamp, out sessionState, defaultModelName);
    }

    public static IReadOnlyList<AntigravityTokenRecord> ParseReader(
        TextReader reader,
        string sourceKey,
        DateTimeOffset fallbackTimestamp,
        out AntigravitySessionState? sessionState,
        string? defaultModelName = null)
    {
        var records = new Dictionary<string, AntigravityTokenRecord>(StringComparer.Ordinal);
        var lineNumber = 0;
        var activeModel = defaultModelName;
        AntigravitySessionState? state = null;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (!MayContainRelevantData(line))
            {
                continue;
            }

            Accumulate(
                line,
                sourceKey,
                lineNumber,
                fallbackTimestamp,
                records,
                ref activeModel,
                ref state);
        }

        sessionState = state;
        return records.Values.ToArray();
    }

    internal static bool MayContainRelevantData(string line)
    {
        return line.Contains("\"input_tokens\"", StringComparison.Ordinal) ||
               line.Contains("\"output_tokens\"", StringComparison.Ordinal) ||
               line.Contains("\"usage\"", StringComparison.Ordinal) ||
               line.Contains("Model Selection", StringComparison.Ordinal) ||
               line.Contains("\"modelName\"", StringComparison.Ordinal) ||
               line.Contains("\"model_name\"", StringComparison.Ordinal);
    }

    internal static bool MayContainRelevantDataUtf8(ReadOnlySpan<byte> line)
    {
        return line.IndexOf("\"input_tokens\""u8) >= 0 ||
               line.IndexOf("\"output_tokens\""u8) >= 0 ||
               line.IndexOf("\"usage\""u8) >= 0 ||
               line.IndexOf("Model Selection"u8) >= 0 ||
               line.IndexOf("\"modelName\""u8) >= 0 ||
               line.IndexOf("\"model_name\""u8) >= 0;
    }

    internal static void Accumulate(
        string line,
        string sourceKey,
        int lineNumber,
        DateTimeOffset fallbackTimestamp,
        Dictionary<string, AntigravityTokenRecord> records,
        ref string? activeModel,
        ref AntigravitySessionState? sessionState)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var stepType = TryGetString(root, "type");
            var stepSource = TryGetString(root, "source");

            // Track model selection changes from USER_INPUT steps.
            if (string.Equals(stepType, "USER_INPUT", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(stepSource, "USER_EXPLICIT", StringComparison.OrdinalIgnoreCase))
            {
                if (TryExtractExplicitModel(root) is { } explicitUserModel)
                {
                    activeModel = explicitUserModel;
                }
                else if (TryGetString(root, "content") is { } content &&
                         TryParseModelSelectionFromContent(content) is { } selectedModel)
                {
                    activeModel = selectedModel;
                }
            }

            // Ignore tool result / generic steps so file contents read by tools never count as turns.
            if (string.Equals(stepType, "GENERIC", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(stepType, "TOOL_RESULT", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (TryExtractExplicitModel(root) is { } stepModel)
            {
                activeModel = stepModel;
            }

            if (!TryReadTokenCounts(root, out var inputTokens, out var cacheReadTokens, out var outputTokens))
            {
                return;
            }

            var totalTokens = SumSaturated([inputTokens, cacheReadTokens, outputTokens]);
            if (totalTokens <= 0)
            {
                return;
            }

            var observedAt = TryGetTimestamp(root) ?? fallbackTimestamp;
            var conversationId = ResolveConversationId(root, sourceKey);
            var resolvedModel = string.IsNullOrWhiteSpace(activeModel) ? "Auto" : activeModel.Trim();
            var key = BuildRecordKey(root, conversationId, sourceKey, lineNumber);

            var record = new AntigravityTokenRecord(
                Key: key,
                ConversationId: conversationId,
                ModelName: resolvedModel,
                Date: DateOnly.FromDateTime(observedAt.ToLocalTime().DateTime),
                InputTokens: inputTokens,
                CacheReadTokens: cacheReadTokens,
                OutputTokens: outputTokens,
                TotalTokens: totalTokens,
                ObservedAt: observedAt);

            if (!records.TryGetValue(record.Key, out var existing) || record.TotalTokens >= existing.TotalTokens)
            {
                records[record.Key] = record;
            }

            var contextTokens = SumSaturated([inputTokens, cacheReadTokens]);
            var candidateSession = new AntigravitySessionState(
                ConversationId: conversationId,
                ModelName: resolvedModel,
                ContextTokens: contextTokens > 0 ? contextTokens : totalTokens,
                OutputTokens: outputTokens,
                TurnCount: records.Count,
                ObservedAt: observedAt);

            if (sessionState is null || candidateSession.ObservedAt >= sessionState.ObservedAt)
            {
                sessionState = candidateSession;
            }
            else
            {
                sessionState = sessionState with
                {
                    TurnCount = records.Count,
                    ModelName = resolvedModel,
                };
            }
        }
        catch (JsonException)
        {
            // Ignore malformed or partial lines.
        }
    }

    internal static string? TryParseModelSelectionFromContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        const string marker = "Model Selection";
        var searchIndex = 0;
        string? lastMatchedModel = null;

        while (searchIndex < content.Length)
        {
            var markerIndex = content.IndexOf(marker, searchIndex, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
            {
                break;
            }

            var lineEnd = content.IndexOfAny(['\r', '\n'], markerIndex);
            var line = lineEnd < 0
                ? content[markerIndex..]
                : content[markerIndex..lineEnd];

            const string toDelimiter = " to ";
            var toIndex = line.IndexOf(toDelimiter, StringComparison.OrdinalIgnoreCase);
            if (toIndex >= 0)
            {
                var candidate = line[(toIndex + toDelimiter.Length)..];
                var sentenceBreak = candidate.IndexOf(". ", StringComparison.Ordinal);
                if (sentenceBreak >= 0)
                {
                    candidate = candidate[..sentenceBreak];
                }

                var tagBreak = candidate.IndexOf('<');
                if (tagBreak >= 0)
                {
                    candidate = candidate[..tagBreak];
                }

                candidate = candidate.Trim().Trim('`', '"', '\'', '.');
                if (!string.IsNullOrWhiteSpace(candidate) &&
                    !candidate.Equals("None", StringComparison.OrdinalIgnoreCase))
                {
                    lastMatchedModel = candidate;
                }
            }

            searchIndex = lineEnd < 0 ? content.Length : lineEnd + 1;
        }

        return lastMatchedModel;
    }

    public static AccountTokenUsage BuildAccountUsage(
        IEnumerable<AntigravityTokenRecord> sourceRecords,
        DateTimeOffset fetchedAt)
    {
        var deduplicated = DeduplicateRecords(sourceRecords);
        if (deduplicated.Length == 0)
        {
            return new AccountTokenUsage(
                LifetimeTokens: 0,
                PeakDailyTokens: null,
                LongestRunningTurnSeconds: null,
                CurrentStreakDays: null,
                LongestStreakDays: null,
                DailyUsage: [],
                FetchedAt: fetchedAt);
        }

        var dailyUsage = deduplicated
            .GroupBy(record => record.Date)
            .Select(group => new DailyTokenUsage(group.Key, SumSaturated(group.Select(item => item.TotalTokens))))
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

    public static IReadOnlyList<AntigravityModelUsage> BuildModelUsage(
        IEnumerable<AntigravityTokenRecord> sourceRecords,
        DateOnly today,
        IReadOnlyList<AntigravityModelQuota>? modelQuotas = null)
    {
        var deduplicated = DeduplicateRecords(sourceRecords);
        var sevenDayStart = today.AddDays(-6);
        var grandTotal = Math.Max(1L, SumSaturated(deduplicated.Select(record => record.TotalTokens)));

        var byModel = deduplicated
            .GroupBy(
                record => AntigravitySessionState.NormalizeModelDisplayName(record.ModelName) ?? "Auto",
                StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var displayName = group.Key;
                var rawName = group
                    .OrderByDescending(item => item.ObservedAt)
                    .First()
                    .ModelName;
                var totalTokens = SumSaturated(group.Select(item => item.TotalTokens));
                var inputTokens = SumSaturated(group.Select(item => item.InputTokens));
                var cacheReadTokens = SumSaturated(group.Select(item => item.CacheReadTokens));
                var outputTokens = SumSaturated(group.Select(item => item.OutputTokens));
                var todayTokens = SumSaturated(group.Where(item => item.Date == today).Select(item => item.TotalTokens));
                var sevenDayTokens = SumSaturated(
                    group.Where(item => item.Date >= sevenDayStart && item.Date <= today)
                         .Select(item => item.TotalTokens));
                var turnCount = group.Count();
                var sharePercent = totalTokens <= 0
                    ? 0
                    : (int)Math.Clamp(
                        Math.Round(totalTokens * 100d / grandTotal, MidpointRounding.AwayFromZero),
                        1,
                        100);
                var lastUsedAt = group.Max(item => item.ObservedAt);
                var quota = FindMatchingQuota(displayName, rawName, modelQuotas);

                return new AntigravityModelUsage(
                    ModelName: rawName,
                    DisplayName: displayName,
                    TotalTokens: totalTokens,
                    InputTokens: inputTokens,
                    CacheReadTokens: cacheReadTokens,
                    OutputTokens: outputTokens,
                    TodayTokens: todayTokens,
                    SevenDayTokens: sevenDayTokens,
                    TurnCount: turnCount,
                    SharePercent: sharePercent,
                    LastUsedAt: lastUsedAt,
                    QuotaWindow: quota is null
                        ? null
                        : new RateLimitWindow(quota.UsedPercent, quota.ResetsAt, 10_080));
            })
            .OrderByDescending(model => model.TotalTokens)
            .ThenByDescending(model => model.LastUsedAt)
            .ToList();

        // Include any quota-bridged models that have not yet produced local transcript turns.
        if (modelQuotas is { Count: > 0 })
        {
            foreach (var quota in modelQuotas)
            {
                var displayName = AntigravitySessionState.NormalizeModelDisplayName(quota.DisplayName) ??
                                  AntigravitySessionState.NormalizeModelDisplayName(quota.ModelName) ??
                                  quota.DisplayName;
                if (byModel.Any(existing =>
                        string.Equals(existing.DisplayName, displayName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(existing.ModelName, quota.ModelName, StringComparison.OrdinalIgnoreCase) ||
                        (quota.ModelAliases?.Contains(existing.ModelName, StringComparer.OrdinalIgnoreCase) ?? false)))
                {
                    continue;
                }

                byModel.Add(new AntigravityModelUsage(
                    ModelName: quota.ModelName,
                    DisplayName: displayName,
                    TotalTokens: 0,
                    InputTokens: 0,
                    CacheReadTokens: 0,
                    OutputTokens: 0,
                    TodayTokens: 0,
                    SevenDayTokens: 0,
                    TurnCount: 0,
                    SharePercent: 0,
                    LastUsedAt: DateTimeOffset.MinValue,
                    QuotaWindow: new RateLimitWindow(quota.UsedPercent, quota.ResetsAt, 10_080)));
            }
        }

        return byModel;
    }

    private static AntigravityModelQuota? FindMatchingQuota(
        string displayName,
        string rawName,
        IReadOnlyList<AntigravityModelQuota>? quotas)
    {
        if (quotas is null || quotas.Count == 0)
        {
            return null;
        }

        return quotas.FirstOrDefault(quota =>
            (quota.ModelAliases?.Any(alias =>
                string.Equals(alias, displayName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(alias, rawName, StringComparison.OrdinalIgnoreCase)) ?? false) ||
            string.Equals(quota.DisplayName, displayName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(quota.ModelName, rawName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                AntigravitySessionState.NormalizeModelDisplayName(quota.ModelName),
                displayName,
                StringComparison.OrdinalIgnoreCase));
    }

    private static AntigravityTokenRecord[] DeduplicateRecords(IEnumerable<AntigravityTokenRecord> sourceRecords)
    {
        return sourceRecords
            .GroupBy(record => record.Key, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(record => record.TotalTokens)
                .ThenByDescending(record => record.ObservedAt)
                .First())
            .ToArray();
    }

    private static bool TryReadTokenCounts(
        JsonElement root,
        out long inputTokens,
        out long cacheReadTokens,
        out long outputTokens)
    {
        inputTokens = 0;
        cacheReadTokens = 0;
        outputTokens = 0;

        var target = root;
        if (root.TryGetProperty("usage", out var usageElement) &&
            usageElement.ValueKind == JsonValueKind.Object)
        {
            target = usageElement;
        }
        else if (root.TryGetProperty("message", out var messageElement) &&
                 messageElement.ValueKind == JsonValueKind.Object &&
                 messageElement.TryGetProperty("usage", out var messageUsage) &&
                 messageUsage.ValueKind == JsonValueKind.Object)
        {
            target = messageUsage;
        }

        inputTokens = GetNonNegativeInt64(target, "input_tokens") +
                      GetNonNegativeInt64(target, "prompt_tokens");
        cacheReadTokens = GetNonNegativeInt64(target, "cache_read_tokens") +
                          GetNonNegativeInt64(target, "cached_input_tokens") +
                          GetNonNegativeInt64(target, "cache_read_input_tokens") +
                          GetNonNegativeInt64(target, "cache_creation_input_tokens");
        outputTokens = GetNonNegativeInt64(target, "output_tokens") +
                       GetNonNegativeInt64(target, "completion_tokens") +
                       GetNonNegativeInt64(target, "thinking_tokens") +
                       GetNonNegativeInt64(target, "reasoning_output_tokens");

        return inputTokens > 0 || cacheReadTokens > 0 || outputTokens > 0;
    }

    private static string? TryExtractExplicitModel(JsonElement root)
    {
        var model = TryGetString(root, "modelName") ??
                    TryGetString(root, "model_name") ??
                    TryGetString(root, "model");
        if (!string.IsNullOrWhiteSpace(model))
        {
            return model;
        }

        if (root.TryGetProperty("metadata", out var metadata) &&
            metadata.ValueKind == JsonValueKind.Object)
        {
            model = TryGetString(metadata, "modelName") ??
                    TryGetString(metadata, "model_name") ??
                    TryGetString(metadata, "model");
            if (!string.IsNullOrWhiteSpace(model))
            {
                return model;
            }
        }

        if (root.TryGetProperty("message", out var message) &&
            message.ValueKind == JsonValueKind.Object)
        {
            model = TryGetString(message, "model") ?? TryGetString(message, "modelName");
            if (!string.IsNullOrWhiteSpace(model))
            {
                return model;
            }
        }

        return null;
    }

    internal static string ResolveConversationId(JsonElement root, string sourceKey)
    {
        var explicitId = TryGetString(root, "conversationId") ??
                         TryGetString(root, "conversation_id") ??
                         TryGetString(root, "sessionId") ??
                         TryGetString(root, "session_id");
        if (!string.IsNullOrWhiteSpace(explicitId))
        {
            return explicitId;
        }

        return ResolveConversationIdFromPath(sourceKey);
    }

    internal static string ResolveConversationIdFromPath(string sourceKey)
    {
        if (string.IsNullOrWhiteSpace(sourceKey))
        {
            return "default";
        }

        try
        {
            var normalized = sourceKey.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            var segments = normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            for (var index = 1; index < segments.Length; index++)
            {
                if (segments[index].Equals(".system_generated", StringComparison.OrdinalIgnoreCase))
                {
                    return segments[index - 1];
                }
            }

            for (var index = 0; index + 1 < segments.Length; index++)
            {
                if (segments[index].Equals("brain", StringComparison.OrdinalIgnoreCase))
                {
                    return segments[index + 1];
                }
            }

            return Path.GetFileNameWithoutExtension(normalized);
        }
        catch (ArgumentException)
        {
            return sourceKey;
        }
    }

    private static string BuildRecordKey(
        JsonElement root,
        string conversationId,
        string sourceKey,
        int lineNumber)
    {
        if (root.TryGetProperty("step_index", out var stepIdxElement) &&
            stepIdxElement.ValueKind == JsonValueKind.Number &&
            stepIdxElement.TryGetInt64(out var stepIndex))
        {
            return $"{conversationId}:step:{stepIndex}";
        }

        if (TryGetString(root, "id") is { } id && !string.IsNullOrWhiteSpace(id))
        {
            return $"{conversationId}:id:{id}";
        }

        if (TryGetString(root, "uuid") is { } uuid && !string.IsNullOrWhiteSpace(uuid))
        {
            return $"{conversationId}:uuid:{uuid}";
        }

        return $"{sourceKey}:line:{lineNumber}";
    }

    private static string? TryGetString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static DateTimeOffset? TryGetTimestamp(JsonElement root)
    {
        var timestamp = TryGetString(root, "created_at") ??
                        TryGetString(root, "timestamp") ??
                        TryGetString(root, "observed_at");
        return DateTimeOffset.TryParse(
            timestamp,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : null;
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
               long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
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
