using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TrackTrim.Application.Appearance;
using TrackTrim.Desktop.Appearance;
using TrackTrim.Desktop.Views;
using Xunit;

namespace TrackTrim.Desktop.Tests;

/// <summary>Protects accepted cosmetic state from invalid-input races and checks selected-row foreground contrast.</summary>
public sealed class AppearancePersistenceRegressionTests
{
    /// <summary>A normal failed save remains visible after invalid HEX is restored, without losing reviewed media state.</summary>
    [AvaloniaTheory]
    [InlineData(AppearanceErrorCode.Busy)]
    [InlineData(AppearanceErrorCode.PersistenceUnavailable)]
    public async Task InvalidIntermediateHexCannotHideFailureForCurrentAcceptedAppearance(AppearanceErrorCode code)
    {
        const string failureDetail = "Controlled normal appearance save refusal";
        var fixture = new BatchViewModelFixture();
        await fixture.PrepareAsync();
        var reviewed = fixture.Batch.Snapshot.Review;
        var approvals = fixture.Batch.Items.Select(item => item.ApprovedPlan).ToArray();
        var entered = BatchViewModelFixture.Signal();
        var release = new TaskCompletionSource<AppearanceSaveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeCount = 0;
        var store = new AppearanceUiStore
        {
            Save = _ =>
            {
                if (Interlocked.Increment(ref writeCount) == 1)
                {
                    entered.TrySetResult();
                    return release.Task;
                }

                return Task.FromResult(new AppearanceSaveResult(true));
            }
        };
        var editor = new AppearanceViewModel(new(store, fixture.Localization, new(null)));
        await editor.InitializeAsync();
        editor.ThemeIndex = (int)AppearanceTheme.Dark;
        await BatchViewModelFixture.Await(entered.Task);
        var accepted = editor.Current;
        editor.AccentHex = "#invalid";
        Assert.Same(accepted, editor.Current);
        release.SetResult(new(false, new(code, failureDetail)));
        await editor.WhenIdleAsync();
        editor.AccentHex = accepted.AccentHex;

        Assert.Same(accepted, editor.Current);
        Assert.True(editor.HasError);
        Assert.Equal(editor.Localization["AppearanceSessionOnly"], editor.ErrorText);
        Assert.Equal(failureDetail, editor.Diagnostics);
        Assert.Single(store.Writes);
        Assert.False(editor.HasPendingSave);
        Assert.Same(reviewed, fixture.Batch.Snapshot.Review);
        Assert.Equal(approvals, fixture.Batch.Items.Select(item => item.ApprovedPlan));
        Assert.True(fixture.Batch.CanRun);
        Assert.Empty(fixture.Backend.RemuxRequests);
        editor.AccentHex = "#168272";
        await editor.WhenIdleAsync();
        Assert.False(editor.HasError);
        Assert.Empty(editor.Diagnostics);
        Assert.Equal(2, store.Writes.Count);
        Assert.Equal(editor.Current, store.Writes.Last().Settings);
        Assert.Same(reviewed, fixture.Batch.Snapshot.Review);
    }

    /// <summary>A custom muted color accepted on normal surfaces must remain readable on the selected file-row surface.</summary>
    [Fact]
    public void SelectedSurfaceRetainsContrastForCustomSecondaryText()
    {
        var result = AppearancePalettePolicy.Resolve(new(AccentHex: "#5B6CDA",
            LightOverrides: new(SecondaryText: "#707070")));
        Assert.True(result.IsValid, result.ErrorKey);
        var light = Assert.IsType<AppearancePalette>(result.Light);

        foreach (var surface in new[] { light.Window, light.Panel, light.Secondary, light.Selection })
        {
            Assert.True(AppearancePalettePolicy.Contrast(light.Text, surface) >= AppearancePalettePolicy.TextContrast);
            Assert.True(AppearancePalettePolicy.Contrast(light.Muted, surface) >= AppearancePalettePolicy.TextContrast,
                $"Secondary text contrast on {surface} was {AppearancePalettePolicy.Contrast(light.Muted, surface)}.");
        }
    }
    /// <summary>Checks effective selected-row brushes and a visible selected boundary on the safe fallback surface.</summary>
    [AvaloniaFact]
    public async Task ActualSelectedRowPreservesMutedContrastAndItsSelectionIndicator()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.Main.InitializeAsync();
        fixture.Appearance.ThemeIndex = (int)AppearanceTheme.Light;
        fixture.Appearance.LightMuted = "#707070";
        await fixture.LoadAsync();
        var view = new BatchView { DataContext = fixture.Batch };
        var window = new Window { Content = view, Width = 1040, Height = 800 };
        window.Show();

        try
        {
            Dispatcher.UIThread.RunJobs();
            var list = view.FindControl<ListBox>("BatchFileList")!;
            var item = fixture.Batch.Items[0];
            list.SelectedItem = item;
            Dispatcher.UIThread.RunJobs();
            var container = Assert.IsType<ListBoxItem>(list.ContainerFromItem(item));
            var presenter = container.GetVisualDescendants().OfType<ContentPresenter>().First(control => control.Name == "PART_ContentPresenter");
            var secondary = container.GetVisualDescendants().OfType<TextBlock>().First(control => control.Text == item.ContextPath);
            var surface = Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Background).Color;
            var text = Assert.IsAssignableFrom<ISolidColorBrush>(secondary.Foreground).Color;
            var indicator = Assert.IsAssignableFrom<ISolidColorBrush>(presenter.BorderBrush);
            Assert.True(AppearancePalettePolicy.Contrast(text, surface) >= AppearancePalettePolicy.TextContrast);
            Assert.True(presenter.BorderThickness.Left > 0);
            Assert.Equal(1d, indicator.Opacity);
            Assert.True(AppearancePalettePolicy.Contrast(indicator.Color, surface) >= AppearancePalettePolicy.IndicatorContrast);
            Assert.Empty(fixture.Backend.RemuxRequests);
        }
        finally
        {
            await fixture.Appearance.WhenIdleAsync();
            window.Close();
        }
    }

}
