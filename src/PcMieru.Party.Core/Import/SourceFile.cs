using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace PcMieru.Party.Import;

/// <summary>
/// What identifies one version of the old save: the file itself (volume serial number + file index, from GetFileInformationByHandle),
/// its size and the SHA-256 of its bytes (lower-case hex; for a file over the cap, of the part that was read).
/// The old program writes a <c>.tmp</c> and then replaces <c>state.json</c>, so a stable content on an open handle does not prove that the
/// same version was read: the identity says "the same file", the SHA-256 says "the same bytes".
/// </summary>
public sealed record SourceFingerprint(uint VolumeSerial, ulong FileIndex, long Size, string Sha256);

/// <summary>Why a file could not be opened. Missing is only FileNotFound / DirectoryNotFound: "not there", nothing else.</summary>
internal enum FileProblem
{
    Missing,
    Denied,
    Sharing,
    Io,
}

/// <summary>One look at a source: its bytes and fingerprint, or why not.</summary>
internal sealed class SourceRead
{
    public byte[]? Bytes { get; init; }
    public SourceFingerprint? Fingerprint { get; init; }
    public FileProblem? Problem { get; init; }

    /// <summary>The file changed under the reader (between two looks, or while it was read), or vanished.</summary>
    public bool Changed { get; init; }

    public string? Detail { get; init; }

    public bool Ok => Bytes is not null;
}

/// <summary>Reading a file the old program may rewrite at any time, and saying whether it stayed the same.</summary>
internal static partial class SourceFile
{
    /// <summary>The old program (and its replace-by-rename) must never be blocked by our read.</summary>
    internal const FileShare ShareAll = FileShare.ReadWrite | FileShare.Delete;

    internal static FileProblem Classify(Exception e) => e switch
    {
        FileNotFoundException or DirectoryNotFoundException => FileProblem.Missing,
        UnauthorizedAccessException => FileProblem.Denied,
        IOException io when IsSharing(io) => FileProblem.Sharing,
        _ => FileProblem.Io,
    };

    /// <summary>ERROR_SHARING_VIOLATION (32) and ERROR_LOCK_VIOLATION (33).</summary>
    private static bool IsSharing(IOException e) => (e.HResult & 0xFFFF) is 32 or 33;

    /// <summary>ERROR_FILE_EXISTS (80) and ERROR_ALREADY_EXISTS (183).</summary>
    internal static bool IsAlreadyExists(IOException e) => (e.HResult & 0xFFFF) is 80 or 183;

    /// <summary>Opens and closes the file with the sharing of a read. Null when it opens; otherwise why not.</summary>
    internal static FileProblem? TryOpen(string path, out string? detail)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, ShareAll, bufferSize: 1, FileOptions.None);
            detail = null;
            return null;
        }
        catch (Exception e)
        {
            detail = e.GetType().Name;
            return Classify(e);
        }
    }

    /// <summary>
    /// Reads the file twice, each time through a fresh open of the path, and says <see cref="SourceRead.Changed"/> unless identity, size and
    /// SHA-256 are the same both times (and the same on the handle before and after each read). At most <paramref name="cap"/> bytes are read.
    /// A file that is gone on the second look is a change too, not "absent": it was there a moment ago.
    /// </summary>
    internal static SourceRead ReadStable(string path, int cap)
    {
        var first = ReadOnce(path, cap);
        if (!first.Ok) return first;
        var second = ReadOnce(path, cap);
        if (!second.Ok) return second;
        if (first.Fingerprint != second.Fingerprint)
            return new SourceRead { Changed = true, Detail = "the file is not the same between two reads" };
        return first;
    }

    private static SourceRead ReadOnce(string path, int cap)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, ShareAll, bufferSize: 1, FileOptions.None);
            var before = Info(fs.SafeFileHandle);
            long size = before.Size;
            int want = (int)Math.Min(size, cap);
            var bytes = new byte[want];
            int got = 0;
            while (got < want)
            {
                int n = fs.Read(bytes, got, want - got);
                if (n == 0) break;
                got += n;
            }

            var after = Info(fs.SafeFileHandle);
            if (got != want || after != before)
                return new SourceRead { Changed = true, Detail = "the file changed while it was read" };

            return new SourceRead
            {
                Bytes = bytes,
                Fingerprint = new SourceFingerprint(before.Volume, before.Index, size, Convert.ToHexStringLower(SHA256.HashData(bytes))),
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            var problem = Classify(e);
            // Not there although the caller saw it a moment ago: it was removed or renamed away in between.
            if (problem == FileProblem.Missing)
                return new SourceRead { Changed = true, Detail = "the file is gone" };
            return new SourceRead { Problem = problem, Detail = e.GetType().Name };
        }
    }

    private readonly record struct HandleInfo(uint Volume, ulong Index, long Size);

    private static HandleInfo Info(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info))
            throw new IOException("GetFileInformationByHandle failed (" + Marshal.GetLastPInvokeError() + ")");
        return new HandleInfo(
            info.VolumeSerialNumber,
            ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow,
            ((long)info.FileSizeHigh << 32) | info.FileSizeLow);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);
}
