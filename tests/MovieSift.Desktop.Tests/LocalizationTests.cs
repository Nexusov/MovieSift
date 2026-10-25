using System.Collections;
using System.Collections.Immutable;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using MovieSift.Core.Failures;
using MovieSift.Core.Media;
using MovieSift.Desktop.Localization;
using MovieSift.Desktop.ViewModels;
using Xunit;

namespace MovieSift.Desktop.Tests;

/// <summary>Checks explicit language policy and complete resources rather than OS defaults.</summary>
public sealed partial class LocalizationTests
{
    /// <summary>Starts in English even when the process starts with Russian UI culture.</summary>
    [Fact]
    public void FirstLaunchUsesEnglishUnderRussianCulture()
    {
        var previousCulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ru-RU");
            var localization = new LocalizationService();
            Assert.Equal("en", localization.Language);
            Assert.Equal("Open MKV / MP4…", localization["OpenFile"]);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousCulture;
        }
    }

    /// <summary>Falls back to English for corrupt or unsupported stored language values.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("de")]
    [InlineData("ru-RU")]
    [InlineData("RU")]
    public void InvalidLanguageFallsBackToEnglish(string? language)
    {
        var localization = new LocalizationService();
        localization.SetLanguage("ru");
        localization.SetLanguage(language);
        Assert.Equal("en", localization.Language);
    }

    /// <summary>Requires matching keys and argument positions in the actual embedded resources.</summary>
    [Fact]
    public void ResourcesHaveIdenticalKeysAndFormatArguments()
    {
        var manager = new ResourceManager("MovieSift.Desktop.Localization.Strings", typeof(LocalizationService).Assembly);
        var english = ReadResources(manager.GetResourceSet(CultureInfo.GetCultureInfo("en"), true, false)!);
        var russian = ReadResources(manager.GetResourceSet(CultureInfo.GetCultureInfo("ru"), true, false)!);
        Assert.Equal(english.Keys.Order(), russian.Keys.Order());

        foreach (var (key, englishText) in english)
        {
            var englishArguments = FormatArgumentPattern().Matches(englishText).Select(match => match.Groups[1].Value).Order();
            var russianArguments = FormatArgumentPattern().Matches(russian[key]).Select(match => match.Groups[1].Value).Order();
            Assert.Equal(englishArguments, russianArguments);
        }

        foreach (var errorCode in Enum.GetValues<ErrorCode>())
        {
            Assert.Contains($"Error{errorCode}", english.Keys);
        }

        foreach (var reason in Enum.GetValues<ErrorReason>().Where(reason => reason != ErrorReason.None))
        {
            Assert.Contains($"ErrorReason{reason}", english.Keys);
        }

        foreach (var disposition in Enum.GetValues<TrackDisposition>())
        {
            Assert.Contains($"Disposition{disposition}", english.Keys);
        }
    }

    /// <summary>Shows inspected commentary and disabled state in the selected application language.</summary>
    [Fact]
    public void TrackDispositionsAreReadableInBothLanguages()
    {
        var localization = new LocalizationService();
        var track = new MediaTrack(2, 123, TrackKind.Audio, "PCM", "eng", "en", "Commentary", 1,
            false, false, false, ImmutableDictionary<string, string>.Empty, [TrackDisposition.Commentary]);
        var viewModel = new TrackViewModel(new(track, localization, () => { }));
        Assert.Contains("disabled", viewModel.FlagsText);
        Assert.Contains("commentary", viewModel.FlagsText);
        localization.SetLanguage("ru");
        viewModel.RefreshLocalization();
        Assert.Contains("отключена", viewModel.FlagsText);
        Assert.Contains("комментарий", viewModel.FlagsText);
    }

    /// <summary>Reads resource keys independently of generated UI property names.</summary>
    private static Dictionary<string, string> ReadResources(ResourceSet resources)
    {
        return resources.Cast<DictionaryEntry>().ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value!);
    }

    /// <summary>Extracts format argument indexes from localized text.</summary>
    [GeneratedRegex(@"\{(\d+)(?:[^}]*)\}")]
    private static partial Regex FormatArgumentPattern();
}
