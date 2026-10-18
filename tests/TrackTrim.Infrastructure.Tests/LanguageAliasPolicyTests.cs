using System.Xml.Linq;
using TrackTrim.Infrastructure.MediaTools;
using Xunit;

namespace TrackTrim.Infrastructure.Tests;

/// <summary>Checks explicit ISO aliases and strict region, script, extension, and unknown-code distinctions.</summary>
public sealed class LanguageAliasPolicyTests
{
    /// <summary>Permits only registered primary aliases while retaining suffixes exactly.</summary>
    [Theory]
    [InlineData("ger", "deu", true)]
    [InlineData("ger", "de", true)]
    [InlineData("fre", "fra", true)]
    [InlineData("fre", "fr", true)]
    [InlineData("eng", "en", true)]
    [InlineData("ger-Latn-DE", "de-Latn-DE", true)]
    [InlineData("ger-DE", "de-AT", false)]
    [InlineData("zho-Hans", "zh-Hant", false)]
    [InlineData("fr-CA", "fre-FR", false)]
    [InlineData("en-x-private", "eng-x-other", false)]
    [InlineData("qaa", "qaa", true)]
    [InlineData("qaa", "und", false)]
    [InlineData("unknown", "und", false)]
    public void ComparesExplicitAliasesWithoutLosingDistinctions(string expected, string actual, bool equivalent)
    {
        Assert.Equal(equivalent, LanguageAliasPolicy.Equivalent(new LanguageComparison(expected, actual)));
    }

    /// <summary>Rejects invented distinctions and unknown-code IETF additions while preserving existing IETF fields.</summary>
    [Theory]
    [InlineData("ger", null, "de", true)]
    [InlineData("fre", null, "fr", true)]
    [InlineData("fre", null, "fr-CA", false)]
    [InlineData("qaa", null, "qaa", false)]
    [InlineData("unknown", null, "und", false)]
    [InlineData("ger", "de-Latn-DE", "ger-Latn-DE", true)]
    [InlineData("ger", "de-Latn-DE", "de-Latn-AT", false)]
    [InlineData("fre", "fr", null, false)]
    [InlineData("qaa", "qaa", "qaa", true)]
    [InlineData("qaa", "qaa", "und", false)]
    public void ValidatesIetfPresenceAndPlainCodeAdditions(string legacy, string? expectedIetf, string? actualIetf, bool equivalent)
    {
        Assert.Equal(equivalent, LanguageAliasPolicy.MatchesIetf(new IetfLanguageComparison(legacy, expectedIetf, actualIetf)));
    }

    /// <summary>Applies the same alias policy to chapter displays and tags without rewriting inspected XML.</summary>
    [Theory]
    [InlineData("ChapterDisplay", "ChapterLanguage", "ChapLanguageIETF")]
    [InlineData("Simple", "TagLanguage", "TagLanguageIETF")]
    public void XmlUsesSharedAliasesAndLeavesOriginalTreesUntouched(string parentName, string legacyName, string ietfName)
    {
        var source = XDocument.Parse($"<{parentName}><{legacyName}>ger</{legacyName}><{legacyName}>fre</{legacyName}></{parentName}>");
        var candidate = XDocument.Parse($"<{parentName}><{legacyName}>deu</{legacyName}><{legacyName}>fra</{legacyName}><{ietfName}>de</{ietfName}><{ietfName}>fr</{ietfName}></{parentName}>");
        var originalSource = source.ToString();
        var originalCandidate = candidate.ToString();
        Assert.True(MkvMetadataXml.Equivalent(new MkvMetadataXml.XmlComparison(source, candidate)));
        Assert.Equal(originalSource, source.ToString());
        Assert.Equal(originalCandidate, candidate.ToString());
    }

    /// <summary>Retains regional, script, private, and unregistered metadata rather than treating them as base aliases.</summary>
    [Theory]
    [InlineData("ger", "de-Latn-DE", "de-Latn-AT")]
    [InlineData("fre", "fr-CA", "fr-FR")]
    [InlineData("zho", "zh-Hans", "zh-Hant")]
    [InlineData("eng", "en-x-private", "en-x-other")]
    [InlineData("qaa", "qaa", "und")]
    public void XmlRejectsChangedLanguageDistinctions(string legacy, string expectedIetf, string actualIetf)
    {
        var source = XDocument.Parse($"<Simple><TagLanguage>{legacy}</TagLanguage><TagLanguageIETF>{expectedIetf}</TagLanguageIETF></Simple>");
        var candidate = new XDocument(source);
        candidate.Root!.Element("TagLanguageIETF")!.Value = actualIetf;
        Assert.False(MkvMetadataXml.Equivalent(new MkvMetadataXml.XmlComparison(source, candidate)));
    }

    /// <summary>Rejects a new unknown or region-specific XML language field.</summary>
    [Theory]
    [InlineData("qaa", "qaa")]
    [InlineData("unknown", "und")]
    [InlineData("fre", "fr-CA")]
    public void XmlRejectsUnprovenIetfAdditions(string legacy, string addedIetf)
    {
        var source = XDocument.Parse($"<Simple><TagLanguage>{legacy}</TagLanguage></Simple>");
        var candidate = new XDocument(source);
        candidate.Root!.Add(new XElement("TagLanguageIETF", addedIetf));
        Assert.False(MkvMetadataXml.Equivalent(new MkvMetadataXml.XmlComparison(source, candidate)));
    }
}
