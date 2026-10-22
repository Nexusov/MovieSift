using TrackTrim.Application;
using TrackTrim.Core.Failures;
using TrackTrim.Core.Media;
using TrackTrim.Infrastructure.Files;
using Xunit;

namespace TrackTrim.Application.Tests;

/// <summary>Exercises real destination policy without reserving or filling a developer disk.</summary>
public sealed class CapacityValidationTests
{
    /// <summary>Enforces the frozen plan's explicit container without enabling arbitrary suffixes or conversion.</summary>
    [Theory]
    [InlineData(MediaContainer.Matroska, ".mkv", true)]
    [InlineData(MediaContainer.Matroska, ".mp4", false)]
    [InlineData(MediaContainer.Mp4, ".mp4", true)]
    [InlineData(MediaContainer.Mp4, ".MP4", true)]
    [InlineData(MediaContainer.Mp4, ".mkv", false)]
    [InlineData(MediaContainer.Mp4, ".mov", false)]
    [InlineData((MediaContainer)99, ".mp4", false)]
    public async Task DestinationMustMatchTheFrozenContainer(MediaContainer container, string extension, bool accepted)
    {
        var directory = Directory.CreateTempSubdirectory("TrackTrim-container-").FullName;
        var sourcePath = Path.Combine(directory, "source.mp4");
        var cancellationToken = TestContext.Current.CancellationToken;

        try
        {
            await File.WriteAllTextAsync(sourcePath, "controlled source", cancellationToken);
            var access = new FileSourceAccess();
            var source = await access.ReadRevisionAsync(new(sourcePath));
            var request = new Ports.ValidateDestinationRequest(source, Path.Combine(directory, "output" + extension), 0,
                cancellationToken, Container: container);

            if (accepted)
            {
                await access.ValidateDestinationAsync(request);
                return;
            }

            var failure = await Assert.ThrowsAsync<OperationFailureException>(() => access.ValidateDestinationAsync(request));
            Assert.Equal(ErrorCode.InvalidDestination, failure.Failure.Code);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Separates initial allocation advice from publication checks while retaining collision rejection.</summary>
    [Fact]
    public async Task PublicationChecksDoNotRequireAnotherMediaAllocation()
    {
        var directory = Directory.CreateTempSubdirectory("TrackTrim-capacity-").FullName;
        var sourcePath = Path.Combine(directory, "source.mkv");
        var destinationPath = Path.Combine(directory, "output.mkv");
        var cancellationToken = TestContext.Current.CancellationToken;

        try
        {
            await File.WriteAllTextAsync(sourcePath, "small controlled source", cancellationToken);
            var access = new FileSourceAccess();
            var revision = await access.ReadRevisionAsync(new(sourcePath));
            // Only the revision's estimate is large; no large file is allocated.
            var estimatedSource = revision with { Length = long.MaxValue };
            var capacityFailure = await Assert.ThrowsAsync<OperationFailureException>(() =>
                access.ValidateDestinationAsync(new(estimatedSource, destinationPath, estimatedSource.Length)));
            Assert.Equal(ErrorCode.InsufficientSpace, capacityFailure.Failure.Code);
            await access.ValidateDestinationAsync(new(estimatedSource, destinationPath, AdditionalAllocationBytes: 0));

            await File.WriteAllTextAsync(destinationPath, "unrelated destination", cancellationToken);
            var collisionFailure = await Assert.ThrowsAsync<OperationFailureException>(() =>
                access.ValidateDestinationAsync(new(estimatedSource, destinationPath, AdditionalAllocationBytes: 0)));
            Assert.Equal(ErrorCode.DestinationExists, collisionFailure.Failure.Code);
            Assert.Equal("unrelated destination", await File.ReadAllTextAsync(destinationPath, cancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
