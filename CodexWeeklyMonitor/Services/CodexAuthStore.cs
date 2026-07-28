using System.Text;
using System.Text.Json;

namespace CodexWeeklyMonitor.Services;

internal sealed record CodexCredentials(
    string AccessToken,
    string? AccountId,
    DateTimeOffset? ExpiresAt,
    string? AuthMode,
    string Source)
{
    // Treat a token that is about to lapse as already gone, so the fallback path starts before
    // the endpoint begins refusing calls.
    public bool IsExpired => ExpiresAt is { } expiresAt &&
                             expiresAt <= DateTimeOffset.UtcNow.AddMinutes(2);
}

/// <summary>
/// Reads the ChatGPT credentials the Codex CLI keeps in <c>~/.codex/auth.json</c> so usage can be
/// queried without spawning <c>codex app-server</c>.
/// </summary>
/// <remarks>
/// This is read-only on purpose. OpenAI rotates refresh tokens, so refreshing here and failing to
/// persist the replacement would invalidate the CLI's own login; renewal is left to the CLI and the
/// caller falls back to the app-server when the stored token lapses.
/// </remarks>
internal static class CodexAuthStore
{
    public static CodexCredentials? Resolve(string? codexHome = null)
    {
        var path = Path.Combine(ResolveCodexHome(codexHome), "auth.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            return Parse(document.RootElement, path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    internal static CodexCredentials? Parse(JsonElement root, string source)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("tokens", out var tokens) ||
            tokens.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var accessToken = TryGetString(tokens, "access_token");
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        return new CodexCredentials(
            accessToken,
            TryGetString(tokens, "account_id"),
            ReadJwtExpiry(accessToken),
            TryGetString(root, "auth_mode"),
            source);
    }

    /// <summary>Reads <c>exp</c> from the JWT payload without validating the signature.</summary>
    internal static DateTimeOffset? ReadJwtExpiry(string token)
    {
        var parts = token.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(DecodeBase64Url(parts[1]));
            if (!document.RootElement.TryGetProperty("exp", out var exp) ||
                !exp.TryGetInt64(out var seconds))
            {
                return null;
            }

            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (Exception exception) when (
            exception is JsonException or FormatException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var builder = new StringBuilder(value.Replace('-', '+').Replace('_', '/'));
        while (builder.Length % 4 != 0)
        {
            builder.Append('=');
        }

        return Convert.FromBase64String(builder.ToString());
    }

    private static string ResolveCodexHome(string? codexHome)
    {
        if (!string.IsNullOrWhiteSpace(codexHome))
        {
            return Path.GetFullPath(codexHome);
        }

        var configured = Environment.GetEnvironmentVariable("CODEX_HOME");
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));
    }

    private static string? TryGetString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
