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
                            if (Parse(document.RootElement, now) is { } status)
                            {
                                _cached = status;
                                _nextPoll = now.AddMinutes(1);
                                break;
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

    internal static AntigravityStatusUsage? Parse(JsonElement root, DateTimeOffset now)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("userStatus", out var status) || status.ValueKind != JsonValueKind.Object ||
            !status.TryGetProperty("cascadeModelConfigData", out var config) || config.ValueKind != JsonValueKind.Object ||
            !config.TryGetProperty("clientModelConfigs", out var models) || models.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

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
            quotas.Add(new AntigravityModelQuota($"model:{name}", name, name, used, resetsAt));
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
        var grouped = quotas.GroupBy(quota => (BaseModelName(quota.ModelName), quota.UsedPercent,
                quota.ResetsAt, remainingFractions[quota.ModelName]))
            .Select(group => group.Count() == 1 ? group.First() : new AntigravityModelQuota(
                $"model:{group.Key.Item1}", group.Key.Item1, group.Key.Item1, group.Key.UsedPercent,
                group.Key.ResetsAt, group.Select(quota => quota.ModelName).ToArray()))
            .ToArray();
        return new AntigravityStatusUsage(null, null, null, null, null, plan, now, grouped);
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
