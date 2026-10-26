using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using MovieSift.Application.Updates;
using MovieSift.Infrastructure.Files;

namespace MovieSift.Infrastructure.Updates;

/// <summary>Preserves access restrictions while copying one existing portable path into an update.</summary>
internal sealed class UpdateFilePermissions
{
    private const uint OrdinaryPermissions = 0x1FF;
    private const uint SpecialPermissions = 0xE00;
    private const uint FileTypeMask = 0xF000;
    private const uint DirectoryType = 0x4000;
    private const uint RegularFileType = 0x8000;
    private readonly string source;
    private readonly bool directory;
    private readonly LinuxFileInformation linux;
    private readonly FileSystemSecurity? windows;

    private UpdateFilePermissions(string source, bool directory, LinuxFileInformation linux, FileSystemSecurity? windows)
    {
        this.source = source;
        this.directory = directory;
        this.linux = linux;
        this.windows = windows;
    }

    /// <summary>Rejects unsupported security metadata rather than weakening it during migration.</summary>
    internal static UpdateFilePermissions Capture(string source, bool directory)
    {
        PathSafety.RejectReparsePoints(source);
        if (OperatingSystem.IsWindows())
        {
            return new(source, directory, default, ReadWindows(source, directory));
        }
        if (!OperatingSystem.IsLinux())
        {
            throw Unsupported("This platform cannot preserve portable data permissions.");
        }
        using var handle = OpenLinux(source, directory);
        return new(source, directory, ReadLinux(handle, directory), null);
    }

    /// <summary>Applies Windows restrictions before any private child bytes can be copied.</summary>
    internal void PrepareDirectory(string destination)
    {
        if (OperatingSystem.IsWindows())
        {
            VerifyWindowsSource();
            var permissions = new DirectorySecurity();
            permissions.SetSecurityDescriptorBinaryForm(windows!.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
            var output = new DirectoryInfo(destination);
            if (output.Exists)
            {
                output.SetAccessControl(permissions);
            }
            else
            {
                output.Create(permissions);
            }
            VerifyWindows(destination);
        }
        else
        {
            Directory.CreateDirectory(destination);
        }
    }

    /// <summary>Restores Unix directory modes after children are copied, before the directory switch.</summary>
    internal void CompleteDirectory(string destination)
    {
        if (OperatingSystem.IsWindows())
        {
            VerifyWindowsSource();
            VerifyWindows(destination);
            return;
        }
        VerifyLinuxSource();
        using var handle = OpenLinux(destination, directory: true);
        ApplyLinux(handle);
    }

    /// <summary>Creates a user file with its access restrictions before writing its first byte.</summary>
    internal FileStream CreateFile(string destination)
    {
        FileStream output;
        if (OperatingSystem.IsWindows())
        {
            VerifyWindowsSource();
            var permissions = new FileSecurity();
            permissions.SetSecurityDescriptorBinaryForm(windows!.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
            output = new FileInfo(destination).Create(FileMode.CreateNew,
                FileSystemRights.Write | FileSystemRights.ReadPermissions | FileSystemRights.Synchronize,
                FileShare.None, 4096, FileOptions.None, permissions);
        }
        else
        {
            VerifyLinuxSource();
            output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        try
        {
            if (OperatingSystem.IsWindows())
            {
                VerifyWindowsSecurity(output.GetAccessControl());
            }
            else
            {
                ApplyLinux(output.SafeFileHandle);
            }
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    /// <summary>Rechecks source restrictions and the completed copy before publication can begin.</summary>
    internal void VerifyFile(string destination)
    {
        if (OperatingSystem.IsWindows())
        {
            VerifyWindowsSource();
            VerifyWindows(destination);
        }
        else
        {
            VerifyLinuxSource();
            using var handle = OpenLinux(destination, directory: false);
            var current = ReadLinux(handle, directory: false);
            if (!SameLinuxAccess(linux, current))
            {
                throw Unsupported("The copied user file's permissions do not match its original.");
            }
        }
    }

    private void VerifyLinuxSource()
    {
        using var handle = OpenLinux(source, directory);
        var current = ReadLinux(handle, directory);
        if (current.Device != linux.Device || current.Inode != linux.Inode || !SameLinuxAccess(linux, current))
        {
            throw Unsupported("Portable data permissions changed while the update was being prepared.");
        }
    }

    private void ApplyLinux(SafeFileHandle handle)
    {
        var current = ReadLinux(handle, directory);
        if (current.User != linux.User || current.Group != linux.Group)
        {
            throw Unsupported("The update cannot preserve the owner and group of portable data. No ownership changes were attempted.");
        }
        if (NativeMethods.FChmod(handle.DangerousGetHandle().ToInt32(), linux.Mode & OrdinaryPermissions) != 0 ||
            !SameLinuxAccess(linux, ReadLinux(handle, directory)))
        {
            throw Unsupported("The update could not preserve portable data permissions.");
        }
    }

    private static bool SameLinuxAccess(LinuxFileInformation first, LinuxFileInformation second) =>
        first.User == second.User && first.Group == second.Group &&
        (first.Mode & (OrdinaryPermissions | SpecialPermissions)) == (second.Mode & (OrdinaryPermissions | SpecialPermissions));

    private static SafeFileHandle OpenLinux(string path, bool directory)
    {
        PathSafety.RejectReparsePoints(path);
        // O_NONBLOCK | O_NOFOLLOW | O_CLOEXEC, with O_DIRECTORY for directory metadata.
        var descriptor = NativeMethods.Open(path, 0x800 | 0x20000 | 0x80000 | (directory ? 0x10000 : 0));
        if (descriptor < 0)
        {
            throw Unsupported($"Portable data permissions could not be opened (OS error {Marshal.GetLastPInvokeError()}).");
        }
        return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
    }

    private static LinuxFileInformation ReadLinux(SafeFileHandle handle, bool directory)
    {
        var descriptor = handle.DangerousGetHandle().ToInt32();
        if (NativeMethods.FStat(descriptor, out var information) != 0 ||
            (information.Mode & FileTypeMask) != (directory ? DirectoryType : RegularFileType))
        {
            throw Unsupported("Portable data must remain ordinary files and directories during migration.");
        }
        if ((information.Mode & SpecialPermissions) != 0)
        {
            throw Unsupported("Special Unix permission bits are not supported for portable data migration.");
        }
        RejectLinuxAcl(descriptor, "system.posix_acl_access");
        if (directory)
        {
            RejectLinuxAcl(descriptor, "system.posix_acl_default");
        }
        return information;
    }

    private static void RejectLinuxAcl(int descriptor, string attribute)
    {
        if (NativeMethods.FGetXAttr(descriptor, attribute, IntPtr.Zero, 0) >= 0)
        {
            throw Unsupported("Extended POSIX ACLs are not supported for portable data migration.");
        }
        if (Marshal.GetLastPInvokeError() is not (61 or 95)) // ENODATA or ENOTSUP
        {
            throw Unsupported("The update could not inspect portable data access ACLs.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static FileSystemSecurity ReadWindows(string path, bool directory)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.Encrypted))
        {
            throw Unsupported("Encrypted portable data cannot be migrated by the automatic updater.");
        }
        var sections = AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group;
        FileSystemSecurity permissions = directory
            ? new DirectoryInfo(path).GetAccessControl(sections)
            : new FileInfo(path).GetAccessControl(sections);
        // Freeze current grants and denials; the staging parent's inheritance must not broaden them.
        permissions.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);
        var descriptor = new RawSecurityDescriptor(permissions.GetSecurityDescriptorBinaryForm(), 0);
        if (descriptor.DiscretionaryAcl is { } acl)
        {
            foreach (GenericAce entry in acl)
            {
                entry.AceFlags &= ~AceFlags.Inherited;
            }
        }
        var snapshot = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(snapshot, 0);
        permissions.SetSecurityDescriptorBinaryForm(snapshot, AccessControlSections.Access);
        if (!permissions.AreAccessRulesCanonical)
        {
            // Reordering a deny around an allow can change access; refuse instead of rewriting the policy.
            throw Unsupported("The portable data ACL cannot be frozen without changing its access policy.");
        }
        return permissions;
    }

    [SupportedOSPlatform("windows")]
    private void VerifyWindowsSource() => VerifyWindowsSecurity(ReadWindows(source, directory));

    [SupportedOSPlatform("windows")]
    private void VerifyWindows(string destination)
    {
        var sections = AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group;
        FileSystemSecurity permissions = directory
            ? new DirectoryInfo(destination).GetAccessControl(sections)
            : new FileInfo(destination).GetAccessControl(sections);
        VerifyWindowsSecurity(permissions);
    }

    [SupportedOSPlatform("windows")]
    private void VerifyWindowsSecurity(FileSystemSecurity current)
    {
        if (!current.AreAccessRulesProtected ||
            !Equals(windows!.GetOwner(typeof(SecurityIdentifier)), current.GetOwner(typeof(SecurityIdentifier))) ||
            !Equals(windows.GetGroup(typeof(SecurityIdentifier)), current.GetGroup(typeof(SecurityIdentifier))) ||
            !AccessBytes(windows).AsSpan().SequenceEqual(AccessBytes(current)))
        {
            throw Unsupported("The update cannot preserve the owner, group and access ACL of portable data.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static byte[] AccessBytes(FileSystemSecurity permissions)
    {
        var acl = new RawSecurityDescriptor(permissions.GetSecurityDescriptorBinaryForm(), 0).DiscretionaryAcl;
        if (acl is null)
        {
            return [];
        }
        var bytes = new byte[acl.BinaryLength];
        acl.GetBinaryForm(bytes, 0);
        return bytes;
    }

    private static UpdateException Unsupported(string message) => new(UpdateErrorCode.UnsupportedInstallation, message);
}
