using System.Runtime.InteropServices;

namespace PcMieru.Checks;

/// <summary>A file the recorder writes: bytes in, then forced to the disk. Disposing closes it.</summary>
internal interface IRunFile : IDisposable
{
    void Write(ReadOnlySpan<byte> bytes);

    /// <summary>Forces the bytes to the disk (<see cref="FileStream.Flush(bool)"/> with true).</summary>
    void FlushToDisk();
}

/// <summary>
/// The file system of the run recorder and store. A test puts a fault in anywhere (a full disk, a sharing violation, a failed rename) through this
/// seam instead of locking real files. Every method may throw the I/O exceptions of .NET.
/// </summary>
internal interface IRunFileSystem
{
    void CreateDirectory(string directory);

    /// <summary>Creates the file exclusively (<see cref="FileMode.CreateNew"/>) for writing; others may read it while it is written.</summary>
    IRunFile CreateNew(string path);

    /// <summary>Creates the file or empties an existing one (the <c>.json.tmp</c> of an atomic save), for writing, not shared.</summary>
    IRunFile Create(string path);

    /// <summary>Renames <paramref name="temp"/> over <paramref name="destination"/> (replacing it) and does not return before the rename is on the disk.</summary>
    void ReplaceAtomic(string temp, string destination);

    /// <summary>
    /// Takes the run's lock: the lock file, opened exclusively and deleted when the handle closes (so a process that dies leaves none). Null when
    /// somebody else holds it. Disposing releases it.
    /// </summary>
    IDisposable? TryLock(string lockPath);

    /// <summary>True when the lock file exists and is held by somebody (a read-only look: nothing is created).</summary>
    bool IsLockHeld(string lockPath);

    bool Exists(string path);

    /// <summary>The names (not paths) of the files directly in the folder that match the pattern. A folder that does not exist is an empty list.</summary>
    IReadOnlyList<string> ListFiles(string directory, string pattern);

    /// <summary>The whole file, shared with a writer (read while a run is being recorded). <see cref="InvalidDataException"/> when longer than <paramref name="maxBytes"/>.</summary>
    byte[] ReadAllBytes(string path, int maxBytes);

    /// <summary>Deletes the file; a file that is not there is not an error.</summary>
    void Delete(string path);

    /// <summary>The free space of the volume the folder is on (the nearest existing parent when the folder is not there yet), or null when unknown.</summary>
    long? GetFreeBytes(string directory);
}

internal sealed class PhysicalRunFileSystem : IRunFileSystem
{
    // MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH.
    private const uint ReplaceWriteThrough = 0x9;

    public static PhysicalRunFileSystem Instance { get; } = new();

    public void CreateDirectory(string directory) => Directory.CreateDirectory(directory);

    public IRunFile CreateNew(string path) =>
        new RunFile(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.None));

    public IRunFile Create(string path) =>
        new RunFile(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.None));

    public void ReplaceAtomic(string temp, string destination)
    {
        if (!HistoryNative.MoveFileEx(Path.GetFullPath(temp), Path.GetFullPath(destination), ReplaceWriteThrough))
        {
            throw HistoryNative.ToException(Marshal.GetLastPInvokeError());
        }
    }

    public IDisposable? TryLock(string lockPath)
    {
        try
        {
            return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException ex) when (IsSharing(ex))
        {
            return null;
        }
        catch (UnauthorizedAccessException) when (File.Exists(lockPath))
        {
            // A file somebody holds with delete pending answers "access denied" rather than "in use": still not ours to take.
            return null;
        }
    }

    public bool IsLockHeld(string lockPath)
    {
        try
        {
            using var _ = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.None, 1, FileOptions.None);
            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException ex) when (IsSharing(ex))
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return File.Exists(lockPath);
        }
    }

    public bool Exists(string path) => File.Exists(path);

    public IReadOnlyList<string> ListFiles(string directory, string pattern)
    {
        var list = new List<string>();
        try
        {
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles(pattern, SearchOption.TopDirectoryOnly))
            {
                list.Add(file.Name);
            }
        }
        catch (DirectoryNotFoundException) when (!File.Exists(directory))
        {
            // No folder yet: nothing has been recorded.
        }

        return list;
    }

    public byte[] ReadAllBytes(string path, int maxBytes)
    {
        // Read and Write shared (a run being recorded is still open for writing) and Delete shared (a delete in another place may race the read).
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        if (stream.Length > maxBytes)
        {
            throw new InvalidDataException($"The file is longer than {maxBytes} bytes.");
        }

        var buffer = new byte[stream.Length];
        stream.ReadExactly(buffer);
        return buffer;
    }

    public void Delete(string path) => File.Delete(path);

    public long? GetFreeBytes(string directory)
    {
        try
        {
            // The directory itself, not its drive letter: a UNC share and a folder that is a mounted volume have no letter of their own, and the
            // free space of a quota-limited folder is what the caller may use (GetDiskFreeSpaceEx answers for the volume the directory is on).
            string? dir = Path.GetFullPath(directory);
            while (dir is not null && !Directory.Exists(dir))
            {
                dir = Path.GetDirectoryName(dir);
            }

            if (dir is null)
            {
                return null;
            }

            // A trailing backslash: GetDiskFreeSpaceEx wants one on a UNC name, and takes it on every other directory.
            var name = dir.EndsWith('\\') ? dir : dir + "\\";
            return PcMieru.Core.Hardware.Native.Kernel32.GetDiskFreeSpaceExW(name, out var freeToCaller, out _, out _) ? (long)freeToCaller : null;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Sharing violation (32) or lock violation (33): the file is open somewhere else.</summary>
    internal static bool IsSharing(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33;

    private sealed class RunFile(FileStream stream) : IRunFile
    {
        public void Write(ReadOnlySpan<byte> bytes) => stream.Write(bytes);

        public void FlushToDisk() => stream.Flush(flushToDisk: true);

        public void Dispose() => stream.Dispose();
    }
}
