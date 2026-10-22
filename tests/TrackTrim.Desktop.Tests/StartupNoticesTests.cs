using Avalonia.Headless.XUnit;
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
}
