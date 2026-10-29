using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MovieSift.Application.Updates;
using MovieSift.Infrastructure.Files;
using MovieSift.Infrastructure.Persistence;
using MovieSift.Infrastructure.Updates;
using Xunit;

namespace MovieSift.Infrastructure.Tests;

/// <summary>Checks the actual updater filesystem boundary, including preserved data and rollback after a failed rename.</summary>
public sealed class PortableUpdateInstallerTests(ITestOutputHelper output)
{
    /// <summary>A receipt written between the initial check and confirmed termination must be evaluated before rollback.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StartupReceiptCreatedAtTerminationIsReadAgain(bool validReceipt)
    {
        using var workspace = new TestWorkspace();
        var readyPath = Path.Combine(workspace.Path, "ready.txt");
        var token = Guid.NewGuid().ToString("N");
        var terminationChecks = 0;

        bool ConfirmTermination()
        {
            terminationChecks++;
            Assert.False(File.Exists(readyPath));
            File.WriteAllText(readyPath, validReceipt ? token : "unrelated-token");
            return true;
        }

        var started = await PortableUpdateInstaller.ObserveStartupAsync(ConfirmTermination, workspace.Path, token,
            TestContext.Current.CancellationToken);
        Assert.Equal(validReceipt, started);
        Assert.Equal(1, terminationChecks);
    }

    [Theory]
    [InlineData("MovieSift-win-x64/../outside.txt")]
    [InlineData("MovieSift-win-x64/C:/outside.txt")]
    [InlineData("/MovieSift-win-x64/outside.txt")]
    [InlineData("MovieSift-win-x64/folder\\outside.txt")]
    public async Task ArchiveTraversalIsRejectedWithoutWritingOutsideStaging(string name)
    {
        using var workspace = new TestWorkspace();
        var archive = Path.Combine(workspace.Path, "test.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            await using var output = zip.CreateEntry(name).Open();
            await output.WriteAsync(new byte[] { 1 }, TestContext.Current.CancellationToken);
        }
        var staging = Path.Combine(workspace.Path, "stage");
        Directory.CreateDirectory(staging);
        await Assert.ThrowsAsync<UpdateException>(() => UpdatePackage.ExtractAsync(archive, staging, "win-x64", TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(staging, "*", SearchOption.AllDirectories));
        Assert.False(File.Exists(Path.Combine(workspace.Path, "outside.txt")));
    }

    [Fact]
    public async Task InternalLinuxLibraryAliasBecomesOrdinaryVerifiedBytes()
    {
        using var workspace = new TestWorkspace();
        var archive = Path.Combine(workspace.Path, "test.tar.gz");
        await using (var file = File.Create(archive))
        await using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        await using (var tar = new TarWriter(gzip))
        {
            var payload = new PaxTarEntry(TarEntryType.RegularFile, "MovieSift-linux-x64/lib/library.so.1")
            {
                DataStream = new MemoryStream([1, 2, 3])
            };
            await tar.WriteEntryAsync(payload, TestContext.Current.CancellationToken);
            await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.SymbolicLink, "MovieSift-linux-x64/lib/library.so")
            {
                LinkName = "library.so.1"
            }, TestContext.Current.CancellationToken);
        }
        var destination = Path.Combine(workspace.Path, "stage");
        Directory.CreateDirectory(destination);
        await UpdatePackage.ExtractAsync(archive, destination, "linux-x64", TestContext.Current.CancellationToken);
        var alias = Path.Combine(destination, "lib", "library.so");
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(alias, TestContext.Current.CancellationToken));
        Assert.False(File.GetAttributes(alias).HasFlag(FileAttributes.ReparsePoint));
    }

    [Fact]
    public async Task SuccessfulDirectorySwitchKeepsSettingsAndUnknownUserFiles()
    {
        using var fixture = new InstallationFixture();
        await fixture.PrepareAsync();
        await PortableUpdateInstaller.ApplyAsync(fixture.Session, fixture.Plan!, Directory.Move, TestContext.Current.CancellationToken);
        Assert.Equal("new version", await File.ReadAllTextAsync(Path.Combine(fixture.App, "version.txt"), TestContext.Current.CancellationToken));
        Assert.Equal("personal preferences", await File.ReadAllTextAsync(Path.Combine(fixture.App, "data", "settings.json"), TestContext.Current.CancellationToken));
        Assert.Equal("user file", await File.ReadAllTextAsync(Path.Combine(fixture.App, "personal.txt"), TestContext.Current.CancellationToken));
        Assert.Equal("old version", await File.ReadAllTextAsync(Path.Combine(fixture.Session, "previous", "version.txt"), TestContext.Current.CancellationToken));
    }

    /// <summary>A nested payload still replaces the whole installation while preserving data beside the root launcher.</summary>
    [Fact]
    public async Task NestedPayloadSwitchPreservesRootDataAndRecognizesItsActualAssembly()
    {
        using var fixture = new InstallationFixture();
        await fixture.PrepareNestedPayloadAsync();
        await PortableUpdateInstaller.ApplyAsync(fixture.Session, fixture.Plan!, Directory.Move, TestContext.Current.CancellationToken);
        Assert.False(File.Exists(Path.Combine(fixture.App, "MovieSift.dll")));
        Assert.True(File.Exists(Path.Combine(fixture.App, "app", "MovieSift.dll")));
        Assert.False(Directory.Exists(Path.Combine(fixture.App, "app", "data")));
        Assert.Equal("personal preferences", await File.ReadAllTextAsync(Path.Combine(fixture.App, "data", "settings.json"), TestContext.Current.CancellationToken));
        Assert.Equal("user file", await File.ReadAllTextAsync(Path.Combine(fixture.App, "personal.txt"), TestContext.Current.CancellationToken));
        Assert.Equal(fixture.App, PortableLayout.ApplicationRootFromBaseDirectory(Path.Combine(fixture.App, "app")));
        Assert.Equal("old version", await File.ReadAllTextAsync(Path.Combine(fixture.Session, "previous", "version.txt"), TestContext.Current.CancellationToken));
    }

    /// <summary>Neither a misplaced root DLL nor an unrecognized marker can substitute for the nested package contract.</summary>
    [Theory]
    [InlineData("misplaced-assembly")]
    [InlineData("unknown-marker")]
    public async Task InvalidNestedLayoutCannotMoveTheOriginalInstallation(string mutation)
    {
        using var fixture = new InstallationFixture();
        await fixture.PrepareNestedPayloadAsync();
        var payload = Path.Combine(fixture.Session, "payload");

        if (mutation == "misplaced-assembly")
        {
            File.Move(Path.Combine(payload, "app", "MovieSift.dll"), Path.Combine(payload, "MovieSift.dll"));
        }

        if (mutation == "unknown-marker")
        {
            await File.WriteAllTextAsync(Path.Combine(payload, "portable.flag"), "MovieSift portable data policy version 99", TestContext.Current.CancellationToken);
        }

        await fixture.RefreshPayloadManifestAsync();
        await Assert.ThrowsAsync<UpdateException>(() => PortableUpdateInstaller.ApplyAsync(fixture.Session, fixture.Plan!, Directory.Move, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(Path.Combine(fixture.Session, "previous")));
        Assert.Equal("old version", await File.ReadAllTextAsync(Path.Combine(fixture.App, "version.txt"), TestContext.Current.CancellationToken));
        Assert.Equal("personal preferences", await File.ReadAllTextAsync(Path.Combine(fixture.App, "data", "settings.json"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FailedNewTreeRenameRestoresOldTreeAndDoesNotLoseUserData()
    {
        using var fixture = new InstallationFixture();
        await fixture.PrepareAsync();
        var calls = 0;
        void ControlledMove(string source, string destination)
        {
            if (++calls == 2)
            {
                throw new IOException("controlled new-tree move failure");
            }
            Directory.Move(source, destination);
        }
        var error = await Assert.ThrowsAsync<IOException>(() => PortableUpdateInstaller.ApplyAsync(
            fixture.Session, fixture.Plan!, ControlledMove, TestContext.Current.CancellationToken));
        Assert.Contains("controlled", error.Message);
        Assert.Equal(3, calls);
        Assert.Equal("old version", await File.ReadAllTextAsync(Path.Combine(fixture.App, "version.txt"), TestContext.Current.CancellationToken));
        Assert.Equal("personal preferences", await File.ReadAllTextAsync(Path.Combine(fixture.App, "data", "settings.json"), TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(Path.Combine(fixture.Session, "previous")));
    }

    [Fact]
    public async Task ChangedStagedPayloadCannotReachDirectorySwitch()
    {
        using var fixture = new InstallationFixture();
        await fixture.PrepareAsync();
        await File.WriteAllTextAsync(Path.Combine(fixture.Session, "payload", "version.txt"), "tampered", TestContext.Current.CancellationToken);
        var moved = false;
        var error = await Assert.ThrowsAsync<UpdateException>(() => PortableUpdateInstaller.ApplyAsync(fixture.Session, fixture.Plan!,
            (_, _) => moved = true, TestContext.Current.CancellationToken));
        Assert.Equal(UpdateErrorCode.Integrity, error.Code);
        Assert.False(moved);
        Assert.Equal("old version", await File.ReadAllTextAsync(Path.Combine(fixture.App, "version.txt"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UserFileCollisionFailsBeforeReplacingAnything()
    {
        using var fixture = new InstallationFixture();
        await fixture.PrepareAsync();
        await File.WriteAllTextAsync(Path.Combine(fixture.App, "LICENSE"), "personal unexpected file", TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<UpdateException>(() => PortableUpdateInstaller.ApplyAsync(fixture.Session, fixture.Plan!,
            Directory.Move, TestContext.Current.CancellationToken));
        Assert.Equal(UpdateErrorCode.UnsupportedInstallation, error.Code);
        Assert.Equal("personal unexpected file", await File.ReadAllTextAsync(Path.Combine(fixture.App, "LICENSE"), TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(Path.Combine(fixture.Session, "previous")));
    }

    [Fact]
    public void EveryActiveInstancePreventsInstallerAndExclusiveInstallerPreventsNewStartup()
    {
        using var workspace = new TestWorkspace();
        var app = Path.Combine(workspace.Path, "app");
        Directory.CreateDirectory(app);
        using (var first = UpdateApplicationLease.Acquire(app))
        {
            Assert.True(first.CanInstallUpdates);
            using (var second = UpdateApplicationLease.Acquire(app))
            {
                Assert.True(second.CanInstallUpdates);
                Assert.Throws<PortableRecordLockException>(() => UpdateApplicationLease.AcquireInstaller(app));
            }
            Assert.Throws<PortableRecordLockException>(() => UpdateApplicationLease.AcquireInstaller(app));
        }
        using (UpdateApplicationLease.AcquireInstaller(app))
        {
            Assert.Equal(UpdateErrorCode.Busy, Assert.Throws<UpdateException>(() => UpdateApplicationLease.Acquire(app)).Code);
        }
        using var after = UpdateApplicationLease.Acquire(app);
        Assert.True(after.CanInstallUpdates);
    }

    [Fact]
    public async Task PreferencesDefaultOnAndNeverOverwriteMalformedRecord()
    {
        using var workspace = new TestWorkspace();
        var store = new PortableUpdatePreferencesStore(workspace.Path);
        Assert.True(await store.LoadAsync(TestContext.Current.CancellationToken));
        await store.SaveAsync(false, TestContext.Current.CancellationToken);
        Assert.False(await store.LoadAsync(TestContext.Current.CancellationToken));
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "updates.json"), "broken record", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<UpdateException>(() => store.LoadAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<UpdateException>(() => store.SaveAsync(true, TestContext.Current.CancellationToken));
        Assert.Equal("broken record", await File.ReadAllTextAsync(Path.Combine(workspace.Path, "updates.json"), TestContext.Current.CancellationToken));
    }

    /// <summary>Uses real apphost processes to prove parent exit precedes mutation and startup failure restores the old tree.</summary>
    [Theory]
    [InlineData("acknowledge", 0, "new version")]
    [InlineData("fail", 1, "old version")]
    public async Task ExternalHelperWaitsForActualParentAndHonorsRestartOutcome(string restart, int expectedExit, string expectedVersion)
    {
        using var fixture = new InstallationFixture();
        await fixture.PrepareAsync();
        await fixture.PrepareProcessPayloadAsync();
        using var parent = StartWorker("update-parent", fixture.App);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        Assert.Equal("UPDATE_PARENT_READY", await parent.StandardOutput.ReadLineAsync(timeout.Token));
        var plan = fixture.Plan! with { ParentProcessId = parent.Id, ParentStartedUtcTicks = parent.StartTime.ToUniversalTime().Ticks };
        await UpdateInstallPlan.WriteAsync(fixture.Session, plan, timeout.Token);
        var planPath = Path.Combine(fixture.Session, UpdateInstallPlan.FileName);
        var planHash = await UpdatePackage.HashAsync(planPath, timeout.Token);
        var helperInfo = WorkerStart("update-installer", planPath, planHash);
        helperInfo.Environment["MOVIESIFT_TEST_UPDATE_RESTART"] = restart;
        using var helper = Process.Start(helperInfo)!;
        try
        {
            Assert.False(parent.HasExited);
            Assert.Equal("old version", await File.ReadAllTextAsync(Path.Combine(fixture.App, "version.txt"), timeout.Token));
            await parent.StandardInput.WriteLineAsync("exit");
            await parent.StandardInput.FlushAsync(timeout.Token);
            await parent.WaitForExitAsync(timeout.Token);
            await helper.WaitForExitAsync(timeout.Token);
            var report = await File.ReadAllTextAsync(Path.Combine(fixture.Session, "UPDATE-RESULT.txt"), timeout.Token);
            output.WriteLine($"Updater result: {report}");
            Assert.True(helper.ExitCode == expectedExit, $"Helper exit {helper.ExitCode}; {report}");
            Assert.Equal(expectedVersion, await File.ReadAllTextAsync(Path.Combine(fixture.App, "version.txt"), timeout.Token));
            Assert.Equal("personal preferences", await File.ReadAllTextAsync(Path.Combine(fixture.App, "data", "settings.json"), timeout.Token));
            if (expectedExit == 0)
            {
                Assert.Contains("startup confirmed", report);
                Assert.True(File.Exists(Path.Combine(fixture.Session, "ready.txt")));
            }
            else
            {
                Assert.True(Directory.Exists(Path.Combine(fixture.Session, "failed-update")));
            }
        }
        finally
        {
            foreach (var process in new[] { parent, helper })
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(TestContext.Current.CancellationToken);
                }
            }
        }
    }

    private static Process StartWorker(params string[] arguments) => Process.Start(WorkerStart(arguments))!;

    private static ProcessStartInfo WorkerStart(params string[] arguments)
    {
        var runtime = new DirectoryInfo(Path.GetDirectoryName(typeof(object).Assembly.Location)!);
        var dotnetDirectory = runtime.Parent!.Parent!.Parent!.FullName;
        var result = new ProcessStartInfo(Path.Combine(dotnetDirectory, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"))
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        result.Environment["DOTNET_ROOT"] = dotnetDirectory;
        result.ArgumentList.Add(Path.Combine(WorkerDirectory(), "MovieSift.ProcessTestWorker.dll"));
        foreach (var argument in arguments)
        {
            result.ArgumentList.Add(argument);
        }
        return result;
    }

    private static string WorkerDirectory() => Path.Combine(MediaTestEnvironment.RepositoryDirectory, "tests", "MovieSift.ProcessTestWorker",
        "bin", new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name, "net10.0");

    internal sealed class InstallationFixture : IDisposable
    {
        private readonly TestWorkspace workspace = new();
        internal string App => Path.Combine(workspace.Path, "app Кириллица");
        internal string Session => Path.Combine(workspace.Path, ".moviesift-update-" + new string('a', 32));
        internal UpdateInstallPlan? Plan { get; private set; }

        internal async Task PrepareAsync()
        {
            Directory.CreateDirectory(App);
            Directory.CreateDirectory(Path.Combine(App, "data"));
            await File.WriteAllTextAsync(Path.Combine(App, "version.txt"), "old version", TestContext.Current.CancellationToken);
            await WriteManifestAsync(App);
            await File.WriteAllTextAsync(Path.Combine(App, "data", "settings.json"), "personal preferences", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(App, "personal.txt"), "user file", TestContext.Current.CancellationToken);
            var payload = Path.Combine(Session, "payload");
            Directory.CreateDirectory(payload);
            var runtime = OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";
            var executable = OperatingSystem.IsWindows() ? "MovieSift.exe" : "MovieSift";
            var updater = OperatingSystem.IsWindows() ? "MovieSift.Updater.exe" : "MovieSift.Updater";
            foreach (var name in new[] { executable, updater, "MovieSift.Updater.dll", "MovieSift.Updater.deps.json", "MovieSift.Updater.runtimeconfig.json", "LICENSE", "portable.flag" })
            {
                await File.WriteAllTextAsync(Path.Combine(payload, name), "fixture", TestContext.Current.CancellationToken);
            }
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(Path.Combine(payload, executable), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            var assembly = new PersistedAssemblyBuilder(new AssemblyName("MovieSift") { Version = new(1, 1, 0, 0) }, typeof(object).Assembly);
            assembly.DefineDynamicModule("MovieSift");
            assembly.Save(Path.Combine(payload, "MovieSift.dll"));
            await File.WriteAllTextAsync(Path.Combine(payload, "version.txt"), "new version", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(payload, "build-provenance.json"), JsonSerializer.Serialize(new { runtimeIdentifier = runtime }), TestContext.Current.CancellationToken);
            await WriteManifestAsync(payload);
            Plan = new(1, new string('b', 32), App, runtime, "1.1.0",
                await UpdatePackage.HashAsync(Path.Combine(payload, "SHA256SUMS"), TestContext.Current.CancellationToken), 123, 456);
        }

        /// <summary>Uses the controlled worker apphost as a restart target, without publishing test binaries.</summary>
        internal async Task PrepareProcessPayloadAsync()
        {
            var payload = Path.Combine(Session, "payload");
            foreach (var file in Directory.GetFiles(WorkerDirectory()))
            {
                File.Copy(file, Path.Combine(payload, Path.GetFileName(file)), overwrite: true);
            }
            var worker = OperatingSystem.IsWindows() ? "MovieSift.ProcessTestWorker.exe" : "MovieSift.ProcessTestWorker";
            var application = OperatingSystem.IsWindows() ? "MovieSift.exe" : "MovieSift";
            File.Copy(Path.Combine(WorkerDirectory(), worker), Path.Combine(payload, application), overwrite: true);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(Path.Combine(payload, application), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            await WriteManifestAsync(payload);
            Plan = Plan! with { ManifestSha256 = await UpdatePackage.HashAsync(Path.Combine(payload, "SHA256SUMS"), TestContext.Current.CancellationToken) };
        }

        /// <summary>Moves only fixture-owned runtime files; production layout does not ship compatibility DLLs in its root.</summary>
        internal async Task PrepareNestedPayloadAsync()
        {
            await PrepareAsync();
            var payload = Path.Combine(Session, "payload");
            var nested = Path.Combine(payload, PortableLayout.PayloadDirectoryName);
            Directory.CreateDirectory(nested);
            var executable = OperatingSystem.IsWindows() ? "MovieSift.exe" : "MovieSift";
            var rootFiles = new HashSet<string>(StringComparer.Ordinal)
            {
                executable, "portable.flag", "LICENSE", "SHA256SUMS", "build-provenance.json", "version.txt"
            };
            foreach (var file in Directory.GetFiles(payload))
            {
                if (rootFiles.Contains(Path.GetFileName(file)))
                {
                    continue;
                }

                File.Move(file, Path.Combine(nested, Path.GetFileName(file)));
            }
            await File.WriteAllTextAsync(Path.Combine(payload, "portable.flag"), PortableLayout.NestedMarker, TestContext.Current.CancellationToken);
            await RefreshPayloadManifestAsync();
        }

        /// <summary>Binds controlled payload edits to a fresh transaction without relaxing production manifest validation.</summary>
        internal async Task RefreshPayloadManifestAsync()
        {
            var payload = Path.Combine(Session, "payload");
            await WriteManifestAsync(payload);
            Plan = Plan! with { ManifestSha256 = await UpdatePackage.HashAsync(Path.Combine(payload, "SHA256SUMS"), TestContext.Current.CancellationToken) };
        }

        public void Dispose() => workspace.Dispose();

        private static async Task WriteManifestAsync(string directory)
        {
            var lines = new List<string>();
            foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                if (Path.GetFileName(file) == "SHA256SUMS")
                {
                    continue;
                }
                var digest = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken))).ToLowerInvariant();
                lines.Add($"{digest}  {Path.GetRelativePath(directory, file).Replace(Path.DirectorySeparatorChar, '/')}");
            }
            await File.WriteAllTextAsync(Path.Combine(directory, "SHA256SUMS"), string.Join('\n', lines) + "\n", new UTF8Encoding(false), TestContext.Current.CancellationToken);
        }
    }
}
