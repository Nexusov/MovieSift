using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using TrackTrim.Desktop.Views;
using Xunit;

namespace TrackTrim.Desktop.Tests;

/// <summary>Tests compiled bindings, review invalidation and bilingual live-job presentation.</summary>
public sealed class MainWindowTests
{
    /// <summary>Shows no-change evidence without invoking a redundant remux from the UI.</summary>
    [AvaloniaFact]
    public async Task UnchangedSelectionProducesVisibleNoChangeWithoutRemux()
    {
        var fixture = new ViewModelFixture();
        await fixture.LoadAsync();
        var viewModel = fixture.ViewModel;
        viewModel.ReviewPlanCommand.Execute(null);
        await viewModel.RunCommand.ExecuteAsync(null);
        Assert.Equal(0, fixture.Backend.RemuxCount);
        Assert.Contains("No tracks removed", viewModel.StatusText);
        Assert.False(viewModel.HasError);
    }

    /// <summary>Loads the real compiled XAML and refreshes its text without replacing inspection state.</summary>
    [AvaloniaFact]
    public async Task LanguageSwitchUpdatesVisibleBindingsAndPreservesSelections()
    {
        var fixture = new ViewModelFixture();
        var viewModel = fixture.ViewModel;
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        await fixture.LoadAsync();
        viewModel.Tracks[1].Keep = false;
        viewModel.ReviewPlanCommand.Execute(null);
        var selectedTrack = viewModel.Tracks[2];
        await viewModel.ChangeLanguageCommand.ExecuteAsync("ru");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Открыть MKV…", window.FindControl<Button>("OpenButton")!.Content);
        Assert.Equal("ru", fixture.Settings.Language);
        Assert.False(viewModel.Tracks[1].Keep);
        Assert.Same(selectedTrack, viewModel.Tracks[2]);
        Assert.True(viewModel.HasPlan);
        Assert.Equal("Аудио", selectedTrack.KindText);
        window.Close();
    }

    /// <summary>Requires a fresh immutable review when Keep state or destination changes.</summary>
    [AvaloniaFact]
    public async Task ChangingSelectionOrDestinationInvalidatesTheReview()
    {
        var fixture = new ViewModelFixture();
        await fixture.LoadAsync();
        var viewModel = fixture.ViewModel;
        Assert.All(viewModel.Tracks, track => Assert.True(track.Keep));
        viewModel.ReviewPlanCommand.Execute(null);
        Assert.True(viewModel.CanRun);
        viewModel.Tracks[1].Keep = false;
        Assert.False(viewModel.HasPlan);
        Assert.False(viewModel.RunCommand.CanExecute(null));
        viewModel.ReviewPlanCommand.Execute(null);
        viewModel.OutputPath = "changed-output.mkv";
        Assert.False(viewModel.HasPlan);
        viewModel.Tracks[0].Keep = false;
        Assert.True(viewModel.Tracks[0].Keep);
    }

    /// <summary>Shows explicit confirmation before a plan can remove all existing audio.</summary>
    [AvaloniaFact]
    public async Task RemovingEveryAudioRequiresExplicitConfirmation()
    {
        var fixture = new ViewModelFixture();
        await fixture.LoadAsync();
        var viewModel = fixture.ViewModel;
        viewModel.Tracks[1].Keep = false;
        viewModel.Tracks[2].Keep = false;
        Assert.True(viewModel.NeedsZeroAudioConfirmation);
        viewModel.ReviewPlanCommand.Execute(null);
        Assert.False(viewModel.HasPlan);
        Assert.True(viewModel.HasError);
        viewModel.ZeroAudioConfirmed = true;
        viewModel.ReviewPlanCommand.Execute(null);
        Assert.True(viewModel.HasPlan);
    }

    /// <summary>Allows language switching during actual application coordination without cancelling its worker.</summary>
    [AvaloniaFact]
    public async Task LanguageSwitchDuringJobKeepsTheJobAndProgress()
    {
        var fixture = new ViewModelFixture();
        await fixture.LoadAsync();
        fixture.Backend.ContinueRemux = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = fixture.ViewModel;
        viewModel.Tracks[1].Keep = false;
        viewModel.ReviewPlanCommand.Execute(null);
        var execution = viewModel.RunCommand.ExecuteAsync(null);
        await fixture.Backend.RemuxStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Dispatcher.UIThread.RunJobs();
        var originalProgress = viewModel.ProgressValue;
        await viewModel.ChangeLanguageCommand.ExecuteAsync("ru");
        Assert.True(viewModel.IsBusy);
        Assert.False(viewModel.CanEdit);
        Assert.True(viewModel.HasPlan);
        Assert.Equal(originalProgress, viewModel.ProgressValue);
        fixture.Backend.ContinueRemux.TrySetResult();
        await execution;
        Assert.Contains(ViewModelFixture.OutputPath, viewModel.StatusText);
        Assert.False(viewModel.IsBusy);
        Assert.Equal(1, fixture.Backend.RemuxCount);
    }

    /// <summary>Presents simulated warning review and publishes that same candidate once approved.</summary>
    [AvaloniaFact]
    public async Task WarningsRequireExplicitActionAndDoNotRestartRemuxing()
    {
        var fixture = new ViewModelFixture();
        await fixture.LoadAsync();
        fixture.Backend.ExitCode = 1;
        var viewModel = fixture.ViewModel;
        viewModel.Tracks[1].Keep = false;
        viewModel.ReviewPlanCommand.Execute(null);
        await viewModel.RunCommand.ExecuteAsync(null);
        Assert.True(viewModel.HasWarningCandidate);
        Assert.False(viewModel.CanEdit);
        await viewModel.ChangeLanguageCommand.ExecuteAsync("ru");
        Assert.Contains("предупреждения", viewModel.StatusText);
        await viewModel.PublishWarningsCommand.ExecuteAsync(null);
        Assert.False(viewModel.HasWarningCandidate);
        Assert.Contains(ViewModelFixture.OutputPath, viewModel.StatusText);
        Assert.Contains("с предупреждениями", viewModel.StatusText);
        Assert.Equal(1, fixture.Backend.RemuxCount);
    }

    /// <summary>Keeps failed required verification out of the warning-approval flow.</summary>
    [AvaloniaFact]
    public async Task VerificationFailureShowsLocalizedErrorAndCannotBeApproved()
    {
        var fixture = new ViewModelFixture();
        await fixture.LoadAsync();
        fixture.Backend.ExitCode = 1;
        fixture.Backend.VerificationPassed = false;
        var viewModel = fixture.ViewModel;
        viewModel.Tracks[1].Keep = false;
        viewModel.ReviewPlanCommand.Execute(null);
        await viewModel.RunCommand.ExecuteAsync(null);
        Assert.True(viewModel.HasError);
        Assert.False(viewModel.HasWarningCandidate);
        await viewModel.ChangeLanguageCommand.ExecuteAsync("ru");
        Assert.Contains("проверка не пройдена", viewModel.ErrorText);
        Assert.False(viewModel.PublishWarningsCommand.CanExecute(null));
    }

    /// <summary>Waits for owned cancellation before completing a close request.</summary>
    [AvaloniaFact]
    public async Task WindowCloseSafelyCancelsAndAwaitsTheActiveJob()
    {
        var fixture = new ViewModelFixture();
        await fixture.LoadAsync();
        fixture.Backend.ContinueRemux = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = fixture.ViewModel;
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        viewModel.Tracks[1].Keep = false;
        viewModel.ReviewPlanCommand.Execute(null);
        var execution = viewModel.RunCommand.ExecuteAsync(null);
        await fixture.Backend.RemuxStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        window.Close();
        await execution;
        Dispatcher.UIThread.RunJobs();
        Assert.False(viewModel.IsBusy);
        Assert.False(window.IsVisible);
        Assert.False(viewModel.HasError);
    }

    /// <summary>Requires an explicit close decision if worker termination cannot be confirmed.</summary>
    [AvaloniaFact]
    public async Task UnconfirmedWorkerKeepsWindowOpenUntilExplicitClose()
    {
        var fixture = new ViewModelFixture();
        await fixture.LoadAsync();
        fixture.Backend.TerminationFails = true;
        var viewModel = fixture.ViewModel;
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        viewModel.Tracks[1].Keep = false;
        viewModel.ReviewPlanCommand.Execute(null);
        await viewModel.RunCommand.ExecuteAsync(null);
        Assert.True(viewModel.RequiresCloseDecision);
        Assert.False(viewModel.CanEdit);
        window.Close();
        Assert.True(window.IsVisible);
        window.FindControl<Button>("CloseDespiteUncertaintyButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(window.IsVisible);
    }
}
