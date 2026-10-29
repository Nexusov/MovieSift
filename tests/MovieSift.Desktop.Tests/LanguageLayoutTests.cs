using System.Collections.Immutable;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MovieSift.Desktop.Localization;
using MovieSift.Desktop.Views;
using Xunit;

namespace MovieSift.Desktop.Tests;

/// <summary>Checks the actual compiled language panel and compact caption with complete shipped-source translations.</summary>
public sealed class LanguageLayoutTests
{
    /// <summary>Keeps independent cosmetic actions reachable at minimum size even with a visible update badge.</summary>
    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("ru")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("pt-BR")]
    [InlineData("uk")]
    [InlineData("pl")]
    [InlineData("tr")]
    [InlineData("ja")]
    [InlineData("ko")]
    [InlineData("zh-Hans")]
    public async Task CompleteLanguagesFitMinimumWindow(string locale)
    {
        var fixture = new BatchViewModelFixture();
        await fixture.Main.InitializeAsync();

        if (locale != "en")
        {
            using var pack = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "lang", locale + ".json")));
            fixture.Localization.InstallPack(new(locale, pack.RootElement.GetProperty("messages").EnumerateObject()
                .ToImmutableDictionary(property => property.Name, property => property.Value.GetString()!)));
        }

        fixture.Localization.SetLanguage(locale);
        var window = new MainWindow { DataContext = fixture.Main, Width = 640, Height = 480 };
        window.Show();

        try
        {
            await fixture.Main.ChangeLanguageCommand.ExecuteAsync(locale);
            var update = window.FindControl<Button>("AvailableUpdateButton")!;
            update.IsVisible = true;
            update.Content = fixture.Localization["UpdatesBadge"];
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var language = window.FindControl<Button>("LanguageButton")!;
            var appearance = window.FindControl<Button>("AppearanceButton")!;
            var caption = window.FindControl<WindowChrome>("Caption")!;
            var brand = window.FindControl<Grid>("WindowDragRegion")!;
            Assert.True(brand.Bounds.Right <= update.Bounds.Left + 1);
            Assert.True(update.Bounds.Right <= language.Bounds.Left + 1);
            Assert.True(language.Bounds.Right <= appearance.Bounds.Left + 1);

            if (OperatingSystem.IsWindows())
            {
                Assert.True(appearance.Bounds.Right <= caption.Bounds.Left + 1);
            }

            fixture.Main.ToggleLanguageCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var panel = window.FindControl<Border>("LanguagePanel")!;
            var origin = panel.TranslatePoint(default, window)!.Value;
            Assert.True(origin.Y + panel.Bounds.Height <= window.ClientSize.Height + 1);
            Assert.True(origin.X >= 0);
            Assert.True(origin.X + panel.Bounds.Width <= window.ClientSize.Width + 1);
            Assert.True(window.FindControl<Button>("CloseLanguageButton")!.IsEffectivelyVisible);
            SoftWorkspaceRenderTests.Save(window, $"language-{locale}-640x480", 1);
            Assert.Empty(fixture.Backend.RemuxRequests);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Finds source-owned translation fixtures without depending on a developer machine path.</summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "lang", "catalog.json")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Translation source fixtures are required.");
    }
}
