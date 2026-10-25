using System.Collections.Immutable;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using TrackTrim.Application.Batches;
using TrackTrim.Core.Failures;
using TrackTrim.Core.Media;
using TrackTrim.Desktop.Views;
using TrackTrim.Infrastructure.Persistence;
using Xunit;

namespace TrackTrim.Desktop.Tests;

/// <summary>Checks mixed-container presentation and fresh restoration with controlled media ports.</summary>
public sealed class MixedContainerUiTests
{
    /// <summary>Independent MP4/MKV approvals and compiled controls survive row changes and live localization.</summary>
    [AvaloniaFact]
    public async Task EnglishRussianSwitchesPreserveMixedPlansAndFormatSpecificDestinations()
    {
        var fixture = new BatchViewModelFixture();
        ConfigureMixedBackend(fixture);
        await fixture.PrepareAsync();
        Assert.Equal(2, fixture.Batch.Items.Count);
        var plans = fixture.Batch.Items.Select(item => item.ApprovedPlan).ToArray();
        Assert.Equal([MediaContainer.Matroska, MediaContainer.Mp4], plans.Select(plan => plan!.Inspection.Container));
        Assert.EndsWith(".trimmed.mkv", fixture.Batch.Items[0].OutputPath, StringComparison.Ordinal);
        Assert.EndsWith(".trimmed.mp4", fixture.Batch.Items[1].OutputPath, StringComparison.Ordinal);
        Assert.All(fixture.Batch.Items[1].Tracks, track => Assert.Null(track.Track.Uid));
        var paths = fixture.Batch.Items.Select(item => item.OutputPath).ToArray();
        var kept = fixture.Batch.Items.Select(item => item.Tracks.Select(track => track.Keep).ToArray()).ToArray();
        var titles = fixture.Batch.Items.Select(item => item.Tracks.Select(track => track.TitleText).ToArray()).ToArray();
        var review = fixture.Batch.Snapshot.Review;
        var view = new BatchView { DataContext = fixture.Batch };
        var window = new Window { Content = view };
        window.Show();

        try
        {
            var add = view.FindControl<Button>("AddBatchFilesButton")!;
            var reviewButton = view.FindControl<Button>("ReviewBatchButton")!;

            foreach (var language in new[] { "ru", "en" })
            {
                fixture.Batch.SelectedItem = fixture.Batch.Items[1];
                await fixture.Main.ChangeLanguageCommand.ExecuteAsync(language);
                Dispatcher.UIThread.RunJobs();

                Assert.Equal(fixture.Localization["BatchAddFiles"], add.Content);
                Assert.Equal(fixture.Localization["BatchReviewRemaining"], reviewButton.Content);
                Assert.Equal(MediaContainer.Mp4, fixture.Batch.Items[1].Snapshot.Inspection!.Container);
                Assert.Same(review, fixture.Batch.Snapshot.Review);
                Assert.Equal(paths, fixture.Batch.Items.Select(item => item.OutputPath));

                for (var index = 0; index < fixture.Batch.Items.Count; index++)
                {
                    var item = fixture.Batch.Items[index];
                    Assert.Same(plans[index], item.ApprovedPlan);
                    Assert.Equal(kept[index], item.Tracks.Select(track => track.Keep));
                    Assert.Equal(titles[index], item.Tracks.Select(track => track.TitleText));
                }

                Assert.True(fixture.Batch.RunCommand.CanExecute(null));
                Assert.Contains(".trimmed.mp4", fixture.Batch.ReviewText, StringComparison.Ordinal);
                Assert.Empty(fixture.Backend.RemuxRequests);
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A visible failed nonmedia row can receive an output-folder edit without throwing during filename derivation.</summary>
    [AvaloniaFact]
    public async Task UnsupportedDiscoveryRowKeepsItsErrorAndDoesNotAcquireInventedDestination()
    {
        var fixture = new BatchViewModelFixture();
        ConfigureMixedBackend(fixture);
        var invalid = Path.Combine(Path.GetTempPath(), "TrackTrim failed input", "not supported.mov");
        fixture.Discovery.Result = fixture.Discovery.Result! with
        {
            Issues = [new(invalid, new(ErrorCode.UnsupportedContainer))]
        };
        await fixture.LoadAsync();
        var row = Assert.Single(fixture.Batch.Items, item => item.SourcePath == invalid);

        Assert.True(row.HasError);
        Assert.Equal(ErrorCode.UnsupportedContainer, row.Snapshot.Failure?.Code);
        Assert.Empty(row.OutputPath);
        Assert.Empty(row.Tracks);
        Assert.False(row.HasPlan);
        await fixture.Batch.Recovery.WhenIdleAsync();
        Assert.Null(row.Snapshot.IntendedDestinationPath);
        Assert.False(fixture.Batch.Recovery.HasFailure, fixture.Batch.Recovery.Diagnostics);
        Assert.Null(Assert.Single(fixture.QueueStore.List.Entries, entry => entry.SourcePath == invalid).DestinationPath);
        fixture.Batch.SelectedItem = row;
        await fixture.Main.ChangeLanguageCommand.ExecuteAsync("ru");
        Assert.Equal(fixture.Localization["ErrorUnsupportedContainer"], row.ErrorText);
        Assert.Empty(row.OutputPath);
        Assert.Empty(fixture.Backend.RemuxRequests);
    }

    /// <summary>A real portable mixed listing restores only path intent and requires a fresh independent MP4 inspection.</summary>
    [AvaloniaFact]
    public async Task MixedPortableListingRestoresWithoutTrackIndexesApprovalsOrAutomaticWork()
    {
        var root = Directory.CreateTempSubdirectory("TrackTrim mixed recovery Кириллица ").FullName;

        try
        {
            var data = Path.Combine(root, "data");
            var original = new BatchViewModelFixture(queueStore: new PortableQueueListStore(data));
            ConfigureMixedBackend(original);
            await original.PrepareAsync();
            await original.Batch.Recovery.WhenIdleAsync();
            var priorIds = original.Batch.Items.Select(item => item.Id).ToArray();
            var paths = original.Batch.Items.Select(item => item.SourcePath).ToArray();
            var destinations = original.Batch.Items.Select(item => item.OutputPath).ToArray();
            var stored = await new PortableQueueListStore(data).ReadAsync(TestContext.Current.CancellationToken);
            Assert.Null(stored.Failure);
            Assert.Equal(paths, stored.List!.Entries.Select(entry => entry.SourcePath));
            var restored = new BatchViewModelFixture(queueStore: new PortableQueueListStore(data));
            ConfigureMixedBackend(restored);
            var inspections = 0;
            restored.Backend.OnInspect = _ => { inspections++; return Task.CompletedTask; };
            await restored.QueueInitialization;

            Assert.True(restored.Batch.Recovery.HasRecoveryChoice);
            Assert.Empty(restored.Batch.Items);
            Assert.Equal(0, inspections);
            await restored.Batch.Recovery.RestoreCommand.ExecuteAsync(null);
            Assert.Equal(paths, restored.Batch.Items.Select(item => item.SourcePath));
            Assert.Equal(destinations, restored.Batch.Items.Select(item => item.OutputPath));
            Assert.DoesNotContain(restored.Batch.Items, item => priorIds.Contains(item.Id));
            Assert.All(restored.Batch.Items, item =>
            {
                Assert.Equal(BatchItemState.PendingInspection, item.Snapshot.State);
                Assert.Null(item.Snapshot.Inspection);
                Assert.Empty(item.Snapshot.CurrentRetainedTrackIds);
                Assert.Empty(item.Tracks);
                Assert.False(item.HasPlan);
                Assert.False(item.ZeroAudioConfirmed);
            });
            Assert.Equal(0, inspections);
            Assert.False(restored.Batch.RunCommand.CanExecute(null));
            await restored.Batch.InspectPendingCommand.ExecuteAsync(null);

            Assert.Equal(2, inspections);
            Assert.Equal([MediaContainer.Matroska, MediaContainer.Mp4],
                restored.Batch.Items.Select(item => item.Snapshot.Inspection!.Container));
            Assert.All(restored.Batch.Items, item =>
            {
                Assert.All(item.Tracks, track => Assert.True(track.Keep));
                Assert.False(item.HasPlan);
            });
            Assert.False(restored.Batch.HasReview);
            Assert.False(restored.Batch.RunCommand.CanExecute(null));
            Assert.Empty(original.Backend.RemuxRequests);
            Assert.Empty(restored.Backend.RemuxRequests);
            Assert.Equal(0, restored.Outputs.CleanupCount);
            await restored.Batch.Recovery.WhenIdleAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Changes only fixture metadata so the production ViewModels receive two independent container identities.</summary>
    private static void ConfigureMixedBackend(BatchViewModelFixture fixture)
    {
        var prior = fixture.Backend.Inspections.Values.Last();
        var source = prior.Source with { FullPath = Path.ChangeExtension(prior.Source.FullPath, ".mp4") };
        var inspection = prior with
        {
            Source = source,
            Backend = new("FFmpeg", "controlled UI port", 1),
            Container = MediaContainer.Mp4,
            Tracks = prior.Tracks.Select(track => track with { Uid = null }).ToImmutableArray()
        };
        fixture.Backend.Inspections.Remove(prior.Source.FullPath);
        fixture.Backend.Inspections.Add(source.FullPath, inspection);
        fixture.Dialogs.Sources = fixture.Backend.Inspections.Keys.ToArray();
        fixture.Discovery.Result = new(fixture.Backend.Inspections.Values.Select(value => value.Source).ToImmutableArray(), []);
    }
}
