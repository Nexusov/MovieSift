using MovieSift.Core.Failures;
using MovieSift.Core.Planning;
using Xunit;

namespace MovieSift.Application.Tests.Batches;

/// <summary>Checks that source replacement is explicit immutable approval, not an inferred path collision.</summary>
public sealed class BatchOutputModeTests
{
    /// <summary>Only uncertain replacements retain all recovery storage; copy publication retains its established owned-cleanup behavior.</summary>
    [Theory]
    [InlineData(OutputMode.Copy, false, 1)]
    [InlineData(OutputMode.Copy, true, 1)]
    [InlineData(OutputMode.ReplaceSource, false, 0)]
    [InlineData(OutputMode.ReplaceSource, true, 0)]
    public async Task PublicationUncertaintyPreservesModeSpecificCleanup(OutputMode mode, bool warning, int expectedCleanupCalls)
    {
        var fixture = new BatchTestHarness(1);
        await fixture.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        var item = Assert.Single(fixture.Session.Snapshot.Items);
        fixture.ConfigureSelection(item);

        if (mode == OutputMode.ReplaceSource)
        {
            Assert.True(fixture.Session.SetDestination(new(item.Id, item.SourcePath, OutputMode: mode)).Applied);
        }

        var review = await fixture.Coordinator.ReviewSelectionsAsync(new(fixture.Session.Snapshot.Revision, [], TestContext.Current.CancellationToken));
        Assert.NotNull(review.Review);
        fixture.PublicationFailure = new(ErrorCode.PublicationUncertain);
        fixture.ExitCodes[item.SourcePath] = warning ? 1 : 0;
        await fixture.Coordinator.RunAsync(new(review.Review.Id));

        if (warning)
        {
            Assert.Equal(0, fixture.CleanupCalls);
            await fixture.Coordinator.PublishWarningsAsync(new(item.Id));
        }

        var result = Assert.Single(fixture.Session.Snapshot.Items).Result;
        Assert.Equal(ErrorCode.PublicationUncertain, result!.Failure?.Code);
        Assert.True(fixture.Session.Snapshot.HasSafetyBlock);
        Assert.Equal(expectedCleanupCalls, fixture.CleanupCalls);
        Assert.Single(fixture.RemuxedPaths);
    }

    /// <summary>Review accepts each replacement's own source but rejects the same destination in ordinary copy mode.</summary>
    [Theory]
    [InlineData(OutputMode.Copy, false)]
    [InlineData(OutputMode.ReplaceSource, true)]
    public async Task SourceDestinationRequiresAnExplicitReplacementMode(OutputMode mode, bool accepted)
    {
        var fixture = new BatchTestHarness(1);
        await fixture.Coordinator.InspectPendingAsync(TestContext.Current.CancellationToken);
        var item = Assert.Single(fixture.Session.Snapshot.Items);
        fixture.ConfigureSelection(item);
        Assert.True(fixture.Session.SetDestination(new(item.Id, item.SourcePath, OutputMode: mode)).Applied);
        var result = await fixture.Coordinator.ReviewSelectionsAsync(new(fixture.Session.Snapshot.Revision, [], TestContext.Current.CancellationToken));
        Assert.Equal(accepted, result.Review is not null);

        if (accepted)
        {
            Assert.Equal(mode, Assert.Single(result.Review!.Items).Plan.OutputMode);
            Assert.Equal(mode, Assert.Single(fixture.DestinationValidations).OutputMode);
        }
        else
        {
            Assert.Equal(ErrorCode.InvalidDestination, Assert.Single(result.ItemFailures).Failure.Code);
        }
    }

    /// <summary>A deliberate mode change invalidates the whole Review and prevents the old run from dispatching.</summary>
    [Fact]
    public async Task ChangingOutputModeRevokesTheReviewedRun()
    {
        var fixture = new BatchTestHarness(1);
        var reviewed = await fixture.PrepareAsync();
        var item = Assert.Single(fixture.Session.Snapshot.Items);
        Assert.True(fixture.Session.SetDestination(new(item.Id, item.SourcePath, OutputMode: OutputMode.ReplaceSource)).Applied);
        Assert.Null(fixture.Session.Snapshot.Review);
        Assert.Null(Assert.Single(fixture.Session.Snapshot.Items).ApprovedPlan);
        var result = await fixture.Coordinator.RunAsync(new(reviewed.Id));
        Assert.Equal(ErrorCode.StalePlan, result.Failure?.Code);
        Assert.Empty(fixture.RemuxedPaths);
    }

    /// <summary>Replacement cannot target a different batch source, even if the caller supplies its path.</summary>
    [Fact]
    public void ReplacementOfAnotherItemsSourceIsRejected()
    {
        var fixture = new BatchTestHarness(2);
        var first = fixture.Session.Snapshot.Items[0];
        var second = fixture.Session.Snapshot.Items[1];
        var result = fixture.Session.SetDestination(new(first.Id, second.SourcePath, OutputMode: OutputMode.ReplaceSource));
        Assert.False(result.Applied);
        Assert.Equal(ErrorCode.InvalidDestination, result.Failure?.Code);
        Assert.Null(fixture.Session.Snapshot.Items[0].IntendedDestinationPath);
    }
}
