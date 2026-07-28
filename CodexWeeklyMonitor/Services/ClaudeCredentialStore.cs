using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexWeeklyMonitor.Services;

internal sealed record ClaudeOAuthToken(
    string AccessToken,
    DateTimeOffset? ExpiresAt,
    string? SubscriptionType,
    string Source)
{
    public bool IsExpired => ExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow;
}

/// <summary>
/// Locates the Claude OAuth access token that Claude Code itself uses to call
/// <c>https://api.anthropic.com/api/oauth/usage</c>.
/// </summary>
/// <remarks>
/// Three storage layouts are supported, in priority order:
/// <list type="number">
/// <item>the <c>CLAUDE_CODE_OAUTH_TOKEN</c> environment variable;</item>
/// <item><c>~/.claude/.credentials.json</c> written by the standalone CLI;</item>
/// <item>the Claude desktop app's <c>config.json</c>, whose <c>oauth:tokenCache*</c> entries are
/// sealed with Chromium's OSCrypt (DPAPI-protected AES-256-GCM key in <c>Local State</c>).</item>
/// </list>
/// </remarks>
internal static class ClaudeCredentialStore
{
    private const string RequiredAudience = "https://api.anthropic.com";

    /// <summary>
    /// Returns every usable token, most authoritative first. A machine can hold several (an explicit
    /// override, a CLI login, one or more desktop caches) and only the caller can tell which one the
    /// server still accepts, so all of them are offered rather than guessed at here.
    /// </summary>
    public static IReadOnlyList<ClaudeOAuthToken> ResolveAll(
        string? claudeHome = null,
        string? desktopDirectory = null)
    {
        var candidates = new List<ClaudeOAuthToken>();

        if (ReadFromEnvironment() is { } fromEnvironment)
        {
            candidates.Add(fromEnvironment);
        }

        if (ReadFromCliCredentials(ResolveClaudeHome(claudeHome)) is { } fromCli)
        {
            candidates.Add(fromCli);
        }

        candidates.AddRange(ReadFromDesktopApp(ResolveDesktopDirectory(desktopDirectory)));

        // Expired tokens stay in the list only as a last resort, so the caller can tell
        // "never logged in" apart from "the login went stale".
        return candidates
            .OrderBy(token => token.IsExpired)
            .ToArray();
    }

    public static ClaudeOAuthToken? Resolve(string? claudeHome = null, string? desktopDirectory = null)
    {
        return ResolveAll(claudeHome, desktopDirectory).FirstOrDefault();
    }

    private static ClaudeOAuthToken? ReadFromEnvironment()
    {
        var token = Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN");
        return string.IsNullOrWhiteSpace(token)
            ? null
            : new ClaudeOAuthToken(token.Trim(), null, null, "CLAUDE_CODE_OAUTH_TOKEN");
    }

    internal static ClaudeOAuthToken? ReadFromCliCredentials(string claudeHome)
    {
        var path = Path.Combine(claudeHome, ".credentials.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return ParseCliCredentials(document.RootElement, path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    internal static ClaudeOAuthToken? ParseCliCredentials(JsonElement root, string source)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in new[] { "claudeAiOauth", "claude_ai_oauth", "oauth" })
        {
            if (!root.TryGetProperty(name, out var node) || node.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var accessToken = TryGetString(node, "accessToken") ?? TryGetString(node, "access_token");
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                continue;
            }

            return new ClaudeOAuthToken(
                accessToken,
                TryGetEpochMilliseconds(node, "expiresAt") ?? TryGetEpochMilliseconds(node, "expires_at"),
                TryGetString(node, "subscriptionType") ?? TryGetString(node, "subscription_type"),
                source);
        }

        return null;
    }

    private static IEnumerable<ClaudeOAuthToken> ReadFromDesktopApp(string desktopDirectory)
    {
        var configPath = Path.Combine(desktopDirectory, "config.json");
        var localStatePath = Path.Combine(desktopDirectory, "Local State");
        if (!File.Exists(configPath) || !File.Exists(localStatePath))
        {
            yield break;
        }

        byte[] key;
        string configText;
        try
        {
            key = ReadOsCryptKey(localStatePath);
            configText = File.ReadAllText(configPath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or
                FormatException or CryptographicException)
        {
            yield break;
        }

        foreach (var propertyName in new[] { "oauth:tokenCacheV2", "oauth:tokenCache" })
        {
            string? plaintext;
            try
            {
                using var document = JsonDocument.Parse(configText);
                if (!document.RootElement.TryGetProperty(propertyName, out var sealedValue) ||
                    sealedValue.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                plaintext = DecryptOsCrypt(sealedValue.GetString(), key);
            }
            catch (Exception exception) when (
                exception is JsonException or FormatException or CryptographicException)
            {
                continue;
            }

            if (plaintext is null)
            {
                continue;
            }

            foreach (var token in ParseDesktopTokenCache(plaintext, $"{configPath}#{propertyName}"))
            {
                yield return token;
            }
        }
    }

    /// <summary>
    /// The desktop cache is keyed by "&lt;account&gt;:&lt;device&gt;:&lt;audience&gt;:&lt;scopes&gt;";
    /// only entries issued for the Anthropic API can query the usage endpoint.
    /// </summary>
    internal static IReadOnlyList<ClaudeOAuthToken> ParseDesktopTokenCache(string json, string source)
    {
        var tokens = new List<ClaudeOAuthToken>();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return tokens;
            }

            foreach (var entry in document.RootElement.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object ||
                    !entry.Name.Contains(RequiredAudience, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var accessToken = TryGetString(entry.Value, "token") ??
                                  TryGetString(entry.Value, "accessToken");
                if (string.IsNullOrWhiteSpace(accessToken))
                {
                    continue;
                }

                tokens.Add(new ClaudeOAuthToken(
                    accessToken,
                    TryGetEpochMilliseconds(entry.Value, "expiresAt"),
                    TryGetString(entry.Value, "subscriptionType"),
                    source));
            }
        }
        catch (JsonException)
        {
            // A future cache format simply yields no candidates.
        }

        return tokens;
    }

    private static byte[] ReadOsCryptKey(string localStatePath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(localStatePath));
        var encoded = document.RootElement.GetProperty("os_crypt").GetProperty("encrypted_key").GetString()
                      ?? throw new CryptographicException("Local State 缺少 os_crypt 密钥。");
        var blob = Convert.FromBase64String(encoded);
        var prefix = "DPAPI"u8;
        if (blob.Length <= prefix.Length || !blob.AsSpan(0, prefix.Length).SequenceEqual(prefix))
        {
            throw new CryptographicException("os_crypt 密钥不是预期的 DPAPI 格式。");
        }

        return ProtectedData.Unprotect(
            blob.AsSpan(prefix.Length).ToArray(),
            optionalEntropy: null,
            scope: DataProtectionScope.CurrentUser);
    }

    /// <summary>Decrypts a Chromium OSCrypt "v10" blob: 3-byte tag, 12-byte nonce, ciphertext, 16-byte GCM tag.</summary>
    internal static string? DecryptOsCrypt(string? base64, byte[] key)
    {
        if (string.IsNullOrWhiteSpace(base64))
        {
            return null;
        }

        var blob = Convert.FromBase64String(base64);
        const int prefixLength = 3;
        const int nonceLength = 12;
        const int tagLength = 16;
        if (blob.Length <= prefixLength + nonceLength + tagLength ||
            Encoding.ASCII.GetString(blob, 0, prefixLength) is not ("v10" or "v11"))
        {
            return null;
        }

        var nonce = blob.AsSpan(prefixLength, nonceLength);
        var cipherLength = blob.Length - prefixLength - nonceLength - tagLength;
        var cipher = blob.AsSpan(prefixLength + nonceLength, cipherLength);
        var tag = blob.AsSpan(blob.Length - tagLength, tagLength);
        var plaintext = new byte[cipherLength];

        using var aes = new AesGcm(key, tagLength);
        aes.Decrypt(nonce, cipher, tag, plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }

    private static string ResolveClaudeHome(string? claudeHome)
    {
        return ClaudePaths.ResolveHome(claudeHome);
    }

    private static string ResolveDesktopDirectory(string? desktopDirectory)
    {
        return string.IsNullOrWhiteSpace(desktopDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude")
            : Path.GetFullPath(desktopDirectory);
    }

    private static string? TryGetString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static DateTimeOffset? TryGetEpochMilliseconds(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        long milliseconds;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out milliseconds))
        {
            // Fall through.
        }
        else if (value.ValueKind == JsonValueKind.String &&
                 long.TryParse(value.GetString(), out milliseconds))
        {
            // Fall through.
        }
        else
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
