using System.Collections.Immutable;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TrackTrim.Application.Batches;
using TrackTrim.Core.Media;
using TrackTrim.Desktop.ViewModels;
using TrackTrim.Desktop.Views;
using Xunit;

namespace TrackTrim.Desktop.Tests;

/// <summary>Exercises the unified selection projection through actual compiled controls and UI commands.</summary>
public sealed class AggregateWorkspaceTests
{
    private const string SharedAudioTitle = "English soundtrack";
    private const string SpanishTitle = "Español — last episode only";

    /// <summary>Exercises real three-state checkbox input rather than bypassing its toggle/command ordering.</summary>
    [AvaloniaFact]
    public async Task AggregateCheckboxSpaceRemovesCheckedAndKeepsMixedMembers()
    {
        var fixture = CreateSimilarEpisodes();
        await fixture.LoadAsync();
        var group = Assert.Single(fixture.Batch.AggregateTracks.Groups, row => row.Snapshot.Key.Title == SharedAudioTitle);
        var view = new AggregateTracksView { DataContext = fixture.Batch.AggregateTracks };
        var window = new Window { Content = view };
        window.Show();

        try
        {
            Dispatcher.UIThread.RunJobs();
            var checkBox = Assert.Single(window.GetVisualDescendants().OfType<CheckBox>(), control =>
                ReferenceEquals(control.DataContext, group) && ReferenceEquals(control.Command, group.ToggleKeepCommand));
            Assert.True(checkBox.IsChecked);
            Assert.True(checkBox.IsThreeState);
            Assert.True(checkBox.Focus());
            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.False(group.Keep);
            Assert.False(checkBox.IsChecked);
            Assert.All(group.Members, member => Assert.False(member.Keep));
            group.Members[0].Keep = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Null(group.Keep);
            Assert.Null(checkBox.IsChecked);
            Assert.True(checkBox.Focus());
            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.True(group.Keep);
            Assert.True(checkBox.IsChecked);
            Assert.All(group.Members, member => Assert.True(member.Keep));
            Assert.All(fixture.Batch.Items, item => Assert.Null(item.ApprovedPlan));
            Assert.False(fixture.Batch.HasReview);
            Assert.Empty(fixture.Backend.RemuxRequests);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Keeps scope independent of details and reviews every file once, including genuine no-change jobs.</summary>
    [AvaloniaFact]
    public async Task UnionIncludesMovieAndLastEpisodeSpanishWithoutChangingOtherFiles()
    {
        var fixture = CreateSimilarEpisodes();
        var template = fixture.Backend.Inspections.Values.First();
        var third = AddInspection(fixture, template, "episode 3.mkv", 30);
        var fourth = AddInspection(fixture, template, "episode 4.mkv", 40);
        var spanish = template.Tracks.First(track => track.Kind == TrackKind.Audio) with
        {
            Id = 0,
            Uid = 900,
            Language = "spa",
            LanguageIetf = "es",
            Title = SpanishTitle
        };
        fixture.Backend.Inspections[fourth.Source.FullPath] = fourth with { Tracks = fourth.Tracks.Add(spanish) };
        var movie = AddInspection(fixture, template, "movie другой набор.mkv", 90);
        fixture.Backend.Inspections[movie.Source.FullPath] = movie with
        {
            Tracks = movie.Tracks.Select(track => track with { Title = "Movie " + track.Title, Language = "rus", LanguageIetf = "ru" }).ToImmutableArray()
        };
        SetDiscovery(fixture, fixture.Backend.Inspections.Values);
        await fixture.LoadAsync();
        var batch = fixture.Batch;
        var spanishGroup = Assert.Single(batch.AggregateTracks.Groups, group => group.Snapshot.Key.Title == SpanishTitle);
        var spanishMember = Assert.Single(spanishGroup.Members);
        Assert.Equal(1, spanishGroup.Snapshot.FileCoverage);
        Assert.Equal(1, spanishGroup.Snapshot.OccurrenceCount);
        Assert.Contains("1 of 5", spanishGroup.CoverageText);
        Assert.Equal(fourth.Source.FullPath, spanishMember.SourcePath);
        Assert.Equal("0", spanishMember.IdentifierText);
        var shared = Assert.Single(batch.AggregateTracks.Groups, group => group.Snapshot.Key.Title == SharedAudioTitle);
        Assert.Equal(4, shared.Snapshot.FileCoverage);
        Assert.Contains(batch.AggregateTracks.Groups, group => group.Snapshot.Key.Title == "Movie " + SharedAudioTitle);
        var rows = batch.AggregateTracks.Groups.ToArray();
        shared.IsExpanded = true;
        batch.SelectedItem = Assert.Single(batch.Items, item => item.SourcePath == movie.Source.FullPath);
        batch.SelectedItem = Assert.Single(batch.Items, item => item.SourcePath == third.Source.FullPath);
        Assert.Equal(rows, batch.AggregateTracks.Groups);
        Assert.True(shared.IsExpanded);
        spanishGroup.ToggleKeepCommand.Execute(null);
        Assert.False(spanishGroup.Keep);

        foreach (var item in batch.Items)
        {
            var removed = item.Tracks.Where(track => !track.Keep).ToArray();

            if (item.SourcePath != fourth.Source.FullPath)
            {
                Assert.Empty(removed);
                continue;
            }

            Assert.Equal(SpanishTitle, Assert.Single(removed).Track.Title);
        }

        Assert.All(batch.Items, item => Assert.Null(item.ApprovedPlan));
        await batch.ReviewCommand.ExecuteAsync(null);
        Assert.Equal(5, batch.Snapshot.Review!.Items.Length);
        Assert.Equal(4, batch.Snapshot.Review.Items.Count(item => item.Plan.IsNoChange));
        Assert.True(batch.CanRun);
        Assert.Empty(fixture.Backend.RemuxRequests);
        await batch.RunCommand.ExecuteAsync(null);
        Assert.Equal(BatchPhase.Completed, batch.Snapshot.Phase);
        Assert.Equal(4, batch.Items.Count(item => item.Snapshot.State == BatchItemState.NoChange));
        Assert.Equal(fourth.Source.FullPath, Assert.Single(fixture.Backend.RemuxRequests).Plan.Inspection.Source.FullPath);
        Assert.All(batch.AggregateTracks.Groups, group => Assert.False(group.CanEdit));
    }

    /// <summary>Derives mixed state from member choices and never turns an old group edit into a rule for later files.</summary>
    [AvaloniaFact]
    public async Task IndividualExceptionAndLaterInputProduceMixedStateWithoutReusingOldChoices()
    {
        var fixture = CreateSimilarEpisodes();
        await fixture.LoadAsync();
        var batch = fixture.Batch;
        var group = Assert.Single(batch.AggregateTracks.Groups, row => row.Snapshot.Key.Title == SharedAudioTitle);
        var member = group.Members[0];
        member.Keep = false;
        Assert.Null(group.Keep);
        Assert.False(member.Keep);
        Assert.True(group.Members[1].Keep);
        group.ToggleKeepCommand.Execute(null);
        Assert.True(group.Keep);
        group.ToggleKeepCommand.Execute(null);
        Assert.False(group.Keep);
        await batch.ReviewCommand.ExecuteAsync(null);
        Assert.True(batch.HasReview);
        var template = fixture.Backend.Inspections.Values.First();
        var later = AddInspection(fixture, template, "later episode.mkv", 100);
        SetDiscovery(fixture, [later]);
        await batch.AddFilesCommand.ExecuteAsync(null);
        Assert.Same(group, Assert.Single(batch.AggregateTracks.Groups, row => row.Snapshot.Key.Title == SharedAudioTitle));
        Assert.Null(group.Keep);
        Assert.Equal(3, group.Snapshot.FileCoverage);
        Assert.True(Assert.Single(group.Members, row => row.SourcePath == later.Source.FullPath).Keep);
        Assert.False(batch.HasReview);
        var laterItem = Assert.Single(batch.Items, item => item.SourcePath == later.Source.FullPath);
        laterItem.Included = false;
        Assert.False(group.Keep);
        Assert.Equal(2, group.Members.Count);
        laterItem.Included = true;
        Assert.Null(group.Keep);
        batch.SelectedItem = laterItem;
        batch.RemoveSelectedCommand.Execute(null);
        Assert.False(group.Keep);
        Assert.Equal(2, group.Snapshot.OccurrenceCount);
        batch.SelectedItem = batch.Items[0];
        await batch.InspectSelectedCommand.ExecuteAsync(null);
        Assert.Null(group.Keep);
        Assert.True(Assert.Single(group.Members, row => row.Snapshot.ItemId == batch.Items[0].Id).Keep);
        Assert.Empty(fixture.Backend.RemuxRequests);
    }

    /// <summary>Uses the compiled single workspace and preserves expansion, selections and review across live language changes.</summary>
    [AvaloniaFact]
    public async Task CompiledWorkspaceAndRulesShareSelectionsAcrossLanguagesWithoutSeparateTabs()
    {
        var fixture = CreateSimilarEpisodes();
        var window = new MainWindow { DataContext = fixture.Main };
        window.Show();

        try
        {
            await fixture.LoadAsync();
            var batch = fixture.Batch;
            var workspace = Assert.IsType<BatchView>(window.FindControl<BatchView>("Workspace"));
            Assert.Empty(window.GetVisualDescendants().OfType<TabControl>());
            Assert.Null(workspace.FindControl<Button>("ApproveBatchItemButton"));
            Assert.Same(batch.AddFilesCommand, workspace.FindControl<Button>("AddBatchFilesButton")!.Command);
            Assert.Same(batch.ReviewCommand, workspace.FindControl<Button>("ReviewBatchButton")!.Command);
            var group = Assert.Single(batch.AggregateTracks.Groups, row => row.Snapshot.Key.Title == "Captions");
            group.IsExpanded = true;
            await batch.ReviewCommand.ExecuteAsync(null);
            var originalReview = batch.Snapshot.Review;
            var plans = batch.Items.Select(item => item.ApprovedPlan).ToArray();
            await fixture.Main.ChangeLanguageCommand.ExecuteAsync("ru");
            Dispatcher.UIThread.RunJobs();
            Assert.Same(originalReview, batch.Snapshot.Review);
            Assert.Equal(plans, batch.Items.Select(item => item.ApprovedPlan));
            Assert.Same(group, Assert.Single(batch.AggregateTracks.Groups, row => row.Snapshot.Key.Title == "Captions"));
            Assert.True(group.IsExpanded);
            Assert.Contains("из", group.CoverageText);
            await fixture.Main.ChangeLanguageCommand.ExecuteAsync("en");
            Assert.Same(originalReview, batch.Snapshot.Review);
            Assert.True(group.Keep);
            Assert.True(group.IsExpanded);
            var rules = batch.LanguageRules;
            rules.SubtitleRuleEnabled = true;
            rules.SubtitleLanguages = "ru";
            rules.PreviewCommand.Execute(null);
            Assert.True(group.Keep);
            Assert.Same(originalReview, batch.Snapshot.Review);
            rules.ApplyCommand.Execute(null);
            Assert.False(group.Keep);
            Assert.All(group.Members, member => Assert.False(member.Keep));
            Assert.All(batch.Items, item => Assert.Null(item.ApprovedPlan));
            Assert.False(batch.HasReview);
            Assert.All(batch.AggregateTracks.Groups.Where(row => row.IsRequired), row =>
            {
                Assert.True(row.Keep);
                Assert.False(row.CanEdit);
            });
            Assert.Empty(fixture.Backend.RemuxRequests);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Requires explicit file-specific silent-output consent in the ordinary one-item workflow.</summary>
    [AvaloniaFact]
    public async Task OneFileUsesAggregateReviewAndSelectionChangesInvalidateZeroAudioConsent()
    {
        var fixture = CreateSimilarEpisodes();
        SetDiscovery(fixture, [fixture.Backend.Inspections.Values.First()]);
        await fixture.LoadAsync();
        var batch = fixture.Batch;
        var item = Assert.Single(batch.Items);

        foreach (var group in batch.AggregateTracks.Groups.Where(row => row.Snapshot.Key.Kind == TrackKind.Audio))
        {
            group.ToggleKeepCommand.Execute(null);
        }

        Assert.True(item.NeedsZeroAudioConfirmation);
        await batch.ReviewCommand.ExecuteAsync(null);
        Assert.False(batch.CanRun);
        Assert.Contains(item.SourcePath, batch.ReviewIssuesText);
        item.ZeroAudioConfirmed = true;
        await batch.ReviewCommand.ExecuteAsync(null);
        Assert.True(batch.CanRun);
        Assert.True(item.ApprovedPlan!.ZeroAudioConfirmed);
        Assert.DoesNotContain(item.ApprovedPlan.RetainedTracks, track => track.Kind == TrackKind.Audio);
        var audioGroup = batch.AggregateTracks.Groups.First(row => row.Snapshot.Key.Kind == TrackKind.Audio);
        audioGroup.ToggleKeepCommand.Execute(null);
        Assert.False(item.ZeroAudioConfirmed);
        Assert.Null(item.ApprovedPlan);
        Assert.False(batch.CanRun);
        Assert.Empty(fixture.Backend.RemuxRequests);
        Assert.Equal(0, fixture.Outputs.CleanupCount);
    }

    /// <summary>Normalizes only controlled test metadata so grouping must ignore source-local identifiers and defaults.</summary>
    private static BatchViewModelFixture CreateSimilarEpisodes()
    {
        var fixture = new BatchViewModelFixture();

        foreach (var (path, inspection) in fixture.Backend.Inspections.ToArray())
        {
            fixture.Backend.Inspections[path] = inspection with
            {
                Tracks = inspection.Tracks.Select(track => track with
                {
                    Title = track.Kind switch
                    {
                        TrackKind.Video => "Video",
                        TrackKind.Audio when track.IsDefault => SharedAudioTitle,
                        TrackKind.Audio => "Alternative soundtrack",
                        _ => "Captions"
                    }
                }).ToImmutableArray()
            };
        }

        return fixture;
    }

    /// <summary>Creates another controlled source with deliberately different file-local IDs without altering accepted rows.</summary>
    private static MediaInspection AddInspection(BatchViewModelFixture fixture, MediaInspection template, string name, int firstIdentifier)
    {
        var path = Path.Combine(Path.GetDirectoryName(template.Source.FullPath)!, name);
        var inspection = template with
        {
            Source = template.Source with { FullPath = path, FileIdentity = name },
            Tracks = template.Tracks.Select((track, index) => track with
            {
                Id = firstIdentifier + index,
                Uid = (ulong)(firstIdentifier + index + 1000)
            }).ToImmutableArray()
        };
        fixture.Backend.Inspections.Add(path, inspection);
        return inspection;
    }

    /// <summary>Supplies the exact controlled addition through the existing discovery and dialog boundaries.</summary>
    private static void SetDiscovery(BatchViewModelFixture fixture, IEnumerable<MediaInspection> inspections)
    {
        var sources = inspections.Select(inspection => inspection.Source).ToImmutableArray();
        fixture.Dialogs.Sources = sources.Select(source => source.FullPath).ToArray();
        fixture.Discovery.Result = new(sources, []);
    }
}
