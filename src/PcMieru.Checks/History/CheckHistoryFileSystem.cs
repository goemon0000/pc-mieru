namespace PcMieru.Checks;

/// <summary>One regular file directly inside the history folder (sub folders are never listed).</summary>
internal readonly record struct HistoryFileEntry(string Name, DateTime LastWriteUtc);

/// <summary>
/// The reading half of the file system the history needs. The read-only history (the MCP side) is built from this
/// interface alone, so it has no way to write, rename or delete (DESIGN-A §4, §8).
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

    /// <summary>Renames within the same volume. An existing destination is an <see cref="IOException"/>; it is never replaced.</summary>
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

/// <summary>The real file system.</summary>
internal sealed class PhysicalHistoryFileSystem : ICheckHistoryFileSystem
{
    public static PhysicalHistoryFileSystem Instance { get; } = new();

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
        catch (DirectoryNotFoundException)
        {
            // No history folder yet: nothing has been saved.
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

    public void CreateDirectory(string directory) => Directory.CreateDirectory(directory);

    public ICheckHistoryNewFile CreateNew(string path) => new NewFile(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None));

    public void Move(string source, string destination) => File.Move(source, destination, overwrite: false);

    public void Delete(string path) => File.Delete(path);

    private sealed class NewFile(FileStream stream) : ICheckHistoryNewFile
    {
        public void Write(ReadOnlySpan<byte> bytes) => stream.Write(bytes);

        public void FlushToDisk() => stream.Flush(flushToDisk: true);

        public void Dispose() => stream.Dispose();
    }
}
