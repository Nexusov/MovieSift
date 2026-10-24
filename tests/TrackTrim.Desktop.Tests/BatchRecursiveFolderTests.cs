using System.Collections.Immutable;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using TrackTrim.Application.Batches;
using TrackTrim.Core.Media;
using TrackTrim.Desktop.Views;
using Xunit;

namespace TrackTrim.Desktop.Tests;

/// <summary>Checks recursive input presentation and immutable approvals without claiming native dialog execution.</summary>
public sealed class BatchRecursiveFolderTests
{
    /// <summary>Defaults to direct entries, requires an explicit root, and captures the mode only for a later addition.</summary>
    [AvaloniaFact]
    public async Task FolderModeRequiresOutputAndDoesNotReinterpretAcceptedRows()
    {
        var fixture = new BatchViewModelFixture();
        var view = new BatchView { DataContext = fixture.Batch };
        var window = new Window { Content = view };
        window.Show();

        try
        {
            var options = view.FindControl<Button>("WorkspaceOptions")!;
            options.Flyout!.ShowAt(options);
            Dispatcher.UIThread.RunJobs();
            var control = view.FindControl<CheckBox>("IncludeSubfoldersCheckBox")!;
            Assert.False(fixture.Batch.IncludeSubfolders);
            Assert.False(control.IsChecked);
            Assert.Equal("Include subfolders", control.Content);
            fixture.Batch.IncludeSubfolders = true;
            var outputFolder = fixture.Dialogs.OutputFolder;
            fixture.Dialogs.OutputFolder = null;
            await fixture.Batch.AddFolderCommand.ExecuteAsync(null);
            Assert.Null(fixture.Discovery.LastRequest);
            Assert.Equal(0, fixture.Dialogs.InputFolderRequests);
            Assert.False(fixture.Batch.HasError);
            Assert.True(fixture.Batch.HasPreparationNotice);
            Assert.Equal(fixture.Localization["RecursiveOutputGuide"], fixture.Batch.PreparationNoticeText);

            SetFolderInputs(fixture);
            fixture.Dialogs.OutputFolder = outputFolder;
            await fixture.Batch.AddFolderCommand.ExecuteAsync(null);
            Assert.Equal(BatchInputKind.RecursiveFolder, fixture.Discovery.LastRequest!.Kind);
            Assert.Equal(fixture.Batch.OutputFolder, fixture.Discovery.LastRequest.OutputRoot);
            Assert.Equal(2, fixture.Batch.Items.Count);
            Assert.Equal([Path.Combine("Season 1", "episode.mkv"), Path.Combine("Season 2", "episode.mkv")],
                fixture.Batch.Items.Select(item => item.RelativeSourcePath));
            Assert.All(fixture.Batch.Items, item => Assert.True(item.HasFolderOrigin));
            Assert.All(fixture.Batch.Items, item => Assert.Null(item.ApprovedPlan));
            ApproveItems(fixture);
            await fixture.Batch.ReviewCommand.ExecuteAsync(null);
            var before = fixture.Session.Snapshot;
            var paths = fixture.Batch.Items.Select(item => item.OutputPath).ToArray();
            Assert.Equal([Path.Combine(fixture.Batch.OutputFolder, "Season 1", "episode.trimmed.mkv"),
                Path.Combine(fixture.Batch.OutputFolder, "Season 2", "episode.trimmed.mkv")], paths);
            Assert.All(before.Items, item => Assert.Equal(fixture.Batch.OutputFolder, item.ApprovedPlan!.OutputDirectoryRoot));

            await fixture.Main.ChangeLanguageCommand.ExecuteAsync("ru");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Включать вложенные папки", control.Content);
            Assert.True(fixture.Batch.IncludeSubfolders);
            Assert.True(control.IsChecked);
            Assert.Same(before, fixture.Session.Snapshot);
            await fixture.Main.ChangeLanguageCommand.ExecuteAsync("en");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Include subfolders", control.Content);
            Assert.True(fixture.Batch.IncludeSubfolders);
            Assert.Equal(paths, fixture.Batch.Items.Select(item => item.OutputPath));
            Assert.Same(before, fixture.Session.Snapshot);
            Assert.Empty(fixture.Backend.RemuxRequests);

            fixture.Batch.IncludeSubfolders = false;
            Assert.Same(before, fixture.Session.Snapshot);
            Assert.Equal(paths, fixture.Batch.Items.Select(item => item.OutputPath));

            fixture.Batch.IncludeSubfolders = true;
            fixture.Batch.NewBatchCommand.Execute(null);
            fixture.Batch.ConfirmNewBatchCommand.Execute(null);
            Assert.Empty(fixture.Batch.Items);
            Assert.True(fixture.Batch.IncludeSubfolders);
            Assert.Equal(fixture.Dialogs.OutputFolder, fixture.Batch.OutputFolder);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Recomputes season destinations from original roots while keeping explicit files flat and duplicates untouched.</summary>
    [AvaloniaFact]
    public async Task MixedAdditionsKeepOriginalProvenanceAndRootChangesRequireFreshApprovals()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.LoadAsync();
        var explicitItem = fixture.Batch.Items[0];
        var explicitSource = explicitItem.Snapshot.DiscoveredSource!;
        fixture.Batch.SelectedItem = fixture.Batch.Items[1];
        fixture.Batch.RemoveSelectedCommand.Execute(null);
        var sourceRoot = Path.Combine(Path.GetTempPath(), "recursive extra source");
        var folderPath = Path.Combine(sourceRoot, "Season 2", "episode.mkv");
        var template = fixture.Backend.Inspections.Values.Last();
        var folderInspection = template with { Source = template.Source with { FullPath = folderPath } };
        fixture.Backend.Inspections.Add(folderPath, folderInspection);
        var aliasPath = Path.Combine(sourceRoot, "Season 1", "alias.mkv");
        fixture.Discovery.Result = new([explicitSource with { FullPath = aliasPath }, folderInspection.Source], [],
            FolderOrigins: new Dictionary<string, FolderSourceOrigin>
            {
                [aliasPath] = new(sourceRoot, Path.Combine("Season 1", "alias.mkv")),
                [folderPath] = new(sourceRoot, Path.Combine("Season 2", "episode.mkv"))
            }.ToImmutableDictionary());
        fixture.Batch.IncludeSubfolders = true;
        await fixture.Batch.AddFolderCommand.ExecuteAsync(null);
        Assert.Equal(2, fixture.Batch.Items.Count);
        Assert.Same(explicitItem, fixture.Batch.Items[0]);
        Assert.Null(explicitItem.Snapshot.FolderOrigin);
        var folderItem = fixture.Batch.Items[1];
        var origin = folderItem.Snapshot.FolderOrigin;
        ApproveItems(fixture);
        await fixture.Batch.ReviewCommand.ExecuteAsync(null);
        var reviewed = fixture.Session.Snapshot;
        await fixture.Batch.AddFolderCommand.ExecuteAsync(null);
        Assert.Same(reviewed, fixture.Session.Snapshot);
        Assert.Same(origin, folderItem.Snapshot.FolderOrigin);

        fixture.Dialogs.OutputFolder = Path.Combine(Path.GetTempPath(), "new recursive output Кириллица");
        await fixture.Batch.ChooseOutputCommand.ExecuteAsync(null);
        Assert.Equal(Path.Combine(fixture.Batch.OutputFolder, "episode A.trimmed.mkv"), explicitItem.OutputPath);
        Assert.Equal(Path.Combine(fixture.Batch.OutputFolder, "Season 2", "episode.trimmed.mkv"), folderItem.OutputPath);
        Assert.All(fixture.Batch.Items, item => Assert.Null(item.ApprovedPlan));
        Assert.False(fixture.Batch.HasReview);
        Assert.Same(origin, folderItem.Snapshot.FolderOrigin);
        ApproveItems(fixture);
        await fixture.Batch.ReviewCommand.ExecuteAsync(null);
        var unchanged = fixture.Session.Snapshot;
        await fixture.Batch.ChooseOutputCommand.ExecuteAsync(null);
        Assert.Same(unchanged, fixture.Session.Snapshot);
        Assert.Null(explicitItem.ApprovedPlan!.OutputDirectoryRoot);
        Assert.Equal(fixture.Batch.OutputFolder, folderItem.ApprovedPlan!.OutputDirectoryRoot);
        Assert.Empty(fixture.Backend.RemuxRequests);
    }

    /// <summary>Keeps explicit manual destinations while limiting missing-parent permission to the selected output root.</summary>
    [AvaloniaFact]
    public async Task ManualDestinationOutsideOutputRootUsesTheOrdinaryExistingParentContract()
    {
        var fixture = new BatchViewModelFixture();
        SetFolderInputs(fixture);
        await fixture.Batch.ChooseOutputCommand.ExecuteAsync(null);
        await fixture.Batch.AddFolderCommand.ExecuteAsync(null);
        var item = fixture.Batch.Items[0];
        fixture.Batch.SelectedItem = item;
        item.Tracks.Single(track => track.Track.Kind == TrackKind.Subtitles).Keep = false;
        item.OutputPath = Path.Combine(Path.GetTempPath(), "manual separate episode.mkv");
        fixture.Batch.ApproveSelectedCommand.Execute(null);
        Assert.Null(item.ApprovedPlan!.OutputDirectoryRoot);
        item.OutputPath = Path.Combine(fixture.Batch.OutputFolder, "manual season", "different episode.mkv");
        Assert.Null(item.ApprovedPlan);
        fixture.Batch.ApproveSelectedCommand.Execute(null);
        Assert.Equal(fixture.Batch.OutputFolder, item.ApprovedPlan!.OutputDirectoryRoot);
        Assert.Equal(item.OutputPath, item.ApprovedPlan.DestinationPath);
        Assert.Empty(fixture.Backend.RemuxRequests);
    }

    /// <summary>Distinguishes pre-accept cancellation from cancellation after rows have entered automatic inspection.</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationPreservesTheDiscoveryAcceptanceBoundary(bool cancelDuringInspection)
    {
        var fixture = new BatchViewModelFixture();
        SetFolderInputs(fixture);
        await fixture.Batch.ChooseOutputCommand.ExecuteAsync(null);
        var before = fixture.Session.Snapshot;

        if (cancelDuringInspection)
        {
            fixture.Backend.OnInspect = request =>
            {
                fixture.Batch.CancelCommand.Execute(null);
                request.CancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            };
        }
        else
        {
            fixture.Discovery.OnDiscover = _ =>
            {
                fixture.Batch.CancelCommand.Execute(null);
                return Task.FromResult(fixture.Discovery.Result!);
            };
        }

        await fixture.Batch.AddFolderCommand.ExecuteAsync(null);

        if (cancelDuringInspection)
        {
            Assert.Equal(2, fixture.Session.Snapshot.Items.Length);
            Assert.Contains(fixture.Session.Snapshot.Items, item => item.State == BatchItemState.Cancelled);
            Assert.Contains(fixture.Session.Snapshot.Items, item => item.State == BatchItemState.PendingInspection);
            Assert.All(fixture.Session.Snapshot.Items, item => Assert.NotNull(item.FolderOrigin));
        }
        else
        {
            Assert.Same(before, fixture.Session.Snapshot);
            Assert.Empty(fixture.Batch.Items);
        }

        Assert.Empty(fixture.Backend.RemuxRequests);
    }

    /// <summary>Uses the existing workspace gate for both the compiled checkbox and direct setting edits.</summary>
    [AvaloniaFact]
    public void ConflictingWorkspaceDisablesRecursionWithoutLosingItsSetting()
    {
        var fixture = new BatchViewModelFixture();
        var view = new BatchView { DataContext = fixture.Batch };
        var window = new Window { Content = view };
        window.Show();

        try
        {
            fixture.Batch.IncludeSubfolders = true;
            fixture.Main.IsBusy = true;
            Dispatcher.UIThread.RunJobs();
            var options = view.FindControl<Button>("WorkspaceOptions")!;
            options.Flyout!.ShowAt(options);
            Dispatcher.UIThread.RunJobs();
            var control = view.FindControl<CheckBox>("IncludeSubfoldersCheckBox")!;
            Assert.False(control.IsEffectivelyEnabled);
            fixture.Batch.IncludeSubfolders = false;
            Assert.True(fixture.Batch.IncludeSubfolders);
            Assert.False(fixture.Batch.AddFolderCommand.CanExecute(null));
            fixture.Main.IsBusy = false;
            Dispatcher.UIThread.RunJobs();
            Assert.True(control.IsEffectivelyEnabled);
            Assert.True(control.IsChecked);
            Assert.True(fixture.Batch.AddFolderCommand.CanExecute(null));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Provides two independent fake inspections with identical basenames and distinct immutable origins.</summary>
    private static void SetFolderInputs(BatchViewModelFixture fixture)
    {
        var sourceRoot = Path.Combine(Path.GetTempPath(), "TrackTrim recursive UI источники");
        var originals = fixture.Backend.Inspections.Values.ToArray();
        fixture.Backend.Inspections.Clear();
        var origins = ImmutableDictionary.CreateBuilder<string, FolderSourceOrigin>();

        for (var index = 0; index < originals.Length; index++)
        {
            var relative = Path.Combine($"Season {index + 1}", "episode.mkv");
            var path = Path.Combine(sourceRoot, relative);
            fixture.Backend.Inspections.Add(path, originals[index] with { Source = originals[index].Source with { FullPath = path } });
            origins.Add(path, new(sourceRoot, relative));
        }

        fixture.Discovery.Result = new(fixture.Backend.Inspections.Values.Select(inspection => inspection.Source).ToImmutableArray(),
            [], FolderOrigins: origins.ToImmutable());
    }

    /// <summary>Approves one intentional subtitle removal from each file's actual local inspection.</summary>
    private static void ApproveItems(BatchViewModelFixture fixture)
    {
        foreach (var item in fixture.Batch.Items)
        {
            fixture.Batch.SelectedItem = item;
            item.Tracks.Single(track => track.Track.Kind == TrackKind.Subtitles).Keep = false;
            fixture.Batch.ApproveSelectedCommand.Execute(null);
            Assert.NotNull(item.ApprovedPlan);
        }
    }
}
