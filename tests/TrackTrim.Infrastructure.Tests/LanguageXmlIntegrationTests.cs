using System.Xml.Linq;
using TrackTrim.Application.Ports;
using TrackTrim.Core.Media;
using TrackTrim.Core.Planning;
using TrackTrim.Infrastructure.MediaTools;
using TrackTrim.Infrastructure.Processes;
using Xunit;

namespace TrackTrim.Infrastructure.Tests;

/// <summary>Exercises German and French chapter/tag language additions emitted by the actual pinned tools.</summary>
public sealed class LanguageXmlIntegrationTests
{
    private const int MaximumDiagnosticsCharacters = 32768;
    private static readonly TimeSpan WorkerTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Preserves raw bibliographic fields while accepting only their equivalent new IETF chapter and tag fields.</summary>
    [MediaFact]
    public async Task RetainsRealGermanFrenchChapterAndTagLanguages()
    {
        using var workspace = new TestWorkspace();
        var chaptersPath = Path.Combine(workspace.Path, "original.chapters.xml");
        var tagsPath = Path.Combine(workspace.Path, "original.tags.xml");
        await File.WriteAllTextAsync(chaptersPath, """
            <?xml version="1.0" encoding="UTF-8"?>
            <Chapters><EditionEntry><EditionUID>701</EditionUID><ChapterAtom><ChapterUID>702</ChapterUID>
            <ChapterTimeStart>00:00:00.000000000</ChapterTimeStart>
            <ChapterDisplay><ChapterString>Original German chapter</ChapterString><ChapterLanguage>ger</ChapterLanguage></ChapterDisplay>
            <ChapterDisplay><ChapterString>Original French chapter</ChapterString><ChapterLanguage>fre</ChapterLanguage></ChapterDisplay>
            </ChapterAtom></EditionEntry></Chapters>
            """, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(tagsPath, """
            <?xml version="1.0" encoding="UTF-8"?>
            <Tags><Tag><Targets/>
            <Simple><Name>COMMENT</Name><String>Original German tag</String><TagLanguage>ger</TagLanguage></Simple>
            <Simple><Name>COMMENT</Name><String>Original French tag</String><TagLanguage>fre</TagLanguage></Simple>
            </Tag></Tags>
            """, TestContext.Current.CancellationToken);
        var sourcePath = Path.Combine(workspace.Path, "bibliographic-chapters-tags.mkv");
        var tools = MkvToolPaths.FromDirectory(MediaTestEnvironment.ToolDirectory);
        await RunToolAsync(new ToolInvocation(tools.MkvMerge,
            ["--disable-language-ietf", "--normalize-language-ietf", "off", "--disable-track-statistics-tags",
                "--output", sourcePath, "--chapters", chaptersPath, "--global-tags", tagsPath,
                "--no-chapters", "--no-global-tags", "--no-track-tags",
                Path.Combine(MediaTestEnvironment.FixtureDirectory, "ordinary.mkv")]));
        var sourceXml = await ExtractXmlAsync(new XmlExtraction(sourcePath, Path.Combine(workspace.Path, "source")));
        Assert.Equal(["ger", "fre"], sourceXml.Chapters.Descendants("ChapterLanguage").Select(element => element.Value));
        Assert.Equal(["ger", "fre"], sourceXml.Tags.Descendants("TagLanguage").Select(element => element.Value));
        Assert.Empty(sourceXml.Chapters.Descendants("ChapLanguageIETF"));
        Assert.Empty(sourceXml.Tags.Descendants("TagLanguageIETF"));
        var backend = MediaTestEnvironment.CreateBackend(Path.Combine(workspace.Path, "scratch"));
        var inspection = await backend.InspectAsync(new InspectRequest(sourcePath));
        var retainedIdentifiers = inspection.Tracks.Where(track => track.Kind != TrackKind.Audio || !track.IsDefault)
            .Select(track => track.Id).ToArray();
        var candidate = Path.Combine(workspace.Path, "retained.mkv");
        var plan = PlanBuilder.Build(new PlanRequest(inspection, retainedIdentifiers, candidate, false)).Plan!;
        Assert.Equal(0, (await backend.RemuxAsync(new RemuxRequest(plan, candidate, null))).ExitCode);
        var verification = await backend.VerifyAsync(new VerifyRequest(plan, candidate));
        Assert.True(verification.Passed, verification.Details);
        var candidateXml = await ExtractXmlAsync(new XmlExtraction(candidate, Path.Combine(workspace.Path, "candidate")));
        Assert.Equal(["ger", "fre"], candidateXml.Chapters.Descendants("ChapterLanguage").Select(element => element.Value));
        Assert.Equal(["ger", "fre"], candidateXml.Tags.Descendants("TagLanguage").Select(element => element.Value));
        Assert.Equal(["de", "fr"], candidateXml.Chapters.Descendants("ChapLanguageIETF").Select(element => element.Value));
        Assert.Empty(candidateXml.Tags.Descendants("TagLanguageIETF"));

        // Pinned102 automatically adds chapter IETF fields, but retains absent tag IETF fields.
        // A second actual MKV candidate independently proves acceptance of equivalent tag IETF additions.
        var equivalentTags = new XDocument(sourceXml.Tags);

        foreach (var simple in equivalentTags.Descendants("Simple"))
        {
            var ietfLanguage = simple.Element("TagLanguage")!.Value == "ger" ? "de" : "fr";
            simple.Add(new XElement("TagLanguageIETF", ietfLanguage));
        }

        var equivalentTagsPath = Path.Combine(workspace.Path, "equivalent.tags.xml");
        await File.WriteAllTextAsync(equivalentTagsPath,
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + equivalentTags, TestContext.Current.CancellationToken);
        var equivalentCandidate = Path.Combine(workspace.Path, "equivalent-tag-ietf.mkv");
        var arguments = MkvArguments.BuildRemux(new RemuxRequest(plan, equivalentCandidate, null)).ToList();
        arguments.InsertRange(0, ["--global-tags", equivalentTagsPath]);
        arguments.InsertRange(arguments.Count - 1, ["--no-global-tags", "--no-track-tags"]);
        await RunToolAsync(new ToolInvocation(tools.MkvMerge, arguments));
        var equivalentVerification = await backend.VerifyAsync(new VerifyRequest(plan, equivalentCandidate));
        Assert.True(equivalentVerification.Passed, equivalentVerification.Details);
        var equivalentXml = await ExtractXmlAsync(new XmlExtraction(equivalentCandidate, Path.Combine(workspace.Path, "equivalent")));
        Assert.Equal(["ger", "fre"], equivalentXml.Tags.Descendants("TagLanguage").Select(element => element.Value));
        Assert.Equal(["de", "fr"], equivalentXml.Tags.Descendants("TagLanguageIETF").Select(element => element.Value));
    }

    /// <summary>Extracts complete real XML into isolated owned paths for explicit before/after assertions.</summary>
    private static async Task<ExtractedXml> ExtractXmlAsync(XmlExtraction request)
    {
        Directory.CreateDirectory(request.Directory);
        var chaptersPath = Path.Combine(request.Directory, "chapters.xml");
        var tagsPath = Path.Combine(request.Directory, "tags.xml");
        var tools = MkvToolPaths.FromDirectory(MediaTestEnvironment.ToolDirectory);
        await RunToolAsync(new ToolInvocation(tools.MkvExtract,
            [request.SourcePath, "chapters", chaptersPath, "tags", tagsPath]));
        return new ExtractedXml(MkvMetadataXml.Read(chaptersPath), MkvMetadataXml.Read(tagsPath));
    }

    /// <summary>Runs a pinned native test worker with the same explicit library and UTF-8 locale environment.</summary>
    private static async Task RunToolAsync(ToolInvocation request)
    {
        var tools = MkvToolPaths.FromDirectory(MediaTestEnvironment.ToolDirectory);
        var environment = new Dictionary<string, string> { ["LC_ALL"] = "C.UTF-8", ["LANG"] = "C.UTF-8" };

        if (tools.NativeLibraryDirectory is { } nativeLibraryDirectory)
        {
            environment["LD_LIBRARY_PATH"] = nativeLibraryDirectory;
        }

        var result = await new OwnedProcessRunner().RunAsync(new ProcessRequest(request.ExecutablePath, request.Arguments,
            MaximumDiagnosticsCharacters, WorkerTimeout, null, TestContext.Current.CancellationToken, environment));
        Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
    }

    private sealed record ToolInvocation(string ExecutablePath, IReadOnlyList<string> Arguments);
    private sealed record XmlExtraction(string SourcePath, string Directory);
    private sealed record ExtractedXml(XDocument Chapters, XDocument Tags);
}
