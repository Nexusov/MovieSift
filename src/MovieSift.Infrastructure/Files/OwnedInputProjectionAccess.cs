using MovieSift.Application;
using MovieSift.Application.Ports;
using MovieSift.Core.Failures;
using MovieSift.Core.Media;

namespace MovieSift.Infrastructure.Files;

/// <summary>Opens only the identity-bound writable input copy; it never opens the original for writing.</summary>
internal static class OwnedInputProjectionAccess
{
    /// <summary>Rejects aliases, redirections and substituted resources while allowing the owned copy's content to grow.</summary>
    internal static void Validate(OwnedInputProjection projection)
    {
        if (projection.CreatedFile.FileIdentity == projection.OriginalSource.FileIdentity ||
            projection.CreatedFile.FullPath == projection.OriginalSource.FullPath)
        {
            throw new OperationFailureException(new(ErrorCode.OwnershipInvalid, "An input projection must be distinct from the original source."));
        }

        ValidateCreatedFile(projection.CreatedFile);
    }

    /// <summary>Checks stable identity and a single writable regular link without trusting stale empty-file length or mtime.</summary>
    internal static void ValidateCreatedFile(SourceRevision created)
    {
        PathSafety.RejectReparsePoints(created.FullPath);
        using var stream = FileSourceAccess.OpenRegularReadStream(created.FullPath);
        var current = FileSourceAccess.ReadRegularRevision(new(created.FullPath, stream.SafeFileHandle));

        if (current.FileIdentity != created.FileIdentity)
        {
            throw new OperationFailureException(new(ErrorCode.OwnershipInvalid, "The input projection file identity changed; it was preserved."));
        }

        FileSourceAccess.ValidateReplaceableSource(current);
    }

    /// <summary>Returns a non-truncating handle only after checking the actually opened file against the reservation.</summary>
    internal static FileStream OpenReadWrite(OwnedInputProjection projection)
    {
        Validate(projection);
        var stream = new FileStream(projection.CreatedFile.FullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);

        try
        {
            var opened = FileSourceAccess.ReadRegularRevision(new(projection.CreatedFile.FullPath, stream.SafeFileHandle));

            if (opened.FileIdentity != projection.CreatedFile.FileIdentity)
            {
                throw new OperationFailureException(new(ErrorCode.OwnershipInvalid, "The opened projection does not identify the reserved file."));
            }

            Validate(projection);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }
}
