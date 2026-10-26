using System.Diagnostics;
using System.Text.Json;
using MovieSift.Application.Updates;
using MovieSift.Infrastructure.Files;

namespace MovieSift.Infrastructure.Updates;

/// <summary>Applies one verified directory transaction after the owning app and all cooperating instances exit.</summary>
public static class PortableUpdateInstaller
{
    private const string SessionEnvironment = "MOVIESIFT_UPDATE_SESSION";
    private const string TokenEnvironment = "MOVIESIFT_UPDATE_TOKEN";
    private const string ReadyFile = "ready.txt";

    /// <summary>Checks a produced archive through the installer's parser without touching an installation.</summary>
    public static async Task<string> VerifyArchiveAsync(string archivePath, string runtimeIdentifier, AppVersion version,
        CancellationToken cancellationToken = default)
    {
        archivePath = Path.GetFullPath(archivePath);
        using (var archive = FileSourceAccess.OpenRegularReadStream(archivePath))
        {
            if (archive.Length is <= 0 or > UpdatePackage.MaximumArchiveBytes)
            {
                throw new UpdateException(UpdateErrorCode.InvalidPackage, "The update archive exceeds its supported size bounds.");
            }
        }
        var temporary = Path.Combine(Path.GetTempPath(), $"moviesift-update-check-{Guid.NewGuid():N}");
        using var ownership = ReservationDirectory.CreateNew(temporary);
        Exception? primaryFailure = null;
        try
        {
            await UpdatePackage.ExtractAsync(archivePath, temporary, runtimeIdentifier, cancellationToken);
            return await UpdatePackage.VerifyAsync(temporary, runtimeIdentifier, version, null, cancellationToken);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            throw;
        }
        finally
        {
            try
            {
                ownership.ValidateCurrent(temporary);
                _ = UpdatePackage.EnumerateRegularFiles(temporary).Count();
                Directory.Delete(temporary, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Application.OperationFailureException or UpdateException)
            {
                throw new UpdateException(UpdateErrorCode.Storage,
                    $"Update archive verification cleanup failed; temporary storage was preserved at {temporary}: {exception.Message}",
                    primaryFailure ?? exception);
            }
        }
    }

    /// <summary>Runs only from the external helper; failures retain the old tree and an explicit recovery report.</summary>
    public static async Task<int> RunAsync(string planPath, string expectedSha256, CancellationToken cancellationToken = default)
    {
        var session = Path.GetDirectoryName(Path.GetFullPath(planPath))!;
        UpdateInstallPlan? plan = null;
        Process? restarted = null;
        var parentExited = false;
        var installed = false;
        try
        {
            plan = await ReadPlanAsync(planPath, expectedSha256, cancellationToken);
            await WaitForParentAsync(plan, cancellationToken);
            parentExited = true;
            using (UpdateApplicationLease.AcquireInstaller(plan.ApplicationDirectory))
            {
                await ApplyAsync(session, plan, Directory.Move, cancellationToken);
                installed = true;
            }
            restarted = StartApplication(plan.ApplicationDirectory, session, plan.Token);
            var started = await WaitForStartupAsync(restarted, session, plan.Token, cancellationToken);
            if (!started && restarted.HasExited)
            {
                using (UpdateApplicationLease.AcquireInstaller(plan.ApplicationDirectory))
                {
                    Rollback(session, plan.ApplicationDirectory, Directory.Move);
                    installed = false;
                }
                using var previous = StartApplication(plan.ApplicationDirectory, null, null);
                WriteReport(session, "The new application exited before confirming startup. The previous version was restored.");
                return 1;
            }
            WriteReport(session, started
                ? "Update installed and startup confirmed. The previous installation remains in the previous folder for recovery."
                : "Update installed, but startup was not confirmed. The running app was not terminated. The previous folder is retained.");
            return started ? 0 : 2;
        }
        catch (Exception exception)
        {
            if (plan is not null)
            {
                if (installed && (restarted is null || restarted.HasExited))
                {
                    try
                    {
                        using var ownership = UpdateApplicationLease.AcquireInstaller(plan.ApplicationDirectory);
                        Rollback(session, plan.ApplicationDirectory, Directory.Move);
                        installed = false;
                    }
                    catch (Exception rollbackException) when (rollbackException is IOException or UnauthorizedAccessException or Persistence.PortableRecordLockException)
                    {
                        WriteReport(session, $"Startup failed and rollback needs manual recovery: {rollbackException.Message}\n{exception.Message}");
                        return 1;
                    }
                }
                WriteReport(session, $"Update did not complete: {exception.Message}\nPrevious installation, if moved: {Path.Combine(session, "previous")}\nTarget: {plan.ApplicationDirectory}");
                // Application files remain runnable after preparation failures or a successful transaction rollback.
                if (parentExited && !installed && File.Exists(Path.Combine(plan.ApplicationDirectory, ExecutableName())))
                {
                    try
                    {
                        using var previous = StartApplication(plan.ApplicationDirectory, null, null);
                    }
                    catch (Exception launchException) when (launchException is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
                    {
                        WriteReport(session, $"The application could not be restarted: {launchException.Message}\n{exception.Message}");
                    }
                }
            }
            return 1;
        }
        finally
        {
            restarted?.Dispose();
        }
    }

    /// <summary>Confirms initialized desktop startup without trusting arbitrary receipt paths.</summary>
    public static void AcknowledgeStartup()
    {
        var session = Environment.GetEnvironmentVariable(SessionEnvironment);
        var token = Environment.GetEnvironmentVariable(TokenEnvironment);
        Environment.SetEnvironmentVariable(SessionEnvironment, null);
        Environment.SetEnvironmentVariable(TokenEnvironment, null);
        if (session is null || token is null || !Guid.TryParseExact(token, "N", out _))
        {
            return;
        }
        try
        {
            PathSafety.RejectReparsePoints(session);
            var app = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
            var bytes = UpdatePackage.ReadBoundedAsync(Path.Combine(session, UpdateInstallPlan.FileName), 16_384, CancellationToken.None).GetAwaiter().GetResult();
            var plan = JsonSerializer.Deserialize<UpdateInstallPlan>(bytes);
            if (plan is null || plan.Token != token || !SamePath(plan.ApplicationDirectory, app) ||
                !SamePath(Path.GetDirectoryName(session)!, Path.GetDirectoryName(app)!))
            {
                return;
            }
            var temporary = Path.Combine(session, $"ready-{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var writer = new StreamWriter(stream);
                writer.Write(token);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, Path.Combine(session, ReadyFile), overwrite: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or Application.OperationFailureException or UpdateException)
        {
            // Startup must remain possible even if an updater receipt cannot be written.
        }
    }

    /// <summary>Verifies the new tree and preserves user files before the short noncancellable directory switch.</summary>
    internal static async Task ApplyAsync(string session, UpdateInstallPlan plan, Action<string, string> moveDirectory,
        CancellationToken cancellationToken)
    {
        var target = plan.ApplicationDirectory;
        var payload = Path.Combine(session, "payload");
        var backup = Path.Combine(session, "previous");
        if (!AppVersion.TryParse(plan.Version, out var version) || Directory.Exists(backup))
        {
            throw new UpdateException(UpdateErrorCode.Installer, "The update transaction is invalid or has already started.");
        }
        PathSafety.RejectReparsePoints(target);
        PathSafety.RejectReparsePoints(session);
        await UpdatePackage.VerifyAsync(payload, plan.RuntimeIdentifier, version, plan.ManifestSha256, cancellationToken);
        await PreserveUserFilesAsync(target, payload, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        WriteReport(session, "Prepared; the existing installation has not yet been moved.");
        moveDirectory(target, backup);
        try
        {
            moveDirectory(payload, target);
        }
        catch (Exception exception)
        {
            try
            {
                moveDirectory(backup, target);
            }
            catch (Exception rollbackException)
            {
                throw new UpdateException(UpdateErrorCode.Installer,
                    $"Installation failed and automatic rollback could not complete. The intact previous installation is at {backup}. {rollbackException.Message}", exception);
            }
            throw;
        }
        WriteReport(session, "Installed; waiting for the new application to confirm startup.");
    }

    /// <summary>Copies data and unrecognized files without deleting the previous complete portable tree.</summary>
    private static async Task PreserveUserFilesAsync(string source, string destination, CancellationToken cancellationToken)
    {
        var managed = await UpdatePackage.ReadManifestAsync(source, cancellationToken);
        managed.TryAdd("SHA256SUMS", string.Empty);
        var rootPermissions = UpdateFilePermissions.Capture(source, directory: true);
        rootPermissions.PrepareDirectory(destination);
        var directories = new List<(UpdateFilePermissions Permissions, string Destination)> { (rootPermissions, destination) };
        var pending = new Stack<string>();
        pending.Push(source);
        var count = 0;
        while (pending.TryPop(out var current))
        {
            PathSafety.RejectReparsePoints(current);
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++count > 100_000)
                {
                    throw new UpdateException(UpdateErrorCode.UnsupportedInstallation, "Too many user files are inside the application directory.");
                }
                var relative = Path.GetRelativePath(source, entry).Replace(Path.DirectorySeparatorChar, '/');
                if (managed.ContainsKey(relative))
                {
                    // Existing shipped Linux library aliases may be symlinks; do not follow or modify them.
                    continue;
                }
                PathSafety.RejectReparsePoints(entry);
                var output = UpdatePackage.SafeRelativePath(destination, relative);
                if (Directory.Exists(entry))
                {
                    var permissions = UpdateFilePermissions.Capture(entry, directory: true);
                    permissions.PrepareDirectory(output);
                    directories.Add((permissions, output));
                    pending.Push(entry);
                    continue;
                }
                if (File.Exists(output))
                {
                    throw new UpdateException(UpdateErrorCode.UnsupportedInstallation,
                        $"A user file conflicts with the new package: {relative}. The existing installation was preserved.");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                var filePermissions = UpdateFilePermissions.Capture(entry, directory: false);
                await using var input = FileSourceAccess.OpenRegularReadStream(entry);
                await using (var copied = filePermissions.CreateFile(output))
                {
                    await input.CopyToAsync(copied, cancellationToken);
                    await copied.FlushAsync(cancellationToken);
                    copied.Flush(flushToDisk: true);
                }
                filePermissions.VerifyFile(output);
            }
        }
        // Children are complete before restrictive Unix parent modes are restored.
        foreach (var directory in Enumerable.Reverse(directories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            directory.Permissions.CompleteDirectory(directory.Destination);
        }
    }

    private static async Task<UpdateInstallPlan> ReadPlanAsync(string path, string expectedSha256, CancellationToken cancellationToken)
    {
        var bytes = await UpdatePackage.ReadBoundedAsync(path, 16_384, cancellationToken);
        var actualHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        if (!UpdatePackage.IsSha256(expectedSha256) ||
            !string.Equals(actualHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateException(UpdateErrorCode.Integrity, "The installation plan changed before the helper started.");
        }
        var plan = JsonSerializer.Deserialize<UpdateInstallPlan>(bytes) ?? throw new JsonException("The installation plan is empty.");
        var session = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(plan.ApplicationDirectory));
        var sessionName = Path.GetFileName(session);
        var runtime = OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";
        if (Path.GetFileName(path) != UpdateInstallPlan.FileName || plan.SchemaVersion != 1 ||
            !Path.IsPathFullyQualified(plan.ApplicationDirectory) ||
            !Guid.TryParseExact(plan.Token, "N", out _) || !UpdatePackage.IsSha256(plan.ManifestSha256) ||
            !AppVersion.TryParse(plan.Version, out _) || plan.RuntimeIdentifier != runtime ||
            plan.ParentProcessId <= 0 || plan.ParentStartedUtcTicks <= 0 ||
            !SamePath(target, plan.ApplicationDirectory) || !SamePath(Path.GetDirectoryName(target)!, Path.GetDirectoryName(session)!) ||
            !sessionName.StartsWith(".moviesift-update-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(sessionName[".moviesift-update-".Length..], "N", out _))
        {
            throw new UpdateException(UpdateErrorCode.Installer, "The installation plan contains invalid paths or identities.");
        }
        PathSafety.RejectReparsePoints(session);
        return plan;
    }

    private static async Task WaitForParentAsync(UpdateInstallPlan plan, CancellationToken cancellationToken)
    {
        Process parent;
        try
        {
            parent = Process.GetProcessById(plan.ParentProcessId);
        }
        catch (ArgumentException)
        {
            return;
        }
        using (parent)
        {
            if (parent.StartTime.ToUniversalTime().Ticks != plan.ParentStartedUtcTicks)
            {
                throw new UpdateException(UpdateErrorCode.Installer, "The original application process identity no longer matches.");
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            await parent.WaitForExitAsync(timeout.Token);
        }
    }

    private static Process StartApplication(string directory, string? session, string? token)
    {
        var info = new ProcessStartInfo(Path.Combine(directory, ExecutableName()))
        {
            WorkingDirectory = directory,
            UseShellExecute = false
        };
        if (session is not null && token is not null)
        {
            info.Environment[SessionEnvironment] = session;
            info.Environment[TokenEnvironment] = token;
        }
        else
        {
            info.Environment["MOVIESIFT_UPDATE_FAILED"] = "1";
        }
        return Process.Start(info) ?? throw new IOException("The application did not restart.");
    }

    private static async Task<bool> WaitForStartupAsync(Process process, string session, string token, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(60))
        {
            var path = Path.Combine(session, ReadyFile);
            if (File.Exists(path))
            {
                var bytes = await UpdatePackage.ReadBoundedAsync(path, 128, cancellationToken);
                return System.Text.Encoding.UTF8.GetString(bytes) == token;
            }
            if (process.HasExited)
            {
                return false;
            }
            await Task.Delay(100, cancellationToken);
        }
        return false;
    }

    private static void Rollback(string session, string target, Action<string, string> moveDirectory)
    {
        moveDirectory(target, Path.Combine(session, "failed-update"));
        moveDirectory(Path.Combine(session, "previous"), target);
    }

    private static string ExecutableName() => OperatingSystem.IsWindows() ? "MovieSift.exe" : "MovieSift";

    private static bool SamePath(string first, string second) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void WriteReport(string session, string text)
    {
        try
        {
            PathSafety.RejectReparsePoints(session);
            File.WriteAllText(Path.Combine(session, "UPDATE-RESULT.txt"), text);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Application.OperationFailureException)
        {
            // Reporting must not replace the original installation or rollback failure.
        }
    }
}
