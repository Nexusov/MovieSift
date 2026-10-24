using Avalonia.Headless.XUnit;
using TrackTrim.Application.Ports;
using TrackTrim.Desktop.Views;
using Xunit;

namespace TrackTrim.Desktop.Tests;

/// <summary>Shows unresolved ownership records independently of portable language-setting failures.</summary>
public sealed class StartupNoticesTests
{
    /// <summary>Explains invalid records in the selected language without offering automatic deletion.</summary>
    [AvaloniaTheory]
    [InlineData("en", "needs manual review")]
    [InlineData("ru", "требует ручной проверки")]
    public async Task InvalidOwnershipRecordOffersOnlyLocalizedManualReview(string language, string expectedNotice)
    {
        const string recordPath = "untrusted-record.json";
        var fixture = new ViewModelFixture();
        fixture.Outputs.Interrupted = [new(null, "InvalidRecord", false, recordPath)];
        var viewModel = fixture.ViewModel;
        await viewModel.ChangeLanguageCommand.ExecuteAsync(language);
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        Assert.True(viewModel.HasInterrupted);
        Assert.False(viewModel.HasCleanableInterrupted);
        Assert.Contains(expectedNotice, viewModel.InterruptedDetails);
        Assert.Contains(recordPath, viewModel.InterruptedDetails);
        Assert.False(viewModel.CleanInterruptedCommand.CanExecute(null));
        Assert.Equal(0, fixture.Outputs.CleanupCount);
        window.Close();
    }

    /// <summary>Still discovers invalid ownership notices after portable language settings cannot be read.</summary>
    [AvaloniaFact]
    public async Task SettingsLoadFailureDoesNotHideInterruptedOwnershipRecords()
    {
        var fixture = new ViewModelFixture();
        fixture.Settings.LoadException = new IOException("Synthetic settings read failure.");
        fixture.Outputs.Interrupted = [new(null, "InvalidRecord", false, "untrusted-record.json")];
        await fixture.ViewModel.InitializeAsync();
        Assert.True(fixture.ViewModel.PersistenceFailed);
        Assert.True(fixture.ViewModel.HasInterrupted);
        Assert.Contains("needs manual review", fixture.ViewModel.InterruptedDetails);
        Assert.Equal(1, fixture.Outputs.DiscoveryCount);
        Assert.Equal(0, fixture.Outputs.CleanupCount);
    }

    /// <summary>Prevents interrupted-record cleanup from competing with the unified media owner.</summary>
    [AvaloniaFact]
    public async Task ActiveUnifiedJobBlocksInterruptedCleanupWithoutLosingNotice()
    {
        var fixture = new ViewModelFixture();
        fixture.Outputs.Interrupted = [CreateCleanableRecord()];
        await fixture.ViewModel.InitializeAsync();
        await fixture.LoadAsync();
        fixture.Item.Tracks[1].Keep = false;
        await fixture.Workspace.ReviewCommand.ExecuteAsync(null);
        fixture.Backend.ContinueRemux = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var execution = fixture.Workspace.RunCommand.ExecuteAsync(null);
        await fixture.Backend.RemuxStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            Assert.True(fixture.ViewModel.HasInterrupted);
            Assert.False(fixture.ViewModel.CleanInterruptedCommand.CanExecute(null));
            Assert.True(fixture.ViewModel.HasActiveOperation);
            Assert.Equal(0, fixture.Outputs.CleanupCount);
        }
        finally
        {
            fixture.Backend.ContinueRemux.TrySetResult();
            await execution;
        }
    }

    /// <summary>Keeps explicit cleanup failures visible and blocks subsequent media writes safely.</summary>
    [AvaloniaFact]
    public async Task InterruptedCleanupFailureRemainsVisibleAndBlocksWorkspace()
    {
        const string detail = "Controlled interrupted cleanup access denied.";
        var fixture = new ViewModelFixture();
        fixture.Outputs.Interrupted = [CreateCleanableRecord()];
        fixture.Outputs.CleanupIssues = [new("fixture-owned", detail)];
        await fixture.ViewModel.InitializeAsync();
        await fixture.ViewModel.CleanInterruptedCommand.ExecuteAsync(null);

        Assert.True(fixture.ViewModel.HasInterrupted);
        Assert.True(fixture.ViewModel.HasCleanupIssue);
        Assert.True(fixture.ViewModel.RequiresCloseDecision);
        Assert.Contains(detail, fixture.ViewModel.Diagnostics);
        Assert.False(fixture.Workspace.AddFilesCommand.CanExecute(null));
        Assert.False(fixture.ViewModel.CleanInterruptedCommand.CanExecute(null));
        Assert.Equal(1, fixture.Outputs.CleanupCount);
        Assert.Equal(0, fixture.Backend.RemuxCount);
    }

    /// <summary>Creates controlled ownership evidence without touching files or claiming restart acceptance.</summary>
    private static InterruptedOutput CreateCleanableRecord() => new(
        new OwnedOutput(Guid.NewGuid(), "fixture-owned", "fixture-candidate.mkv", ViewModelFixture.OutputPath, "fixture-token"),
        "Interrupted", true, null);
}
