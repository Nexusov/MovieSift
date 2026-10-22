using TrackTrim.Application;
using TrackTrim.Core.Failures;
using TrackTrim.Infrastructure.Files;
using Xunit;

namespace TrackTrim.Application.Tests;

/// <summary>Exercises real destination policy without reserving or filling a developer disk.</summary>
public sealed class CapacityValidationTests
{
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
