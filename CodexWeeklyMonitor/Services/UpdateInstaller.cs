using System.Diagnostics;
using System.Security;

namespace CodexWeeklyMonitor.Services;

internal static class UpdateInstaller
{
    internal const string ApplyArgument = "--apply-update";
    internal const string CleanupArgument = "--cleanup-update";

    private static readonly string UpdateRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AiTokenMonitor",
        "updates");

    internal static string GetUpdateDirectory(string tagName)
    {
        var safeTag = string.Concat(tagName.Where(character =>
            char.IsLetterOrDigit(character) || character is '.' or '-' or '_'));
        if (string.IsNullOrWhiteSpace(safeTag))
        {
            safeTag = "latest";
        }

        return Path.Combine(UpdateRoot, safeTag);
    }

    internal static bool TryApplyFromArguments(string[] args)
    {
        var index = Array.IndexOf(args, ApplyArgument);
        if (index < 0)
        {
            return false;
        }

        if (index + 3 >= args.Length ||
            !int.TryParse(args[index + 1], out var parentProcessId) ||
            parentProcessId <= 0 ||
            !GitHubUpdateService.TryParseSha256($"sha256:{args[index + 3]}", out var expectedSha256))
        {
            throw new UpdateServiceException("update.installFailed");
        }

        Apply(parentProcessId, args[index + 2], expectedSha256);
        return true;
    }

    /// <summary>
    /// Probes whether the folder holding <paramref name="executablePath"/> will accept a new file
    /// from this user right now, by creating and deleting one.
    /// </summary>
    /// <remarks>
    /// Asked before the download starts, because the answer is usually no for a reason no amount of
    /// retrying fixes — Controlled folder access refusing writes to the Desktop, an EXE parked under
    /// Program Files, or security software vetoing writes to a .exe. Finding that out after pulling
    /// the whole release down wastes a 165 MB transfer to end at the same error.
    /// </remarks>
    internal static bool IsTargetWritable(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        string probe;
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(executablePath));
            if (string.IsNullOrWhiteSpace(directory))
            {
                return false;
            }

            // Named like the real staging file so a folder rule that singles out executables is
            // exercised the same way the install itself would exercise it.
            probe = Path.Combine(
                directory,
                $".{Path.GetFileName(executablePath)}.{Guid.NewGuid():N}.probe");
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        try
        {
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0);
            }

            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
        finally
        {
            TryDelete(probe);
        }
    }

    /// <summary>
    /// The EXE this helper was asked to overwrite, so a failed install can put it back on screen.
    /// </summary>
    internal static string? GetApplyTarget(string[] args)
    {
        var index = Array.IndexOf(args, ApplyArgument);
        if (index < 0 || index + 2 >= args.Length)
        {
            return null;
        }

        try
        {
            var target = Path.GetFullPath(args[index + 2]);
            return target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(target)
                ? target
                : null;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// Relaunches the version that is still installed after an install failed part way.
    /// </summary>
    /// <remarks>
    /// The parent exited before this helper started work, so without this the user clicks update,
    /// sees an error, and is left with nothing running at all. The replace is staged through a
    /// temporary file and moved into place atomically, so a failure leaves the old EXE intact.
    /// </remarks>
    internal static void TryRelaunchAfterFailedApply(string[] args)
    {
        if (GetApplyTarget(args) is not { } target)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = false });
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // Nothing further to try; the user still has the intact EXE to start by hand.
        }
    }

    internal static string? GetCleanupPath(string[] args)
    {
        var index = Array.IndexOf(args, CleanupArgument);
        if (index < 0 || index + 1 >= args.Length)
        {
            return null;
        }

        var path = Path.GetFullPath(args[index + 1]);
        return IsInsideUpdateRoot(path) ? path : null;
    }

    internal static void ScheduleCleanup(string? path)
    {
        if (path is null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    if (!File.Exists(path))
                    {
                        return;
                    }

                    File.Delete(path);
                    TryDeleteEmptyParents(Path.GetDirectoryName(path));
                    return;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    await Task.Delay(250).ConfigureAwait(false);
                }
            }
        });
    }

    private static void Apply(int parentProcessId, string targetExecutable, string expectedSha256)
    {
        var sourceExecutable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(sourceExecutable) || !File.Exists(sourceExecutable))
        {
            throw new UpdateServiceException("update.installFailed");
        }

        if (!GitHubUpdateService.HasExpectedSha256Async(sourceExecutable, expectedSha256)
                .GetAwaiter().GetResult())
        {
            throw new UpdateServiceException("update.integrityFailed");
        }

        var target = Path.GetFullPath(targetExecutable);
        if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateServiceException("update.installFailed");
        }

        WaitForParentExit(parentProcessId);

        ReplaceExecutable(sourceExecutable, target, expectedSha256);
        Thread.Sleep(350);

        var startInfo = new ProcessStartInfo
        {
            FileName = target,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(CleanupArgument);
        startInfo.ArgumentList.Add(sourceExecutable);
        Process.Start(startInfo);
    }

    internal static void ReplaceExecutable(string sourceExecutable, string target, string expectedSha256)
    {
        var targetDirectory = Path.GetDirectoryName(target)
            ?? throw new UpdateServiceException("update.installFailed");
        var staged = Path.Combine(targetDirectory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.update");
        try
        {
            Directory.CreateDirectory(targetDirectory);
            File.Copy(sourceExecutable, staged, overwrite: false);
            if (!GitHubUpdateService.HasExpectedSha256Async(staged, expectedSha256)
                    .GetAwaiter().GetResult())
            {
                throw new UpdateServiceException("update.integrityFailed");
            }

            ReplaceWithRetry(staged, target);
        }
        catch (UnauthorizedAccessException exception)
        {
            // Raw, this surfaced as "Access to the path is denied." in an otherwise localized UI,
            // with nothing to act on. The cause is always the same shape — the folder the EXE lives
            // in will not take a write — so say that and what usually causes it.
            throw new UpdateServiceException("update.notWritable", exception);
        }
        finally
        {
            TryDelete(staged);
        }
    }

    private static void WaitForParentExit(int parentProcessId)
    {
        try
        {
            using var process = Process.GetProcessById(parentProcessId);
            if (!process.WaitForExit((int)TimeSpan.FromMinutes(2).TotalMilliseconds))
            {
                throw new UpdateServiceException("update.installFailed");
            }
        }
        catch (ArgumentException)
        {
            // The parent already exited between launching this helper and resolving its PID.
        }
    }

    private static void ReplaceWithRetry(string staged, string target)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                File.Move(staged, target, overwrite: true);
                return;
            }
            catch (Exception exception) when (
                attempt < 19 && (exception is IOException or UnauthorizedAccessException))
            {
                Thread.Sleep(250);
            }
        }

        throw new UpdateServiceException("update.installFailed");
    }

    private static bool IsInsideUpdateRoot(string path)
    {
        var root = Path.GetFullPath(UpdateRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
               path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeleteEmptyParents(string? directory)
    {
        try
        {
            if (directory is not null && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Cleanup is best effort and must never block a successfully installed update.
        }
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
            // A later update can safely overwrite this uniquely named staging file.
        }
    }
}
