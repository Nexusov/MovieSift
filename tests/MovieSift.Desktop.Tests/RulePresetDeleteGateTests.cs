using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MovieSift.Core.Rules;
using MovieSift.Desktop.Views;
using Xunit;

namespace MovieSift.Desktop.Tests;

/// <summary>Revalidates workspace write access throughout an already open preset deletion confirmation.</summary>
public sealed class RulePresetDeleteGateTests
{
    /// <summary>Disables confirmation when writes become blocked while leaving the safe decline action available.</summary>
    [AvaloniaFact]
    public async Task WriteGateClosingDisablesConfirmationButNotDecline()
    {
        var fixture = await PrepareAsync();
        var presets = fixture.Batch.Presets;
        var catalog = fixture.PresetStore.Catalog;
        var session = fixture.Session.Snapshot;
        var preview = fixture.Batch.LanguageRules.Preview;
        var configuration = fixture.Batch.LanguageRules.CaptureConfiguration();
        var selected = presets.SelectedPreset;
        var view = new RulePresetsView { DataContext = presets };
        var window = new Window { Content = view };
        window.Show();

        try
        {
            presets.DeleteCommand.Execute(null);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Assert.True(presets.ConfirmDeleteCommand.CanExecute(null));
            Assert.Same(presets.ConfirmDeleteCommand, view.FindControl<Button>("ConfirmDeleteRulePresetButton")!.Command);
            Assert.True(view.FindControl<Button>("ConfirmDeleteRulePresetButton")!.IsEffectivelyEnabled);
            fixture.Main.PersistenceFailed = true;
            fixture.Batch.SetWorkspaceAccess(new(false, true));
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            Assert.True(fixture.Batch.WritesBlocked);
            Assert.True(presets.HasDeleteConfirmation);
            Assert.False(presets.ConfirmDeleteCommand.CanExecute(null));
            Assert.False(view.FindControl<Button>("ConfirmDeleteRulePresetButton")!.IsEffectivelyEnabled);
            Assert.True(presets.KeepPresetCommand.CanExecute(null));
            Assert.True(view.FindControl<Button>("KeepRulePresetButton")!.IsEffectivelyEnabled);
            presets.KeepPresetCommand.Execute(null);
            Assert.False(presets.HasDeleteConfirmation);
            Assert.Same(selected, presets.SelectedPreset);
            Assert.Same(catalog, fixture.PresetStore.Catalog);
            Assert.Equal(2, fixture.PresetStore.WriteCount);
            Assert.Same(session, fixture.Session.Snapshot);
            Assert.Same(preview, fixture.Batch.LanguageRules.Preview);
            Assert.True(LanguageRuleConfiguration.ContentEquals(configuration, fixture.Batch.LanguageRules.CaptureConfiguration()));
            Assert.True(fixture.Batch.HasReview);
            Assert.Empty(fixture.Backend.RemuxRequests);
            Assert.Equal(0, fixture.Outputs.CleanupCount);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Direct asynchronous invocation cannot bypass a write restriction that appeared after consent was requested.</summary>
    [AvaloniaFact]
    public async Task DirectConfirmationExecutionRechecksWriteGateWithoutChangingLoadedWorkflow()
    {
        var fixture = await PrepareAsync();
        var presets = fixture.Batch.Presets;
        var catalog = fixture.PresetStore.Catalog;
        var session = fixture.Session.Snapshot;
        var preview = fixture.Batch.LanguageRules.Preview;
        var configuration = fixture.Batch.LanguageRules.CaptureConfiguration();
        var selected = presets.SelectedPreset;
        presets.DeleteCommand.Execute(null);
        fixture.Main.PersistenceFailed = true;
        fixture.Batch.SetWorkspaceAccess(new(false, true));

        await presets.ConfirmDeleteCommand.ExecuteAsync(null);

        Assert.Same(catalog, fixture.PresetStore.Catalog);
        Assert.Equal(2, fixture.PresetStore.WriteCount);
        Assert.True(presets.HasDeleteConfirmation);
        Assert.Same(selected, presets.SelectedPreset);
        Assert.Equal(catalog.Items, presets.Presets);
        Assert.Same(session, fixture.Session.Snapshot);
        Assert.Same(preview, fixture.Batch.LanguageRules.Preview);
        Assert.True(LanguageRuleConfiguration.ContentEquals(configuration, fixture.Batch.LanguageRules.CaptureConfiguration()));
        Assert.True(fixture.Batch.HasReview);
        Assert.True(presets.KeepPresetCommand.CanExecute(null));
        presets.KeepPresetCommand.Execute(null);
        Assert.False(presets.HasDeleteConfirmation);
        Assert.Empty(fixture.Backend.RemuxRequests);
        Assert.Equal(0, fixture.Outputs.CleanupCount);
    }

    /// <summary>A permitted confirmation deletes only its captured stable identity and preserves the loaded workflow.</summary>
    [AvaloniaFact]
    public async Task PermittedConfirmationDeletesOnlyIntendedPreset()
    {
        var fixture = await PrepareAsync();
        var presets = fixture.Batch.Presets;
        var target = presets.SelectedPreset!;
        var other = presets.Presets.Single(preset => preset.Id != target.Id);
        var session = fixture.Session.Snapshot;
        var preview = fixture.Batch.LanguageRules.Preview;
        var configuration = fixture.Batch.LanguageRules.CaptureConfiguration();
        presets.DeleteCommand.Execute(null);
        Assert.True(presets.HasDeleteConfirmation);
        Assert.False(presets.CanRead);
        Assert.False(presets.CanWrite);
        Assert.True(presets.ConfirmDeleteCommand.CanExecute(null));

        await presets.ConfirmDeleteCommand.ExecuteAsync(null);

        Assert.Equal(other, Assert.Single(fixture.PresetStore.Catalog.Items));
        Assert.Equal(other, Assert.Single(presets.Presets));
        Assert.Equal(3, fixture.PresetStore.WriteCount);
        Assert.False(presets.HasDeleteConfirmation);
        Assert.Null(presets.SelectedPreset);
        Assert.Same(session, fixture.Session.Snapshot);
        Assert.Same(preview, fixture.Batch.LanguageRules.Preview);
        Assert.True(LanguageRuleConfiguration.ContentEquals(configuration, fixture.Batch.LanguageRules.CaptureConfiguration()));
        Assert.True(fixture.Batch.HasReview);
        Assert.Empty(fixture.Backend.RemuxRequests);
        Assert.Equal(0, fixture.Outputs.CleanupCount);
    }

    /// <summary>Loads a saved rule snapshot into a genuinely approved and reviewed headless batch before requesting deletion.</summary>
    private static async Task<BatchViewModelFixture> PrepareAsync()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.PrepareAsync();
        await fixture.Batch.Presets.InitializeAsync();
        var presets = fixture.Batch.Presets;
        var editor = fixture.Batch.LanguageRules;
        editor.LoadConfiguration(new(
            new(LanguageRuleMode.KeepMatching, ["eng"], new(false, " inactive audio ")),
            new(LanguageRuleMode.KeepMatching, ["en"], new(false, " Captions ")), true));
        presets.PresetName = "Loaded target";
        await presets.SaveAsNewCommand.ExecuteAsync(null);
        var target = Assert.Single(presets.Presets);
        presets.PresetName = "Other preserved preset";
        await presets.SaveAsNewCommand.ExecuteAsync(null);
        presets.SelectedPreset = target;
        editor.AudioLanguages = "ru";
        await presets.LoadCommand.ExecuteAsync(null);
        Assert.True(LanguageRuleConfiguration.ContentEquals(target.Rules, editor.CaptureConfiguration()));
        editor.PreviewCommand.Execute(null);
        Assert.NotNull(editor.Preview);
        Assert.True(fixture.Batch.HasReview);
        Assert.All(fixture.Batch.Items, item => Assert.NotNull(item.ApprovedPlan));
        return fixture;
    }
}
