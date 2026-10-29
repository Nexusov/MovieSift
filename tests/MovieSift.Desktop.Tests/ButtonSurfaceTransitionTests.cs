using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using MovieSift.Application.Appearance;
using MovieSift.Desktop.Appearance;
using Xunit;

namespace MovieSift.Desktop.Tests;

/// <summary>Observes intermediate button surface updates, not only the settled hover color.</summary>
public sealed class ButtonSurfaceTransitionTests
{
    /// <summary>Foreign Fluent state colors cannot enter a semantic surface during rapid hover/press changes.</summary>
    [AvaloniaTheory]
    [InlineData(AppearanceTheme.Dark, false, "queueRemove")]
    [InlineData(AppearanceTheme.Light, false, "queueRemove")]
    [InlineData(AppearanceTheme.Dark, true, "queueRemove")]
    [InlineData(AppearanceTheme.Dark, false, "primary")]
    [InlineData(AppearanceTheme.Light, false, "")]
    public async Task SemanticButtonNeverTransitionsThroughFluentStateColors(AppearanceTheme theme, bool custom, string semanticClass)
    {
        const int transitionFrames = 12;
        var fixture = new BatchViewModelFixture();
        await fixture.Main.InitializeAsync();
        fixture.Appearance.ThemeIndex = (int)theme;

        if (custom)
        {
            fixture.Appearance.DarkPage = "#171322";
        }

        var clicks = 0;
        var button = new Button
        {
            Classes = { "iconClose" },
            Content = new Avalonia.Controls.Shapes.Path { Classes = { "closeGlyph" } },
            Command = new RelayCommand(() => clicks++)
        };

        if (!string.IsNullOrEmpty(semanticClass))
        {
            button.Classes.Add(semanticClass);
        }

        var window = new Window { Width = 320, Height = 200, Content = button };
        // Deliberately distinguish the unused default-theme colors from every MovieSift palette.
        var foreignColors = new[] { Colors.Fuchsia, Colors.Lime, Colors.Cyan };
        window.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(foreignColors[0]);
        window.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(foreignColors[1]);
        window.Resources["ButtonBackgroundDisabled"] = new SolidColorBrush(foreignColors[2]);
        window.Show();

        try
        {
            AppearanceMotionPolicy.Configure(AppearanceMotion.Full);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            using var initialFrame = window.CaptureRenderedFrame();
            var presenter = Assert.Single(button.GetVisualDescendants().OfType<ContentPresenter>(), control => control.Name == "PART_ContentPresenter");
            var glyph = Assert.IsType<Avalonia.Controls.Shapes.Path>(button.Content);
            var glyphCenter = glyph.TranslatePoint(new Point(glyph.Bounds.Width / 2, glyph.Bounds.Height / 2), button)!.Value;
            Assert.InRange(glyphCenter.X, button.Bounds.Width / 2 - 1, button.Bounds.Width / 2 + 1);
            Assert.InRange(glyphCenter.Y, button.Bounds.Height / 2 - 1, button.Bounds.Height / 2 + 1);
            var samples = new List<Color>();
            presenter.PropertyChanged += (_, change) =>
            {
                if (change.Property == ContentPresenter.BackgroundProperty && change.NewValue is ISolidColorBrush brush)
                {
                    samples.Add(brush.Color);
                }
            };
            var center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
            window.MouseMove(center);
            Assert.True(button.IsPointerOver);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(transitionFrames);
            window.MouseDown(center, MouseButton.Left, RawInputModifiers.None);
            Assert.Equal(0, clicks);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(transitionFrames);
            window.MouseUp(center, MouseButton.Left, RawInputModifiers.None);
            Assert.Equal(1, clicks);
            window.MouseMove(new Point(window.ClientSize.Width - 1, window.ClientSize.Height - 1));
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(transitionFrames);
            button.IsEnabled = false;
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(transitionFrames);
            Dispatcher.UIThread.RunJobs();
            Assert.NotEmpty(samples);
            Assert.DoesNotContain(samples, color => foreignColors.Contains(color));
            var palettes = AppearancePalettePolicy.Resolve(fixture.Appearance.Current);
            var palette = theme == AppearanceTheme.Light ? palettes.Light! : palettes.Dark!;
            var surfaces = semanticClass == "primary"
                ? new[] { palette.Secondary, palette.Accent, palette.AccentHover, palette.AccentPressed }
                : new[] { palette.Secondary, palette.Selection };
            // Interpolation may include transparency, but must not introduce unrelated bright RGB channels.
            Assert.All(samples.Where(color => color.A != 0), color =>
            {
                Assert.InRange((int)color.R, 0, surfaces.Max(surface => surface.R) + 1);
                Assert.InRange((int)color.G, 0, surfaces.Max(surface => surface.G) + 1);
                Assert.InRange((int)color.B, 0, surfaces.Max(surface => surface.B) + 1);
            });
            Assert.False(button.IsEffectivelyEnabled);
        }
        finally
        {
            AppearanceMotionPolicy.Configure(AppearanceMotion.System);
            window.Close();
        }
    }
}
