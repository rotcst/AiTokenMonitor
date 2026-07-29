using System.Reflection;

namespace CodexWeeklyMonitor.Services;

internal static class AppVersion
{
    public static Version Current { get; } = Normalize(
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0));

    public static string Display => $"v{ToDisplayString(Current)}";

    internal static Version Normalize(Version version) => new(
        version.Major,
        version.Minor,
        Math.Max(0, version.Build),
        Math.Max(0, version.Revision));

    internal static bool TryParseTag(string? tag, out Version version)
    {
        var value = tag?.Trim().TrimStart('v', 'V');
        if (Version.TryParse(value, out var parsed) && parsed.Major >= 0 && parsed.Minor >= 0)
        {
            version = Normalize(parsed);
            return true;
        }

        version = new Version(0, 0, 0, 0);
        return false;
    }

    internal static string ToDisplayString(Version version)
    {
        var normalized = Normalize(version);
        return normalized.Revision == 0
            ? $"{normalized.Major}.{normalized.Minor}.{normalized.Build}"
            : normalized.ToString(4);
    }
}
