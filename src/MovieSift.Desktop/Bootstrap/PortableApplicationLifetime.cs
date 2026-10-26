using MovieSift.Application.Updates;
using MovieSift.Infrastructure.Updates;

namespace MovieSift.Desktop.Bootstrap;

/// <summary>Registers both desktop and diagnostic processes for their complete executable lifetime.</summary>
internal sealed class PortableApplicationLifetime : IDisposable
{
    private readonly UpdateApplicationLease? lease;

    private PortableApplicationLifetime(UpdateApplicationLease? lease, bool installationInProgress)
    {
        this.lease = lease;
        InstallationInProgress = installationInProgress;
    }

    public bool CanInstallUpdates => lease?.CanInstallUpdates == true;
    public bool InstallationInProgress { get; }

    /// <summary>An installer blocks startup; a read-only parent disables installation while ordinary use remains possible.</summary>
    internal static PortableApplicationLifetime Acquire()
    {
        try
        {
            return new(UpdateApplicationLease.Acquire(AppContext.BaseDirectory), false);
        }
        catch (UpdateException exception) when (exception.Code == UpdateErrorCode.Busy)
        {
            return new(null, true);
        }
    }

    /// <summary>Releases coordination only after the desktop or diagnostic entry point returns.</summary>
    public void Dispose() => lease?.Dispose();
}
