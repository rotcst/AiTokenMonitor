namespace CodexWeeklyMonitor.Services;

internal static class ClaudeExecutableResolver
{
    public static bool IsAvailable()
    {
        return EnumerateCandidates().Any(File.Exists);
    }

    private static IEnumerable<string> EnumerateCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var claudeHome = ClaudePaths.ResolveHome();
        foreach (var candidate in new[]
                 {
                     Environment.GetEnvironmentVariable("CLAUDE_EXE"),

                     // Native installer and the config-dir-local copy.
                     Path.Combine(userProfile, ".local", "bin", "claude.exe"),
                     Path.Combine(claudeHome, "local", "claude.exe"),
                     Path.Combine(claudeHome, "local", "claude.cmd"),

                     // Global installs from the common Node package managers.
                     Path.Combine(appData, "npm", "claude.cmd"),
                     Path.Combine(appData, "npm", "claude.exe"),
                     Path.Combine(localAppData, "pnpm", "claude.cmd"),
                     Path.Combine(localAppData, "pnpm", "claude.exe"),
                     Path.Combine(userProfile, ".bun", "bin", "claude.exe"),
                     Path.Combine(userProfile, ".yarn", "bin", "claude.cmd"),

                     // Desktop app, plus the CLI build it manages under its own version folder.
                     Path.Combine(localAppData, "Programs", "Claude", "Claude.exe"),
                     Path.Combine(localAppData, "Claude", "Claude.exe"),
                     Path.Combine(appData, "Claude", "Claude.exe"),
                 })
        {
            if (TryNormalize(candidate, out var path) && seen.Add(path))
            {
                yield return path;
            }
        }

        foreach (var path in EnumerateManagedCliBuilds(Path.Combine(appData, "Claude", "claude-code")))
        {
            if (seen.Add(path))
            {
                yield return path;
            }
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            yield break;
        }

        foreach (var rawDirectory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = rawDirectory.Trim().Trim('"');
            foreach (var fileName in new[] { "claude.exe", "claude.cmd" })
            {
                string? candidate;
                try
                {
                    candidate = Path.Combine(directory, fileName);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (TryNormalize(candidate, out var path) && seen.Add(path))
                {
                    yield return path;
                }
            }
        }
    }

    /// <summary>
    /// The desktop app downloads the CLI into a per-version folder
    /// (<c>%APPDATA%\Claude\claude-code\&lt;version&gt;\claude.exe</c>), so the exact path is not
    /// knowable ahead of time.
    /// </summary>
    private static IEnumerable<string> EnumerateManagedCliBuilds(string root)
    {
        string[] versionDirectories;
        try
        {
            if (!Directory.Exists(root))
            {
                yield break;
            }

            versionDirectories = Directory.GetDirectories(root);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var directory in versionDirectories)
        {
            if (TryNormalize(Path.Combine(directory, "claude.exe"), out var path))
            {
                yield return path;
            }
        }
    }

    private static bool TryNormalize(string? candidate, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        try
        {
            path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(candidate));
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
