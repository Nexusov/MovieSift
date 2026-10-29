using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MovieSift.Application.Appearance;
using MovieSift.Desktop.Appearance;
using MovieSift.Desktop.Views;
using Xunit;

namespace MovieSift.Desktop.Tests;

/// <summary>Protects shared cosmetic controls from disabled-workspace and overlay-scrollbar regressions.</summary>
public sealed class CosmeticPanelConsistencyTests
{
    /// <summary>Opening preferences blocks workspace input without applying disabled colors or losing reviewed plans.</summary>
    [AvaloniaFact]
    public async Task CosmeticPanelsPreserveEnabledWorkspaceAndReview()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.PrepareAsync();
        var review = fixture.Batch.Snapshot.Review;
        var window = new MainWindow { DataContext = fixture.Main, Width = 760, Height = 600 };
        window.Show();

        try
        {
            fixture.Main.ToggleAppearanceCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var workspace = window.FindControl<BatchView>("Workspace")!;
            Assert.True(workspace.IsEffectivelyEnabled);
            Assert.Same(review, fixture.Batch.Snapshot.Review);
            Assert.True(window.FindControl<Border>("PreferencesBackdrop")!.IsVisible);
            var close = window.FindControl<Button>("CloseAppearanceButton")!;
            Assert.IsType<Avalonia.Controls.Shapes.Path>(close.Content);
            var appearance = window.FindControl<AppearanceView>("AppearanceEditor");
            Assert.NotNull(appearance);
            var settingsScroll = appearance.FindControl<ScrollViewer>("AppearanceSettingsScroll")!;
            Assert.False(settingsScroll.AllowAutoHide);
            var motion = appearance.FindControl<ComboBox>("AppearanceMotion")!;
            var scrollbar = Assert.Single(settingsScroll.GetVisualDescendants().OfType<ScrollBar>(),
                control => control.Name == "PART_VerticalScrollBar" &&
                    ReferenceEquals(control.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault(), settingsScroll));
            Assert.True(scrollbar.IsEffectivelyVisible);
            var motionRight = motion.TranslatePoint(new Point(motion.Bounds.Width, 0), settingsScroll)!.Value.X;
            var scrollbarLeft = scrollbar.TranslatePoint(default, settingsScroll)!.Value.X;
            Assert.True(motionRight <= scrollbarLeft + 1, $"Motion ends at {motionRight}, scrollbar starts at {scrollbarLeft}.");
            Assert.Empty(fixture.Backend.RemuxRequests);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A completed outgoing fade cannot return focus to a trigger after another panel has opened.</summary>
    [AvaloniaFact]
    public async Task SwitchingPanelsRetainsFocusAfterPreviousPanelHides()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.Main.InitializeAsync();
        var window = new MainWindow { DataContext = fixture.Main };
        window.Show();

        try
        {
            AppearanceMotionPolicy.Configure(AppearanceMotion.Full);
            fixture.Main.ToggleAppearanceCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            fixture.Main.ToggleLanguageCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var closeLanguage = window.FindControl<Button>("CloseLanguageButton")!;
            closeLanguage.Focus();
            // Signal the old panel's final visibility boundary directly rather than depending on wall-clock animation timing.
            window.FindControl<Border>("AppearancePanel")!.IsVisible = false;
            Dispatcher.UIThread.RunJobs();
            Assert.Same(closeLanguage, window.FocusManager!.GetFocusedElement());
            Assert.True(fixture.Main.IsLanguageOpen);
            Assert.False(fixture.Main.IsAppearanceOpen);
            Assert.Empty(fixture.Backend.RemuxRequests);
        }
        finally
        {
            AppearanceMotionPolicy.Configure(AppearanceMotion.System);
            window.Close();
        }
    }

    /// <summary>Rapidly reopening a still-visible fading panel must move focus using logical open state.</summary>
    [AvaloniaFact]
    public async Task ReopeningBeforeOutgoingHideFocusesTheReopenedPanel()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.Main.InitializeAsync();
        var window = new MainWindow { DataContext = fixture.Main };
        window.Show();

        try
        {
            AppearanceMotionPolicy.Configure(AppearanceMotion.Reduced);
            fixture.Main.ToggleAppearanceCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            fixture.Main.ToggleLanguageCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var closeLanguage = window.FindControl<Button>("CloseLanguageButton")!;
            Assert.Same(closeLanguage, window.FocusManager!.GetFocusedElement());
            var appearance = window.FindControl<Border>("AppearancePanel")!;
            // Retain the final outgoing frame explicitly, avoiding a dependency on the animation clock.
            appearance.IsVisible = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Same(closeLanguage, window.FocusManager.GetFocusedElement());
            fixture.Main.ToggleAppearanceCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(appearance.IsVisible);
            Assert.Same(window.FindControl<Button>("CloseAppearanceButton"), window.FocusManager.GetFocusedElement());
            Assert.True(fixture.Main.IsAppearanceOpen);
            Assert.False(fixture.Main.IsLanguageOpen);
            Assert.Empty(fixture.Backend.RemuxRequests);
        }
        finally
        {
            window.Close();
            AppearanceMotionPolicy.Configure(AppearanceMotion.System);
        }
    }

    /// <summary>The small disclosure template retains Fluent direction layout and collapse semantics.</summary>
    [AvaloniaTheory]
    [InlineData(ExpandDirection.Down)]
    [InlineData(ExpandDirection.Up)]
    [InlineData(ExpandDirection.Left)]
    [InlineData(ExpandDirection.Right)]
    public void DisclosurePreservesDirectionalLayout(ExpandDirection direction)
    {
        AppearanceMotionPolicy.Configure(AppearanceMotion.Reduced);
        var expander = new Expander
        {
            Header = "Disclosure header",
            ExpandDirection = direction,
            Content = new Border { Width = 160, Height = 90 },
            IsExpanded = false
        };
        var window = new Window { Width = 600, Height = 400, Content = expander };
        window.Show();

        try
        {
            expander.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var content = Assert.Single(expander.GetVisualDescendants().OfType<Border>(), control => control.Name == "ExpanderContent");
            var header = Assert.Single(expander.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>(), control => control.Name == "ExpanderHeader");
            Assert.True(content.IsVisible);
            Assert.True(content.Bounds.Width > 0);
            Assert.True(content.Bounds.Height > 0);
            var separated = direction switch
            {
                ExpandDirection.Down => content.Bounds.Top >= header.Bounds.Bottom - 1,
                ExpandDirection.Up => content.Bounds.Bottom <= header.Bounds.Top + 1,
                ExpandDirection.Left => content.Bounds.Right <= header.Bounds.Left + 1,
                _ => content.Bounds.Left >= header.Bounds.Right - 1
            };
            Assert.True(separated, $"Header {header.Bounds} overlaps content {content.Bounds} for {direction}.");
            expander.IsExpanded = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(content.IsVisible);
        }
        finally
        {
            window.Close();
            AppearanceMotionPolicy.Configure(AppearanceMotion.System);
        }
    }

    /// <summary>Effective defaults appear as actual HEX and swatches without writing default overrides to storage.</summary>
    [AvaloniaFact]
    public async Task CustomColorFieldsShowResolvedValuesWithoutInventingOverrides()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.Main.InitializeAsync();
        var editor = fixture.Appearance;
        var palettes = AppearancePalettePolicy.Resolve(editor.Current);
        Assert.Equal(AppearancePalettePolicy.ToHex(palettes.Light!.Window), editor.LightPage);
        Assert.Equal(AppearancePalettePolicy.ToHex(palettes.Dark!.Panel), editor.DarkSurface);
        Assert.Equal(palettes.Dark.Panel, Assert.IsType<SolidColorBrush>(editor.DarkSurfaceBrush).Color);
        Assert.True(editor.Current.LightOverrides is null || editor.Current.LightOverrides.Page is null);
        Assert.True(editor.Current.DarkOverrides is null || editor.Current.DarkOverrides.Surface is null);
        editor.DarkPage = "#invalid";
        Assert.True(editor.HasError);
        Assert.Equal("#invalid", editor.DarkPage);
        Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(editor.DarkPageBrush).Color);
        await editor.ResetCommand.ExecuteAsync(null);
        Assert.False(editor.HasError);
        Assert.Equal(AppearanceSettings.Default, editor.Current);
        Assert.Equal(AppearancePalettePolicy.ToHex(palettes.Dark.Window), editor.DarkPage);
        Assert.Empty(fixture.Backend.RemuxRequests);
    }
}

