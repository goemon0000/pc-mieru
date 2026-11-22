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

    // ---------------------------------------------------------------------------------------------------------------- removing a tree on handles

    private const uint AccessDelete = 0x0001_0000;
    private const uint AccessListDirectory = 0x1;
    private const uint AccessReadAttributes = 0x80;
    private const uint AccessSynchronize = 0x0010_0000;
    private const uint ShareReadWrite = 0x3; // NOT FILE_SHARE_DELETE: while a folder is held, nobody can rename or replace it
    private const uint AttributeDirectory = 0x10;
    private const uint AttributeReparsePoint = 0x400;
    private const uint NtOpenForBackupIntent = 0x4000;
    private const uint NtSynchronousIoNonAlert = 0x20;
    private const uint NtCaseInsensitive = 0x40;
    private const int InfoAttributeTag = 9;
    private const int InfoDisposition = 4;
    private const int InfoDispositionEx = 21;
    private const int InfoIdBothDirectory = 10;
    private const int InfoIdBothDirectoryRestart = 11;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorNoMoreFiles = 18;
    private const int StatusObjectNameInvalid = unchecked((int)0xC000_0033);
    private const int StatusObjectNameNotFound = unchecked((int)0xC000_0034);
    private const int StatusObjectPathNotFound = unchecked((int)0xC000_003A);
    private const int StatusDeletePending = unchecked((int)0xC000_0056);

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public nint Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public uint Length;
        public nint RootDirectory;
        public nint ObjectName;
        public uint Attributes;
        public nint SecurityDescriptor;
        public nint SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public nint Status;
        public nint Information;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, void* buffer, uint size);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, void* buffer, uint size);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("ntdll.dll")]
    private static unsafe partial int NtCreateFile(out nint handle, uint access, ObjectAttributes* attributes, out IoStatusBlock status, long* allocationSize, uint fileAttributes, uint share, uint disposition, uint options, void* extendedAttributes, uint extendedAttributesLength);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("ntdll.dll")]
    private static partial uint RtlNtStatusToDosError(int status);

    /// <summary>
    /// Deletes the folder at <paramref name="path"/> and everything in it, WALKING ON HANDLES (review of step 4, M1 residual; a walk by name checks, enumerates and deletes in three steps, and a folder swapped for a
    /// junction in between is entered and what it points to is deleted). Every folder is opened ONCE, with FILE_FLAG_OPEN_REPARSE_POINT (a link is opened as itself) and a sharing mode that does NOT include
    /// FILE_SHARE_DELETE, so it cannot be renamed, replaced or deleted while the walk is inside it. What it is (a link, a folder, a file) is read from THAT handle. A link is a leaf: it is deleted as itself, through its
    /// own handle, and never entered. The entries are listed through the folder's handle, and each child is opened RELATIVE TO the parent's handle (NtCreateFile with a root directory: there is no path to resolve,
    /// so nothing can be redirected by changing a name higher up), also with OPEN_REPARSE_POINT, and is deleted through that handle (POSIX semantics where the file system has them: the name goes away at once).
    /// <paramref name="linkOnly"/>: delete it only when it is a link (a folder that has become a real one meanwhile is left alone). A top that is a plain file is left alone. Nothing there is not an error.
    /// <paramref name="hook"/> is the test seam (null in the product): called with "open" before a node is opened and with "opened" after it was opened and looked at, with the name the walk knows it by.
    /// </summary>
    internal static unsafe void RemoveTree(string path, bool linkOnly, Action<string, string>? hook)
    {
        var full = Path.GetFullPath(path);
        hook?.Invoke("open", full);
        var access = AccessDelete | AccessReadAttributes | AccessSynchronize | (linkOnly ? 0 : AccessListDirectory);
        using var top = OpenTop(full, access);
        if (top is null)
        {
            return;
        }

        RemoveOpened(top, full, isTop: true, canList: !linkOnly, linkOnly, hook);
    }

    private static SafeFileHandle? OpenTop(string full, uint access)
    {
        var handle = CreateFile(Extended(full), access, ShareReadWrite, 0, OpenExisting, BackupSemantics | OpenReparsePoint, 0);
        if (!handle.IsInvalid)
        {
            return handle;
        }

        var error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        if (error is ErrorFileNotFound or ErrorPathNotFound)
        {
            return null;
        }

        // Asked for more than a held file gives (it is not a folder that the walk was asked to take apart): look at it with the attributes alone. A plain file at the top is left alone, as it always was.
        using var peek = CreateFile(Extended(full), AccessReadAttributes, 0x7, 0, OpenExisting, BackupSemantics | OpenReparsePoint, 0);
        if (!peek.IsInvalid && (ReadAttributes(peek, full) & (AttributeDirectory | AttributeReparsePoint)) == 0)
        {
            return null;
        }

        throw Failure(error, $"could not open '{full}' to remove it");
    }

    private static unsafe void RemoveOpened(SafeFileHandle node, string path, bool isTop, bool canList, bool linkOnly, Action<string, string>? hook)
    {
        var attributes = ReadAttributes(node, path);
        hook?.Invoke("opened", path);
        if ((attributes & AttributeReparsePoint) != 0)
        {
            SetDelete(node, path); // the link itself, through its own handle: what it points to is never opened
            return;
        }

        if ((attributes & AttributeDirectory) == 0)
        {
            if (!isTop && !linkOnly)
            {
                SetDelete(node, path);
            }

            return;
        }

        if (linkOnly)
        {
            return; // it was a link when it was looked at; it is a real folder now, and a real folder is not this call's to delete
        }

        if (!canList)
        {
            throw new IOException($"'{path}' became a folder while it was being removed; it is left for the next cleaning");
        }

        foreach (var (name, hint) in ListEntries(node, path))
        {
            var childPath = path + '\\' + name;
            hook?.Invoke("open", childPath);
            var wantList = (hint & AttributeDirectory) != 0 && (hint & AttributeReparsePoint) == 0;
            using var child = OpenChild(node, name, AccessDelete | AccessReadAttributes | AccessSynchronize | (wantList ? AccessListDirectory : 0), childPath);
            if (child is not null)
            {
                RemoveOpened(child, childPath, isTop: false, wantList, linkOnly: false, hook);
            }
        }

        SetDelete(node, path);
    }

    private static unsafe uint ReadAttributes(SafeFileHandle handle, string path)
    {
        var tag = stackalloc uint[2];
        if (!GetFileInformationByHandleEx(handle, InfoAttributeTag, tag, 8))
        {
            throw Failure(Marshal.GetLastPInvokeError(), $"could not read what '{path}' is");
        }

        return tag[0];
    }

    /// <summary>The names in an open folder (FILE_ID_BOTH_DIR_INFO through the handle) with the attributes the listing gave; the attributes are only a hint for how to open the child, what it is is read from its own handle.</summary>
    private static unsafe List<(string Name, uint Attributes)> ListEntries(SafeFileHandle folder, string path)
    {
        const int nameOffset = 104; // FILE_ID_BOTH_DIR_INFO.FileName
        const int attributesOffset = 56;
        const int nameLengthOffset = 60;
        var entries = new List<(string, uint)>();
        var buffer = new byte[64 * 1024];
        var infoClass = InfoIdBothDirectoryRestart;
        fixed (byte* start = buffer)
        {
            while (GetFileInformationByHandleEx(folder, infoClass, start, (uint)buffer.Length))
            {
                infoClass = InfoIdBothDirectory;
                var at = 0;
                while (true)
                {
                    var next = *(uint*)(start + at);
                    var attributes = *(uint*)(start + at + attributesOffset);
                    var nameBytes = *(uint*)(start + at + nameLengthOffset);
                    var name = new string((char*)(start + at + nameOffset), 0, (int)(nameBytes / 2));
                    if (name is not ("." or ".."))
                    {
                        entries.Add((name, attributes));
                    }

                    if (next == 0)
                    {
                        break;
                    }

                    at += (int)next;
                }
            }
        }

        var error = Marshal.GetLastPInvokeError();
        if (error != ErrorNoMoreFiles)
        {
            throw Failure(error, $"could not list '{path}'");
        }

        return entries;
    }

    /// <summary>Opens <paramref name="name"/> inside the held <paramref name="parent"/> (no path is resolved), as itself if it is a link. Null: it is not there any more (or is already being deleted).</summary>
    private static unsafe SafeFileHandle? OpenChild(SafeFileHandle parent, string name, uint access, string pathForErrors)
    {
        fixed (char* chars = name)
        {
            var text = new UnicodeString { Length = checked((ushort)(name.Length * 2)), MaximumLength = checked((ushort)(name.Length * 2)), Buffer = (nint)chars };
            var attributes = new ObjectAttributes
            {
                Length = (uint)sizeof(ObjectAttributes),
                RootDirectory = parent.DangerousGetHandle(),
                ObjectName = (nint)(&text),
                Attributes = NtCaseInsensitive,
            };
            var status = NtCreateFile(out var raw, access, &attributes, out _, null, 0, ShareReadWrite, 1 /* FILE_OPEN */, NtOpenForBackupIntent | OpenReparsePoint | NtSynchronousIoNonAlert, null, 0);
            GC.KeepAlive(parent);
            if (status >= 0)
            {
                return new SafeFileHandle(raw, ownsHandle: true);
            }

            if (status is StatusObjectNameNotFound or StatusObjectPathNotFound or StatusObjectNameInvalid or StatusDeletePending)
            {
                return null;
            }

            throw Failure((int)RtlNtStatusToDosError(status), $"could not open '{pathForErrors}' to remove it");
        }
    }

    /// <summary>Marks the open file or folder deleted: POSIX semantics (the name is free at once, even where another handle still has it open) where they exist, the classic "delete on close" where they do not.</summary>
    private static unsafe void SetDelete(SafeFileHandle handle, string path)
    {
        var flags = 0x1u | 0x2u; // FILE_DISPOSITION_FLAG_DELETE | FILE_DISPOSITION_FLAG_POSIX_SEMANTICS
        if (SetFileInformationByHandle(handle, InfoDispositionEx, &flags, 4))
        {
            return;
        }

        var error = Marshal.GetLastPInvokeError();
        if (error is 1 or 50 or 87) // INVALID_FUNCTION, NOT_SUPPORTED, INVALID_PARAMETER: this system or file system has no "Ex"
        {
            byte on = 1;
            if (SetFileInformationByHandle(handle, InfoDisposition, &on, 1))
            {
                return;
            }

            error = Marshal.GetLastPInvokeError();
        }

        throw Failure(error, $"could not delete '{path}'");
    }

    // ---------------------------------------------------------------------------------------------------------------- the real volume of a folder

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationByHandleW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetVolumeInformationByHandle(SafeFileHandle file, char* volumeName, uint volumeNameSize, out uint serial, out uint maxComponent, out uint flags, char* fileSystemName, uint fileSystemNameSize);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandle(SafeFileHandle file, char* buffer, uint size, uint flags);

    /// <summary>
    /// The volume the folder REALLY lives on, asked of an open handle to it (FILE_FLAG_BACKUP_SEMANTICS; a junction or a symbolic link on the way is followed, so a volume mounted in a folder, or a folder reached through a
    /// junction to another disk, answers for itself and not for the drive letter of its path). A folder that is not there yet is asked through the nearest folder above it that is. Null: it cannot be told
    /// (nothing of the path opens, or it is on a network share).
    /// </summary>
    internal static unsafe VolumeFacts? ReadVolume(string folder)
    {
        var path = Path.GetFullPath(folder);
        while (!string.IsNullOrEmpty(path))
        {
            using var handle = CreateFile(Extended(path), AccessReadAttributes, 0x7, 0, OpenExisting, BackupSemantics, 0);
            if (!handle.IsInvalid)
            {
                return ReadVolume(handle);
            }

            var error = Marshal.GetLastPInvokeError();
            if (error is not (ErrorFileNotFound or ErrorPathNotFound))
            {
                return null;
            }

            path = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));
        }

        return null;
    }

    private static unsafe VolumeFacts? ReadVolume(SafeFileHandle handle)
    {
        var name = stackalloc char[261];
        if (!GetVolumeInformationByHandle(handle, null, 0, out var serial, out _, out _, name, 261))
        {
            return null;
        }

        var size = 1024u;
        var finalPath = new char[size];
        uint needed;
        fixed (char* buffer = finalPath)
        {
            needed = GetFinalPathNameByHandle(handle, buffer, size, 0);
        }

        if (needed > size)
        {
            finalPath = new char[needed];
            fixed (char* buffer = finalPath)
            {
                needed = GetFinalPathNameByHandle(handle, buffer, needed, 0);
            }
        }

        var text = needed == 0 || needed > finalPath.Length ? string.Empty : new string(finalPath, 0, (int)needed);
        return new VolumeFacts(new string(name), serial, text.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase));
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

/// <summary>What the file system says about the volume a folder is really on: the name of its format ("NTFS", "FAT32", ...), its serial number, and whether it is a network share.</summary>
internal readonly record struct VolumeFacts(string Format, uint Serial, bool IsNetwork);

/// <summary>A file's identity on a volume (what its name pointed to): the swapper keeps the gate file's from the start to the end of a swap.</summary>
public readonly record struct FileIdentity(uint Volume, ulong Index, uint Links)
{
    /// <summary>The identity of the file at <paramref name="path"/>, or null when it cannot be asked.</summary>
    public static FileIdentity? Of(string path) => TxNative.IdentityOf(path);

    /// <summary>Same volume and same file index (the link count is not part of "the same file").</summary>
    public bool IsSameFile(FileIdentity other) => Volume == other.Volume && Index == other.Index;
}
