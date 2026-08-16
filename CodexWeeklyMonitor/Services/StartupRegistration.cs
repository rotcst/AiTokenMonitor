using System.Security;
using Microsoft.Win32;

namespace CodexWeeklyMonitor.Services;

/// <summary>
/// Per-user "start with Windows" registration for the portable single-file EXE.
/// </summary>
/// <remarks>
/// The HKCU Run key is the only mechanism that fits this app. The manifest requests
/// <c>asInvoker</c>, so a machine-wide entry or a scheduled task would need elevation the app never
/// has, and a Start-menu shortcut would outlive the user simply deleting the portable EXE. The
/// value stores an absolute path, which stops matching as soon as the EXE is moved or renamed, so
/// <see cref="SyncRegisteredPath"/> repairs it on every normal launch.
/// </remarks>
internal static class StartupRegistration
{
    /// <summary>Marks the launch Windows performs at sign-in, which comes up hidden in the tray.</summary>
    internal const string AutostartArgument = "--autostart";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AiTokenMonitor";

    /// <summary>
    /// False only when the process has no resolvable path on disk, in which case there is nothing
    /// meaningful to register. A published single-file host always has one.
    /// </summary>
    public static bool IsSupported => !string.IsNullOrWhiteSpace(Environment.ProcessPath);

    /// <summary>
    /// True while the Run value exists, regardless of which path it points at — a stale path is a
    /// registration to repair, not a registration the user turned off.
    /// </summary>
    public static bool IsEnabled() => ReadCommand() is not null;

    /// <summary>Writes or removes the Run value. False means the registry refused the change.</summary>
    public static bool TrySetEnabled(bool enabled)
    {
        var command = BuildCommand(Environment.ProcessPath);
        if (enabled && command is null)
        {
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                return false;
            }

            if (enabled)
            {
                key.SetValue(ValueName, command!, RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception exception) when (
            exception is SecurityException or UnauthorizedAccessException or IOException)
        {
            DiagnosticsLog.Write(
                "StartupRegistration",
                $"{(enabled ? "enable" : "disable")} failed: {exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// Repairs the stored command after the portable EXE was moved or renamed. It never creates the
    /// value, so a user who never opted in stays unregistered.
    /// </summary>
    /// <remarks>
    /// Only a normally launched process may call this. <c>--apply-update</c> runs from the staging
    /// copy under <c>%LOCALAPPDATA%</c>, and registering that path would aim auto-start at a file
    /// deleted moments later; <see cref="App"/> shuts that helper down before reaching this call.
    /// The update itself replaces the EXE in place, so the path survives it unchanged.
    /// </remarks>
    public static void SyncRegisteredPath()
    {
        var expected = BuildCommand(Environment.ProcessPath);
        if (expected is null ||
            ReadCommand() is not { } stored ||
            string.Equals(stored, expected, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        TrySetEnabled(true);
    }

    internal static bool IsAutostartLaunch(IEnumerable<string> args) =>
        args.Any(argument => string.Equals(argument, AutostartArgument, StringComparison.OrdinalIgnoreCase));

    /// <summary>The exact Run value an EXE at <paramref name="executablePath"/> should hold.</summary>
    internal static string? BuildCommand(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        try
        {
            // Quoted because the portable EXE is routinely dropped on a desktop path containing
            // spaces, and Windows splits an unquoted Run value at the first one.
            return $"\"{Path.GetFullPath(executablePath)}\" {AutostartArgument}";
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string? ReadCommand()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var value = key?.GetValue(ValueName) as string;
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (Exception exception) when (
            exception is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
