using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using CodexWeeklyMonitor.Models;

namespace CodexWeeklyMonitor.Services;

internal enum CodexApiFailure
{
    NotLoggedIn,
    Unauthorized,
    Throttled,
    Network,
    Protocol,
}

internal sealed class CodexApiException(CodexApiFailure failure, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public CodexApiFailure Failure { get; } = failure;

    public TimeSpan? RetryAfter { get; init; }
}

/// <summary>
/// Queries the ChatGPT backend directly with the credentials the Codex CLI stored, so usage is
/// available whether or not Codex is running locally.
/// </summary>
internal sealed class CodexApiClient : IDisposable
{
    private const string UsageEndpoint = "https://chatgpt.com/backend-api/wham/usage";
    private const string ProfileEndpoint = "https://chatgpt.com/backend-api/wham/profiles/me";
    private const string ResetCreditsEndpoint =
        "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits";

    private readonly HttpClient _httpClient;
    private readonly Func<CodexCredentials?> _credentialResolver;
    private bool _disposed;

    public CodexApiClient(
        Func<CodexCredentials?>? credentialResolver = null,
        HttpMessageHandler? handler = null)
    {
        _credentialResolver = credentialResolver ?? (() => CodexAuthStore.Resolve());
        _httpClient = handler is null
            ? new HttpClient()
            : new HttpClient(handler, disposeHandler: true);
        _httpClient.Timeout = TimeSpan.FromSeconds(20);
        _httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("AiTokenMonitor", "1.4"));
    }

    public async Task<CodexUsageSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var credentials = _credentialResolver()
                          ?? throw new CodexApiException(
                              CodexApiFailure.NotLoggedIn,
                              Loc.T("err.codex.noCred"));
        if (credentials.IsExpired)
        {
            throw new CodexApiException(
                CodexApiFailure.Unauthorized,
                Loc.T("err.codex.expired"));
        }

        var fetchedAt = DateTimeOffset.Now;

        // Reset credits are supplementary: a failure there must not hide the headline windows.
        var resetCreditsTask = TryGetAsync(credentials, ResetCreditsEndpoint, cancellationToken);
        var profileTask = TryGetAsync(credentials, ProfileEndpoint, cancellationToken);
        var usagePayload = await GetAsync(credentials, UsageEndpoint, cancellationToken)
            .ConfigureAwait(false);

        long? availableResetCount = null;
        if (await resetCreditsTask.ConfigureAwait(false) is { } resetPayload)
        {
            availableResetCount = TryParse(
                resetPayload,
                root => CodexApiParser.ParseResetCredits(root));
        }

        AccountRateLimits limits;
        CodexAccountDetail detail;
        try
        {
            using var document = JsonDocument.Parse(usagePayload);
            (limits, detail) = CodexApiParser.ParseUsage(
                document.RootElement,
                fetchedAt,
                availableResetCount);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            throw new CodexApiException(
                CodexApiFailure.Protocol,
                Loc.T("err.codex.parseQuota"),
                exception);
        }

        AccountTokenUsage? tokenUsage = null;
        string? tokenUsageError = null;
        if (await profileTask.ConfigureAwait(false) is { } profilePayload)
        {
            try
            {
                using var document = JsonDocument.Parse(profilePayload);
                var (usage, stats) = CodexApiParser.ParseProfile(document.RootElement, fetchedAt);
                tokenUsage = usage;
                detail = detail with { Profile = stats };
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException)
            {
                tokenUsageError = Loc.T("err.codex.parseStats");
            }
        }
        else
        {
            tokenUsageError = Loc.T("err.codex.statsUnavailable");
        }

        return new CodexUsageSnapshot(limits, tokenUsage, tokenUsageError, fetchedAt, detail);
    }

    private async Task<string?> TryGetAsync(
        CodexCredentials credentials,
        string endpoint,
        CancellationToken cancellationToken)
    {
        try
        {
            return await GetAsync(credentials, endpoint, cancellationToken).ConfigureAwait(false);
        }
        catch (CodexApiException)
        {
            return null;
        }
    }

    private async Task<string> GetAsync(
        CodexCredentials credentials,
        string endpoint,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(credentials.AccountId))
        {
            request.Headers.TryAddWithoutValidation("chatgpt-account-id", credentials.AccountId);
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CodexApiException(CodexApiFailure.Network, Loc.T("err.codex.timeout"), exception);
        }
        catch (HttpRequestException exception)
        {
            throw new CodexApiException(CodexApiFailure.Network, Loc.T("err.codex.network"), exception);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new CodexApiException(
                    CodexApiFailure.Unauthorized,
                    Loc.T("err.codex.invalid"));
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new CodexApiException(
                    CodexApiFailure.Throttled,
                    Loc.T("err.codex.throttled"))
                {
                    RetryAfter = ClaudeUsageClient.ReadRetryAfter(response),
                };
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new CodexApiException(
                    CodexApiFailure.Protocol,
                    Loc.T("err.codex.http", (int)response.StatusCode));
            }

            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static T? TryParse<T>(string payload, Func<JsonElement, T?> parse)
        where T : struct
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            return parse(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _httpClient.Dispose();
    }
}
