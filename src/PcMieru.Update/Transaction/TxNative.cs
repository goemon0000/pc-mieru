using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PcMieru.Update.Transaction;

/// <summary>
/// The Windows calls the swap needs that the BCL does not give: a rename that can replace (MoveFileEx), the free space of a volume (GetDiskFreeSpaceEx), and the identity of a file (volume serial and file index,
/// asked of an open HANDLE with no access, so a gate held exclusively can still be asked). Library-import P/Invoke: the swapper that calls this is NativeAOT. The DLL is resolved from System32 only.
///
/// <b>Why MoveFileEx and not ReplaceFile (DESIGN-B 5, "a Windows API that waits for the write to complete").</b> The journal, installed.json and every swapped file are put in place with one call that
/// (a) works when the target does not exist yet (the first journal, a new name) and when it does (MOVEFILE_REPLACE_EXISTING), where ReplaceFile fails on a missing target;
/// (b) has no backup file and no merge of attributes, ACLs or alternate streams, so what a replace does is one thing and the same in every case;
/// (c) is one rename of the directory entry, atomic;
/// (d) without MOVEFILE_COPY_ALLOWED is an error across volumes, never a silent copy-then-delete (a copy would not be atomic).
///
/// <b>What is promised, and what is not.</b> MOVEFILE_WRITE_THROUGH is passed, but for a rename inside one volume it is not a promise that the new name is on the disk when the call returns (the flag is documented for
/// the copy case, which is refused here). What the swap stands on is the ORDER: a file's data is flushed (FileStream.Flush(true), see <see cref="TxIo"/>) BEFORE the rename that gives it its name, and NTFS (and ReFS) log
/// their metadata changes in order, so after a power cut a name never points at data that was not on the disk. Whether the last rename survived is not assumed: the journal says what was intended, and every recovery decides from
/// the real hashes of the files (a file is the old one, the new one, or recovery stops). That is why a swap is prepared only on NTFS or ReFS (<see cref="SystemVolumeFormat.IsSupported"/>); a recovery runs on any file system.
/// </summary>
internal static partial class TxNative
{
    private const uint MoveReplaceExisting = 0x1;
    private const uint MoveWriteThrough = 0x8;
    private const uint ShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x0200_0000;
    private const uint OpenReparsePoint = 0x0020_0000;

    private const int ErrorAccessDenied = 5;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileEx(string existing, string target, uint flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceEx(string directory, out ulong freeToCaller, out ulong total, out ulong totalFree);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleInformation
    {
        public uint Attributes;
        public FileTime CreationTime;
        public FileTime LastAccessTime;
        public FileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleInformation information);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", EntryPoint = "GetConsoleProcessList")]
    private static partial uint GetConsoleProcessList(ref uint processIds, uint count);

    /// <summary>
    /// True when this process is the only one on its console: a console program started by a double-click owns a window of its own that goes away when it ends. False when it shares the console with a shell (it was
    /// started from a terminal) or has no console at all (nothing to keep open). The buffer holds one id, so only the count is read (the API returns the number needed when the buffer is too small).
    /// </summary>
    internal static bool ConsoleIsOwnedByThisProcess()
    {
        uint slot = 0;
        return GetConsoleProcessList(ref slot, 1) == 1;
    }

    /// <summary>Renames <paramref name="source"/> to <paramref name="target"/> and returns when it is on the disk. An existing target is replaced only when <paramref name="replace"/> is true (else it is an <see cref="IOException"/>).</summary>
    /// <exception cref="IOException">The rename failed (a sharing violation is one, with the HRESULT of ERROR_SHARING_VIOLATION).</exception>
    /// <exception cref="UnauthorizedAccessException">Access was denied.</exception>
    internal static void Move(string source, string target, bool replace)
    {
        var flags = MoveWriteThrough | (replace ? MoveReplaceExisting : 0);
        if (!MoveFileEx(Extended(source), Extended(target), flags))
        {
            throw Failure(Marshal.GetLastPInvokeError(), $"could not rename '{source}' to '{target}'");
        }
    }

    /// <summary>The bytes free to the calling user on the volume of <paramref name="path"/> (an existing folder, or a path whose folder exists).</summary>
    internal static long FreeBytes(string path)
    {
        var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(Path.GetFullPath(path)) ?? path;
        if (!GetDiskFreeSpaceEx(Extended(folder), out var free, out _, out _))
        {
            throw Failure(Marshal.GetLastPInvokeError(), $"could not read the free space of '{folder}'");
        }

        return free > long.MaxValue ? long.MaxValue : (long)free;
    }

    /// <summary>
    /// "Which file is this": volume serial and file index of the file at <paramref name="path"/>, asked with access 0 (sharing is not looked at, so a file another handle holds exclusively - the gate - can be asked).
    /// Null when the file cannot be opened for the question. A path is a name; this is the thing the name pointed to at that moment.
    /// </summary>
    internal static FileIdentity? IdentityOf(string path)
    {
        using var handle = CreateFile(Extended(path), 0, ShareAll, 0, OpenExisting, BackupSemantics | OpenReparsePoint, 0);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info))
        {
            return null;
        }

        return new FileIdentity(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow, info.NumberOfLinks);
    }

    /// <summary>Paths of 248 characters or more are given to the API in the extended form (a path of this install is under a user's folder and may be long).</summary>
    internal static string Extended(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.Length < 248 || full.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return full;
        }

        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }

    private static Exception Failure(int error, string what)
    {
        var message = what + ": " + new Win32Exception(error).Message;
        if (error == ErrorAccessDenied)
        {
            return new UnauthorizedAccessException(message);
        }

        // HRESULT_FROM_WIN32, so a caller (and a test) can tell a sharing violation from any other failure.
        return new IOException(message, unchecked((int)(0x8007_0000u | (uint)error)));
    }

    /// <summary>Whether the exception is the failure of a file held open by someone else.</summary>
    internal static bool IsSharingViolation(Exception ex) =>
        ex is IOException io && ((io.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation);

    /// <summary>
    /// Whether a rename or a delete failed in the way a file in use fails: a sharing or lock violation, or "access denied" (a mapped image of a running program answers a replace or a delete with that). Used to decide
    /// whether the name should be tried aside; it does not say the file is in use (a file with a read-only attribute answers the same way, and renaming it aside works as well).
    /// </summary>
    internal static bool IsLockedFailure(Exception ex) => ex is UnauthorizedAccessException || IsSharingViolation(ex);

    /// <summary>Whether the exception says "a running program is using a managed file": it could not even be moved aside (<see cref="TxInUseException"/>) or a read ran into a lock.</summary>
    internal static bool IsInUse(Exception ex) => ex is TxInUseException || IsSharingViolation(ex);
}

/// <summary>A file's identity on a volume (what its name pointed to): the swapper keeps the gate file's from the start to the end of a swap.</summary>
public readonly record struct FileIdentity(uint Volume, ulong Index, uint Links)
{
    /// <summary>The identity of the file at <paramref name="path"/>, or null when it cannot be asked.</summary>
    public static FileIdentity? Of(string path) => TxNative.IdentityOf(path);

    /// <summary>Same volume and same file index (the link count is not part of "the same file").</summary>
    public bool IsSameFile(FileIdentity other) => Volume == other.Volume && Index == other.Index;
}
