using Avalonia.Headless.XUnit;
using MovieSift.Application.Batches;
using Xunit;

namespace MovieSift.Desktop.Tests;

/// <summary>Separates optional language durability from mandatory media ownership availability.</summary>
public sealed class LanguagePreferenceFailureTests
{
    /// <summary>A failed preference read must not prevent an otherwise healthy reviewed media job.</summary>
    [AvaloniaFact]
    public async Task LanguageReadFailureAllowsHealthyMediaExecution()
    {
        var fixture = new ViewModelFixture();
        fixture.Settings.LoadException = new IOException("Controlled preference read failure.");
        await fixture.ViewModel.InitializeAsync();
        await fixture.LoadAsync();
        fixture.Item.Tracks[1].Keep = false;
        await fixture.Workspace.ReviewCommand.ExecuteAsync(null);
        Assert.True(fixture.Workspace.HasCurrentReview);
        Assert.True(fixture.Workspace.CanRun);
        await fixture.Workspace.RunCommand.ExecuteAsync(null);
        Assert.Equal(BatchPhase.Completed, fixture.Workspace.Snapshot.Phase);
        Assert.Equal(1, fixture.Backend.RemuxCount);
        Assert.Equal(1, fixture.Outputs.DiscoveryCount);
        Assert.True(fixture.ViewModel.LanguagePreferenceFailed);
        Assert.False(fixture.ViewModel.PersistenceFailed);
    }

    /// <summary>A failed save stays session-only until a later successful save, without revoking Review.</summary>
    [AvaloniaFact]
    public async Task PreferenceSaveFailureAndRecoveryPreserveMediaAuthority()
    {
        var fixture = new ViewModelFixture();
        await fixture.ViewModel.InitializeAsync();
        await fixture.LoadAsync();
        fixture.Item.Tracks[1].Keep = false;
        await fixture.Workspace.ReviewCommand.ExecuteAsync(null);
        var review = fixture.Workspace.Snapshot.Review;
        fixture.Settings.SaveException = new IOException("Controlled preference save failure.");
        await fixture.ViewModel.ChangeLanguageCommand.ExecuteAsync("ru");
        Assert.Equal("ru", fixture.ViewModel.Localization.Language);
        Assert.True(fixture.ViewModel.LanguagePreferenceFailed);
        Assert.False(fixture.ViewModel.PersistenceFailed);
        Assert.True(fixture.Workspace.CanRun);
        Assert.Same(review, fixture.Workspace.Snapshot.Review);

        fixture.Settings.SaveException = null;
        await fixture.ViewModel.ChangeLanguageCommand.ExecuteAsync("en");
        Assert.Equal("en", fixture.Settings.Language);
        Assert.False(fixture.ViewModel.LanguagePreferenceFailed);
        Assert.True(fixture.Workspace.CanRun);
        Assert.Same(review, fixture.Workspace.Snapshot.Review);
    }

    /// <summary>Preference recovery cannot clear unavailable required data or unreadable ownership records.</summary>
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RequiredStorageFailureRemainsBlockedAfterSuccessfulLanguageSave(bool dataWritable)
    {
        var fixture = new ViewModelFixture(dataWritable);

        if (dataWritable)
        {
            fixture.Outputs.DiscoveryException = new IOException("Controlled ownership read failure.");
        }

        var main = fixture.ViewModel;
        await main.InitializeAsync();
        await fixture.LoadAsync();
        fixture.Item.Tracks[1].Keep = false;
        await fixture.Workspace.ReviewCommand.ExecuteAsync(null);
        await main.ChangeLanguageCommand.ExecuteAsync("ru");
        Assert.True(main.PersistenceFailed);
        Assert.False(main.LanguagePreferenceFailed);
        Assert.True(fixture.Workspace.HasCurrentReview);
        Assert.False(fixture.Workspace.CanRun);
        Assert.Equal(0, fixture.Backend.RemuxCount);
    }
}
