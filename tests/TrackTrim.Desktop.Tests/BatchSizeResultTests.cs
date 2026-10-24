using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TrackTrim.Application.Batches;
using TrackTrim.Application.Jobs;
using TrackTrim.Desktop.Views;
using Xunit;

namespace TrackTrim.Desktop.Tests;

/// <summary>Checks recorded per-file size evidence without probing files or claiming real-media execution.</summary>
public sealed class BatchSizeResultTests
{
    private const long SourceLength = 1000;
    private const long SmallerOutputLength = 700;
    private const long LargerOutputLength = 1300;
    private const long SizeDelta = 300;

    /// <summary>Reports measured reduction, growth and equality in either language without changing file authority.</summary>
    [AvaloniaTheory]
    [InlineData("en", SmallerOutputLength, "SizeDifference")]
    [InlineData("ru", SmallerOutputLength, "SizeDifference")]
    [InlineData("en", LargerOutputLength, "SizeIncrease")]
    [InlineData("ru", LargerOutputLength, "SizeIncrease")]
    [InlineData("en", SourceLength, "SizeEqual")]
    [InlineData("ru", SourceLength, "SizeEqual")]
    public async Task PublishedSizesUseRecordedEvidenceWithoutChangingSelectionOrReview(string language, long outputLength, string resourceKey)
    {
        var fixture = new ViewModelFixture();
        await fixture.LoadAsync();
        fixture.Item.Tracks[1].Keep = false;
        await fixture.Workspace.ReviewCommand.ExecuteAsync(null);
        var review = fixture.Workspace.Snapshot.Review;
        var approval = fixture.Item.ApprovedPlan;
        var kept = fixture.Item.Tracks.Select(track => track.Keep).ToArray();
        var completed = fixture.Item.Snapshot with
        {
            State = BatchItemState.Completed,
            Result = new(approval!.Id, JobPhase.Completed, PublishedPath: ViewModelFixture.OutputPath, OutputLength: outputLength)
        };
        fixture.Item.ApplySnapshot(completed);
        await fixture.ViewModel.ChangeLanguageCommand.ExecuteAsync(language);

        var expected = resourceKey == "SizeEqual" ? fixture.Item.Localization[resourceKey]
            : fixture.Item.Localization.Format(new(resourceKey, [SizeDelta]));
        Assert.True(fixture.Item.HasSizeDifference);
        Assert.Equal(expected, fixture.Item.SizeDifferenceText);
        Assert.Contains(language == "ru" ? "Оригинал остаётся на диске" : "The original remains on disk", fixture.Item.SizeDifferenceText);
        Assert.Same(completed, fixture.Item.Snapshot);
        Assert.Same(approval, fixture.Item.ApprovedPlan);
        Assert.Same(review, fixture.Workspace.Snapshot.Review);
        Assert.Equal(kept, fixture.Item.Tracks.Select(track => track.Keep));
        Assert.Equal(0, fixture.Backend.RemuxCount);
    }

    /// <summary>Shows known publication sizes even when subsequent cleanup reports a separate problem.</summary>
    [AvaloniaFact]
    public async Task CompiledResultsRetainPublishedSizesAcrossCleanupProblemsAndLanguageChanges()
    {
        const string cleanupDetail = "Controlled cleanup failure after publication.";
        var fixture = new ViewModelFixture();
        fixture.Outputs.CleanupIssues = [new("fixture-owned", cleanupDetail)];
        await fixture.LoadAsync();
        fixture.Item.Tracks[1].Keep = false;
        await fixture.Workspace.ReviewCommand.ExecuteAsync(null);
        await fixture.Workspace.RunCommand.ExecuteAsync(null);
        Assert.True(fixture.Workspace.HasCleanupIssue);
        Assert.Equal(ViewModelFixture.OutputPath, fixture.Item.PublishedPath);
        Assert.Contains(cleanupDetail, fixture.Item.Diagnostics);
        var result = fixture.Item.Snapshot.Result;
        var kept = fixture.Item.Tracks.Select(track => track.Keep).ToArray();
        var view = new BatchView { DataContext = fixture.Workspace };
        var window = new Window { Content = view };
        window.Show();
        view.FindControl<Expander>("SelectedFileDetails")!.IsExpanded = true;

        try
        {
            foreach (var language in new[] { "en", "ru", "en" })
            {
                await fixture.ViewModel.ChangeLanguageCommand.ExecuteAsync(language);
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var expected = fixture.Item.Localization.Format(new("SizeDifference", [SizeDelta]));
                var rows = view.GetVisualDescendants().OfType<TextBlock>().Where(text =>
                    text.Name is "BatchFileSizeDifferenceText" or "BatchDetailsSizeDifferenceText").ToArray();
                Assert.Equal(2, rows.Length);
                Assert.All(rows, text =>
                {
                    Assert.True(text.IsVisible);
                    Assert.Equal(expected, text.Text);
                    Assert.Same(fixture.Item, text.DataContext);
                });
                Assert.Same(result, fixture.Item.Snapshot.Result);
                Assert.Equal(kept, fixture.Item.Tracks.Select(track => track.Keep));
                Assert.False(fixture.Workspace.HasReview);
                Assert.False(fixture.Workspace.RunCommand.CanExecute(null));
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Never invents measured publication from a missing length, an uncreated copy or an unfinished result.</summary>
    [AvaloniaTheory]
    [InlineData(JobPhase.Completed, true, null, true)]
    [InlineData(JobPhase.Completed, true, SmallerOutputLength, false)]
    [InlineData(JobPhase.Failed, false, SmallerOutputLength, true)]
    [InlineData(JobPhase.Cancelled, false, SmallerOutputLength, true)]
    [InlineData(JobPhase.NoChange, false, SmallerOutputLength, true)]
    [InlineData(JobPhase.AwaitingWarningReview, false, SmallerOutputLength, true)]
    public async Task MissingPublicationEvidenceProducesNoSizeClaim(JobPhase phase, bool published, long? outputLength, bool inspected)
    {
        var fixture = new ViewModelFixture();
        await fixture.LoadAsync();
        var result = new JobResult(Guid.NewGuid(), phase,
            PublishedPath: published ? ViewModelFixture.OutputPath : null, OutputLength: outputLength);
        fixture.Item.ApplySnapshot(fixture.Item.Snapshot with { Result = result, Inspection = inspected ? fixture.Item.Snapshot.Inspection : null });

        foreach (var language in new[] { "en", "ru" })
        {
            await fixture.ViewModel.ChangeLanguageCommand.ExecuteAsync(language);
            Assert.False(fixture.Item.HasSizeDifference);
            Assert.Empty(fixture.Item.SizeDifferenceText);
            Assert.Same(result, fixture.Item.Snapshot.Result);
        }

        Assert.Equal(0, fixture.Backend.RemuxCount);
    }

    /// <summary>Retains the actual no-copy result without presenting a fictional output or savings.</summary>
    [AvaloniaFact]
    public async Task NoChangeDoesNotClaimAnOutputMeasurement()
    {
        var fixture = new ViewModelFixture();
        await fixture.LoadAsync();
        await fixture.Workspace.ReviewCommand.ExecuteAsync(null);
        await fixture.Workspace.RunCommand.ExecuteAsync(null);

        Assert.Equal(BatchItemState.NoChange, fixture.Item.Snapshot.State);
        Assert.Equal(fixture.Item.Localization["BatchItemNoChange"], fixture.Item.StateText);
        Assert.False(fixture.Item.HasPublishedPath);
        Assert.False(fixture.Item.HasSizeDifference);
        Assert.Empty(fixture.Item.SizeDifferenceText);
        Assert.Equal(0, fixture.Backend.RemuxCount);
    }
}
