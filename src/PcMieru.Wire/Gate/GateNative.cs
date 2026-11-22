using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PcMieru.Wire.Gate;

/// <summary>
/// The two questions the gate asks of a file that the BCL does not answer: "what is your final path" (GetFinalPathNameByHandle) and "how many names do you have, and are you a reparse
/// point" (GetFileInformationByHandle). Both are asked of an open HANDLE, so the answer is about the file that is really held, not about what a path points to a moment later. Library-import
/// P/Invoke (the exes that call the gate are NativeAOT, which has no run-time marshalling stubs to generate); the DLL is resolved from System32 only (assembly-level setting of this project).
/// </summary>
internal static unsafe partial class GateNative
{
    private const uint ShareAll = 0x7; // FILE_SHARE_READ | WRITE | DELETE: asking for a path must never get in the way of anybody
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x0200_0000; // lets a folder be opened as well as a file
    private const uint VolumeNameDosAndNormalized = 0; // VOLUME_NAME_DOS | FILE_NAME_NORMALIZED

    /// <summary>The prefix of a final path of a drive-letter path: <c>\\?\C:\...</c>.</summary>
    private const string Extended = @"\\?\";

    /// <summary>The prefix of a final path of a network path: <c>\\?\UNC\server\share\...</c>.</summary>
    private const string ExtendedUnc = @"\\?\UNC\";

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileTime
    {
        public uint Low;
        public uint High;
    }

    /// <summary>BY_HANDLE_FILE_INFORMATION.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ByHandleInformation
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

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static partial uint GetFinalPathNameByHandle(SafeFileHandle file, char* buffer, uint length, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleInformation information);

    /// <summary>
    /// The final path of an existing file or folder, in the form a program can use (<c>C:\...</c> or <c>\\server\share\...</c>): links of every folder on the way are followed, 8.3 names are made long and
    /// the letters are in the case that is stored. A hard-linked file has no "other" name to give: it is the name that was opened. Null when the path does not exist, cannot be opened for a look,
    /// or has no drive-letter or network form.
    /// </summary>
    internal static string? FinalPathOf(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        // Access 0: only the file's metadata is wanted, and nobody's sharing is affected (not even a running exe's, nor a gate held exclusively).
        using var handle = Native.CreateFile(path, 0, ShareAll, 0, OpenExisting, BackupSemantics, 0);
        return handle.IsInvalid ? null : FinalPathOf(handle);
    }

    /// <summary>The final path of an open handle (see <see cref="FinalPathOf(string)"/>); null when it cannot be had.</summary>
    internal static string? FinalPathOf(SafeFileHandle handle)
    {
        const int Stack = 520;
        var buffer = stackalloc char[Stack];
        var length = GetFinalPathNameByHandle(handle, buffer, Stack, VolumeNameDosAndNormalized);
        if (length == 0)
        {
            return null;
        }

        if (length < Stack)
        {
            return DosPathOf(new string(buffer, 0, (int)length));
        }

        // Longer than the stack buffer (the call said how many characters it needs, the terminator included).
        var big = new char[length];
        fixed (char* heap = big)
        {
            var again = GetFinalPathNameByHandle(handle, heap, length, VolumeNameDosAndNormalized);
            return again == 0 || again >= length ? null : DosPathOf(new string(heap, 0, (int)again));
        }
    }

    /// <summary>
    /// A final path as the system writes it (<c>\\?\C:\a</c>, <c>\\?\UNC\server\share\a</c>) as the path a program uses (<c>C:\a</c>, <c>\\server\share\a</c>). A form that has neither
    /// (<c>\\?\Volume{...}\a</c>, a device) is null: there is no ordinary path to give back, and the caller keeps the path it was given.
    /// </summary>
    internal static string? DosPathOf(string finalPathName)
    {
        if (finalPathName.StartsWith(ExtendedUnc, StringComparison.OrdinalIgnoreCase))
        {
            return finalPathName.Length > ExtendedUnc.Length ? @"\\" + finalPathName[ExtendedUnc.Length..] : null;
        }

        if (finalPathName.StartsWith(Extended, StringComparison.Ordinal))
        {
            var rest = finalPathName[Extended.Length..];
            return rest.Length >= 3 && char.IsAsciiLetter(rest[0]) && rest[1] == ':' && rest[2] == '\\' ? rest : null;
        }

        // Not in the extended form at all (it should not happen with VOLUME_NAME_DOS): only an ordinary drive-letter or network path is taken as it is.
        return Path.IsPathFullyQualified(finalPathName) && !finalPathName.StartsWith(@"\\.\", StringComparison.Ordinal) ? finalPathName : null;
    }

    /// <summary>GetFileInformationByHandle on an open handle: attributes and the number of names the file has. False when the system could not say.</summary>
    internal static bool TryInformationOf(SafeFileHandle handle, out ByHandleInformation information) =>
        GetFileInformationByHandle(handle, out information);
}
