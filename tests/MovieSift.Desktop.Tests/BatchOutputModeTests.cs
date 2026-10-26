using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MovieSift.Core.Failures;
using MovieSift.Core.Media;
using MovieSift.Core.Planning;
using MovieSift.Desktop.Views;
using Xunit;

namespace MovieSift.Desktop.Tests;

/// <summary>Checks output choices and explicit review without claiming native file operations.</summary>
public sealed class BatchOutputModeTests
{
    /// <summary>A restored missing destination adopts the displayed adjacent default without changing another explicitly saved destination.</summary>
    [AvaloniaFact]
    public async Task RestoredMissingDestinationCanReviewAnAdjacentCopy()
    {
        var paths = new BatchUiBackend().Inspections.Keys.ToArray();
        var explicitDestination = Path.Combine(Path.GetTempPath(), "saved explicit output.mkv");
        var store = new BatchUiQueueListStore
        {
            List = new([new(paths[0], null), new(paths[1], explicitDestination)]),
            Revision = Guid.NewGuid()
        };
        var fixture = new BatchViewModelFixture(queueStore: store);
        await fixture.QueueInitialization;
        await fixture.Batch.Recovery.RestoreCommand.ExecuteAsync(null);
        var first = fixture.Batch.Items[0];
        var displayedDestination = first.OutputPath;
        Assert.Null(first.Snapshot.IntendedDestinationPath);
        Assert.Equal(Path.Combine(Path.GetDirectoryName(first.SourcePath)!,
            MediaContainerPolicy.TrimmedFileName(first.SourcePath)), displayedDestination);
        await fixture.Batch.InspectPendingCommand.ExecuteAsync(null);

        foreach (var item in fixture.Batch.Items)
        {
            item.Tracks.Single(track => track.Track.Kind == TrackKind.Subtitles).Keep = false;
        }

        await fixture.Batch.ReviewCommand.ExecuteAsync(null);
        Assert.True(fixture.Batch.HasCurrentReview);
        Assert.True(fixture.Batch.CanRun);
        Assert.Equal(displayedDestination, first.ApprovedPlan!.DestinationPath);
        Assert.Equal(explicitDestination, fixture.Batch.Items[1].ApprovedPlan!.DestinationPath);
        Assert.All(fixture.Batch.Items, item => Assert.Equal(OutputMode.Copy, item.ApprovedPlan!.OutputMode));
        Assert.Empty(fixture.Backend.RemuxRequests);
    }

    /// <summary>A visible unsupported input row must not throw from the shared output-mode help binding.</summary>
    [AvaloniaFact]
    public async Task UnsupportedInputKeepsOutputHelpReadable()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.QueueInitialization;
        var path = Path.Combine(Path.GetTempPath(), "unsupported input.mov");
        fixture.Discovery.Result = new([], [new(path, new(ErrorCode.UnsupportedContainer))]);
        await fixture.Batch.AddFilesCommand.ExecuteAsync(null);

        Assert.Equal(ErrorCode.UnsupportedContainer, Assert.Single(fixture.Batch.Items).Snapshot.Failure?.Code);
        Assert.Equal(fixture.Localization["WorkspaceOriginalShort"], fixture.Batch.OutputSafetyText);
        Assert.Empty(fixture.Backend.RemuxRequests);
    }

    /// <summary>Files from each source directory get adjacent default copies with no additional folder-picker step.</summary>
    [AvaloniaFact]
    public async Task CopyIsTheDefaultAndNeedsNoOutputFolderPicker()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.QueueInitialization;
        await fixture.Batch.AddFilesCommand.ExecuteAsync(null);
        Assert.Equal(0, fixture.Batch.SelectedOutputModeIndex);
        Assert.False(fixture.Batch.HasCustomOutputFolder);
        Assert.All(fixture.Batch.Items, item =>
        {
            Assert.Equal(OutputMode.Copy, item.Snapshot.OutputMode);
            Assert.Equal(Path.Combine(Path.GetDirectoryName(item.SourcePath)!,
                Path.GetFileNameWithoutExtension(item.SourcePath) + ".trimmed.mkv"), item.OutputPath);
        });
    }

    /// <summary>A compiled mode selector revokes old approval, displays replacement, and never starts media itself.</summary>
    [AvaloniaFact]
    public async Task ReplacementChoiceRequiresFreshReviewAndDisablesDestinationEditing()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.PrepareAsync();
        var view = new BatchView { DataContext = fixture.Batch };
        var window = new Window { Content = view };
        window.Show();

        try
        {
            var previous = fixture.Session.Snapshot.Review;
            fixture.Batch.CloseReviewCommand.Execute(null);
            var selector = view.FindControl<ComboBox>("OutputModeSelector")!;
            selector.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, fixture.Batch.SelectedOutputModeIndex);
            Assert.False(fixture.Batch.HasCurrentReview);
            Assert.False(fixture.Batch.RunCommand.CanExecute(null));
            Assert.All(fixture.Batch.Items, item =>
            {
                Assert.Equal(item.SourcePath, item.OutputPath);
                Assert.Equal(OutputMode.ReplaceSource, item.Snapshot.OutputMode);
                Assert.False(item.CanEditDestination);
                Assert.Null(item.ApprovedPlan);
            });
            Assert.Empty(fixture.Backend.RemuxRequests);
            await fixture.Batch.ReviewCommand.ExecuteAsync(null);
            Assert.NotEqual(previous!.Id, fixture.Session.Snapshot.Review!.Id);
            Assert.All(fixture.Batch.ReviewRows, row => Assert.True(row.ReplacesOriginal));
            Assert.Contains("Replace", fixture.Batch.RunActionText, StringComparison.Ordinal);
            var snapshot = fixture.Session.Snapshot;
            fixture.Localization.SetLanguage("ru");
            Assert.Same(snapshot, fixture.Session.Snapshot);
            Assert.Contains("Заменить", fixture.Batch.RunActionText, StringComparison.Ordinal);
            Assert.Empty(fixture.Backend.RemuxRequests);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Default keep-all remains a no-change result and must not promise replacement.</summary>
    [AvaloniaFact]
    public async Task NoChangeDoesNotPromiseReplacement()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.LoadAsync();
        fixture.Batch.SelectedOutputModeIndex = 1;
        await fixture.Batch.ReviewCommand.ExecuteAsync(null);
        Assert.All(fixture.Batch.ReviewRows, row =>
        {
            Assert.True(row.NoChange);
            Assert.False(row.ReplacesOriginal);
        });
        Assert.Equal(fixture.Localization["RunNoChanges"], fixture.Batch.RunActionText);
        Assert.Empty(fixture.Backend.RemuxRequests);
    }
}
