using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MovieSift.Infrastructure.MediaTools;
using MovieSift.Infrastructure.Processes;
using Xunit;

namespace MovieSift.Infrastructure.Tests;

/// <summary>Compares ordered encoded-packet sizes and SHA256 hashes without decoding or accumulating movie-sized JSON.</summary>
internal static class Mp4PayloadEvidence
{
    private const int MaximumDiagnosticsCharacters = 32768;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Checks a source-local index against its independently mapped output index.</summary>
    internal static async Task<PacketPayloadEvidence> CompareAsync(Mp4PayloadComparison request)
    {
        var source = await ReadAsync(request.SourcePath, request.SourceIndex);
        var output = await ReadAsync(request.OutputPath, request.OutputIndex);
        Assert.True(source.Packets > 0, "A retained stream must have actual packet evidence.");
        Assert.Equal(source, output);
        return source;
    }

    /// <summary>Drains compact ffprobe packet evidence into one constant-memory digest.</summary>
    private static async Task<PacketPayloadEvidence> ReadAsync(string path, int index)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long packets = 0;
        long bytes = 0;
        var invalid = false;
        var tools = FfmpegToolPaths.FromDirectory(MediaTestEnvironment.ToolDirectory);
        var arguments = new[]
        {
            "-v", "error", "-protocol_whitelist", "file", "-enable_drefs", "0", "-use_absolute_path", "0",
            "-select_streams", index.ToString(CultureInfo.InvariantCulture), "-show_packets", "-show_data_hash", "sha256",
            "-show_entries", "packet=size,data_hash", "-of", "compact=p=0:nk=0", path,
        };
        var result = await new OwnedProcessRunner().RunAsync(new(tools.Ffprobe, arguments, MaximumDiagnosticsCharacters,
            ProbeTimeout, Observe, TestContext.Current.CancellationToken));
        Assert.True(result.ExitCode == 0 && string.IsNullOrWhiteSpace(result.StandardError), result.StandardError);
        Assert.False(invalid, "The packet evidence was malformed or lacked an encoded-payload hash.");
        return new(packets, bytes, Convert.ToHexString(hash.GetHashAndReset()));

        /// <summary>Hashes only encoded payload evidence; presentation/skip timing has a separate production verifier.</summary>
        void Observe(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            var fields = line.Split('|');
            var size = fields.FirstOrDefault(field => field.StartsWith("size=", StringComparison.Ordinal));
            var digest = fields.FirstOrDefault(field => field.StartsWith("data_hash=SHA256:", StringComparison.Ordinal));
            var valid = size is not null && digest is not null &&
                long.TryParse(size.AsSpan("size=".Length), NumberStyles.None, CultureInfo.InvariantCulture, out _);

            if (!valid)
            {
                invalid = true;
                return;
            }

            packets++;
            bytes += long.Parse(size!.AsSpan("size=".Length), CultureInfo.InvariantCulture);
            hash.AppendData(Encoding.UTF8.GetBytes(size + "|" + digest + "\n"));
        }
    }
}

internal sealed record Mp4PayloadComparison(string SourcePath, int SourceIndex, string OutputPath, int OutputIndex);
internal sealed record PacketPayloadEvidence(long Packets, long Bytes, string Sha256);
