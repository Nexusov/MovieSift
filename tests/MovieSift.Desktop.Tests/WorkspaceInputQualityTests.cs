using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MovieSift.Application.Appearance;
using MovieSift.Desktop.Appearance;
using MovieSift.Desktop.ViewModels;
using MovieSift.Desktop.Views;
using Serilog;
using Xunit;

namespace MovieSift.Desktop.Tests;

/// <summary>Checks effective helper contrast, constrained file shelves and cosmetic overlay keyboard isolation.</summary>
public sealed class WorkspaceInputQualityTests
{
    private const double MinimumTrackViewport = 40;
    private const double MinimumFileViewport = 32;
    private const double BoundsTolerance = 1;
    private const int MaximumFocusTraversalSteps = 100;
    private const int PlatformFooterBudgetDifference = 4;

    /// <summary>Composes actual helper text against its displayed surface rather than trusting the source palette alone.</summary>
    [AvaloniaTheory]
    [InlineData("en", AppearanceTheme.Light, false)]
    [InlineData("ru", AppearanceTheme.Light, false)]
    [InlineData("en", AppearanceTheme.Dark, false)]
    [InlineData("ru", AppearanceTheme.Dark, false)]
    [InlineData("en", AppearanceTheme.System, false)]
    [InlineData("ru", AppearanceTheme.System, false)]
    [InlineData("en", AppearanceTheme.Light, true)]
    [InlineData("ru", AppearanceTheme.Light, true)]
    public async Task RulesAndPresetHelpersUseOpaqueReadableSemanticText(string language, AppearanceTheme theme, bool customText)
    {
        var fixture = new BatchViewModelFixture();
        await fixture.Main.InitializeAsync();
        fixture.Appearance.ThemeIndex = (int)theme;

        if (customText)
        {
            fixture.Appearance.LightText = "#707070";
            Assert.False(fixture.Appearance.HasError, fixture.Appearance.ErrorText);
        }

        await fixture.Main.ChangeLanguageCommand.ExecuteAsync(language);
        var palettes = AppearancePalettePolicy.Resolve(fixture.Appearance.Current);
        var palette = theme == AppearanceTheme.Dark ? palettes.Dark! : palettes.Light!;
        var content = new StackPanel
        {
            Children =
            {
                new RulePresetsView { DataContext = fixture.Batch.Presets },
                new BatchLanguageRulesView { DataContext = fixture.Batch.LanguageRules }
            }
        };
        var window = new Window
        {
            Width = 640,
            Height = 800,
            Content = new Border { Background = new SolidColorBrush(palette.Panel), Child = content }
        };
        window.Show();

        try
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            foreach (var key in new[] { "RuleForcedSubtitleHelp", "RuleTagHelp", "RuleTitleHelp", "PresetNameHelp" })
            {
                var helper = Assert.Single(content.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == fixture.Localization[key]);
                var foreground = Assert.IsAssignableFrom<ISolidColorBrush>(helper.Foreground);
                var opacity = helper.GetVisualAncestors().Aggregate(helper.Opacity * foreground.Opacity,
                    (current, ancestor) => current * ancestor.Opacity);
                var effective = Composite(foreground.Color, palette.Panel, opacity);
                var contrast = AppearancePalettePolicy.Contrast(effective, palette.Panel);
                Assert.True(contrast >= AppearancePalettePolicy.TextContrast, $"{key}: effective contrast {contrast}");
                Assert.Equal(palette.Muted, foreground.Color);
                Assert.Equal(1, opacity);
            }
        }
        finally
        {
            window.Close();
            await fixture.Appearance.WhenIdleAsync();
        }
    }

    /// <summary>Retains a usable clipped track viewport and reachable footer with either minimum-size shelf state.</summary>
    [AvaloniaTheory]
    [InlineData("en", false, 1d, 0)]
    [InlineData("en", true, 1d, 0)]
    [InlineData("ru", false, 1d, 0)]
    [InlineData("ru", true, 1d, 0)]
    [InlineData("en", true, 1.5d, 0)]
    [InlineData("ru", true, 2d, 0)]
    [InlineData("en", false, 2d, 0)]
    [InlineData("ru", false, 1.5d, 0)]
    [InlineData("ru", true, 1d, PlatformFooterBudgetDifference)]
    [InlineData("ru", true, 2d, PlatformFooterBudgetDifference)]
    public async Task MinimumWindowKeepsExpandedFilesAndTracksWithinTheirOwnBounds(string language, bool expanded, double density, int footerExtraSpace)
    {
        var fixture = SoftWorkspaceRenderTests.CreateLongNameFixture();
        await fixture.LoadAsync();
        var window = new MainWindow { DataContext = fixture.Main, Width = 640, Height = 480 };
        window.Show();

        try
        {
            window.SetRenderScaling(density);
            await fixture.Main.ChangeLanguageCommand.ExecuteAsync(language);
            Dispatcher.UIThread.RunJobs();
            var view = window.FindControl<BatchView>("Workspace")!;
            var footer = view.FindControl<StackPanel>("BatchRunControls")!;
            var footerMargin = footer.Margin;
            // Model the observed platform metric difference without changing the declared window minimum or OS fonts.
            footer.Margin = new(footerMargin.Left, footerMargin.Top + footerExtraSpace, footerMargin.Right, footerMargin.Bottom);
            var shelf = view.FindControl<Expander>("FileSidebar")!;
            shelf.IsExpanded = expanded;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var tracks = view.FindControl<ScrollViewer>("WorkspaceScrollViewer")!;
            var folder = view.FindControl<Button>("BatchOutputFolderButton")!;
            Assert.True(tracks.Viewport.Height >= MinimumTrackViewport,
                $"Track viewport was {tracks.Viewport.Height}; shelf={shelf.Bounds}, workspace={view.Bounds}.");
            var shelfBounds = InWindow(shelf, window);
            var trackBounds = InWindow(tracks, window);
            var folderBounds = InWindow(folder, window);
            Assert.True(shelfBounds.Bottom <= trackBounds.Top + BoundsTolerance);
            Assert.True(trackBounds.Bottom <= folderBounds.Top + BoundsTolerance);
            Assert.True(folderBounds.Bottom <= window.ClientSize.Height + BoundsTolerance);
            Assert.True(folder.IsEffectivelyEnabled);
            Assert.True(shelf.ClipToBounds);

            if (expanded)
            {
                var list = view.FindControl<ListBox>("BatchFileList")!;
                var fileScroll = Assert.Single(list.GetVisualDescendants().OfType<ScrollViewer>());
                Assert.True(fileScroll.Viewport.Height >= MinimumFileViewport,
                    $"Expanded file viewport was only {fileScroll.Viewport.Height}; its text must remain reachable.");
                Assert.True(InWindow(list, window).Bottom <= shelfBounds.Bottom + BoundsTolerance);
                var inclusion = list.GetVisualDescendants().OfType<CheckBox>().First();
                inclusion.BringIntoView();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Assert.True(InWindow(inclusion, window).Top >= InWindow(fileScroll, window).Top - BoundsTolerance);
                Assert.True(InWindow(inclusion, window).Bottom <= InWindow(fileScroll, window).Bottom + BoundsTolerance);
            }

            var editableTrack = view.GetVisualDescendants().OfType<CheckBox>().First(control =>
                control.IsEffectivelyVisible && control.DataContext is AggregateTrackGroupViewModel { IsRequired: false });
            editableTrack.BringIntoView();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var editableBounds = InWindow(editableTrack, window);
            Assert.True(editableBounds.Top >= trackBounds.Top - BoundsTolerance);
            Assert.True(editableBounds.Bottom <= trackBounds.Bottom + BoundsTolerance);
            using var renderedFrame = window.CaptureRenderedFrame();
            Assert.NotNull(renderedFrame);
            var editableHit = window.InputHitTest(editableBounds.Center);
            var footerSuffix = footerExtraSpace == 0 ? string.Empty : $"-footer-{footerExtraSpace}";
            SoftWorkspaceRenderTests.Save(window, $"quality-{language}-640x480-{density}-shelf-{expanded}{footerSuffix}", density);
            Assert.True(ReferenceEquals(editableHit, editableTrack) || editableHit is Visual editableVisual &&
                editableVisual.GetVisualAncestors().Contains(editableTrack),
                $"Track bounds={editableBounds}; hit={editableHit}; enabled={editableTrack.IsEffectivelyEnabled}.");

            var point = folderBounds.Center;
            var hit = window.InputHitTest(point);
            Assert.True(ReferenceEquals(hit, folder) || hit is Visual visual && visual.GetVisualAncestors().Contains(folder));
            Assert.Empty(fixture.Backend.RemuxRequests);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Traps cosmetic input inside Appearance, closes only that panel with Escape and restores its invoking control.</summary>
    [AvaloniaFact]
    public async Task AppearanceOverlayCyclesFocusAndEscapePreservesCurrentReview()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.PrepareAsync();
        var review = fixture.Batch.Snapshot.Review;
        var choices = fixture.Batch.Items.SelectMany(item => item.Tracks.Select(track => track.Keep)).ToArray();
        var window = new MainWindow { DataContext = fixture.Main, Width = 1040, Height = 800 };
        window.Show();

        try
        {
            Dispatcher.UIThread.RunJobs();
            fixture.Main.ToggleAppearanceCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var appearanceView = Assert.Single(window.GetVisualDescendants().OfType<AppearanceView>());
            var panel = appearanceView.GetVisualAncestors().OfType<Border>().First(border => border.Classes.Contains("panel"));
            Assert.True(IsWithin(window.FocusManager!.GetFocusedElement(), panel),
                $"Opening Appearance left focus on {window.FocusManager.GetFocusedElement()}.");

            foreach (var modifiers in new[] { RawInputModifiers.None, RawInputModifiers.Shift })
            {
                var initial = window.FocusManager.GetFocusedElement();
                var wrapped = false;

                for (var index = 0; index < MaximumFocusTraversalSteps; index++)
                {
                    Press(window, PhysicalKey.Tab, modifiers);
                    var current = window.FocusManager.GetFocusedElement();
                    Assert.True(IsWithin(current, panel));

                    if (ReferenceEquals(initial, current))
                    {
                        wrapped = true;
                        break;
                    }
                }

                Assert.True(wrapped, "The appearance tab cycle never returned to its starting control.");
            }

            Press(window, PhysicalKey.Escape);
            Assert.False(fixture.Main.IsAppearanceOpen);
            Assert.Same(window.FindControl<Button>("AppearanceButton"), window.FocusManager.GetFocusedElement());
            Assert.Same(review, fixture.Batch.Snapshot.Review);
            Assert.Equal(choices, fixture.Batch.Items.SelectMany(item => item.Tracks.Select(track => track.Keep)).ToArray());
            Assert.True(fixture.Batch.CanRun);
            Assert.Empty(fixture.Backend.RemuxRequests);
            fixture.Main.ToggleAppearanceCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(IsWithin(window.FocusManager.GetFocusedElement(), panel));
            Press(window, PhysicalKey.Escape);
            Assert.False(fixture.Main.IsAppearanceOpen);
            Assert.Same(window.FindControl<Button>("AppearanceButton"), window.FocusManager.GetFocusedElement());
        }
        finally
        {
            window.Close();
            await fixture.Appearance.WhenIdleAsync();
        }
    }

    /// <summary>Closing cosmetic UI neither cancels nor restarts an already pending preference write.</summary>
    [AvaloniaFact]
    public async Task EscapeFromAppearancePreservesTheSamePendingSaveAndReview()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.PrepareAsync();
        var entered = BatchViewModelFixture.Signal();
        var release = new TaskCompletionSource<AppearanceSaveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new AppearanceUiStore
        {
            Save = _ =>
            {
                entered.TrySetResult();
                return release.Task;
            }
        };
        var editor = new AppearanceViewModel(new(store, fixture.Localization, new(Avalonia.Application.Current)));
        using var logger = new LoggerConfiguration().CreateLogger();
        var shell = new MainViewModel(new(fixture.Settings, fixture.Outputs, fixture.Localization, true, logger, fixture.Batch, editor));
        var window = new MainWindow { DataContext = shell, Width = 1040, Height = 800 };
        window.Show();

        try
        {
            await shell.InitializeAsync();
            shell.ToggleAppearanceCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            editor.ThemeIndex = (int)AppearanceTheme.Dark;
            await BatchViewModelFixture.Await(entered.Task);
            var review = fixture.Batch.Snapshot.Review;
            var settings = editor.Current;
            Assert.True(editor.HasPendingSave);
            Press(window, PhysicalKey.Escape);
            Assert.False(shell.IsAppearanceOpen);
            Assert.True(editor.HasPendingSave);
            Assert.Same(settings, editor.Current);
            Assert.Same(review, fixture.Batch.Snapshot.Review);
            Assert.True(fixture.Batch.CanRun);
            Assert.Single(store.Writes);
            Assert.Empty(fixture.Backend.RemuxRequests);
            release.SetResult(new(true));
            await editor.WhenIdleAsync();
            Assert.False(editor.HasPendingSave);
            Assert.Single(store.Writes);
        }
        finally
        {
            release.TrySetResult(new(true));
            await editor.WhenIdleAsync();
            window.Close();
        }
    }

    /// <summary>Cosmetic Escape exposes Cancel and warning review without cancelling media or discarding its candidate.</summary>
    [AvaloniaFact]
    public async Task AppearanceEscapeLeavesActiveWorkAndWarningOwnershipUntouched()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.PrepareAsync();
        var entered = BatchViewModelFixture.Signal();
        var release = BatchViewModelFixture.Signal();
        fixture.Backend.ExitCodes[fixture.Batch.Items[0].SourcePath] = 1;
        fixture.Backend.OnRemux = async request =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(request.CancellationToken);
        };
        var window = new MainWindow { DataContext = fixture.Main, Width = 1040, Height = 800 };
        window.Show();
        var running = fixture.Batch.RunCommand.ExecuteAsync(null);

        try
        {
            await BatchViewModelFixture.Await(entered.Task);
            Dispatcher.UIThread.RunJobs();
            var active = Assert.Single(fixture.Backend.RemuxRequests);
            var snapshot = fixture.Batch.Snapshot;
            fixture.Main.ToggleAppearanceCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(window.FindControl<BatchView>("Workspace")!.IsEffectivelyEnabled);
            Press(window, PhysicalKey.Escape);
            Assert.False(fixture.Main.IsAppearanceOpen);
            Assert.Same(snapshot, fixture.Batch.Snapshot);
            Assert.False(active.CancellationToken.IsCancellationRequested);
            var cancel = Assert.Single(window.GetVisualDescendants().OfType<Button>(), button =>
                ReferenceEquals(button.Command, fixture.Batch.CancelCommand) && button.IsEffectivelyVisible);
            Assert.True(cancel.IsEffectivelyEnabled);
            release.TrySetResult();
            await BatchViewModelFixture.Await(running);
            Dispatcher.UIThread.RunJobs();
            Assert.True(fixture.Batch.HasWarningCandidate);
            var warning = fixture.Batch.Snapshot;
            var cleanupCount = fixture.Outputs.CleanupCount;
            fixture.Main.ToggleAppearanceCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(fixture.Main.IsAppearanceOpen);
            Press(window, PhysicalKey.Escape);
            Assert.False(fixture.Main.IsAppearanceOpen);
            Assert.Same(warning, fixture.Batch.Snapshot);
            Assert.True(fixture.Batch.HasWarningCandidate);
            Assert.Equal(cleanupCount, fixture.Outputs.CleanupCount);
            Assert.Single(fixture.Backend.RemuxRequests);
            var publish = Assert.Single(window.GetVisualDescendants().OfType<Button>(), button =>
                ReferenceEquals(button.Command, fixture.Batch.PublishWarningsCommand) && button.IsEffectivelyVisible);
            Assert.True(publish.IsEffectivelyEnabled);
            fixture.Main.ToggleAppearanceCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            window.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.IsVisible);
            Assert.True(fixture.Main.HasWarningCloseDecision);
            Assert.False(fixture.Main.IsAppearanceOpen);
            Assert.Same(window.FindControl<Button>("RemainInWarningReviewButton"), window.FocusManager!.GetFocusedElement());
            Assert.True(fixture.Batch.HasWarningCandidate);
            Assert.Equal(cleanupCount, fixture.Outputs.CleanupCount);
        }
        finally
        {
            release.TrySetResult();
            await BatchViewModelFixture.Await(running);

            if (fixture.Batch.HasWarningCandidate)
            {
                await fixture.Batch.DiscardWarningsCommand.ExecuteAsync(null);
            }

            window.Close();
        }
    }

    /// <summary>Resolves a control's actual laid-out rectangle in the window coordinate space.</summary>
    private static Rect InWindow(Control control, Window window) => new(control.TranslatePoint(default, window)!.Value, control.Bounds.Size);

    /// <summary>Checks focus ownership without relying on an assumed number of Tab presses.</summary>
    private static bool IsWithin(IInputElement? element, Visual ancestor) => element is Visual visual &&
        (ReferenceEquals(visual, ancestor) || visual.GetVisualAncestors().Contains(ancestor));

    /// <summary>Uses actual headless keyboard routing for forward/backward traversal and Escape.</summary>
    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPressQwerty(key, modifiers);
        window.KeyReleaseQwerty(key, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Composes the actual foreground opacity over its opaque displayed surface.</summary>
    private static Color Composite(Color foreground, Color background, double opacity) => Color.FromRgb(
        (byte)Math.Round(foreground.R * opacity + background.R * (1 - opacity)),
        (byte)Math.Round(foreground.G * opacity + background.G * (1 - opacity)),
        (byte)Math.Round(foreground.B * opacity + background.B * (1 - opacity)));
}
