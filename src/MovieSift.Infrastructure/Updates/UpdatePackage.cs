using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using MovieSift.Application.Updates;
using MovieSift.Infrastructure.Files;

namespace MovieSift.Infrastructure.Updates;

/// <summary>Extracts only bounded ordinary package entries and verifies the complete packaged manifest.</summary>
internal static class UpdatePackage
{
    internal const long MaximumArchiveBytes = 2L * 1024 * 1024 * 1024;
    private const long MaximumExpandedBytes = 8L * 1024 * 1024 * 1024;
    private const int MaximumEntries = 20_000;
    private const int MaximumManifestBytes = 4 * 1024 * 1024;

    /// <summary>Extracts into an empty owned directory without archive-controlled absolute paths or links.</summary>
    internal static async Task ExtractAsync(string archivePath, string destination, string runtimeIdentifier, CancellationToken cancellationToken)
    {
        var budget = new ExtractionBudget();
        var prefix = $"MovieSift-{runtimeIdentifier}/";
        if (runtimeIdentifier == "win-x64")
        {
            using var archive = ZipFile.OpenRead(archivePath);
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var kind = (entry.ExternalAttributes >> 16) & 0xF000;
                if (kind is not (0 or 0x8000 or 0x4000))
                {
                    throw Invalid("The update archive contains a non-regular entry.");
                }
                var directory = entry.FullName.EndsWith('/');
                var target = EntryPath(entry.FullName, prefix, destination, budget);
                if (directory)
                {
                    Directory.CreateDirectory(target);
                    continue;
                }
                await using var input = entry.Open();
                await WriteEntryAsync(input, entry.Length, target, budget, cancellationToken);
            }
        }
        else if (runtimeIdentifier == "linux-x64")
        {
            var aliases = new List<(string Destination, string Target)>();
            await using var file = FileSourceAccess.OpenRegularReadStream(archivePath);
            await using var gzip = new GZipStream(file, CompressionMode.Decompress);
            await using var archive = new TarReader(gzip);
            while (await archive.GetNextEntryAsync(cancellationToken: cancellationToken) is { } entry)
            {
                var target = EntryPath(entry.Name, prefix, destination, budget);
                if (entry.EntryType == TarEntryType.Directory)
                {
                    Directory.CreateDirectory(target);
                    continue;
                }
                if (entry.EntryType == TarEntryType.SymbolicLink)
                {
                    if (Path.IsPathRooted(entry.LinkName) || entry.LinkName.Contains('\\') || entry.LinkName.Contains(':'))
                    {
                        throw Invalid("The update archive contains an external library alias.");
                    }
                    var aliasTarget = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(target)!, entry.LinkName));
                    var relativeTarget = Path.GetRelativePath(destination, aliasTarget).Replace(Path.DirectorySeparatorChar, '/');
                    _ = SafeRelativePath(destination, relativeTarget);
                    aliases.Add((target, aliasTarget));
                    continue;
                }
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream is null)
                {
                    throw Invalid("The update archive contains a link or special file.");
                }
                await WriteEntryAsync(entry.DataStream, entry.Length, target, budget, cancellationToken);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(target, (entry.Mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) |
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                }
            }
            await MaterializeAliasesAsync(aliases, budget, cancellationToken);
        }
        else
        {
            throw Invalid("This runtime has no supported update package.");
        }
    }

    /// <summary>Checks every shipped file, the exact runtime and assembly version before a package can execute.</summary>
    internal static async Task<string> VerifyAsync(string directory, string runtimeIdentifier, AppVersion version,
        string? expectedManifestHash, CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(directory, "SHA256SUMS");
        var manifestHash = await HashAsync(manifestPath, cancellationToken);
        if (expectedManifestHash is not null && !manifestHash.Equals(expectedManifestHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateException(UpdateErrorCode.Integrity, "The staged update manifest changed after verification.");
        }
        var entries = await ReadManifestAsync(directory, cancellationToken);
        foreach (var entry in entries)
        {
            var actual = await HashAsync(Path.Combine(directory, entry.Key), cancellationToken);
            if (!actual.Equals(entry.Value, StringComparison.OrdinalIgnoreCase))
            {
                throw new UpdateException(UpdateErrorCode.Integrity, $"The update payload failed its checksum: {entry.Key}");
            }
        }
        foreach (var file in EnumerateRegularFiles(directory))
        {
            var relative = Path.GetRelativePath(directory, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative != "SHA256SUMS" && !entries.ContainsKey(relative))
            {
                throw Invalid("The package contains a file absent from its manifest.");
            }
        }
        var executable = runtimeIdentifier == "win-x64" ? "MovieSift.exe" : "MovieSift";
        var updater = runtimeIdentifier == "win-x64" ? "MovieSift.Updater.exe" : "MovieSift.Updater";
        foreach (var required in new[] { executable, updater, "MovieSift.dll", "MovieSift.Updater.dll", "MovieSift.Updater.deps.json",
            "MovieSift.Updater.runtimeconfig.json", "portable.flag", "LICENSE", "build-provenance.json" })
        {
            if (!entries.ContainsKey(required))
            {
                throw Invalid($"The update is missing a required package file: {required}");
            }
        }
        var assembly = AssemblyName.GetAssemblyName(Path.Combine(directory, "MovieSift.dll"));
        if (assembly.Name != "MovieSift" || assembly.Version is not { } actualVersion ||
            actualVersion.Major != version.Major || actualVersion.Minor != version.Minor || actualVersion.Build != version.Patch)
        {
            throw Invalid("The packaged application version does not match the offered release.");
        }
        var provenanceBytes = await ReadBoundedAsync(Path.Combine(directory, "build-provenance.json"), 65_536, cancellationToken);
        using var provenance = JsonDocument.Parse(provenanceBytes, new() { MaxDepth = 12 });
        if (!provenance.RootElement.TryGetProperty("runtimeIdentifier", out var runtime) || runtime.GetString() != runtimeIdentifier)
        {
            throw Invalid("The update package targets a different runtime.");
        }
        if (!OperatingSystem.IsWindows() && runtimeIdentifier == "linux-x64" &&
            (File.GetUnixFileMode(Path.Combine(directory, executable)) & UnixFileMode.UserExecute) == 0)
        {
            throw Invalid("The Linux application is not executable.");
        }
        return manifestHash;
    }

    /// <summary>Reads the manifest as data, rejecting aliases and duplicate entries before any file use.</summary>
    internal static async Task<Dictionary<string, string>> ReadManifestAsync(string directory, CancellationToken cancellationToken)
    {
        var bytes = await ReadBoundedAsync(Path.Combine(directory, "SHA256SUMS"), MaximumManifestBytes, cancellationToken);
        var result = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var line in System.Text.Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var text = line.TrimEnd('\r');
            if (text.Length < 67 || text[64..66] != "  " || !IsSha256(text[..64]))
            {
                throw Invalid("The package manifest contains a malformed entry.");
            }
            var relative = text[66..];
            _ = SafeRelativePath(directory, relative);
            if (relative == "SHA256SUMS" || relative.StartsWith("data/", StringComparison.OrdinalIgnoreCase) ||
                !result.TryAdd(relative, text[..64]) || result.Count > MaximumEntries)
            {
                throw Invalid("The package manifest contains a forbidden or duplicate entry.");
            }
        }
        if (result.Count == 0)
        {
            throw Invalid("The update manifest is empty.");
        }
        return result;
    }

    /// <summary>Traverses ordinary directories only, rejecting redirection before descent.</summary>
    internal static IEnumerable<string> EnumerateRegularFiles(string directory)
    {
        var pending = new Stack<string>();
        pending.Push(directory);
        var count = 0;
        while (pending.TryPop(out var current))
        {
            PathSafety.RejectReparsePoints(current);
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                if (++count > 100_000)
                {
                    throw Invalid("The portable installation contains too many entries for a bounded update.");
                }
                PathSafety.RejectReparsePoints(entry);
                if (Directory.Exists(entry))
                {
                    pending.Push(entry);
                }
                else
                {
                    using var file = FileSourceAccess.OpenRegularReadStream(entry);
                    yield return entry;
                }
            }
        }
    }

    /// <summary>Rejects cross-platform path ambiguity rather than normalizing attacker-controlled archive names.</summary>
    internal static string SafeRelativePath(string root, string relative)
    {
        if (relative.Length == 0 || relative.Length > 1024 || relative.Contains('\\') || relative.Contains(':') ||
            relative.Any(char.IsControl) || Path.IsPathRooted(relative) ||
            relative.Split('/').Any(part => part.Length == 0 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.') || IsReservedName(part)))
        {
            throw Invalid("The update contains an unsafe relative path.");
        }
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw Invalid("The update path escapes its staging directory.");
        }
        return full;
    }

    private static bool IsReservedName(string component)
    {
        var stem = component.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$" ||
            stem.Length == 4 && stem[3] is >= '1' and <= '9' && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal));
    }

    /// <summary>Hashes a verified ordinary file with bounded memory.</summary>
    internal static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = FileSourceAccess.OpenRegularReadStream(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    internal static bool IsSha256(string text) => text.Length == 64 && text.All(char.IsAsciiHexDigit);

    /// <summary>Bounds small untrusted package records before materializing them.</summary>
    internal static async Task<byte[]> ReadBoundedAsync(string path, int maximum, CancellationToken cancellationToken)
    {
        await using var stream = FileSourceAccess.OpenRegularReadStream(path);
        if (stream.Length > maximum)
        {
            throw Invalid("The package record exceeds its size limit.");
        }
        var bytes = new byte[maximum + 1];
        var count = await stream.ReadAtLeastAsync(bytes, maximum + 1, throwOnEndOfStream: false, cancellationToken);
        if (count > maximum)
        {
            throw Invalid("The package record exceeds its size limit.");
        }
        return bytes[..count];
    }

    private static string EntryPath(string name, string prefix, string root, ExtractionBudget budget)
    {
        if (++budget.Entries > MaximumEntries)
        {
            throw Invalid("The update archive exceeds its entry limit.");
        }
        if (name == prefix.TrimEnd('/'))
        {
            return root;
        }
        if (!name.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw Invalid("The update archive has an unexpected root or excessive entry count.");
        }
        var relative = name[prefix.Length..].TrimEnd('/');
        if (relative.Length == 0)
        {
            return root;
        }
        var target = SafeRelativePath(root, relative);
        if (!budget.Paths.Add(relative))
        {
            throw Invalid("The update archive contains duplicate paths.");
        }
        return target;
    }

    /// <summary>Turns only package-internal ordinary-file aliases into verified bytes, never creating filesystem links.</summary>
    private static async Task MaterializeAliasesAsync(List<(string Destination, string Target)> aliases,
        ExtractionBudget budget, CancellationToken cancellationToken)
    {
        for (var depth = 0; aliases.Count != 0 && depth < 32; depth++)
        {
            var progressed = false;
            foreach (var alias in aliases.ToArray())
            {
                if (!File.Exists(alias.Target))
                {
                    continue;
                }
                await using var input = FileSourceAccess.OpenRegularReadStream(alias.Target);
                await WriteEntryAsync(input, input.Length, alias.Destination, budget, cancellationToken);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(alias.Destination, File.GetUnixFileMode(alias.Target) & (UnixFileMode)0x1FF);
                }
                aliases.Remove(alias);
                progressed = true;
            }
            if (!progressed)
            {
                break;
            }
        }
        if (aliases.Count != 0)
        {
            throw Invalid("The update contains a missing, cyclic, excessively deep or non-file library alias.");
        }
    }

    private static async Task WriteEntryAsync(Stream input, long length, string destination, ExtractionBudget budget, CancellationToken cancellationToken)
    {
        if (length < 0 || length > MaximumArchiveBytes || budget.Bytes > MaximumExpandedBytes - length)
        {
            throw Invalid("The expanded update exceeds its size limit.");
        }
        budget.Bytes += length;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var remaining = length;
        var buffer = new byte[81_920];
        while (remaining > 0)
        {
            var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
            if (count == 0)
            {
                throw Invalid("The archive entry is truncated.");
            }
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            remaining -= count;
        }
        if (await input.ReadAsync(buffer.AsMemory(0, 1), cancellationToken) != 0)
        {
            throw Invalid("The archive entry exceeds its declared size.");
        }
    }

    private static UpdateException Invalid(string detail) => new(UpdateErrorCode.InvalidPackage, detail);

    private sealed class ExtractionBudget
    {
        internal int Entries;
        internal long Bytes;
        internal HashSet<string> Paths { get; } = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    }
}
