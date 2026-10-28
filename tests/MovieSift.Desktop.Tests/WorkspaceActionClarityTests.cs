using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MovieSift.Desktop.Appearance;
using MovieSift.Desktop.Views;
using Xunit;

namespace MovieSift.Desktop.Tests;

/// <summary>Exercises deliberate row, help and accent actions through actual compiled controls.</summary>
public sealed class WorkspaceActionClarityTests
{
    /// <summary>Opens a concrete filename with keyboard activation while preserving the aggregate union and choices.</summary>
    [AvaloniaFact]
    public async Task FilenameKeyboardActivationOpensConcreteDetailsWithoutNarrowingAggregate()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.LoadAsync();
        var batch = fixture.Batch;
        var item = batch.Items[1];
        var choices = batch.Items.SelectMany(entry => entry.Tracks.Select(track => track.Keep)).ToArray();
        var groups = batch.AggregateTracks.Groups.ToArray();
        var view = new BatchView { DataContext = batch };
        var window = new Window { Content = view, Width = 1040, Height = 720 };
        window.Show();

        try
        {
            Dispatcher.UIThread.RunJobs();
            var button = Assert.Single(view.GetVisualDescendants().OfType<Button>(),
                control => control.Name == "FileDetailsNameButton" && ReferenceEquals(control.DataContext, item));
            Assert.Equal(item.SourcePath, AutomationProperties.GetName(button));
            Assert.Equal(item.SourcePath, ToolTip.GetTip(button));
            Assert.True(button.Focus());
            Press(window, PhysicalKey.Enter);
            Assert.Same(item, batch.SelectedItem);
            Assert.True(batch.IsDetailsOpen);
            Assert.Equal(groups, batch.AggregateTracks.Groups.ToArray());
            Assert.Equal(choices, batch.Items.SelectMany(entry => entry.Tracks.Select(track => track.Keep)).ToArray());
            Assert.Empty(fixture.Backend.RemuxRequests);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Keeps inclusion keyboard input distinct from opening the row's independent editor.</summary>
    [AvaloniaFact]
    public async Task InclusionCheckboxSpaceChangesOnlyInclusionAndEnterOnListOpensDetails()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.LoadAsync();
        var batch = fixture.Batch;
        var view = new BatchView { DataContext = batch };
        var window = new Window { Content = view, Width = 1040, Height = 720 };
        window.Show();

        try
        {
            Dispatcher.UIThread.RunJobs();
            var item = batch.Items[0];
            var choices = item.Tracks.Select(track => track.Keep).ToArray();
            var checkbox = Assert.Single(view.GetVisualDescendants().OfType<CheckBox>(),
                control => control.Name == "FileInclusionCheckBox" && ReferenceEquals(control.DataContext, item));
            Assert.True(checkbox.Focus());
            Press(window, PhysicalKey.Space);
            Assert.False(item.Included);
            Assert.False(batch.IsDetailsOpen);
            Assert.Equal(choices, item.Tracks.Select(track => track.Keep).ToArray());
            Press(window, PhysicalKey.Space);
            Assert.True(item.Included);
            var list = view.FindControl<ListBox>("BatchFileList")!;
            list.SelectedItem = item;
            Assert.True(Assert.IsType<ListBoxItem>(list.ContainerFromItem(item)).Focus());
            Press(window, PhysicalKey.Enter);
            Assert.True(batch.IsDetailsOpen);
            Assert.Same(item, batch.SelectedItem);
            Assert.Empty(fixture.Backend.RemuxRequests);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Exposes adjacent grouping help on keyboard focus without a click flyout or media edit.</summary>
    [AvaloniaFact]
    public async Task GroupingHelpFocusShowsLocalizedExplanationWithoutMutation()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.PrepareAsync();
        fixture.Batch.CloseReviewCommand.Execute(null);
        var review = fixture.Batch.Snapshot.Review;
        var choices = fixture.Batch.Items.SelectMany(item => item.Tracks.Select(track => track.Keep)).ToArray();
        var view = new BatchView { DataContext = fixture.Batch };
        var window = new Window { Content = view, Width = 1040, Height = 720 };
        window.Show();

        try
        {
            Dispatcher.UIThread.RunJobs();
            var hint = Assert.Single(view.GetVisualDescendants().OfType<HelpHintView>(), control => control.Name == "GroupingHelpHint");
            var help = hint.FindControl<Button>("HintButton")!;
            Assert.True(help.Focus());
            Dispatcher.UIThread.RunJobs();
            Assert.True(ToolTip.GetIsOpen(help));
            Assert.Null(help.Flyout);
            var explanation = Assert.IsType<TextBlock>(ToolTip.GetTip(help));
            Assert.Equal(fixture.Localization["AggregateNotice"], explanation.Text);
            await fixture.Main.ChangeLanguageCommand.ExecuteAsync("ru");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(fixture.Localization["AggregateNotice"], explanation.Text);
            Assert.Equal(fixture.Localization["WorkspaceGroupingHelp"], AutomationProperties.GetName(help));
            Assert.Same(review, fixture.Batch.Snapshot.Review);
            Assert.Equal(choices, fixture.Batch.Items.SelectMany(item => item.Tracks.Select(track => track.Keep)).ToArray());
            Assert.Empty(fixture.Backend.RemuxRequests);
            ToolTip.SetIsOpen(help, false);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Shows named real color samples with a checked state following the accepted accent, including invalid draft input.</summary>
    [AvaloniaFact]
    public async Task CuratedAccentSamplesExposeSelectionAndSurviveLanguageSwitchAndInvalidHex()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.Main.InitializeAsync();
        var appearance = fixture.Appearance;
        var view = new AppearanceView { DataContext = appearance };
        var window = new Window { Content = view, Width = 640, Height = 720 };
        window.Show();

        try
        {
            Dispatcher.UIThread.RunJobs();
            var indigo = view.FindControl<RadioButton>("IndigoAccentChoice")!;
            var teal = view.FindControl<RadioButton>("TealAccentChoice")!;
            Assert.True(indigo.IsChecked);
            Assert.False(teal.IsChecked);
            var sample = Assert.Single(Assert.IsType<StackPanel>(teal.Content).Children.OfType<Ellipse>());
            Assert.Equal(Color.Parse("#168272"), Assert.IsAssignableFrom<ISolidColorBrush>(sample.Fill).Color);
            Assert.Equal(fixture.Localization["AppearanceTeal"], AutomationProperties.GetName(teal));
            Assert.True(teal.Focus());
            Press(window, PhysicalKey.Space);
            Assert.Equal("#168272", appearance.Current.AccentHex);
            Assert.True(teal.IsChecked);
            Assert.False(indigo.IsChecked);
            appearance.AccentHex = "#bad input";
            await fixture.Main.ChangeLanguageCommand.ExecuteAsync("ru");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(fixture.Localization["AppearanceTeal"], AutomationProperties.GetName(teal));
            Assert.True(teal.IsChecked);
            await fixture.Main.ChangeLanguageCommand.ExecuteAsync("en");
            Dispatcher.UIThread.RunJobs();
            Assert.True(teal.IsChecked);
            Assert.Equal("#168272", appearance.Current.AccentHex);
            Assert.Empty(fixture.Backend.RemuxRequests);
        }
        finally
        {
            window.Close();
            await appearance.WhenIdleAsync();
        }
    }

    /// <summary>Marks the panels whose own visibility changes and preserves the same controls across reopen operations.</summary>
    [AvaloniaFact]
    public async Task ReviewAndRulesEntryMarkersFollowTheirActualReopenTransitions()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.LoadAsync();
        var view = new BatchView { DataContext = fixture.Batch };
        var window = new Window { Content = view };
        window.Show();

        try
        {
            var review = view.FindControl<BatchReviewView>("StructuredReview")!;
            var rules = view.FindControl<Border>("RulesPanel")!;
            Assert.True(SoftMotion.GetEnter(review));
            Assert.True(SoftMotion.GetEnter(rules));
            Assert.False(review.IsVisible);
            Assert.False(rules.IsVisible);
            await fixture.Batch.ReviewCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(review.IsVisible);
            fixture.Batch.CloseReviewCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(review.IsVisible);
            await fixture.Batch.ReviewCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(review.IsVisible);
            Assert.Same(review, view.FindControl<BatchReviewView>("StructuredReview"));
            fixture.Batch.CloseReviewCommand.Execute(null);
            fixture.Batch.IsRulesOpen = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(rules.IsVisible);
            fixture.Batch.IsRulesOpen = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(rules.IsVisible);
            fixture.Batch.IsRulesOpen = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(rules.IsVisible);
            Assert.Empty(fixture.Backend.RemuxRequests);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Uses actual headless keyboard routing rather than invoking presentation handlers directly.</summary>
    private static void Press(Window window, PhysicalKey key)
    {
        window.KeyPressQwerty(key, RawInputModifiers.None);
        window.KeyReleaseQwerty(key, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }
}
