using System.Collections.Immutable;
using System.Globalization;
using System.Xml.Linq;
using MovieSift.Core.Media;

namespace MovieSift.Infrastructure.MediaTools;

/// <summary>Projects existing Matroska statistics without scanning media or treating possibly stale tags as proof.</summary>
internal static class MkvReportedTrackSizes
{
    /// <summary>Accepts only consistent, positive statistics attached to one actual file-local track UID.</summary>
    internal static MediaInspection Apply(MediaInspection inspection, XDocument? tags)
    {
        if (tags is null)
        {
            return inspection;
        }

        var values = new Dictionary<ulong, List<long?>>();

        foreach (var tag in tags.Descendants("Tag"))
        {
            var targets = tag.Element("Targets");
            var uidElements = targets?.Elements("TrackUID").ToArray() ?? [];
            var supportedTarget = uidElements.Length == 1 && targets!.Elements().All(element =>
                element.Name.LocalName is "TrackUID" or "TargetTypeValue" or "TargetType");

            if (!supportedTarget || !ulong.TryParse(uidElements[0].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var uid))
            {
                continue;
            }

            foreach (var statistic in tag.Elements("Simple").Where(element => element.Element("Name")?.Value == "NUMBER_OF_BYTES"))
            {
                var valid = long.TryParse(statistic.Element("String")?.Value, NumberStyles.None,
                    CultureInfo.InvariantCulture, out var bytes) && bytes > 0 && bytes <= inspection.Source.Length;

                if (!values.TryGetValue(uid, out var trackValues))
                {
                    trackValues = [];
                    values.Add(uid, trackValues);
                }

                trackValues.Add(valid ? bytes : null);
            }
        }

        return inspection with
        {
            Tracks = inspection.Tracks.Select(track => track with { PayloadSize = FindSize(track.Uid, values) }).ToImmutableArray()
        };
    }

    /// <summary>Refuses ambiguous or invalid statistics instead of inventing a precise byte count.</summary>
    private static TrackPayloadSize? FindSize(ulong? uid, Dictionary<ulong, List<long?>> values)
    {
        if (uid is not { } identifier || !values.TryGetValue(identifier, out var trackValues))
        {
            return null;
        }

        var uniqueValues = trackValues.Distinct().ToArray();
        return uniqueValues.Length == 1 && uniqueValues[0] is { } bytes
            ? new(bytes, TrackPayloadSizeKind.Reported) : null;
    }
}
