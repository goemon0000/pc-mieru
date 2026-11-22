using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PcMieru.Update.Transaction;

/// <summary>
/// The four Windows calls the swap needs that the BCL does not give: a rename that returns only when it is on the disk (MoveFileEx with MOVEFILE_WRITE_THROUGH), the free space of a volume
/// (GetDiskFreeSpaceEx), and the identity of a file (volume serial and file index, asked of an open HANDLE with no access, so a gate held exclusively can still be asked).
/// Library-import P/Invoke: the swapper that calls this is NativeAOT. The DLL is resolved from System32 only.
///
/// <b>Why MoveFileEx and not ReplaceFile (DESIGN-B 5, "a Windows API that waits for the write to complete").</b> The journal, installed.json and every swapped file are put in place with one call that
/// (a) works when the target does not exist yet (the first journal, a new name) and when it does (MOVEFILE_REPLACE_EXISTING), where ReplaceFile fails on a missing target;
/// (b) has no backup file and no merge of attributes, ACLs or alternate streams, so what a replace does is one thing and the same in every case;
/// (c) is one rename of the directory entry, atomic, and with MOVEFILE_WRITE_THROUGH does not return before the move has reached the disk;
/// (d) without MOVEFILE_COPY_ALLOWED is an error across volumes, never a silent copy-then-delete (a copy would not be atomic). The file's DATA is flushed before the call (FileStream.Flush(true), see
/// <see cref="TxIo"/>), so "the data is on the disk, then the name points to it" is the order.
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

    /// <summary>
    /// TEST ONLY (the kill entry of PCMieru.Swap.exe, see <see cref="SwapCli"/>): writes the marker file that tells the test where the process stands before it is killed. The marker is a file the TEST named, outside the
    /// install; it is here, with the other native calls, because nothing outside this file and TxIo.cs may change a file (TxFaultMatrixTests checks that), and it is not a step: a cut here would test nothing.
    /// </summary>
    internal static void WriteTestMarker(string path, string text) => File.WriteAllText(path, text);

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
}

/// <summary>A file's identity on a volume (what its name pointed to): the swapper keeps the gate file's from the start to the end of a swap.</summary>
public readonly record struct FileIdentity(uint Volume, ulong Index, uint Links)
{
    /// <summary>The identity of the file at <paramref name="path"/>, or null when it cannot be asked.</summary>
    public static FileIdentity? Of(string path) => TxNative.IdentityOf(path);

    /// <summary>Same volume and same file index (the link count is not part of "the same file").</summary>
    public bool IsSameFile(FileIdentity other) => Volume == other.Volume && Index == other.Index;
}
