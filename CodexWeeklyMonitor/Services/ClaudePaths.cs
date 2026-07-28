namespace CodexWeeklyMonitor.Services;

/// <summary>
/// Single source of truth for where Claude Code keeps its state.
/// </summary>
/// <remarks>
/// The CLI honours <c>CLAUDE_CONFIG_DIR</c> and writes credentials, transcripts and settings under
/// it. Every component here has to agree on that directory, otherwise a CLI user who moved their
/// config gets quota from one place and transcripts from another - or nothing at all.
/// </remarks>
internal static class ClaudePaths
{
    public static string ResolveHome(string? overrideHome = null)
    {
        if (!string.IsNullOrWhiteSpace(overrideHome))
        {
            return Path.GetFullPath(overrideHome);
        }

        var configured = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (string.IsNullOrWhiteSpace(configured))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude");
        }

        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude");
        }
    }
}
