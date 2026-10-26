using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MovieSift.Infrastructure.Files;

internal sealed record HandleRequest(string Path, SafeFileHandle Handle);

[StructLayout(LayoutKind.Sequential)]
internal struct WindowsFileInformation
{
    public uint Attributes;
    public uint CreationTimeLow;
    public uint CreationTimeHigh;
    public uint LastAccessTimeLow;
    public uint LastAccessTimeHigh;
    public uint LastWriteTimeLow;
    public uint LastWriteTimeHigh;
    public uint VolumeSerialNumber;
    public uint FileSizeHigh;
    public uint FileSizeLow;
    public uint NumberOfLinks;
    public uint FileIndexHigh;
    public uint FileIndexLow;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LinuxFileInformation
{
    public ulong Device;
    public ulong Inode;
    public ulong NumberOfLinks;
    public uint Mode;
    public uint User;
    public uint Group;
    public int Padding;
    public ulong DeviceType;
    public long Size;
    public long BlockSize;
    public long Blocks;
    public long AccessSeconds;
    public long AccessNanoseconds;
    public long ModifiedSeconds;
    public long ModifiedNanoseconds;
    public long ChangedSeconds;
    public long ChangedNanoseconds;
    public long Reserved1;
    public long Reserved2;
    public long Reserved3;
}

/// <summary>Exposes file identity APIs and narrowly scoped Linux permission operations on an owned candidate.</summary>
internal static class NativeMethods
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetFileInformationByHandle(SafeFileHandle file, out WindowsFileInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint GetFileType(SafeFileHandle file);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    internal static extern int FStat(int file, out LinuxFileInformation information);

    [DllImport("libc", EntryPoint = "lstat", SetLastError = true)]
    internal static extern int LStat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out LinuxFileInformation information);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    internal static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "fchmod", SetLastError = true)]
    internal static extern int FChmod(int file, uint mode);

    [DllImport("libc", EntryPoint = "fgetxattr", SetLastError = true)]
    internal static extern nint FGetXAttr(int file, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr value, nuint size);
}
