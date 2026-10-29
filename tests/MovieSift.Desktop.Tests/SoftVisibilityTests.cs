using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MovieSift.Application.Appearance;
using MovieSift.Desktop.Appearance;
using MovieSift.Desktop.Views;
using Xunit;

namespace MovieSift.Desktop.Tests;

/// <summary>Checks opacity revealed on animation-priority release without arbitrary timer sleeps.</summary>
public sealed class SoftVisibilityTests
{
    /// <summary>Outgoing views keep pointer input away from newly revealed workflow controls until fully hidden.</summary>
    [AvaloniaFact]
    public async Task OutgoingPreferenceAndRulesPanelsRetainTheirInputBarrier()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.LoadAsync();
        var window = new MainWindow { DataContext = fixture.Main };
        window.Show();
        AppearanceMotionPolicy.Configure(AppearanceMotion.Full);

        try
        {
            fixture.Main.IsAppearanceOpen = true;
            Dispatcher.UIThread.RunJobs();
            fixture.Main.IsAppearanceOpen = false;
            Dispatcher.UIThread.RunJobs();
            var panel = window.FindControl<Border>("AppearancePanel")!;
            Assert.True(panel.IsVisible);
            Assert.False(panel.IsHitTestVisible);
            Assert.True(window.FindControl<Border>("PreferencesBackdrop")!.IsVisible);
            var workspace = window.FindControl<BatchView>("Workspace")!;
            Assert.False(Assert.IsType<Border>(workspace.Parent).IsHitTestVisible);
            AppearanceMotionPolicy.Configure(AppearanceMotion.Reduced);
            Dispatcher.UIThread.RunJobs();
            Assert.False(window.FindControl<Border>("PreferencesBackdrop")!.IsVisible);

            AppearanceMotionPolicy.Configure(AppearanceMotion.Full);
            fixture.Batch.IsRulesOpen = true;
            Dispatcher.UIThread.RunJobs();
            fixture.Batch.IsRulesOpen = false;
            Dispatcher.UIThread.RunJobs();
            var rules = workspace.FindControl<Border>("RulesPanel")!;
            Assert.True(rules.IsVisible);
            Assert.False(rules.IsHitTestVisible);
            var editor = Assert.Single(Assert.IsType<Grid>(workspace.Content).Children.OfType<DockPanel>());
            Assert.False(editor.IsHitTestVisible);
            AppearanceMotionPolicy.Configure(AppearanceMotion.Reduced);
            Dispatcher.UIThread.RunJobs();
            Assert.False(rules.IsVisible);
            Assert.True(editor.IsHitTestVisible);
            Assert.Empty(fixture.Backend.RemuxRequests);
        }
        finally
        {
            AppearanceMotionPolicy.Configure(AppearanceMotion.Reduced);
            window.Close();
        }
    }

    /// <summary>Detaching an active animation cancels it without leaving a stale close attached to a later presentation.</summary>
    [AvaloniaFact]
    public void DetachCancelsAnOutgoingAnimationBeforeReattachment()
    {
        var panel = new Border { Width = 100, Height = 100 };
        var window = new Window { Content = panel };
        window.Show();
        AppearanceMotionPolicy.Configure(AppearanceMotion.Full);

        try
        {
            SoftMotion.SetVisible(panel, false);
            window.Content = null;
            Dispatcher.UIThread.RunJobs();
            Assert.False(panel.IsVisible);
            window.Content = panel;
            SoftMotion.SetVisible(panel, true);
            AppearanceMotionPolicy.Configure(AppearanceMotion.Reduced);
            Dispatcher.UIThread.RunJobs();
            Assert.True(panel.IsVisible);
            Assert.Equal(1d, panel.Opacity);
        }
        finally
        {
            AppearanceMotionPolicy.Configure(AppearanceMotion.Reduced);
            window.Close();
        }
    }

    /// <summary>A closing animation must reveal zero opacity before its asynchronous hide continuation.</summary>
    [AvaloniaFact]
    public void ClosingCannotRevealAnOpaqueBaseValue()
    {
        var panel = new Border { Width = 100, Height = 100 };
        var window = new Window { Content = panel };
        window.Show();
        AppearanceMotionPolicy.Configure(AppearanceMotion.Full);

        try
        {
            SoftMotion.SetVisible(panel, false);
            Assert.True(panel.IsVisible);
            Assert.Equal(0d, panel.GetBaseValue(Visual.OpacityProperty));
            AppearanceMotionPolicy.Configure(AppearanceMotion.Reduced);
            Dispatcher.UIThread.RunJobs();
            Assert.False(panel.IsVisible);
            Assert.Equal(0d, panel.Opacity);
        }
        finally
        {
            AppearanceMotionPolicy.Configure(AppearanceMotion.Reduced);
            window.Close();
        }
    }

    /// <summary>A reopen supersedes an outgoing callback and reduced motion preserves final visibility.</summary>
    [AvaloniaFact]
    public void ReopenAndReducedMotionSupersedeOutgoingCallbacks()
    {
        var panel = new Border { Width = 100, Height = 100 };
        var window = new Window { Content = panel };
        window.Show();
        AppearanceMotionPolicy.Configure(AppearanceMotion.Full);

        try
        {
            SoftMotion.SetVisible(panel, false);
            SoftMotion.SetVisible(panel, true);
            AppearanceMotionPolicy.Configure(AppearanceMotion.Reduced);
            Dispatcher.UIThread.RunJobs();
            Assert.True(panel.IsVisible);
            Assert.Equal(1d, panel.Opacity);
            SoftMotion.SetVisible(panel, false);
            Assert.False(panel.IsVisible);
            Assert.Equal(0d, panel.Opacity);
            SoftMotion.SetVisible(panel, true);
            Assert.True(panel.IsVisible);
            Assert.Equal(1d, panel.Opacity);
        }
        finally
        {
            AppearanceMotionPolicy.Configure(AppearanceMotion.Reduced);
            window.Close();
        }
    }
}
