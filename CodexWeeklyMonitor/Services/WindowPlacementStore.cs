using System.Text.Json;

namespace CodexWeeklyMonitor.Services;

public static class WindowPlacementStore
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexUsageMonitor",
        "window.json");

    public static WindowPlacement? Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return null;
            }

            return JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(SettingsPath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static void Save(WindowPlacement placement)
    {
        // A window that was shut down before it was ever laid out reports NaN/Infinity, which
        // System.Text.Json refuses to write. There is no placement worth keeping in that case.
        if (!double.IsFinite(placement.Left) || !double.IsFinite(placement.Top))
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(directory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(placement));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Window placement is optional; quota monitoring must keep working.
        }
    }
}

public sealed record WindowPlacement(
    double Left,
    double Top,
    bool Topmost,
    bool DetailsExpanded = false);
