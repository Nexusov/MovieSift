using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using TrackTrim.Application;
using TrackTrim.Core.Failures;
using TrackTrim.Desktop.Views;
using Xunit;

namespace TrackTrim.Desktop.Tests;

/// <summary>Tests compiled bindings, review invalidation and bilingual live-job presentation.</summary>
public sealed class MainWindowTests
{
    /// <summary>Shows the actual retained default-audio situation in both languages, including a confirmed silent plan.</summary>
    [AvaloniaFact]
    public async Task PlanReviewShowsResultingDefaultAudioFlagsAcrossSelectionsAndLanguages()
    {
        var fixture = new ViewModelFixture();
        var viewModel = fixture.ViewModel;
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        await fixture.LoadAsync();
        await fixture.Workspace.ReviewCommand.ExecuteAsync(null);
        Assert.Equal("Resulting default audio: retained track IDs 1 keep their existing default flags.", fixture.Item.PlanDefaultAudioText);
        fixture.Item.Tracks[1].Keep = false;
        Assert.False(fixture.Item.HasPlan);
        Assert.Empty(fixture.Item.PlanDefaultAudioText);
        await fixture.Workspace.ReviewCommand.ExecuteAsync(null);
        Assert.Contains("none of the retained audio tracks has the default flag", fixture.Item.PlanDefaultAudioText);
        await viewModel.ChangeLanguageCommand.ExecuteAsync("ru");
        Assert.True(fixture.Item.HasPlan);
        Assert.Contains("ни одна из сохранённых аудиодорожек не имеет флага по умолчанию", fixture.Item.PlanDefaultAudioText);
        Assert.Contains(fixture.Item.PlanDefaultAudioText, fixture.Workspace.ReviewText);
        Assert.True(fixture.Item.RemovesDefaultAudio);
        fixture.Item.Tracks[2].Keep = false;
        fixture.Item.ZeroAudioConfirmed = true;
        await fixture.Workspace.ReviewCommand.ExecuteAsync(null);
        Assert.Equal("В результате не останется аудиодорожек.", fixture.Item.PlanDefaultAudioText);
        await viewModel.ChangeLanguageCommand.ExecuteAsync("en");
        Assert.Equal("Resulting audio: no audio tracks will remain.", fixture.Item.PlanDefaultAudioText);
        fixture.Item.Tracks[1].Keep = true;
        await fixture.Workspace.ReviewCommand.ExecuteAsync(null);
        Assert.Contains("retained track IDs 1 keep their existing default flags", fixture.Item.PlanDefaultAudioText);
        window.Close();
    }

    /// <summary>Localizes authored failure reasons while preserving original adapter detail only as diagnostics.</summary>
    [AvaloniaFact]
    public async Task InspectionFailureReasonSwitchesLanguageWithoutTranslatingRawDiagnostics()
    {
        const string rawDetail = "Linked segment UID 0123456789 is unsupported.";
        var fixture = new ViewModelFixture();
        fixture.Backend.InspectionException = new OperationFailureException(new(ErrorCode.UnsupportedFeature, rawDetail, ErrorReason.LinkedSegments));
        var viewModel = fixture.ViewModel;
        await viewModel.ChangeLanguageCommand.ExecuteAsync("ru");
        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        await fixture.LoadAsync();
        Assert.True(fixture.Item.HasError);
        Assert.Contains("Связанные сегменты Matroska не поддерживаются", fixture.Item.ErrorReasonText);
        Assert.NotEmpty(fixture.Item.ErrorReasonText);
        Assert.DoesNotContain(rawDetail, fixture.Item.ErrorReasonText);
        Assert.Equal(rawDetail, fixture.Item.Diagnostics);
        Assert.Equal("Техническая диагностика (исходный язык)", viewModel.Localization["Diagnostics"]);
        await viewModel.ChangeLanguageCommand.ExecuteAsync("en");
        Assert.Contains("Linked Matroska segments are unsupported", fixture.Item.ErrorReasonText);
        Assert.Equal(rawDetail, fixture.Item.Diagnostics);
        window.Close();
    }

    /// <summary>Moves unexpected boundary exception text into diagnostics when no localized reason exists.</summary>
    [AvaloniaFact]
    public async Task UnexpectedInspectionExceptionDoesNotAppearAsUntranslatedErrorDetail()
    {
        const string rawDetail = "Synthetic inspection boundary exception.";
        var fixture = new ViewModelFixture();
        fixture.Backend.InspectionException = new InvalidOperationException(rawDetail);
        var viewModel = fixture.ViewModel;
        await viewModel.ChangeLanguageCommand.ExecuteAsync("ru");
        await fixture.LoadAsync();
        Assert.True(fixture.Item.HasError);
        Assert.True(string.IsNullOrWhiteSpace(fixture.Item.ErrorReasonText));
        Assert.Empty(fixture.Item.ErrorReasonText);
        Assert.DoesNotContain(rawDetail, fixture.Item.ErrorText);
        Assert.Contains(rawDetail, fixture.Item.Diagnostics);
    }

    /// <summary>Retains publication evidence while exposing cleanup detail only under technical diagnostics.</summary>
    [AvaloniaFact]
    public async Task PublishedOutputRetainsRawCleanupDiagnosticsWithLocalizedExplanation()
    {
        const string rawDetail = "Synthetic cleanup access denied.";
        var fixture = new ViewModelFixture();
        fixture.Outputs.CleanupIssues = [new("fixture-owned-path", rawDetail)];
        await fixture.LoadAsync();
        var viewModel = fixture.ViewModel;
        await viewModel.ChangeLanguageCommand.ExecuteAsync("ru");
        fixture.Item.Tracks[1].Keep = false;
        await fixture.Workspace.ReviewCommand.ExecuteAsync(null);
        await fixture.Workspace.RunCommand.ExecuteAsync(null);
        Assert.True(viewModel.HasCleanupIssue);
        Assert.False(fixture.Item.HasError);
        Assert.Equal(ViewModelFixture.OutputPath, fixture.Item.PublishedPath);
        Assert.Contains("Временные файлы удалены не полностью", viewModel.Localization["CleanupHeading"]);
        Assert.DoesNotContain(rawDetail, viewModel.Localization["CleanupHeading"]);
        Assert.Contains(rawDetail, fixture.Item.Diagnostics);
    }

    /// <summary>Shows no-change evidence without invoking a redundant remux from the UI.</summary>
    [AvaloniaFact]
    public async Task UnchangedSelectionProducesVisibleNoChangeWithoutRemux()
    {
        var fixture = new ViewModelFixture();
        await fixture.LoadAsync();
        var viewModel = fixture.ViewModel;
        await fixture.Workspace.ReviewCommand.ExecuteAsync(null);
        await fixture.Workspace.RunCommand.ExecuteAsync(null);
        Assert.Equal(0, fixture.Backend.RemuxCount);
        Assert.Equal(fixture.Workspace.Localization["BatchItemNoChange"], fixture.Item.StateText);
        Assert.False(fixture.Item.HasError);
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
        fixture.Item.Tracks[1].Keep = false;
        await fixture.Workspace.ReviewCommand.ExecuteAsync(null);
        var selectedTrack = fixture.Item.Tracks[2];
        await viewModel.ChangeLanguageCommand.ExecuteAsync("ru");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(viewModel.Localization["BatchAddFiles"], window.FindControl<BatchView>("Workspace")!.FindControl<Button>("AddBatchFilesButton")!.Content);
        Assert.Equal("ru", fixture.Settings.Language);
        Assert.False(fixture.Item.Tracks[1].Keep);
        Assert.Same(selectedTrack, fixture.Item.Tracks[2]);
        Assert.True(fixture.Item.HasPlan);
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
        Assert.All(fixture.Item.Tracks, track => Assert.True(track.Keep));
        await fixture.Workspace.ReviewCommand.ExecuteAsync(null);
        Assert.True(fixture.Workspace.CanRun);
        fixture.Item.Tracks[1].Keep = false;
        Assert.False(fixture.Item.HasPlan);
        Assert.False(fixture.Workspace.RunCommand.CanExecute(null));
        await fixture.Workspace.ReviewCommand.ExecuteAsync(null);
        fixture.Item.OutputPath = "changed-output.mkv";
        Assert.False(fixture.Item.HasPlan);
        fixture.Item.Tracks[0].Keep = false;
        Assert.True(fixture.Item.Tracks[0].Keep);
    }

    /// <summary>Shows explicit confirmation before a plan can remove all existing audio.</summary>
    [AvaloniaFact]
    public async Task RemovingEveryAudioRequiresExplicitConfirmation()
    {
        var fixture = new ViewModelFixture();
        await fixture.LoadAsync();
        var viewModel = fixture.ViewModel;
        fixture.Item.Tracks[1].Keep = false;
        fixture.Item.Tracks[2].Keep = false;
        Assert.True(fixture.Item.NeedsZeroAudioConfirmation);
        await fixture.Workspace.ReviewCommand.ExecuteAsync(null);
        Assert.False(fixture.Item.HasPlan);
        Assert.True(fixture.Workspace.HasError);
        fixture.Item.ZeroAudioConfirmed = true;
        await fixture.Workspace.ReviewCommand.ExecuteAsync(null);
        Assert.True(fixture.Item.HasPlan);
    }

    /// <summary>Allows language switching during actual application coordination without cancelling its worker.</summary>
    [AvaloniaFact]
    public async Task LanguageSwitchDuringJobKeepsTheJobAndProgress()
    {
        var fixture = new ViewModelFixture();
        await fixture.LoadAsync();
        fixture.Backend.ContinueRemux = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = fixture.ViewModel;
        fixture.Item.Tracks[1].Keep = false;
        await fixture.Workspace.ReviewCommand.ExecuteAsync(null);
        var execution = fixture.Workspace.RunCommand.ExecuteAsync(null);
        await fixture.Backend.RemuxStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Dispatcher.UIThread.RunJobs();
        var originalProgress = fixture.Workspace.ProgressValue;
        await viewModel.ChangeLanguageCommand.ExecuteAsync("ru");
        Assert.True(fixture.Workspace.IsBusy);
        Assert.False(viewModel.CanEdit);
        Assert.True(fixture.Item.HasPlan);
        Assert.Equal(originalProgress, fixture.Workspace.ProgressValue);
        fixture.Backend.ContinueRemux.TrySetResult();
        await execution;
        Assert.Equal(ViewModelFixture.OutputPath, fixture.Item.PublishedPath);
        Assert.False(fixture.Workspace.IsBusy);
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
        fixture.Item.Tracks[1].Keep = false;
        await fixture.Workspace.ReviewCommand.ExecuteAsync(null);
        await fixture.Workspace.RunCommand.ExecuteAsync(null);
        Assert.True(viewModel.HasWarningCandidate);
        Assert.False(viewModel.CanEdit);
        await viewModel.ChangeLanguageCommand.ExecuteAsync("ru");
        Assert.Contains("предупреждения", fixture.Item.StateText);
        await fixture.Workspace.PublishWarningsCommand.ExecuteAsync(null);
        Assert.False(viewModel.HasWarningCandidate);
        Assert.Equal(ViewModelFixture.OutputPath, fixture.Item.PublishedPath);
        Assert.Equal(viewModel.Localization["BatchItemCompletedWithWarnings"], fixture.Item.StateText);
        Assert.Contains("simulated tool warning", fixture.Item.Diagnostics);
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
        fixture.Item.Tracks[1].Keep = false;
        await fixture.Workspace.ReviewCommand.ExecuteAsync(null);
        await fixture.Workspace.RunCommand.ExecuteAsync(null);
        Assert.True(fixture.Item.HasError);
        Assert.False(viewModel.HasWarningCandidate);
        await viewModel.ChangeLanguageCommand.ExecuteAsync("ru");
        Assert.Contains("проверка не пройдена", fixture.Item.ErrorText);
        Assert.Empty(fixture.Item.ErrorReasonText);
        Assert.Contains("simulated tool warning", fixture.Item.Diagnostics);
        Assert.Contains("simulated verification", fixture.Item.Diagnostics);
        Assert.False(fixture.Workspace.PublishWarningsCommand.CanExecute(null));
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
        fixture.Item.Tracks[1].Keep = false;
        await fixture.Workspace.ReviewCommand.ExecuteAsync(null);
        var execution = fixture.Workspace.RunCommand.ExecuteAsync(null);
        await fixture.Backend.RemuxStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        window.Close();
        await execution;
        Dispatcher.UIThread.RunJobs();
        Assert.False(fixture.Workspace.IsBusy);
        Assert.False(window.IsVisible);
        Assert.False(fixture.Item.HasError);
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
        fixture.Item.Tracks[1].Keep = false;
        await fixture.Workspace.ReviewCommand.ExecuteAsync(null);
        await fixture.Workspace.RunCommand.ExecuteAsync(null);
        Assert.True(viewModel.RequiresCloseDecision);
        Assert.False(viewModel.CanEdit);
        window.Close();
        Assert.True(window.IsVisible);
        window.FindControl<Button>("CloseDespiteUncertaintyButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(window.IsVisible);
    }
}
