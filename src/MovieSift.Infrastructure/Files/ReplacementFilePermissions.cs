using System.Runtime.InteropServices;
using MovieSift.Application;
using MovieSift.Core.Failures;
using MovieSift.Core.Media;

namespace MovieSift.Infrastructure.Files;

/// <summary>Preserves ordinary Linux access permissions on a verified owned candidate before it replaces an original.</summary>
internal static class ReplacementFilePermissions
{
    private const uint OrdinaryPermissions = 0x1FF;
    private const uint SpecialPermissions = 0xE00;
    private const int NoAttribute = 61;
    private const int AttributesNotSupported = 95;
    private const string AccessAclAttribute = "system.posix_acl_access";

    /// <summary>Changes only the owned candidate; unsupported ownership or ACLs leave the original untouched.</summary>
    internal static void Prepare(SourceRevision source, SourceRevision candidate)
    {
        if (!OperatingSystem.IsLinux())
        {
            // Windows File.Replace applies the existing destination's ACL through the platform replacement operation.
            return;
        }

        using var sourceStream = FileSourceAccess.OpenRegularReadStream(source.FullPath);
        using var candidateStream = FileSourceAccess.OpenRegularReadStream(candidate.FullPath);
        var sourceHandle = new HandleRequest(source.FullPath, sourceStream.SafeFileHandle);
        var candidateHandle = new HandleRequest(candidate.FullPath, candidateStream.SafeFileHandle);

        if (FileSourceAccess.ReadRegularRevision(sourceHandle) != source)
        {
            throw new OperationFailureException(new(ErrorCode.SourceChanged));
        }

        if (FileSourceAccess.ReadRegularRevision(candidateHandle) != candidate)
        {
            throw new OperationFailureException(new(ErrorCode.VerificationFailed, "The candidate changed before its permissions were prepared."));
        }

        var original = ReadInformation(sourceHandle);
        var replacement = ReadInformation(candidateHandle);

        if (original.User != replacement.User || original.Group != replacement.Group)
        {
            throw Unsupported("The replacement cannot preserve the original owner and group. No ownership or privilege changes were attempted.");
        }

        if (((original.Mode | replacement.Mode) & SpecialPermissions) != 0)
        {
            throw Unsupported("Special Unix permission bits are not supported for source replacement.");
        }

        RejectExtendedAccessAcl(sourceHandle);
        RejectExtendedAccessAcl(candidateHandle);
        var mode = original.Mode & OrdinaryPermissions;

        if (NativeMethods.FChmod(candidateHandle.Handle.DangerousGetHandle().ToInt32(), mode) != 0)
        {
            throw Unsupported($"The owned candidate's permissions could not be preserved (OS error {Marshal.GetLastPInvokeError()}).");
        }

        var currentSource = ReadInformation(sourceHandle);
        var currentCandidate = ReadInformation(candidateHandle);
        var unchangedSource = SamePermissions(original, currentSource);
        var preservedCandidate = currentCandidate.User == original.User && currentCandidate.Group == original.Group &&
            (currentCandidate.Mode & (OrdinaryPermissions | SpecialPermissions)) == mode;

        if (!unchangedSource || !preservedCandidate)
        {
            throw Unsupported("Permissions changed before replacement could be committed.");
        }
    }

    /// <summary>Reads permissions from the same regular-file handle whose payload revision was already checked.</summary>
    private static LinuxFileInformation ReadInformation(HandleRequest request)
    {
        if (NativeMethods.FStat(request.Handle.DangerousGetHandle().ToInt32(), out var information) != 0)
        {
            throw Unsupported($"File permissions could not be read (OS error {Marshal.GetLastPInvokeError()}).");
        }

        return information;
    }

    /// <summary>Rejects access ACLs instead of dropping source rules or retaining additional inherited candidate grants.</summary>
    private static void RejectExtendedAccessAcl(HandleRequest request)
    {
        var length = NativeMethods.FGetXAttr(request.Handle.DangerousGetHandle().ToInt32(), AccessAclAttribute, IntPtr.Zero, 0);

        if (length >= 0)
        {
            throw Unsupported("Extended POSIX access ACLs are not supported for source replacement. Create a copy instead.");
        }

        var error = Marshal.GetLastPInvokeError();

        if (error is not (NoAttribute or AttributesNotSupported))
        {
            throw Unsupported($"The file's POSIX access ACL could not be checked (OS error {error}).");
        }
    }

    /// <summary>Compares owner, group and permission bits without treating unrelated access timestamps as payload changes.</summary>
    private static bool SamePermissions(LinuxFileInformation first, LinuxFileInformation second) =>
        first.User == second.User && first.Group == second.Group &&
        (first.Mode & (OrdinaryPermissions | SpecialPermissions)) == (second.Mode & (OrdinaryPermissions | SpecialPermissions));

    /// <summary>Reports an actionable localized boundary while retaining the precise filesystem detail for diagnostics.</summary>
    private static OperationFailureException Unsupported(string detail) =>
        new(new(ErrorCode.InvalidDestination, detail, ErrorReason.ReplacementPermissions));
}
