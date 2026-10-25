using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TrackTrim.Core.Rules;
using TrackTrim.Desktop.Views;
using TrackTrim.Infrastructure.Persistence;
using Xunit;

namespace TrackTrim.Desktop.Tests;

/// <summary>Connects real malformed catalog bytes to startup, refresh and explicit Load presentation.</summary>
public sealed class RulePresetCatalogInitializationTests
{
    private const string MalformedCatalog = """{"version":1,"presets":[],"\uD800":0}""";
    private const string EmptyCatalog = """{"version":1,"presets":[]}""";

    /// <summary>Completes normal initialization, exposes the actual parsing error, and preserves manual editing until recovery.</summary>
    [AvaloniaFact]
    public async Task ProductionMalformedCatalogDoesNotEscapeStartupAndRefresh()
    {
        using var workspace = new CatalogWorkspace();
        var original = Encoding.ASCII.GetBytes(MalformedCatalog);
        await File.WriteAllBytesAsync(workspace.CatalogPath, original, TestContext.Current.CancellationToken);
        var fixture = new BatchViewModelFixture(new PortableRulePresetStore(workspace.Path));
        await fixture.Main.InitializeAsync();
        var presets = fixture.Batch.Presets;
        var editor = fixture.Batch.LanguageRules;
        var view = new RulePresetsView { DataContext = presets };
        var window = new Window { Content = view };
        window.Show();

        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.True(presets.HasError);
            Assert.Equal(fixture.Localization["PresetErrorInvalidCatalog"], presets.ErrorText);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), text => text.IsVisible && text.Text == presets.ErrorText);
            Assert.False(fixture.Main.PersistenceFailed);
            Assert.True(fixture.Batch.AddFilesCommand.CanExecute(null));
            Assert.True(editor.CanConfigure);
            editor.AudioRuleEnabled = true;
            editor.AudioLanguages = "en";
            var configuration = editor.CaptureConfiguration();
            Assert.False(presets.SaveAsNewCommand.CanExecute(null));
            await presets.SaveAsNewCommand.ExecuteAsync(null);
            await presets.RefreshCatalogCommand.ExecuteAsync(null);
            Assert.True(presets.HasError);
            Assert.True(LanguageRuleConfiguration.ContentEquals(configuration, editor.CaptureConfiguration()));
            Assert.Equal(original, await File.ReadAllBytesAsync(workspace.CatalogPath, TestContext.Current.CancellationToken));
            Assert.Empty(Directory.GetFiles(workspace.Path, ".rule-presets.*.tmp"));

            await File.WriteAllTextAsync(workspace.CatalogPath, EmptyCatalog, TestContext.Current.CancellationToken);
            Assert.False(presets.SaveAsNewCommand.CanExecute(null));
            await presets.RefreshCatalogCommand.ExecuteAsync(null);
            Assert.False(presets.HasError);
            Assert.True(presets.SaveAsNewCommand.CanExecute(null));
            Assert.True(LanguageRuleConfiguration.ContentEquals(configuration, editor.CaptureConfiguration()));
            Assert.Empty(fixture.Backend.RemuxRequests);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Later malformed data is handled identically on Refresh and Load without erasing current workflow state.</summary>
    [AvaloniaTheory]
    [InlineData("refresh")]
    [InlineData("load")]
    public async Task ProductionRefreshAndLoadPreserveWorkflowWhenCatalogBecomesMalformed(string operation)
    {
        using var workspace = new CatalogWorkspace();
        var fixture = new BatchViewModelFixture(new PortableRulePresetStore(workspace.Path));
        await fixture.Main.InitializeAsync();
        await fixture.PrepareAsync();
        var presets = fixture.Batch.Presets;
        var editor = fixture.Batch.LanguageRules;
        presets.PresetName = "Preserved";
        await presets.SaveAsNewCommand.ExecuteAsync(null);
        presets.SelectedPreset = Assert.Single(presets.Presets);
        editor.SubtitleRuleEnabled = true;
        editor.SubtitleLanguages = "en";
        editor.PreviewCommand.Execute(null);
        var preview = Assert.IsType<TrackTrim.Application.Batches.BatchLanguagePreview>(editor.Preview);
        var session = fixture.Session.Snapshot;
        var configuration = editor.CaptureConfiguration();
        var original = Encoding.ASCII.GetBytes(MalformedCatalog);
        await File.WriteAllBytesAsync(workspace.CatalogPath, original, TestContext.Current.CancellationToken);

        var command = operation == "refresh" ? presets.RefreshCatalogCommand : presets.LoadCommand;
        await command.ExecuteAsync(null);

        Assert.True(presets.HasError);
        Assert.Equal(fixture.Localization["PresetErrorInvalidCatalog"], presets.ErrorText);
        Assert.Single(presets.Presets);
        Assert.False(presets.SaveAsNewCommand.CanExecute(null));
        Assert.False(presets.LoadCommand.CanExecute(null));
        Assert.True(editor.CanConfigure);
        Assert.Same(preview, editor.Preview);
        Assert.Same(session, fixture.Session.Snapshot);
        Assert.True(LanguageRuleConfiguration.ContentEquals(configuration, editor.CaptureConfiguration()));
        Assert.Equal(original, await File.ReadAllBytesAsync(workspace.CatalogPath, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(workspace.Path, ".rule-presets.*.tmp"));
        Assert.Empty(fixture.Backend.RemuxRequests);
    }

    /// <summary>Owns only a fresh test catalog and its permanent lock, never application or media data.</summary>
    private sealed class CatalogWorkspace : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("TrackTrim preset JSON ").FullName;
        public string CatalogPath => System.IO.Path.Combine(Path, "rule-presets.json");

        /// <summary>Removes only the two known records and then the empty test directory.</summary>
        public void Dispose()
        {
            File.Delete(CatalogPath);
            File.Delete(System.IO.Path.Combine(Path, "rule-presets.lock"));
            Directory.Delete(Path, recursive: false);
        }
    }
}
