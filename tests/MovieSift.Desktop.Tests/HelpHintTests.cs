using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using MovieSift.Desktop.Views;
using Xunit;

namespace MovieSift.Desktop.Tests;

/// <summary>Checks adjacent help as cosmetic hover/focus content, independent of editor commands.</summary>
public sealed class HelpHintTests
{
    /// <summary>Pointer entry opens help before any click; Escape dismisses it with editor focus intact.</summary>
    [AvaloniaFact]
    public void HoverAndKeyboardFocusExposeTheSameHelpWithoutClicking()
    {
        var hint = new HelpHintView { Label = "Output mode", Text = "The temporary result must be verified." };
        var editor = new TextBox { Text = "unchanged selection", Width = 200 };
        var window = new Window { Width = 640, Height = 400, Content = new StackPanel { Children = { hint, editor } } };
        window.Show();

        try
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var button = hint.FindControl<Button>("HintButton")!;
            editor.Focus();
            var location = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
            window.MouseMove(location);
            Dispatcher.UIThread.RunJobs();
            Assert.True(ToolTip.GetIsOpen(button));
            Assert.True(editor.IsFocused);
            Assert.Null(button.Flyout);
            Assert.Null(button.Command);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Assert.False(ToolTip.GetIsOpen(button));
            Assert.True(editor.IsFocused);
            Assert.Equal("unchanged selection", editor.Text);

            button.Focus();
            Assert.True(ToolTip.GetIsOpen(button));
            var text = Assert.IsType<TextBlock>(ToolTip.GetTip(button));
            Assert.Equal(hint.Text, text.Text);
            hint.Text = "Временный результат проверяется перед заменой.";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(hint.Text, text.Text);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Assert.False(ToolTip.GetIsOpen(button));
            Assert.True(button.IsFocused);
            editor.Focus();
            button.Focus();
            Assert.True(ToolTip.GetIsOpen(button));
            editor.Focus();
            Assert.False(ToolTip.GetIsOpen(button));
        }
        finally
        {
            window.Close();
        }
    }
}
