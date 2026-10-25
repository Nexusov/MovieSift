using System.Collections.Immutable;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using TrackTrim.Application.Presets;
using TrackTrim.Core.Rules;
using TrackTrim.Desktop.Views;
using Xunit;

namespace TrackTrim.Desktop.Tests;

/// <summary>Exercises explicit catalog interactions through compiled controls and the existing batch editor.</summary>
public sealed class RulePresetsTests
{
    /// <summary>Allows empty-batch CRUD without loading, applying, approving or starting any media work.</summary>
    [AvaloniaFact]
    public async Task EmptyBatchCrudIsExplicitAndPreservesEditorAndUserData()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.Main.InitializeAsync();
        var presets = fixture.Batch.Presets;
        var editor = fixture.Batch.LanguageRules;
        Configure(editor);
        var original = editor.CaptureConfiguration();
        var session = fixture.Session.Snapshot;
        presets.PresetName = "  Сериал — English + forced  ";
        await presets.SaveAsNewCommand.ExecuteAsync(null);
        var saved = Assert.Single(presets.Presets);
        Assert.Equal("Сериал — English + forced", saved.Name);
        Assert.True(LanguageRuleConfiguration.ContentEquals(original, saved.Rules));
        Assert.Null(presets.SelectedPreset);
        Assert.True(LanguageRuleConfiguration.ContentEquals(original, editor.CaptureConfiguration()));

        editor.SubtitleTitleContains = "Other editor text";
        presets.SelectedPreset = saved;
        Assert.Equal("Other editor text", editor.SubtitleTitleContains);
        Assert.Equal(" Captions [cut].* ", saved.Rules.Subtitles.TitleFilter!.Text);
        var changedEditor = editor.CaptureConfiguration();
        presets.PresetName = "Имя — renamed";
        await presets.RenameCommand.ExecuteAsync(null);
        Assert.Equal(saved.Id, Assert.Single(presets.Presets).Id);
        Assert.Equal("Имя — renamed", presets.SelectedPreset!.Name);
        Assert.True(LanguageRuleConfiguration.ContentEquals(changedEditor, editor.CaptureConfiguration()));

        presets.DeleteCommand.Execute(null);
        Assert.True(presets.HasDeleteConfirmation);
        presets.KeepPresetCommand.Execute(null);
        Assert.False(presets.HasDeleteConfirmation);
        Assert.Single(presets.Presets);
        Assert.Equal(2, fixture.PresetStore.WriteCount);
        presets.DeleteCommand.Execute(null);
        await presets.ConfirmDeleteCommand.ExecuteAsync(null);
        Assert.Empty(presets.Presets);
        Assert.Null(presets.SelectedPreset);
        Assert.True(LanguageRuleConfiguration.ContentEquals(changedEditor, editor.CaptureConfiguration()));
        Assert.Same(session, fixture.Session.Snapshot);
        Assert.Empty(fixture.Backend.RemuxRequests);
        Assert.Equal(0, fixture.Outputs.CleanupCount);
    }

    /// <summary>Invalidates only a changed configuration's preview while preserving media choices and all approvals/review.</summary>
    [AvaloniaFact]
    public async Task ExplicitLoadChangesOnlyConfigurationAndIdenticalLoadPreservesPreview()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.PrepareAsync();
        await fixture.Batch.Presets.InitializeAsync();
        var presets = fixture.Batch.Presets;
        var editor = fixture.Batch.LanguageRules;
        Configure(editor);
        presets.PresetName = "Saved rules";
        await presets.SaveAsNewCommand.ExecuteAsync(null);
        var saved = Assert.Single(presets.Presets);
        var session = fixture.Session.Snapshot;
        var plans = fixture.Batch.Items.Select(item => item.ApprovedPlan).ToArray();
        editor.SubtitleTitleContains = "Changed";
        editor.PreviewCommand.Execute(null);
        Assert.NotNull(editor.Preview);
        presets.SelectedPreset = saved;
        Assert.Equal("Changed", editor.SubtitleTitleContains);
        await presets.LoadCommand.ExecuteAsync(null);
        Assert.False(editor.HasPreview);
        Assert.True(LanguageRuleConfiguration.ContentEquals(saved.Rules, editor.CaptureConfiguration()));
        Assert.Same(session, fixture.Session.Snapshot);
        Assert.Equal(plans, fixture.Batch.Items.Select(item => item.ApprovedPlan));
        Assert.True(fixture.Batch.HasReview);

        editor.PreviewCommand.Execute(null);
        var preview = editor.Preview;
        fixture.PresetStore.Catalog = new([saved with
        {
            Rules = saved.Rules with
            {
                Audio = saved.Rules.Audio with { Languages = saved.Rules.Audio.Languages.ToArray().ToImmutableArray() },
                Subtitles = saved.Rules.Subtitles with { Languages = saved.Rules.Subtitles.Languages.ToArray().ToImmutableArray() }
            }
        }]);
        await presets.LoadCommand.ExecuteAsync(null);
        Assert.Same(preview, editor.Preview);
        Assert.Same(session, fixture.Session.Snapshot);
        Assert.Equal(plans, fixture.Batch.Items.Select(item => item.ApprovedPlan));
        Assert.Empty(fixture.Backend.RemuxRequests);
    }

    /// <summary>Catalog-only operations preserve the same issued proposal, actual choices, approvals and aggregate review.</summary>
    [AvaloniaFact]
    public async Task SaveRenameAndDeletePreserveAnApprovedWorkflowAndItsPreview()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.PrepareAsync();
        await fixture.Batch.Presets.InitializeAsync();
        var presets = fixture.Batch.Presets;
        var editor = fixture.Batch.LanguageRules;
        Configure(editor);
        editor.PreviewCommand.Execute(null);
        var preview = editor.Preview;
        var session = fixture.Session.Snapshot;
        var configuration = editor.CaptureConfiguration();
        presets.PresetName = "Original";
        await presets.SaveAsNewCommand.ExecuteAsync(null);
        presets.SelectedPreset = Assert.Single(presets.Presets);
        presets.PresetName = "Renamed";
        await presets.RenameCommand.ExecuteAsync(null);
        presets.DeleteCommand.Execute(null);
        presets.KeepPresetCommand.Execute(null);
        Assert.Same(preview, editor.Preview);
        Assert.Same(session, fixture.Session.Snapshot);
        presets.DeleteCommand.Execute(null);
        await presets.ConfirmDeleteCommand.ExecuteAsync(null);
        Assert.Same(preview, editor.Preview);
        Assert.Same(session, fixture.Session.Snapshot);
        Assert.True(fixture.Batch.HasReview);
        Assert.True(LanguageRuleConfiguration.ContentEquals(configuration, editor.CaptureConfiguration()));
        Assert.Empty(presets.Presets);
        Assert.Empty(fixture.Backend.RemuxRequests);
        Assert.Equal(0, fixture.Outputs.CleanupCount);
    }

    /// <summary>Rejects a delayed load after any relevant newer state, using deterministic read gates.</summary>
    [AvaloniaTheory]
    [InlineData("editor")]
    [InlineData("selection")]
    [InlineData("batch")]
    public async Task DelayedLoadCannotReplaceNewerState(string change)
    {
        var fixture = new BatchViewModelFixture();
        await fixture.LoadAsync();
        await fixture.Batch.Presets.InitializeAsync();
        var presets = fixture.Batch.Presets;
        var editor = fixture.Batch.LanguageRules;
        Configure(editor);
        presets.PresetName = "First";
        await presets.SaveAsNewCommand.ExecuteAsync(null);
        presets.PresetName = "Second";
        await presets.SaveAsNewCommand.ExecuteAsync(null);
        editor.SubtitleTitleContains = "Newer edit";
        presets.SelectedPreset = presets.Presets[0];
        var entered = BatchViewModelFixture.Signal();
        var release = BatchViewModelFixture.Signal();
        fixture.PresetStore.OnRead = async () => { entered.TrySetResult(); await release.Task; };
        var loading = presets.LoadCommand.ExecuteAsync(null);
        await BatchViewModelFixture.Await(entered.Task);
        Assert.False(fixture.Batch.NewBatchCommand.CanExecute(null));
        Assert.False(fixture.Batch.AddFilesCommand.CanExecute(null));

        if (change == "editor")
        {
            editor.AudioTitleContains = "Later user edit";
        }

        if (change == "selection")
        {
            presets.SelectedPreset = presets.Presets[1];
        }

        if (change == "batch")
        {
            Assert.True(fixture.Coordinator.StartNewBatch(fixture.Session.Snapshot.Revision).Applied);
        }

        var expected = editor.CaptureConfiguration();
        release.TrySetResult();
        await BatchViewModelFixture.Await(loading);
        Assert.True(LanguageRuleConfiguration.ContentEquals(expected, editor.CaptureConfiguration()));
        Assert.Equal(fixture.Localization["PresetLoadStale"], presets.StatusText);
        Assert.Empty(fixture.Backend.RemuxRequests);
    }

    /// <summary>Restores the catalog at startup without selecting/loading it, and preserves a valid proposal across EN/RU.</summary>
    [AvaloniaFact]
    public async Task StartupAndBilingualControlsDoNotLoadOrApplyPresets()
    {
        var fixture = new BatchViewModelFixture();
        var stored = new RulePreset(Guid.NewGuid(), "Сериал — English + forced", Rules());
        fixture.PresetStore.Catalog = new([stored]);
        await fixture.Main.InitializeAsync();
        var presets = fixture.Batch.Presets;
        var editor = fixture.Batch.LanguageRules;
        Assert.Single(presets.Presets);
        Assert.Null(presets.SelectedPreset);
        Assert.False(editor.AudioRuleEnabled);
        Assert.False(editor.SubtitleRuleEnabled);
        Assert.False(editor.KeepOnlyForcedSubtitles);
        Assert.Empty(fixture.Batch.Items);
        Assert.False(editor.HasPreview);
        await fixture.LoadAsync();
        presets.SelectedPreset = stored;
        await presets.LoadCommand.ExecuteAsync(null);
        editor.PreviewCommand.Execute(null);
        var preview = editor.Preview;
        var session = fixture.Session.Snapshot;
        var view = new RulePresetsView { DataContext = presets };
        var window = new Window { Content = view };
        window.Show();

        try
        {
            foreach (var language in new[] { "ru", "en" })
            {
                await fixture.Main.ChangeLanguageCommand.ExecuteAsync(language);
                Dispatcher.UIThread.RunJobs();
                Assert.Same(preview, editor.Preview);
                Assert.Same(session, fixture.Session.Snapshot);
                Assert.Equal(stored.Id, presets.SelectedPreset!.Id);
                Assert.Equal(stored.Name, presets.SelectedPreset.Name);
                Assert.True(LanguageRuleConfiguration.ContentEquals(stored.Rules, editor.CaptureConfiguration()));
                Assert.Equal(fixture.Localization["PresetLoad"], view.FindControl<Button>("LoadRulePresetButton")!.Content);
                Assert.True(view.FindControl<Button>("LoadRulePresetButton")!.IsEnabled);
                Assert.Same(stored, view.FindControl<ComboBox>("RulePresetList")!.SelectedItem);
            }

            fixture.Batch.NewBatchCommand.Execute(null);
            fixture.Batch.ConfirmNewBatchCommand.Execute(null);
            Assert.False(editor.HasPreview);
            Assert.Single(presets.Presets);
            Assert.Equal(stored.Id, presets.SelectedPreset!.Id);
            Assert.True(LanguageRuleConfiguration.ContentEquals(stored.Rules, editor.CaptureConfiguration()));
            Assert.Empty(fixture.Backend.RemuxRequests);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Shows catalog damage without disabling manual rule configuration or replacing known catalog data.</summary>
    [AvaloniaFact]
    public async Task CatalogErrorDisablesWritesButNotManualRules()
    {
        var fixture = new BatchViewModelFixture();
        fixture.PresetStore.ReadFailure = new(RulePresetErrorCode.InvalidCatalog, "Controlled malformed catalog");
        await fixture.Main.InitializeAsync();
        var presets = fixture.Batch.Presets;
        Assert.True(presets.HasError);
        Assert.False(presets.SaveAsNewCommand.CanExecute(null));
        Assert.False(presets.LoadCommand.CanExecute(null));
        Assert.True(fixture.Batch.LanguageRules.CanConfigure);
        Assert.True(fixture.Batch.AddFilesCommand.CanExecute(null));
        Assert.False(fixture.Main.PersistenceFailed);
        Configure(fixture.Batch.LanguageRules);
        Assert.True(fixture.Batch.LanguageRules.KeepOnlyForcedSubtitles);
        fixture.PresetStore.ReadFailure = null;
        await presets.RefreshCatalogCommand.ExecuteAsync(null);
        Assert.False(presets.HasError);
        Assert.True(presets.SaveAsNewCommand.CanExecute(null));
    }

    /// <summary>A corrupt catalog discovered by explicit Load cannot change the editor or authorize later writes.</summary>
    [AvaloniaFact]
    public async Task LoadRevalidatesTheWholeCatalogBeforeReplacingAnyEditorValue()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.Main.InitializeAsync();
        var presets = fixture.Batch.Presets;
        presets.PresetName = "Saved";
        await presets.SaveAsNewCommand.ExecuteAsync(null);
        presets.SelectedPreset = Assert.Single(presets.Presets);
        Configure(fixture.Batch.LanguageRules);
        var configuration = fixture.Batch.LanguageRules.CaptureConfiguration();
        fixture.PresetStore.ReadFailure = new(RulePresetErrorCode.InvalidCatalog, "Controlled later corruption");
        await presets.LoadCommand.ExecuteAsync(null);
        Assert.True(presets.HasError);
        Assert.False(presets.SaveAsNewCommand.CanExecute(null));
        Assert.False(presets.LoadCommand.CanExecute(null));
        Assert.True(LanguageRuleConfiguration.ContentEquals(configuration, fixture.Batch.LanguageRules.CaptureConfiguration()));
        Assert.True(fixture.Batch.LanguageRules.CanConfigure);
        Assert.Single(presets.Presets);
        Assert.Empty(fixture.Backend.RemuxRequests);
    }

    /// <summary>Warning ownership and subsequent cleanup uncertainty use the same preset-management gate.</summary>
    [AvaloniaFact]
    public async Task WarningAndCleanupUncertaintyDisableCatalogCommandsWithoutChangingTheirData()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.PrepareAsync();
        await fixture.Batch.Presets.InitializeAsync();
        var presets = fixture.Batch.Presets;
        Configure(fixture.Batch.LanguageRules);
        presets.PresetName = "Preserved";
        await presets.SaveAsNewCommand.ExecuteAsync(null);
        presets.SelectedPreset = Assert.Single(presets.Presets);
        var configuration = fixture.Batch.LanguageRules.CaptureConfiguration();
        fixture.Backend.ExitCodes[fixture.Batch.Items[0].SourcePath] = 1;
        await fixture.Batch.RunCommand.ExecuteAsync(null);
        Assert.True(fixture.Batch.HasWarningCandidate);
        Assert.False(presets.SaveAsNewCommand.CanExecute(null));
        Assert.False(presets.LoadCommand.CanExecute(null));
        Assert.False(presets.DeleteCommand.CanExecute(null));
        var catalog = fixture.PresetStore.Catalog;
        presets.DeleteCommand.Execute(null);
        Assert.False(presets.HasDeleteConfirmation);
        fixture.Outputs.CleanupIssues = [new("fixture-owned", "Controlled cleanup uncertainty")];
        await fixture.Batch.DiscardWarningsCommand.ExecuteAsync(null);
        Assert.True(fixture.Batch.HasSafetyBlock);
        Assert.False(presets.SaveAsNewCommand.CanExecute(null));
        Assert.False(presets.LoadCommand.CanExecute(null));
        Assert.Same(catalog, fixture.PresetStore.Catalog);
        Assert.Equal(1, fixture.PresetStore.WriteCount);
        Assert.True(LanguageRuleConfiguration.ContentEquals(configuration, fixture.Batch.LanguageRules.CaptureConfiguration()));
    }

    /// <summary>Uses the shared conflicting-operation gate and waits for finite persistence when closing.</summary>
    [AvaloniaFact]
    public async Task WorkspaceGateAndShutdownCoverPersistenceWithoutMediaCleanup()
    {
        var fixture = new BatchViewModelFixture();
        await fixture.Main.InitializeAsync();
        var entered = BatchViewModelFixture.Signal();
        var release = BatchViewModelFixture.Signal();
        fixture.PresetStore.OnRead = async () => { entered.TrySetResult(); await release.Task; };
        var reading = fixture.Batch.Presets.RefreshCatalogCommand.ExecuteAsync(null);
        await BatchViewModelFixture.Await(entered.Task);
        Assert.True(fixture.Main.HasActiveOperation);
        var shutdown = fixture.Main.ShutdownAsync();
        Assert.False(shutdown.IsCompleted);
        release.TrySetResult();
        await BatchViewModelFixture.Await(reading);
        await BatchViewModelFixture.Await(shutdown);
        Assert.False(fixture.Main.HasActiveOperation);
        Assert.Equal(0, fixture.Outputs.CleanupCount);
        Assert.Empty(fixture.Backend.RemuxRequests);

        var inspectionEntered = BatchViewModelFixture.Signal();
        var inspectionRelease = BatchViewModelFixture.Signal();
        fixture.Backend.OnInspect = async request =>
        {
            inspectionEntered.TrySetResult();
            await inspectionRelease.Task.WaitAsync(request.CancellationToken);
        };
        var inspecting = fixture.Batch.AddFilesCommand.ExecuteAsync(null);

        try
        {
            await BatchViewModelFixture.Await(inspectionEntered.Task);
            Assert.False(fixture.Batch.Presets.SaveAsNewCommand.CanExecute(null));
            Assert.False(fixture.Batch.Presets.RefreshCatalogCommand.CanExecute(null));
            Assert.False(fixture.Batch.Presets.CanSelect);
        }
        finally
        {
            inspectionRelease.TrySetResult();
        }

        await BatchViewModelFixture.Await(inspecting);
    }

    /// <summary>Sets nontrivial active and inactive editor fields without applying media selections.</summary>
    private static void Configure(TrackTrim.Desktop.ViewModels.BatchLanguageRulesViewModel editor) => editor.LoadConfiguration(Rules());

    /// <summary>Retains raw aliases, ordering, title whitespace and forced-only in one immutable configuration.</summary>
    private static LanguageRules Rules() => new(
        new(LanguageRuleMode.Unchanged, ["ger", "deu", "en"], new(true, " inactive [literal].* ")),
        new(LanguageRuleMode.KeepMatching, ["eng"], new(true, " Captions [cut].* ")), true);
}
