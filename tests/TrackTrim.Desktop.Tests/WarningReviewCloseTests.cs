using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using TrackTrim.Desktop.Views;
using Xunit;

namespace TrackTrim.Desktop.Tests;

/// <summary>Exercises explicit close decisions while a verified warning candidate remains unpublished.</summary>
public sealed class WarningReviewCloseTests
{
    /// <summary>Requires a user decision before an idle warning review can leave the application.</summary>
    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ru")]
    public async Task WarningReviewCloseRequiresExplicitDecision(string language)
    {
        var fixture = new ViewModelFixture();
        var viewModel = fixture.ViewModel;
        var window = await ShowWarningReviewAsync(fixture);
        await viewModel.ChangeLanguageCommand.ExecuteAsync(language);
        Assert.True(viewModel.HasWarningCandidate);

        try
        {
            window.Close();
            Assert.True(window.IsVisible);
            Assert.True(viewModel.HasWarningCandidate);
            Assert.True(viewModel.HasWarningCloseDecision);
            Assert.Equal(1, fixture.Backend.RemuxCount);
            Assert.Equal(0, fixture.Outputs.CleanupCount);
            var remainButton = window.FindControl<Button>("RemainInWarningReviewButton")!;
            Assert.Equal(viewModel.Localization["RemainInWarningReview"], remainButton.Content);
            Assert.Equal(viewModel.Localization["DiscardWarningsAndClose"], window.FindControl<Button>("DiscardWarningsAndCloseButton")!.Content);
            remainButton.Command!.Execute(null);
            Assert.True(window.IsVisible);
            Assert.True(viewModel.HasWarningCandidate);
            Assert.False(viewModel.HasWarningCloseDecision);
            Assert.Equal(0, fixture.Outputs.CleanupCount);
        }
        finally
        {
            await fixture.Workspace.DiscardWarningsCommand.ExecuteAsync(null);
            window.Close();
        }
    }

    /// <summary>Awaits one disposal even when repeated close requests arrive while cleanup is blocked.</summary>
    [AvaloniaFact]
    public async Task DiscardAndCloseAwaitsCleanupExactlyOnceWithoutRemuxing()
    {
        var fixture = new ViewModelFixture();
        fixture.Outputs.ContinueCleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = await ShowWarningReviewAsync(fixture);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();

        try
        {
            window.Close();
            var discardButton = window.FindControl<Button>("DiscardWarningsAndCloseButton")!;
            discardButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await fixture.Outputs.CleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(window.IsVisible);
            Assert.True(fixture.Workspace.IsBusy);
            window.Close();
            window.Close();
            discardButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, fixture.Outputs.CleanupCount);
            fixture.Outputs.ContinueCleanup.TrySetResult();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(window.IsVisible);
            Assert.False(fixture.ViewModel.HasWarningCandidate);
            Assert.Equal(1, fixture.Outputs.CleanupCount);
            Assert.Equal(1, fixture.Backend.RemuxCount);
        }
        finally
        {
            fixture.Outputs.ContinueCleanup.TrySetResult();
            await fixture.ViewModel.ShutdownAsync();
            window.Close();
        }
    }

    /// <summary>Keeps cleanup failures visible instead of closing or attempting disposal again.</summary>
    [AvaloniaFact]
    public async Task DiscardAndCloseFailureKeepsWindowOpenWithCleanupDiagnostics()
    {
        const string failureDetail = "Synthetic warning-candidate cleanup failure.";
        var fixture = new ViewModelFixture();
        fixture.Outputs.CleanupIssues = [new("fixture-candidate.mkv", failureDetail)];
        var window = await ShowWarningReviewAsync(fixture);
        var viewModel = fixture.ViewModel;
        await viewModel.ChangeLanguageCommand.ExecuteAsync("ru");
        window.Close();
        var discardButton = window.FindControl<Button>("DiscardWarningsAndCloseButton")!;
        discardButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await fixture.Workspace.DiscardWarningsCommand.ExecutionTask!.WaitAsync(TimeSpan.FromSeconds(5));
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsVisible);
        Assert.True(viewModel.HasCleanupIssue);
        Assert.Contains(failureDetail, fixture.Item.Diagnostics);
        Assert.True(fixture.Workspace.HasCleanupIssue);
        Assert.Equal(1, fixture.Outputs.CleanupCount);
        Assert.Equal(1, fixture.Backend.RemuxCount);
        discardButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1, fixture.Outputs.CleanupCount);
        window.FindControl<Button>("CloseDespiteUncertaintyButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>Runs the real application coordinator into verified warning review using deterministic ports.</summary>
    private static async Task<MainWindow> ShowWarningReviewAsync(ViewModelFixture fixture)
    {
        var viewModel = fixture.ViewModel;
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        await fixture.LoadAsync();
        fixture.Backend.ExitCode = 1;
        fixture.Item.Tracks[1].Keep = false;
        await fixture.Workspace.ReviewCommand.ExecuteAsync(null);
        viewModel.ToggleAppearanceCommand.Execute(null);
        Assert.True(viewModel.IsAppearanceOpen);
        await fixture.Workspace.RunCommand.ExecuteAsync(null);
        Assert.True(viewModel.HasWarningCandidate);
        Assert.False(viewModel.IsAppearanceOpen);
        Assert.True(viewModel.HasPendingMediaDecision);
        Assert.True(viewModel.ToggleAppearanceCommand.CanExecute(null));
        return window;
    }
}
