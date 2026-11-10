namespace CodexWeeklyMonitor.Services;

/// <summary>
/// Single source of truth for where Antigravity (Desktop 2.0, IDE, and CLI) keeps its local
/// conversation state and transcripts.
/// </summary>
internal static class AntigravityPaths
{
    public static string ResolveHome(string? overrideHome = null)
    {
        if (!string.IsNullOrWhiteSpace(overrideHome))
        {
            return Path.GetFullPath(overrideHome);
        }

        var configured = Environment.GetEnvironmentVariable("ANTIGRAVITY_HOME");
        if (string.IsNullOrWhiteSpace(configured))
        {
            return GetDefaultHome();
        }

        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return GetDefaultHome();
        }
    }

    public static IReadOnlyList<string> ResolveBrainDirectories(string? overrideHome = null)
    {
        var primaryHome = ResolveHome(overrideHome);
        var isCustomHome = !string.IsNullOrWhiteSpace(overrideHome) ||
                           !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTIGRAVITY_HOME"));
        if (isCustomHome)
        {
            return NormalizeDistinct(
            [
                Path.Combine(primaryHome, "brain"),
                primaryHome,
            ]);
        }

        var geminiRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".gemini");
        return NormalizeDistinct(
        [
            Path.Combine(primaryHome, "brain"),
            Path.Combine(geminiRoot, "antigravity", "brain"),
            Path.Combine(geminiRoot, "antigravity-ide", "brain"),
            Path.Combine(geminiRoot, "antigravity-cli", "brain"),
        ]);
    }

    private static string GetDefaultHome()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".gemini",
            "antigravity");
    }

    private static IReadOnlyList<string> NormalizeDistinct(IEnumerable<string> candidates)
    {
        var paths = new List<string>();
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            try
            {
                var path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(candidate));
                if (!paths.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    paths.Add(path);
                }
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                // Ignore malformed environment values.
            }
        }

        return paths;
    }
}
