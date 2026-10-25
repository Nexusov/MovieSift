using MovieSift.Core.Media;

namespace MovieSift.Application.Ports;

public interface ISourceAccess
{
    Task<SourceRevision> ReadRevisionAsync(SourceRequest request);
    Task ValidateDestinationAsync(ValidateDestinationRequest request);
    Task PrepareDestinationAsync(ValidateDestinationRequest request);
}
