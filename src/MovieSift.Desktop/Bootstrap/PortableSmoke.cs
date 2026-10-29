using System.Security.Cryptography;
using MovieSift.Application;
using MovieSift.Application.Inspection;
using MovieSift.Application.Jobs;
using MovieSift.Core.Media;
using MovieSift.Core.Planning;
using MovieSift.Infrastructure.Files;
using MovieSift.Infrastructure.MediaTools;

namespace MovieSift.Desktop.Bootstrap;

/// <summary>Runs explicit package acceptance checks before starting any desktop lifetime.</summary>
internal static class PortableSmoke
{
    private const int SuccessExitCode = 0;
    private const int FailureExitCode = 1;
    private const int UsageExitCode = 64;
    private const int ExpectedWorkflowArgumentCount = 3;

    /// <summary>Checks relocated bundled tools or executes one real safe MKV/MP4 operation.</summary>
    public static async Task<int> RunAsync(string[] arguments)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArguments) =>
        {
            eventArguments.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            var sourceAccess = new FileSourceAccess();
            var paths = MkvToolPaths.FromApplicationDirectory();
            var matroska = new MkvBackend(new(sourceAccess, paths));
            var ffmpegPaths = FfmpegToolPaths.FromApplicationDirectory();
            var mp4 = new FfmpegBackend(new(sourceAccess, ffmpegPaths));
            var backend = new ContainerMediaBackend(matroska, mp4);
            await matroska.ValidateToolsAsync(cancellation.Token);
            await mp4.ValidateToolsAsync(cancellation.Token);
            Console.WriteLine($"Bundled MKVToolNix {MkvToolPaths.SupportedVersion}: {paths.MkvMerge}; {paths.MkvExtract}");
            Console.WriteLine($"Bundled FFmpeg {FfmpegToolPaths.SupportedVersion}: {ffmpegPaths.Ffmpeg}; {ffmpegPaths.Ffprobe}");

            if (arguments is ["--verify-tools"])
            {
                return SuccessExitCode;
            }

            if (arguments.Length != ExpectedWorkflowArgumentCount || arguments[0] != "--verify-portable")
            {
                Console.Error.WriteLine("Usage: --verify-tools | --verify-portable <source.mkv|mp4> <new-destination.mkv|mp4>");
                return UsageExitCode;
            }

            var sourcePath = Path.GetFullPath(arguments[1]);
            var destinationPath = Path.GetFullPath(arguments[2]);
            var originalHash = await HashSourceAsync(new(sourcePath, cancellation.Token));
            var inspected = await new InspectionService(backend, sourceAccess).InspectAsync(new(sourcePath, cancellation.Token));

            if (inspected.Inspection is not { } inspection)
            {
                Console.Error.WriteLine($"Inspection failed: {inspected.Failure}");
                return FailureExitCode;
            }

            var audio = inspection.Tracks.Where(track => track.Kind == TrackKind.Audio).ToArray();
            var removedAudioId = audio.Length > 1 ? audio[0].Id : (int?)null;
            var retainedIds = inspection.Tracks
                .Where(track => track.Kind != TrackKind.Subtitles && track.Id != removedAudioId)
                .Select(track => track.Id).ToArray();
            var planResult = PlanBuilder.Build(new(inspection, retainedIds, destinationPath, false));

            if (planResult.Plan is not { IsNoChange: false } plan)
            {
                Console.Error.WriteLine("The diagnostic fixture needs a removable audio or subtitle track.");
                return FailureExitCode;
            }

            var applicationRoot = PortableLayout.ApplicationRootFromBaseDirectory(AppContext.BaseDirectory);
            var dataDirectory = Path.Combine(applicationRoot, "data");
            var coordinator = new JobCoordinator(backend, sourceAccess, new OwnedOutputStore(dataDirectory));
            var result = await coordinator.RunAsync(new(plan, CancellationToken: cancellation.Token));
            var finalHash = await HashSourceAsync(new(sourcePath, cancellation.Token));
            var succeeded = result.Phase == JobPhase.Completed && result.PublishedPath == destinationPath &&
                result.Verification?.Passed == true && result.CleanupIssues.IsEmpty && originalHash == finalHash;
            Console.WriteLine($"Phase: {result.Phase}; source SHA256: {finalHash}; output: {result.PublishedPath}");
            Console.WriteLine($"Verification: {result.Verification?.Details}; failure: {result.Failure}");
            return succeeded ? SuccessExitCode : FailureExitCode;
        }
        catch (Exception exception) when (exception is OperationFailureException or IOException or
            UnauthorizedAccessException or OperationCanceledException)
        {
            Console.Error.WriteLine(exception is OperationFailureException failure ? failure.Failure : exception.Message);
            return FailureExitCode;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    /// <summary>Hashes the source through a read-only stream for explicit acceptance evidence.</summary>
    private static async Task<string> HashSourceAsync(HashRequest request)
    {
        await using var stream = new FileStream(request.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var digest = await SHA256.HashDataAsync(stream, request.CancellationToken);
        return Convert.ToHexString(digest);
    }
}
