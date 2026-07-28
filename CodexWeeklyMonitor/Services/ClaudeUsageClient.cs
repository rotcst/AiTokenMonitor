using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using CodexWeeklyMonitor.Models;

namespace CodexWeeklyMonitor.Services;

internal enum ClaudeUsageFailure
{
    NotLoggedIn,
    Unauthorized,
    Throttled,
    Network,
    Protocol,
}

internal sealed class ClaudeUsageException(
    ClaudeUsageFailure failure,
    string message,
    Exception? inner = null)
    : Exception(message, inner)
{
    public ClaudeUsageFailure Failure { get; } = failure;

    public TimeSpan? RetryAfter { get; init; }
}

/// <summary>
/// Reads the account's real quota from Anthropic's OAuth usage endpoint - the same call Claude
/// Code makes for <c>/usage</c> - instead of inferring it from status-line snapshots.
/// </summary>
internal sealed class ClaudeUsageClient : IDisposable
{
    private const string UsageEndpoint = "https://api.anthropic.com/api/oauth/usage";
    private const string OAuthBetaHeader = "oauth-2025-04-20";

    private readonly HttpClient _httpClient;
    private readonly Func<IReadOnlyList<ClaudeOAuthToken>> _tokenResolver;
    private ClaudeOAuthToken? _lastGoodToken;
    private bool _disposed;

    public ClaudeUsageClient(
        Func<IReadOnlyList<ClaudeOAuthToken>>? tokenResolver = null,
        HttpMessageHandler? handler = null)
    {
        _tokenResolver = tokenResolver ?? (() => ClaudeCredentialStore.ResolveAll());
        _httpClient = handler is null
            ? new HttpClient()
            : new HttpClient(handler, disposeHandler: true);
        _httpClient.Timeout = TimeSpan.FromSeconds(20);
        _httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("AiTokenMonitor", "1.3"));
    }

    /// <summary>
    /// Tries each stored credential in turn. A desktop install can keep a stale token cache
    /// alongside the live one, so a 401 means "try the next candidate", not "not logged in".
    /// </summary>
    public async Task<ClaudeAccountUsage> FetchAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var candidates = _tokenResolver();
        if (_lastGoodToken is { } cached)
        {
            candidates = candidates
                .Where(token => token.AccessToken != cached.AccessToken)
                .Prepend(cached)
                .ToArray();
        }

        if (candidates.Count == 0)
        {
            throw new ClaudeUsageException(
                ClaudeUsageFailure.NotLoggedIn,
                Loc.T("err.claude.noCred"));
        }

        ClaudeUsageException? lastFailure = null;
        foreach (var token in candidates)
        {
            try
            {
                var usage = await FetchWithTokenAsync(token, cancellationToken).ConfigureAwait(false);
                _lastGoodToken = token;
                return usage;
            }
            catch (ClaudeUsageException exception) when (
                exception.Failure == ClaudeUsageFailure.Unauthorized)
            {
                if (_lastGoodToken?.AccessToken == token.AccessToken)
                {
                    _lastGoodToken = null;
                }

                lastFailure = exception;
            }
        }

        throw lastFailure ?? new ClaudeUsageException(
            ClaudeUsageFailure.Unauthorized,
            Loc.T("err.claude.invalid"));
    }

    private async Task<ClaudeAccountUsage> FetchWithTokenAsync(
        ClaudeOAuthToken token,
        CancellationToken cancellationToken)
    {
        if (token.IsExpired)
        {
            throw new ClaudeUsageException(
                ClaudeUsageFailure.Unauthorized,
                Loc.T("err.claude.expired"));
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        request.Headers.TryAddWithoutValidation("anthropic-beta", OAuthBetaHeader);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ClaudeUsageException(
                ClaudeUsageFailure.Network,
                Loc.T("err.claude.timeout"),
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new ClaudeUsageException(
                ClaudeUsageFailure.Network,
                Loc.T("err.claude.network"),
                exception);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new ClaudeUsageException(
                    ClaudeUsageFailure.Unauthorized,
                    Loc.T("err.claude.invalid"));
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new ClaudeUsageException(
                    ClaudeUsageFailure.Throttled,
                    Loc.T("err.claude.throttled"))
                {
                    RetryAfter = ReadRetryAfter(response),
                };
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new ClaudeUsageException(
                    ClaudeUsageFailure.Protocol,
                    Loc.T("err.claude.http", (int)response.StatusCode));
            }

            var payload = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            return ParsePayload(payload, DateTimeOffset.Now, token.SubscriptionType);
        }
    }

    internal static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null)
        {
            return null;
        }

        if (retryAfter.Delta is { } delta)
        {
            return delta;
        }

        return retryAfter.Date is { } date && date > DateTimeOffset.UtcNow
            ? date - DateTimeOffset.UtcNow
            : null;
    }

    internal static ClaudeAccountUsage ParsePayload(
        string payload,
        DateTimeOffset fetchedAt,
        string? subscriptionType)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            return ClaudeUsageParser.Parse(document.RootElement, fetchedAt, subscriptionType);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            throw new ClaudeUsageException(
                ClaudeUsageFailure.Protocol,
                Loc.T("err.claude.parse"),
                exception);
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
