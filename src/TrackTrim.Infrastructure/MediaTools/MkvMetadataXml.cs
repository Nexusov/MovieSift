using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using TrackTrim.Application;
using TrackTrim.Core.Failures;

namespace TrackTrim.Infrastructure.MediaTools;

/// <summary>Validates and compares bounded chapter/tag XML with all external resolution disabled.</summary>
internal static class MkvMetadataXml
{
    internal const long MaximumXmlCharacters = 8 * 1024 * 1024;
    private static readonly string[] UnsupportedChapterElements =
        ["ChapterSegmentUID", "ChapterSegmentEditionUID", "ChapterTrack", "ChapterProcess", "EditionDisplay"];

    /// <summary>Reads tool-generated XML while ignoring its standard external DTD declaration.</summary>
    internal static XDocument Read(string path)
    {

        if (!File.Exists(path))
        {
            throw Invalid("Required XML extraction output is missing.");
        }

        if (new FileInfo(path).Length > MaximumXmlCharacters)
        {
            throw Invalid("Metadata XML exceeds the structured-output bound.");
        }

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumXmlCharacters,
            MaxCharactersFromEntities = 0,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };

        try
        {
            using (var depthReader = XmlReader.Create(path, settings))
            {

                while (depthReader.Read())
                {

                    if (depthReader.Depth > 32)
                    {
                        throw Invalid("Metadata XML exceeds the nesting bound.");
                    }
                }
            }

            using var reader = XmlReader.Create(path, settings);
            var document = XDocument.Load(reader);
            return document;
        }
        catch (XmlException exception)
        {
            throw Invalid($"Malformed metadata XML: {exception.Message}");
        }
    }

    /// <summary>Rejects concrete chapter references and ordered editions before an output is produced.</summary>
    internal static void ValidateChapters(XDocument document)
    {
        var unsupportedElement = document.Descendants().FirstOrDefault(element =>
            UnsupportedChapterElements.Contains(element.Name.LocalName, StringComparer.Ordinal));

        if (unsupportedElement is not null)
        {
            throw Unsupported($"Chapter element {unsupportedElement.Name.LocalName} requires an unsupported reference-preservation policy.");
        }

        if (document.Descendants("EditionFlagOrdered").Any(element => element.Value != "0"))
        {
            throw Unsupported("Ordered chapters are not supported; use a standalone ordinary MKV.");
        }
    }

    /// <summary>Supports ordinary global, single-track, and attachment/chapter/edition target tags.</summary>
    internal static void ValidateTags(XDocument document)
    {

        foreach (var tag in document.Descendants("Tag"))
        {
            var trackTargets = tag.Element("Targets")?.Elements("TrackUID").ToArray() ?? [];
            var otherUidTargets = tag.Element("Targets")?.Elements().Any(element =>
                element.Name.LocalName is "AttachmentUID" or "ChapterUID" or "EditionUID") ?? false;
            var unsupportedTargets = trackTargets.Length > 1 || (trackTargets.Length != 0 && otherUidTargets);

            if (unsupportedTargets)
            {
                throw Unsupported("Tags with multiple or mixed track/non-track UID targets require an explicit reference-preservation policy.");
            }
        }
    }

    /// <summary>Rejects dangling track references rather than classifying unknown targets as removed selections.</summary>
    internal static void ValidateTrackTargets(TrackTargetValidation request)
    {

        foreach (var target in request.Document.Descendants("Targets").Elements("TrackUID"))
        {
            var validTarget = ulong.TryParse(target.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var identifier);

            if (!validTarget)
            {
                throw Invalid("A tag contains an invalid TrackUID reference.");
            }

            if (identifier != 0 && !request.KnownTrackUids.Contains(identifier))
            {
                throw Unsupported("A tag references a TrackUID absent from the inspected source.");
            }
        }
    }

    /// <summary>Builds expected tags by removing deleted-track tags and remapping retained UID references.</summary>
    internal static XDocument ExpectedTags(TagExpectation request)
    {
        var expected = new XDocument(request.Source);

        foreach (var tag in expected.Descendants("Tag").ToArray())
        {
            var trackTarget = tag.Element("Targets")?.Element("TrackUID");

            if (trackTarget is null || trackTarget.Value == "0")
            {
                continue;
            }

            var sourceUid = ulong.Parse(trackTarget.Value, CultureInfo.InvariantCulture);

            if (!request.RetainedUidMapping.TryGetValue(sourceUid, out var outputUid))
            {
                tag.Remove();
                continue;
            }

            trackTarget.Value = outputUid.ToString(CultureInfo.InvariantCulture);
        }

        return expected;
    }

    /// <summary>Compares semantic element content, tolerating only equivalent newly added IETF language fields.</summary>
    internal static bool Equivalent(XmlComparison request)
    {
        var expected = request.Expected.Root;
        var actual = request.Actual.Root;

        if (expected is null || actual is null)
        {
            return expected is null && actual is null;
        }

        return Canonicalize(expected) == Canonicalize(actual);
    }

    /// <summary>Canonicalizes metadata element ordering while retaining chapter-atom sequence and exact text.</summary>
    private static string Canonicalize(XElement element)
    {
        var clone = new XElement(element);

        foreach (var parent in clone.DescendantsAndSelf())
        {
            var ietfName = parent.Name.LocalName switch
            {
                "ChapterDisplay" => "ChapLanguageIETF",
                "Simple" => "TagLanguageIETF",
                _ => string.Empty,
            };
            var legacyName = ietfName == "ChapLanguageIETF" ? "ChapterLanguage" : "TagLanguage";

            if (ietfName.Length == 0)
            {
                continue;
            }

            var ietfLanguage = parent.Element(ietfName);
            var legacyLanguage = parent.Element(legacyName);

            if (ietfLanguage is not null && legacyLanguage is not null && EquivalentLanguage(
                new LanguageComparison(legacyLanguage.Value, ietfLanguage.Value)))
            {
                ietfLanguage.Remove();
            }
        }

        return CanonicalElement(clone);
    }

    /// <summary>Ignores ordering only for unordered tag records and properties within a semantic element.</summary>
    private static string CanonicalElement(XElement element)
    {
        var attributes = element.Attributes().OrderBy(attribute => attribute.Name.ToString(), StringComparer.Ordinal)
            .Select(attribute => new XAttribute(attribute));

        if (!element.HasElements)
        {
            return new XElement(element.Name, attributes, element.Value).ToString(SaveOptions.DisableFormatting);
        }

        var children = element.Elements().Select(CanonicalElement).ToArray();
        var preserveOrder = element.Name.LocalName is "EditionEntry" or "ChapterAtom" or "Chapters";

        if (!preserveOrder)
        {
            Array.Sort(children, StringComparer.Ordinal);
        }

        var canonical = new XElement(element.Name, attributes, children.Select(XElement.Parse));
        return canonical.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>Accepts only the documented alpha-2/alpha-3 equivalent language addition.</summary>
    private static bool EquivalentLanguage(LanguageComparison request)
    {

        if (request.Legacy == request.Ietf)
        {
            return true;
        }

        try
        {
            var culture = CultureInfo.GetCultureInfo(request.Ietf);
            return culture.ThreeLetterISOLanguageName == request.Legacy
                && culture.TwoLetterISOLanguageName == request.Ietf;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Creates a stable structured-output error.</summary>
    private static OperationFailureException Invalid(string detail)
    {
        return new OperationFailureException(new Failure(ErrorCode.InvalidToolOutput, detail));
    }

    /// <summary>Creates a stable unsupported-feature error.</summary>
    private static OperationFailureException Unsupported(string detail)
    {
        return new OperationFailureException(new Failure(ErrorCode.UnsupportedFeature, detail));
    }

    internal sealed record TagExpectation(XDocument Source, IReadOnlyDictionary<ulong, ulong> RetainedUidMapping);
    internal sealed record XmlComparison(XDocument Expected, XDocument Actual);
    internal sealed record TrackTargetValidation(XDocument Document, IReadOnlySet<ulong> KnownTrackUids);
    private sealed record LanguageComparison(string Legacy, string Ietf);
}
