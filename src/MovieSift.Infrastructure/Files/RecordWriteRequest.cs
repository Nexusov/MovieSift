using MovieSift.Application.Ports;
using MovieSift.Core.Media;

namespace MovieSift.Infrastructure.Files;

internal sealed record RecordWriteRequest(OwnedOutput Output, string Stage, SourceRevision? VerifiedCandidate = null);
