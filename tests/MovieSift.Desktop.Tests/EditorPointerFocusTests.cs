using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MovieSift.Application.Appearance;
using MovieSift.Desktop.Appearance;
using MovieSift.Desktop.Views;
using Xunit;

namespace MovieSift.Desktop.Tests;

/// <summary>Exercises real pointer routing for text focus without changing workflow or editor values.</summary>
public sealed class EditorPointerFocusTests
{
    /// <summary>Collapsing a narrow file shelf must not lift its height cap while content is still fading.</summary>
    [AvaloniaFact]
    public async Task ClosingFileShelfKeepsItsFiniteLayoutBudget()
    {
        var fixture = SoftWorkspaceRenderTests.CreateLongNameFixture();
        await fixture.LoadAsync();
        var window = new MainWindow { DataContext = fixture.Main, Width = 640, Height = 480 };
        window.Show();

        try
        {
            AppearanceMotionPolicy.Configure(AppearanceMotion.Full);
            var workspace = window.FindControl<BatchView>("Workspace")!;
            var shelf = workspace.FindControl<Expander>("FileSidebar")!;
            shelf.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var maximumHeight = shelf.MaxHeight;
            Assert.True(double.IsFinite(maximumHeight));
            shelf.IsExpanded = false;
            Assert.Equal(maximumHeight, shelf.MaxHeight);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.True(shelf.Bounds.Height <= shelf.MaxHeight + 1);
            Assert.Empty(fixture.Backend.RemuxRequests);
        }
        finally
        {
            AppearanceMotionPolicy.Configure(AppearanceMotion.Reduced);
            window.Close();
            AppearanceMotionPolicy.Configure(AppearanceMotion.System);
        }
    }

    /// <summary>Blank panel space ends editing while presses inside the same editor preserve caret focus.</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutsideTextEditorPressClearsFocusWithoutClearingValues(bool appearance)
    {
        var fixture = new BatchViewModelFixture();
        await fixture.PrepareAsync();
        fixture.Batch.CloseReviewCommand.Execute(null);
        var snapshot = fixture.Batch.Snapshot;
        var window = new MainWindow { DataContext = fixture.Main, Width = 1100, Height = 800 };
        window.Show();

        try
        {
            AppearanceMotionPolicy.Configure(AppearanceMotion.Reduced);

            if (appearance)
            {
                fixture.Main.ToggleAppearanceCommand.Execute(null);
            }

            if (!appearance)
            {
                fixture.Batch.IsRulesOpen = true;
            }

            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var panelName = appearance ? "AppearancePanel" : "RulesPanel";
            var panel = Assert.Single(window.GetVisualDescendants().OfType<Border>(), control => control.Name == panelName);
            var editor = panel.GetVisualDescendants().OfType<TextBox>().First(control => control.IsEffectivelyVisible && control.IsEffectivelyEnabled);

            if (!appearance)
            {
                editor.Text = "Сериал — edited name";
            }

            var originalValue = editor.Text;
            editor.Focus();
            Click(new(editor.TranslatePoint(default, window)!.Value, editor.Bounds.Size));
            Assert.True(editor.IsFocused);
            var heading = panel.GetVisualDescendants().OfType<TextBlock>().First(control => control.IsEffectivelyVisible && control.Classes.Contains("heading"));
            Click(new(heading.TranslatePoint(default, window)!.Value, heading.Bounds.Size));
            Assert.False(editor.IsFocused);
            Assert.Equal(originalValue, editor.Text);
            Assert.Same(snapshot, fixture.Batch.Snapshot);
            Assert.Empty(fixture.Backend.RemuxRequests);
        }
        finally
        {
            window.Close();
            AppearanceMotionPolicy.Configure(AppearanceMotion.System);
        }

        /// <summary>Refreshes the rendered scene before delivering a primary pointer press.</summary>
        void Click(Rect bounds)
        {
            using var frame = window.CaptureRenderedFrame();
            window.MouseDown(bounds.Center, MouseButton.Left, RawInputModifiers.None);
            window.MouseUp(bounds.Center, MouseButton.Left, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
        }
    }
}
