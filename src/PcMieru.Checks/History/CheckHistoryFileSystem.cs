using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PcMieru.Checks;

/// <summary>One regular file directly inside the history folder (sub folders are never listed).</summary>
internal readonly record struct HistoryFileEntry(string Name, DateTime LastWriteUtc);

/// <summary>
/// The reading half of the file system the history needs. The read-only history (the MCP side) is built from this
/// interface alone, and its default implementation (<see cref="PhysicalHistoryFileReader"/>) is not a
/// <see cref="ICheckHistoryFileSystem"/> either, so nothing reachable from it can write, rename or delete (DESIGN-A §4, §8).
/// </summary>
internal interface ICheckHistoryFileReader
{
    /// <summary>The files directly in the folder. A folder that does not exist is an empty list.</summary>
    IReadOnlyList<HistoryFileEntry> ListFiles(string directory);

    /// <summary>
    /// The whole file. Throws <see cref="FileNotFoundException"/> or <see cref="DirectoryNotFoundException"/> when it is gone,
    /// <see cref="UnauthorizedAccessException"/> when the permission is missing, <see cref="IOException"/> for a sharing violation or
    /// other I/O trouble, and <see cref="InvalidDataException"/> when the file is longer than <paramref name="maxBytes"/>.
    /// </summary>
    byte[] ReadAllBytes(string path, int maxBytes);

    bool Exists(string path);
}

/// <summary>The writing half: what the saver, the quarantine and the retention use. Every method may throw the I/O exceptions.</summary>
internal interface ICheckHistoryFileSystem : ICheckHistoryFileReader
{
    void CreateDirectory(string directory);

    /// <summary>
    /// Creates the file with <see cref="FileMode.CreateNew"/> for writing, exclusively. A file of that name that exists is an
    /// <see cref="IOException"/> and is left untouched, never overwritten. Whatever this throws, no file was created by this call.
    /// </summary>
    ICheckHistoryNewFile CreateNew(string path);

    /// <summary>
    /// Renames within the same volume, and does not return before the rename is on the disk (write-through). An existing destination
    /// is an <see cref="IOException"/>; it is never replaced.
    /// </summary>
    void Move(string source, string destination);

    /// <summary>Deletes the file. A file that is not there is not an error.</summary>
    void Delete(string path);
}

/// <summary>A file just created by <see cref="ICheckHistoryFileSystem.CreateNew"/>. Disposing closes it; the saver writes, flushes to disk, then disposes.</summary>
internal interface ICheckHistoryNewFile : IDisposable
{
    void Write(ReadOnlySpan<byte> bytes);

    /// <summary>Forces the bytes to the disk (<see cref="FileStream.Flush(bool)"/> with true).</summary>
    void FlushToDisk();
}

/// <summary>
/// The real file system, reading only. This is what the read-only history is built on by default: it has no method that changes
/// anything and is not an <see cref="ICheckHistoryFileSystem"/>, so a reflection walk from the read-only history finds no writer.
/// </summary>
internal sealed class PhysicalHistoryFileReader : ICheckHistoryFileReader
{
    public static PhysicalHistoryFileReader Instance { get; } = new();

    public IReadOnlyList<HistoryFileEntry> ListFiles(string directory)
    {
        var list = new List<HistoryFileEntry>();
        try
        {
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.TopDirectoryOnly))
            {
                list.Add(new HistoryFileEntry(file.Name, file.LastWriteTimeUtc));
            }
        }
        catch (DirectoryNotFoundException) when (!File.Exists(directory))
        {
            // No history folder yet: nothing has been saved. (A path that is a file, not a folder, is not this case: it is thrown on
            // and the listing reports it as a failure of the folder itself.)
        }

        return list;
    }

    public byte[] ReadAllBytes(string path, int maxBytes)
    {
        // Share Read and Delete: another reader is fine, and a quarantine or retention in another process may rename or
        // delete the file while it is open here. A writer or an exclusive holder makes the open fail (a sharing violation).
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.SequentialScan);
        if (stream.Length > maxBytes)
        {
            throw new InvalidDataException($"The file is longer than {maxBytes} bytes.");
        }

        var buffer = new byte[stream.Length];
        stream.ReadExactly(buffer);
        return buffer;
    }

    public bool Exists(string path) => File.Exists(path);
}

/// <summary>The real file system, reading and writing.</summary>
internal sealed class PhysicalHistoryFileSystem : ICheckHistoryFileSystem
{
    // MOVEFILE_WRITE_THROUGH. Not MOVEFILE_REPLACE_EXISTING: an existing destination makes the call fail, so a move never overwrites.
    private const uint MoveFileWriteThrough = 0x8;

    public static PhysicalHistoryFileSystem Instance { get; } = new();

    public IReadOnlyList<HistoryFileEntry> ListFiles(string directory) => PhysicalHistoryFileReader.Instance.ListFiles(directory);

    public byte[] ReadAllBytes(string path, int maxBytes) => PhysicalHistoryFileReader.Instance.ReadAllBytes(path, maxBytes);

    public bool Exists(string path) => PhysicalHistoryFileReader.Instance.Exists(path);

    public void CreateDirectory(string directory) => Directory.CreateDirectory(directory);

    public ICheckHistoryNewFile CreateNew(string path) => new NewFile(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None));

    public void Move(string source, string destination)
    {
        if (!HistoryNative.MoveFileEx(Path.GetFullPath(source), Path.GetFullPath(destination), MoveFileWriteThrough))
        {
            throw HistoryNative.ToException(Marshal.GetLastPInvokeError());
        }
    }

    public void Delete(string path) => File.Delete(path);

    private sealed class NewFile(FileStream stream) : ICheckHistoryNewFile
    {
        public void Write(ReadOnlySpan<byte> bytes) => stream.Write(bytes);

        public void FlushToDisk() => stream.Flush(flushToDisk: true);

        public void Dispose() => stream.Dispose();
    }
}

internal static partial class HistoryNative
{
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool MoveFileEx(string existingFileName, string newFileName, uint flags);

    /// <summary>
    /// The exception the file APIs of .NET would throw for the Win32 error: the same types, so the callers' handling and the
    /// HRESULT low word (32 sharing violation, 5 access denied, 39 and 112 disk full) mean what they mean everywhere else.
    /// The message is the system's text for the error and has no path in it.
    /// </summary>
    internal static Exception ToException(int error)
    {
        var message = new Win32Exception(error).Message;
        var hresult = unchecked((int)(0x80070000u | (uint)(error & 0xFFFF)));
        return error switch
        {
            2 => new FileNotFoundException(message) { HResult = hresult },
            3 => new DirectoryNotFoundException(message) { HResult = hresult },
            5 => new UnauthorizedAccessException(message),
            _ => new IOException(message, hresult),
        };
    }
}
