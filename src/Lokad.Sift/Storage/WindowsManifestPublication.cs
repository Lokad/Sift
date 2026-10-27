using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using Microsoft.Win32.SafeHandles;

namespace Lokad.Sift.Storage;

[SupportedOSPlatform("windows")]
internal static class WindowsManifestPublication
{
    private const uint DeleteAccess = 0x00010000;
    private const uint OpenExisting = 3;
    private const uint NormalAttributes = 0x80;
    private const int FileRenameInfoEx = 22;
    private const uint ReplaceExistingWithPosixSemantics = 0x00000003;
    private static readonly int NameOffset = checked((int)Marshal.OffsetOf<RenameInformation>(nameof(RenameInformation.Name)));

    public static FileStream CreateTemporary(string path, string manifestPath)
    {
        if (!File.Exists(manifestPath))
            return new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);

        // Apply the existing DACL at creation, before any metadata bytes are written.
        var security = new FileInfo(manifestPath).GetAccessControl(AccessControlSections.Access);
        return new FileInfo(path).Create(FileMode.Create, FileSystemRights.Write,
            FileShare.None, 4096, FileOptions.None, security);
    }

    public static void Publish(string path, string manifestPath)
    {
        using var handle = CreateFile(ExtendedPath(path), DeleteAccess,
            FileShare.ReadWrite | FileShare.Delete, 0, OpenExisting, NormalAttributes, 0);
        if (handle.IsInvalid)
            throw new IOException("Unable to open the prepared Sift manifest for publication.",
                new Win32Exception(Marshal.GetLastPInvokeError()));

        var target = ExtendedPath(manifestPath);
        var nameBytes = checked(target.Length * sizeof(char));
        // FILE_RENAME_INFO has pointer-sized alignment on x86, x64 and Arm64.
        var information = new byte[checked(Marshal.SizeOf<RenameInformation>() + nameBytes)];
        BinaryPrimitives.WriteUInt32LittleEndian(information, ReplaceExistingWithPosixSemantics);
        BinaryPrimitives.WriteUInt32LittleEndian(information.AsSpan(NameOffset - sizeof(uint)), (uint)nameBytes);
        // Windows paths are UTF-16 code units, including names with lone surrogates.
        MemoryMarshal.AsBytes(target.AsSpan()).CopyTo(information.AsSpan(NameOffset));
        if (!SetFileInformationByHandle(handle, FileRenameInfoEx, information, (uint)information.Length))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error is 1 or 50 or 87 or 120)
                throw new PlatformNotSupportedException(
                    "Atomic Sift manifest publication requires Windows and a filesystem supporting POSIX file rename.",
                    new Win32Exception(error));
            throw new IOException("Unable to atomically publish the Sift manifest.", new Win32Exception(error));
        }

        static string ExtendedPath(string value)
        {
            var fullPath = Path.GetFullPath(value);
            if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal)) return fullPath;
            return fullPath.StartsWith(@"\\", StringComparison.Ordinal)
                ? @"\\?\UNC\" + fullPath[2..]
                : @"\\?\" + fullPath;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RenameInformation
    {
        public uint Flags;
        public nint RootDirectory;
        public uint NameLength;
        public char Name;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share,
        nint security, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass,
        byte[] information, uint length);
}
