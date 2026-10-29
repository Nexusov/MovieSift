using System.Xml.Linq;
using MovieSift.Core.Media;
using MovieSift.Infrastructure.MediaTools;
using Xunit;

namespace MovieSift.Infrastructure.Tests;

/// <summary>Checks descriptive statistics without granting them preservation authority or scanning payloads.</summary>
public sealed class TrackPayloadSizeTests
{
    /// <summary>Uses actual UID ownership, rejecting conflicting, invalid and non-track statistics.</summary>
    [Theory]
    [InlineData("120", "120", true)]
    [InlineData("120", "121", false)]
    [InlineData("120", "-1", false)]
    [InlineData("0", "0", false)]
    [InlineData("999999999999999999999999", "120", false)]
    public void StatisticsMustBeConsistentAndWithinSourceBounds(string first, string second, bool known)
    {
        var source = new SourceRevision("unused.mkv", "identity", 1000, 1);
        var fixture = Path.Combine(MediaTestEnvironment.RepositoryDirectory, "tests", "MovieSift.Infrastructure.Tests", "Fixtures", "ordinary.identification.json");
        var inspection = MkvIdentification.Parse(new(File.ReadAllText(fixture), source));
        var track = inspection.Tracks[1];
        var tags = XDocument.Parse($"""
            <Tags>
              <Tag><Targets><TrackUID>{track.Uid}</TrackUID></Targets><Simple><Name>NUMBER_OF_BYTES</Name><String>{first}</String></Simple></Tag>
              <Tag><Targets><TrackUID>{track.Uid}</TrackUID></Targets><Simple><Name>NUMBER_OF_BYTES</Name><String>{second}</String></Simple></Tag>
              <Tag><Targets/><Simple><Name>NUMBER_OF_BYTES</Name><String>150</String></Simple></Tag>
            </Tags>
            """);

        var result = MkvReportedTrackSizes.Apply(inspection, tags);

        Assert.Equal(inspection.Tracks.Select(item => item.Id), result.Tracks.Select(item => item.Id));
        Assert.Null(inspection.Tracks[1].PayloadSize);
        Assert.Null(result.Tracks[0].PayloadSize);
        Assert.Equal(known ? new TrackPayloadSize(120, TrackPayloadSizeKind.Reported) : null, result.Tracks[1].PayloadSize);
    }
}
