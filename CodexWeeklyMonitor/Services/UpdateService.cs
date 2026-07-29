using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexWeeklyMonitor.Services;

internal interface IUpdateService : IDisposable
{
    Version CurrentVersion { get; }

    Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default);

    Task<string> DownloadAsync(
        UpdateRelease release,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);

    void LaunchInstaller(UpdateRelease release, string downloadedExecutable);
}

internal sealed record UpdateCheckResult(UpdateRelease Release, bool IsUpdateAvailable);

internal sealed record UpdateRelease(
    Version Version,
    string TagName,
    Uri ReleasePage,
    string? Notes,
    UpdateAsset Asset);

internal sealed record UpdateAsset(Uri DownloadUrl, long Size, string Sha256);

internal sealed class UpdateServiceException(string resourceKey, Exception? innerException = null)
    : Exception(resourceKey, innerException)
{
    public string ResourceKey { get; } = resourceKey;
}

internal sealed class GitHubUpdateService : IUpdateService
{
    internal const string RepositoryOwner = "rotcst";
    internal const string RepositoryName = "AiTokenMonitor";
    private const long MaximumAssetSize = 512L * 1024 * 1024;
    private static readonly Uri LatestReleaseUri = new(
        $"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/releases/latest");

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public GitHubUpdateService()
        : this(CreateHttpClient(), ownsHttpClient: true)
    {
    }

    internal GitHubUpdateService(HttpClient httpClient, bool ownsHttpClient = false)
    {
        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;
    }

    public Version CurrentVersion => AppVersion.Current;

    public async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        GitHubReleaseDto? dto;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var response = await _httpClient.GetAsync(LatestReleaseUri, timeout.Token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token)
                .ConfigureAwait(false);
            dto = await JsonSerializer.DeserializeAsync<GitHubReleaseDto>(stream, cancellationToken: timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateServiceException("update.checkFailed", exception);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException)
        {
            throw new UpdateServiceException("update.checkFailed", exception);
        }

        var release = ParseRelease(dto);
        return new UpdateCheckResult(release, release.Version > CurrentVersion);
    }

    public async Task<string> DownloadAsync(
        UpdateRelease release,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var updateDirectory = UpdateInstaller.GetUpdateDirectory(release.TagName);
        Directory.CreateDirectory(updateDirectory);
        var destination = Path.Combine(
            updateDirectory,
            $"AiTokenMonitor-{AppVersion.ToDisplayString(release.Version)}.exe");

        if (await IsValidFileAsync(destination, release.Asset, cancellationToken).ConfigureAwait(false))
        {
            progress?.Report(1);
            return destination;
        }

        var temporary = destination + $".{Guid.NewGuid():N}.download";
        try
        {
            using var response = await _httpClient.GetAsync(
                    release.Asset.DownloadUrl,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var declaredLength = response.Content.Headers.ContentLength;
            if (declaredLength is > MaximumAssetSize ||
                (declaredLength is > 0 && declaredLength != release.Asset.Size))
            {
                throw new UpdateServiceException("update.invalidMetadata");
            }

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            var buffer = new byte[128 * 1024];
            long total = 0;
            await using (var output = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             128 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    total += read;
                    if (total > release.Asset.Size || total > MaximumAssetSize)
                    {
                        throw new UpdateServiceException("update.invalidMetadata");
                    }

                    progress?.Report(Math.Clamp((double)total / release.Asset.Size, 0, 1));
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (total != release.Asset.Size ||
                !await HasExpectedSha256Async(temporary, release.Asset.Sha256, cancellationToken)
                    .ConfigureAwait(false))
            {
                throw new UpdateServiceException("update.integrityFailed");
            }

            File.Move(temporary, destination, overwrite: true);
            progress?.Report(1);
            return destination;
        }
        catch (UpdateServiceException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw new UpdateServiceException("update.downloadFailed", exception);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    public void LaunchInstaller(UpdateRelease release, string downloadedExecutable)
    {
        var target = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(target) || !File.Exists(downloadedExecutable) ||
            !HasExpectedSha256Async(downloadedExecutable, release.Asset.Sha256).GetAwaiter().GetResult())
        {
            throw new UpdateServiceException("update.integrityFailed");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(downloadedExecutable),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(UpdateInstaller.ApplyArgument);
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(Path.GetFullPath(target));
        startInfo.ArgumentList.Add(release.Asset.Sha256);

        try
        {
            Process.Start(startInfo);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new UpdateServiceException("update.installFailed", exception);
        }
    }

    internal static UpdateRelease ParseRelease(GitHubReleaseDto? dto)
    {
        if (dto is null || dto.Draft || dto.Prerelease ||
            !AppVersion.TryParseTag(dto.TagName, out var version) ||
            !Uri.TryCreate(dto.HtmlUrl, UriKind.Absolute, out var releasePage) ||
            releasePage.Scheme != Uri.UriSchemeHttps)
        {
            throw new UpdateServiceException("update.invalidMetadata");
        }

        var versionText = AppVersion.ToDisplayString(version);
        var expectedNames = new[]
        {
            $"AiTokenMonitor-{versionText}.exe",
            "AiTokenMonitor.exe",
        };
        var assetDto = dto.Assets?.FirstOrDefault(asset =>
            expectedNames.Contains(asset.Name, StringComparer.OrdinalIgnoreCase))
            ?? throw new UpdateServiceException("update.assetMissing");

        if (assetDto.Size <= 0 || assetDto.Size > MaximumAssetSize ||
            !TryParseSha256(assetDto.Digest, out var sha256) ||
            !Uri.TryCreate(assetDto.BrowserDownloadUrl, UriKind.Absolute, out var downloadUrl) ||
            downloadUrl.Scheme != Uri.UriSchemeHttps ||
            !IsTrustedDownloadHost(downloadUrl.Host))
        {
            throw new UpdateServiceException("update.invalidMetadata");
        }

        return new UpdateRelease(
            version,
            dto.TagName!,
            releasePage,
            dto.Body,
            new UpdateAsset(downloadUrl, assetDto.Size, sha256));
    }

    internal static bool TryParseSha256(string? digest, out string sha256)
    {
        const string prefix = "sha256:";
        var value = digest?.Trim();
        if (value is not null && value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var candidate = value[prefix.Length..];
            if (candidate.Length == 64 && candidate.All(Uri.IsHexDigit))
            {
                sha256 = candidate.ToLowerInvariant();
                return true;
            }
        }

        sha256 = string.Empty;
        return false;
    }

    internal static async Task<bool> HasExpectedSha256Async(
        string path,
        string expected,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return CryptographicOperations.FixedTimeEquals(
            hash,
            Convert.FromHexString(expected));
    }

    private static async Task<bool> IsValidFileAsync(
        string path,
        UpdateAsset asset,
        CancellationToken cancellationToken)
    {
        try
        {
            return File.Exists(path) &&
                   new FileInfo(path).Length == asset.Size &&
                   await HasExpectedSha256Async(path, asset.Sha256, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
        {
            return false;
        }
    }

    private static bool IsTrustedDownloadHost(string host) =>
        host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"AiTokenMonitor/{AppVersion.ToDisplayString(AppVersion.Current)}");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The next update check uses a new temporary name; a locked partial file is harmless.
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    internal sealed class GitHubReleaseDto
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; init; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; init; }

        [JsonPropertyName("body")]
        public string? Body { get; init; }

        [JsonPropertyName("draft")]
        public bool Draft { get; init; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; init; }

        [JsonPropertyName("assets")]
        public GitHubAssetDto[]? Assets { get; init; }
    }

    internal sealed class GitHubAssetDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("size")]
        public long Size { get; init; }

        [JsonPropertyName("digest")]
        public string? Digest { get; init; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; init; }
    }
}
