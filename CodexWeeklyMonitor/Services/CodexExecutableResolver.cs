using System.Runtime.InteropServices;

namespace CodexWeeklyMonitor.Services;

public static class CodexExecutableResolver
{
    public static string Resolve()
    {
        var candidates = new List<string>();

        AddCandidate(candidates, Environment.GetEnvironmentVariable("CODEX_EXE"));
        AddDesktopInstallCandidates(candidates);
        AddNpmCandidates(candidates);
        AddPathCandidates(candidates);

        var preferred = candidates.FirstOrDefault(path =>
            File.Exists(path) &&
            !path.Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase));

        if (preferred is not null)
        {
            return preferred;
        }

        var fallback = candidates.FirstOrDefault(File.Exists);
        if (fallback is not null)
        {
            return fallback;
        }

        throw new FileNotFoundException(
            Loc.T("err.codex.notFound"));
    }

    private static void AddDesktopInstallCandidates(ICollection<string> candidates)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            return;
        }

        AddCandidate(candidates, Path.Combine(
            localAppData,
            "OpenAI",
            "Codex",
            "codex.exe"));

        var binDirectory = Path.Combine(localAppData, "OpenAI", "Codex", "bin");
        if (!Directory.Exists(binDirectory))
        {
            return;
        }

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(binDirectory)
                         .OrderByDescending(Directory.GetLastWriteTimeUtc))
            {
                AddCandidate(candidates, Path.Combine(directory, "codex.exe"));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // PATH and npm candidates are still available.
        }
    }

    private static void AddNpmCandidates(ICollection<string> candidates)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
        {
            return;
        }

        var architecturePackage = RuntimeInformation.OSArchitecture == Architecture.Arm64
            ? "codex-win32-arm64"
            : "codex-win32-x64";
        var architectureFolder = RuntimeInformation.OSArchitecture == Architecture.Arm64
            ? "aarch64-pc-windows-msvc"
            : "x86_64-pc-windows-msvc";

        AddCandidate(candidates, Path.Combine(
            appData,
            "npm",
            "node_modules",
            "@openai",
            "codex",
            "node_modules",
            "@openai",
            architecturePackage,
            "vendor",
            architectureFolder,
            "bin",
            "codex.exe"));
    }

    private static void AddPathCandidates(ICollection<string> candidates)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return;
        }

        foreach (var rawDirectory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = rawDirectory.Trim().Trim('"');
            if (directory.Length == 0)
            {
                continue;
            }

            try
            {
                AddCandidate(candidates, Path.Combine(directory, "codex.exe"));
            }
            catch (ArgumentException)
            {
                // Ignore malformed PATH entries and continue discovery.
            }
        }
    }

    private static void AddCandidate(ICollection<string> candidates, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
            if (!candidates.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(fullPath);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            // Invalid overrides should not prevent fallback discovery.
        }
    }
}
