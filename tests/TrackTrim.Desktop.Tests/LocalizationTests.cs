using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using TrackTrim.Core.Failures;
using TrackTrim.Desktop.Localization;
using Xunit;

namespace TrackTrim.Desktop.Tests;

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
            Assert.Equal("Open MKV…", localization["OpenFile"]);
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
        var manager = new ResourceManager("TrackTrim.Desktop.Localization.Strings", typeof(LocalizationService).Assembly);
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
