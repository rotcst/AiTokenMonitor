using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CodexWeeklyMonitor.Models;

namespace CodexWeeklyMonitor.Services;

/// <summary>Reads the running Antigravity desktop daemon's authenticated, loopback-only status API.</summary>
internal sealed class AntigravityQuotaClient : IDisposable
{
    private readonly string _daemonDirectory;
    private readonly HttpClient _http = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(4),
    };
    private DateTimeOffset _nextPoll;
    private DateTimeOffset _lastPoll;
    private AntigravityStatusUsage? _cached;

    internal AntigravityQuotaClient(string home) => _daemonDirectory = Path.Combine(home, "daemon");

    internal AntigravityStatusUsage? TryRead(bool force = false)
    {
        var now = DateTimeOffset.Now;
        if (now >= _nextPoll || (force && now - _lastPoll >= TimeSpan.FromSeconds(5)))
        {
            _lastPoll = now;
            _nextPoll = now.AddSeconds(15);
            try
            {
                if (Directory.Exists(_daemonDirectory))
                {
                    foreach (var path in Directory.EnumerateFiles(_daemonDirectory, "hub_*.json")
                                 .OrderByDescending(File.GetLastWriteTimeUtc))
                    {
                        try
                        {
                            using var registration = JsonDocument.Parse(File.ReadAllText(path));
                            var root = registration.RootElement;
                            if (!root.TryGetProperty("pid", out var pid) || !pid.TryGetInt32(out var processId) ||
                                !root.TryGetProperty("httpPort", out var port) || !port.TryGetInt32(out var portNumber) ||
                                portNumber is < 1 or > 65535 ||
                                !root.TryGetProperty("csrfToken", out var csrf) || csrf.ValueKind != JsonValueKind.String)
                            {
                                continue;
                            }

                            using var process = Process.GetProcessById(processId);
                            if (process.HasExited || !process.ProcessName.Equals("language_server", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            using var request = new HttpRequestMessage(HttpMethod.Post,
                                $"http://127.0.0.1:{portNumber}/exa.language_server_pb.LanguageServerService/GetUserStatus");
                            request.Headers.Add("X-Codeium-Csrf-Token", csrf.GetString());
                            request.Headers.Add("Connect-Protocol-Version", "1");
                            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                            using var response = _http.Send(request);
                            response.EnsureSuccessStatusCode();
                            using var body = response.Content.ReadAsStream();
                            using var document = JsonDocument.Parse(body);

                            JsonDocument? summaryDoc = null;
                            try
                            {
                                using var summaryRequest = new HttpRequestMessage(HttpMethod.Post,
                                    $"http://127.0.0.1:{portNumber}/exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary");
                                summaryRequest.Headers.Add("X-Codeium-Csrf-Token", csrf.GetString());
                                summaryRequest.Headers.Add("Connect-Protocol-Version", "1");
                                summaryRequest.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                                using var summaryResponse = _http.Send(summaryRequest);
                                if (summaryResponse.IsSuccessStatusCode)
                                {
                                    using var summaryStream = summaryResponse.Content.ReadAsStream();
                                    summaryDoc = JsonDocument.Parse(summaryStream);
                                }
                            }
                            catch (Exception)
                            {
                                // RetrieveUserQuotaSummary is optional; GetUserStatus remains primary.
                            }

                            using (summaryDoc)
                            {
                                if (Parse(document.RootElement, now, summaryDoc?.RootElement) is { } status)
                                {
                                    _cached = status;
                                    _nextPoll = now.AddMinutes(1);
                                    break;
                                }
                            }
                        }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                            JsonException or HttpRequestException or OperationCanceledException or ArgumentException or
                            InvalidOperationException or System.ComponentModel.Win32Exception)
                        {
                            // Stale registrations are common after daemon restarts. Try another live instance.
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                DiagnosticsLog.Write("AntigravityQuota", exception.GetType().Name);
            }
        }

        return _cached is { } cached && now - cached.ObservedAt <= AntigravityUsageMonitor.StatusMaxAge
            ? cached
            : null;
    }

    private sealed record QuotaGroup(
        string DisplayName,
        string? Description,
        RateLimitWindow? FiveHour,
        RateLimitWindow? Weekly);

    internal static AntigravityStatusUsage? Parse(
        JsonElement root,
        DateTimeOffset now,
        JsonElement? quotaSummaryRoot = null)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("userStatus", out var status) || status.ValueKind != JsonValueKind.Object ||
            !status.TryGetProperty("cascadeModelConfigData", out var config) || config.ValueKind != JsonValueKind.Object ||
            !config.TryGetProperty("clientModelConfigs", out var models) || models.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var groups = ParseQuotaGroups(quotaSummaryRoot, now);

        var quotas = new List<AntigravityModelQuota>();
        var remainingFractions = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var model in models.EnumerateArray())
        {
            if (model.ValueKind != JsonValueKind.Object ||
                (model.TryGetProperty("disabled", out var disabled) && disabled.ValueKind == JsonValueKind.True) ||
                !model.TryGetProperty("label", out var label) || label.ValueKind != JsonValueKind.String ||
                !model.TryGetProperty("quotaInfo", out var quota) || quota.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            // Protobuf JSON omits a zero-valued remainingFraction. A resetTime distinguishes
            // an exhausted bucket from an empty quotaInfo object with no reported quota.
            var hasFraction = quota.TryGetProperty("remainingFraction", out var fraction);
            var hasReset = quota.TryGetProperty("resetTime", out var reset);
            if ((!hasFraction && !hasReset) ||
                (hasFraction && (fraction.ValueKind != JsonValueKind.Number || !fraction.TryGetDouble(out _))))
            {
                continue;
            }

            var remaining = hasFraction ? fraction.GetDouble() : 0d;
            if (!double.IsFinite(remaining) || remaining is < 0 or > 1)
            {
                continue;
            }

            DateTimeOffset? resetsAt = hasReset && reset.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(reset.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed : null;
            var name = label.GetString()!;
            remainingFractions[name] = remaining;
            var used = (int)Math.Round((1 - remaining) * 100, MidpointRounding.AwayFromZero);

            var matchedGroup = MatchQuotaGroup(name, groups);
            RateLimitWindow? model5h = matchedGroup?.FiveHour;
            RateLimitWindow? modelWeekly = matchedGroup?.Weekly;

            if (model5h is null && modelWeekly is null)
            {
                if (resetsAt is { } rt && rt - now > TimeSpan.FromHours(12))
                {
                    modelWeekly = new RateLimitWindow(used, resetsAt, 10_080);
                }
                else
                {
                    model5h = new RateLimitWindow(used, resetsAt, 300);
                }
            }
            else if (model5h is null && resetsAt is { } rt5 && rt5 - now <= TimeSpan.FromHours(12))
            {
                model5h = new RateLimitWindow(used, resetsAt, 300);
            }
            else if (modelWeekly is null && resetsAt is { } rtw && rtw - now > TimeSpan.FromHours(12))
            {
                modelWeekly = new RateLimitWindow(used, resetsAt, 10_080);
            }

            var primaryWindow = model5h ?? modelWeekly ?? new RateLimitWindow(used, resetsAt, 10_080);

            quotas.Add(new AntigravityModelQuota(
                Key: $"model:{name}",
                ModelName: name,
                DisplayName: name,
                UsedPercent: primaryWindow.UsedPercent,
                ResetsAt: primaryWindow.ResetsAt,
                FiveHourWindow: model5h,
                WeeklyWindow: modelWeekly));
        }

        if (quotas.Count == 0) return null;
        string? plan = null;
        if (status.TryGetProperty("planStatus", out var planStatus) &&
            planStatus.TryGetProperty("planInfo", out var info) &&
            info.TryGetProperty("planName", out var planName) && planName.ValueKind == JsonValueKind.String)
        {
            plan = planName.GetString();
        }

        // Effort variants are only combined when their exact reported fraction and reset agree.
        // Otherwise each variant remains an independent selectable quota.
        var grouped = quotas.GroupBy(quota => (
                BaseName: BaseModelName(quota.ModelName),
                Used: quota.UsedPercent,
                Reset: quota.ResetsAt,
                Fraction: remainingFractions[quota.ModelName],
                F5Used: quota.FiveHourWindow?.UsedPercent,
                F5Reset: quota.FiveHourWindow?.ResetsAt,
                WkUsed: quota.WeeklyWindow?.UsedPercent,
                WkReset: quota.WeeklyWindow?.ResetsAt))
            .Select(group => group.Count() == 1 ? group.First() : new AntigravityModelQuota(
                $"model:{group.Key.BaseName}",
                group.Key.BaseName,
                group.Key.BaseName,
                group.Key.Used,
                group.Key.Reset,
                group.Select(quota => quota.ModelName).ToArray(),
                group.First().FiveHourWindow,
                group.First().WeeklyWindow))
            .ToArray();

        var top5h = groups.FirstOrDefault(g => g.FiveHour is not null)?.FiveHour;
        var topWeekly = groups.FirstOrDefault(g => g.Weekly is not null)?.Weekly;

        return new AntigravityStatusUsage(top5h, topWeekly, null, null, null, plan, now, grouped);
    }

    private static List<QuotaGroup> ParseQuotaGroups(JsonElement? quotaSummaryRoot, DateTimeOffset now)
    {
        var result = new List<QuotaGroup>();
        if (quotaSummaryRoot is null)
        {
            return result;
        }

        var root = quotaSummaryRoot.Value;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        var container = root.TryGetProperty("response", out var resp) && resp.ValueKind == JsonValueKind.Object
            ? resp
            : root;

        if (!container.TryGetProperty("groups", out var groupsElem) || groupsElem.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var groupElem in groupsElem.EnumerateArray())
        {
            if (groupElem.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var groupName = TryGetString(groupElem, "displayName") ?? string.Empty;
            var groupDesc = TryGetString(groupElem, "description");
            RateLimitWindow? fiveHour = null;
            RateLimitWindow? weekly = null;

            if (groupElem.TryGetProperty("buckets", out var bucketsElem) &&
                bucketsElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var bucket in bucketsElem.EnumerateArray())
                {
                    if (bucket.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var windowType = TryGetString(bucket, "window")?.ToLowerInvariant();
                    var bucketId = TryGetString(bucket, "bucketId")?.ToLowerInvariant() ?? string.Empty;

                    var hasFraction = bucket.TryGetProperty("remainingFraction", out var fractionProp);
                    var hasReset = bucket.TryGetProperty("resetTime", out var resetProp);
                    if (!hasFraction && !hasReset)
                    {
                        continue;
                    }

                    var remaining = hasFraction && fractionProp.TryGetDouble(out var fractionVal)
                        ? fractionVal
                        : 0d;
                    if (!double.IsFinite(remaining) || remaining is < 0 or > 1)
                    {
                        continue;
                    }

                    DateTimeOffset? resetTime = hasReset && resetProp.ValueKind == JsonValueKind.String &&
                        DateTimeOffset.TryParse(resetProp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                        ? parsed
                        : null;
                    var used = (int)Math.Round((1 - remaining) * 100, MidpointRounding.AwayFromZero);

                    var is5h = windowType is "5h" or "five_hour" || bucketId.Contains("5h");
                    var isWk = windowType is "weekly" or "7d" || bucketId.Contains("weekly");
                    if (!is5h && !isWk)
                    {
                        if (resetTime is { } rt && rt - now > TimeSpan.FromHours(12))
                        {
                            isWk = true;
                        }
                        else
                        {
                            is5h = true;
                        }
                    }

                    if (is5h)
                    {
                        fiveHour = new RateLimitWindow(used, resetTime, 300);
                    }
                    else if (isWk)
                    {
                        weekly = new RateLimitWindow(used, resetTime, 10_080);
                    }
                }
            }

            result.Add(new QuotaGroup(groupName, groupDesc, fiveHour, weekly));
        }

        return result;
    }

    private static QuotaGroup? MatchQuotaGroup(string modelLabel, IReadOnlyList<QuotaGroup> groups)
    {
        if (groups.Count == 0)
        {
            return null;
        }

        var lower = modelLabel.ToLowerInvariant();
        if (lower.Contains("argon"))
        {
            var match = groups.FirstOrDefault(g =>
                g.DisplayName.Contains("Argon", StringComparison.OrdinalIgnoreCase) ||
                (g.Description?.Contains("Argon", StringComparison.OrdinalIgnoreCase) ?? false));
            if (match is not null) return match;
        }

        if (lower.Contains("gemini"))
        {
            var match = groups.FirstOrDefault(g =>
                (g.DisplayName.Contains("Gemini", StringComparison.OrdinalIgnoreCase) &&
                 !g.DisplayName.Contains("Argon", StringComparison.OrdinalIgnoreCase)) ||
                (g.Description?.Contains("Gemini Flash", StringComparison.OrdinalIgnoreCase) ?? false));
            if (match is not null) return match;
        }

        if (lower.Contains("claude") || lower.Contains("gpt") || lower.Contains("oss"))
        {
            var match = groups.FirstOrDefault(g =>
                g.DisplayName.Contains("Claude", StringComparison.OrdinalIgnoreCase) ||
                g.DisplayName.Contains("GPT", StringComparison.OrdinalIgnoreCase) ||
                (g.Description?.Contains("Claude", StringComparison.OrdinalIgnoreCase) ?? false));
            if (match is not null) return match;
        }

        return groups.FirstOrDefault(g =>
            g.DisplayName.Contains(modelLabel, StringComparison.OrdinalIgnoreCase) ||
            (g.Description?.Contains(modelLabel, StringComparison.OrdinalIgnoreCase) ?? false));
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    private static string BaseModelName(string name)
    {
        foreach (var suffix in new[] { " (High)", " (Medium)", " (Low)" })
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal)) return name[..^suffix.Length];
        }

        return name;
    }

    public void Dispose() => _http.Dispose();
}
