using TrackTrim.Core.Media;

namespace TrackTrim.Application.Ports;

public interface ISourceAccess
{
    Task<SourceRevision> ReadRevisionAsync(SourceRequest request);
    Task ValidateDestinationAsync(ValidateDestinationRequest request);
    Task PrepareDestinationAsync(ValidateDestinationRequest request);
}
