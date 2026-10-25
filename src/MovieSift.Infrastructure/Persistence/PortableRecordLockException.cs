namespace MovieSift.Infrastructure.Persistence;

/// <summary>Reports contention separately from an unsafe or unavailable portable lock.</summary>
internal sealed class PortableRecordLockException(bool isBusy, string detail) : Exception(detail)
{
    internal bool IsBusy { get; } = isBusy;
}
