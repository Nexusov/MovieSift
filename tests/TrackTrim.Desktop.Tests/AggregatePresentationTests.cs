using System.Collections.Immutable;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TrackTrim.Core.Media;
using TrackTrim.Desktop.ViewModels;
using TrackTrim.Desktop.Views;
using Xunit;

namespace TrackTrim.Desktop.Tests;

/// <summary>Checks visible collapsed metadata and accessible names without changing aggregate identity or authority.</summary>
public sealed class AggregatePresentationTests
{
    /// <summary>Distinguishes equal-looking subtitle groups using their actual forced flag before expansion.</summary>
    [AvaloniaFact]
    public async Task CollapsedSubtitlesExposeForcedStatusAndAccessibleDistinction()
    {
        var fixture = new BatchViewModelFixture();
        var first = fixture.Backend.Inspections.Values.First();
        var subtitle = first.Tracks.Single(track => track.Kind == TrackKind.Subtitles);
        fixture.Backend.Inspections[first.Source.FullPath] = first with
        {
            Tracks = first.Tracks.Add(subtitle with { Id = 100, Uid = 900, IsForced = true })
        };
        await fixture.LoadAsync();
        var ordinary = Assert.Single(fixture.Batch.AggregateTracks.Groups, group => group.Snapshot.Key.Title == subtitle.Title && !group.Snapshot.Key.IsForced);
        var forced = Assert.Single(fixture.Batch.AggregateTracks.Groups, group => group.Snapshot.Key.Title == subtitle.Title && group.Snapshot.Key.IsForced);
        var view = new AggregateTracksView { DataContext = fixture.Batch.AggregateTracks };
        var window = new Window { Content = view };
        window.Show();

        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.False(ordinary.IsExpanded);
            Assert.False(forced.IsExpanded);
            Assert.NotEqual(CollapsedFlags(view, ordinary), CollapsedFlags(view, forced));
            Assert.Contains("Forced: yes", CollapsedFlags(view, forced));
            Assert.Contains("Forced: no", CollapsedFlags(view, ordinary));
            Assert.NotEqual(AccessibleName(view, ordinary), AccessibleName(view, forced));
            Assert.Contains(forced.CodecText, AccessibleName(view, forced));
            Assert.All(fixture.Batch.Items, item => Assert.Null(item.ApprovedPlan));
            Assert.Empty(fixture.Backend.RemuxRequests);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Makes semantic roles and codec/channel differences visible to users and assistive technology.</summary>
    [AvaloniaFact]
    public async Task SemanticRolesAndAudioFormatArePartOfCollapsedAndAccessiblePresentation()
    {
        var fixture = new BatchViewModelFixture();
        var first = fixture.Backend.Inspections.Values.First();
        var audio = first.Tracks.First(track => track.Kind == TrackKind.Audio);
        fixture.Backend.Inspections[first.Source.FullPath] = first with
        {
            Tracks = first.Tracks.AddRange(new MediaTrack[] {
                audio with { Id = 100, Uid = 900, Dispositions = [TrackDisposition.Commentary] },
                audio with { Id = 101, Uid = 901, Dispositions = [TrackDisposition.HearingImpaired] },
                audio with { Id = 102, Uid = 902, Channels = 6, Codec = "different-codec" } })
        };
        await fixture.LoadAsync();
        var groups = fixture.Batch.AggregateTracks.Groups.Where(group => group.TitleText == audio.Title).ToArray();
        Assert.Equal(4, groups.Length);
        var view = new AggregateTracksView { DataContext = fixture.Batch.AggregateTracks };
        var window = new Window { Content = view };
        window.Show();

        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(4, groups.Select(group => AccessibleName(view, group)).Distinct().Count());

            foreach (var group in groups)
            {
                Assert.Contains(group.CodecText, AccessibleName(view, group));
                Assert.Contains(group.ChannelsText, AccessibleName(view, group));
                Assert.False(group.IsExpanded);
            }

            var commentary = Assert.Single(groups, group => !group.Snapshot.Members[0].Track.Dispositions.IsDefaultOrEmpty && group.Snapshot.Members[0].Track.Dispositions.Contains(TrackDisposition.Commentary));
            var accessible = Assert.Single(groups, group => !group.Snapshot.Members[0].Track.Dispositions.IsDefaultOrEmpty && group.Snapshot.Members[0].Track.Dispositions.Contains(TrackDisposition.HearingImpaired));
            Assert.Contains("commentary", CollapsedFlags(view, commentary));
            Assert.Contains("commentary", AccessibleName(view, commentary));
            Assert.Contains("hearing impaired", CollapsedFlags(view, accessible));
            Assert.Contains("hearing impaired", AccessibleName(view, accessible));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Summarizes varied default/enabled flags while retaining one group, original member flags and valid review.</summary>
    [AvaloniaFact]
    public async Task MixedFlagsAndLanguageSwitchPreserveMembersSelectionsAndReview()
    {
        var fixture = new BatchViewModelFixture();
        const string sharedTitle = "Same audio metadata";

        foreach (var (path, inspection) in fixture.Backend.Inspections.ToArray())
        {
            var isFirst = inspection.Source == fixture.Backend.Inspections.Values.First().Source;
            fixture.Backend.Inspections[path] = inspection with
            {
                Tracks = inspection.Tracks.Select(track => track.Kind == TrackKind.Audio && track.IsDefault
                    ? track with { Title = sharedTitle, IsDefault = isFirst, IsEnabled = isFirst }
                    : track).ToImmutableArray()
            };
        }

        await fixture.LoadAsync();
        var group = Assert.Single(fixture.Batch.AggregateTracks.Groups, row => row.TitleText == sharedTitle);
        Assert.Equal(2, group.Snapshot.FileCoverage);
        var members = group.Snapshot.Members.ToArray();
        await fixture.Batch.ReviewCommand.ExecuteAsync(null);
        var review = fixture.Batch.Snapshot.Review;
        Assert.NotNull(review);
        var plans = fixture.Batch.Items.Select(item => item.ApprovedPlan).ToArray();
        var view = new AggregateTracksView { DataContext = fixture.Batch.AggregateTracks };
        var window = new Window { Content = view };
        window.Show();

        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("default: varies", CollapsedFlags(view, group));
            Assert.Contains("enabled: varies", CollapsedFlags(view, group));
            Assert.Contains("default", group.Members[0].FlagsText);
            Assert.DoesNotContain("default", group.Members[1].FlagsText);
            Assert.Contains("disabled", group.Members[1].FlagsText);
            var english = CollapsedFlags(view, group);
            var englishAccessible = AccessibleName(view, group);
            await fixture.Main.ChangeLanguageCommand.ExecuteAsync("ru");
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("по умолчанию: различается", CollapsedFlags(view, group));
            Assert.Contains("включена: различается", CollapsedFlags(view, group));
            Assert.NotEqual(englishAccessible, AccessibleName(view, group));
            await fixture.Main.ChangeLanguageCommand.ExecuteAsync("en");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(english, CollapsedFlags(view, group));
            Assert.Equal(englishAccessible, AccessibleName(view, group));
            Assert.Same(review, fixture.Batch.Snapshot.Review);
            Assert.Equal(plans, fixture.Batch.Items.Select(item => item.ApprovedPlan));
            Assert.Equal(members.Select(member => member.Track), group.Snapshot.Members.Select(member => member.Track));
            Assert.All(group.Members, member => Assert.True(member.Keep));
            Assert.Empty(fixture.Backend.RemuxRequests);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Reads the actual summary bound into the collapsed group template.</summary>
    private static string CollapsedFlags(AggregateTracksView view, AggregateTrackGroupViewModel group) =>
        Assert.Single(view.GetVisualDescendants().OfType<TextBlock>(), control =>
            control.Name == "AggregateGroupFlagsText" && ReferenceEquals(control.DataContext, group)).Text!;

    /// <summary>Reads the actual checkbox automation property rather than only its ViewModel source.</summary>
    private static string AccessibleName(AggregateTracksView view, AggregateTrackGroupViewModel group) =>
        AutomationProperties.GetName(Assert.Single(view.GetVisualDescendants().OfType<CheckBox>(), control =>
            ReferenceEquals(control.DataContext, group) && ReferenceEquals(control.Command, group.ToggleKeepCommand)))!;
}
