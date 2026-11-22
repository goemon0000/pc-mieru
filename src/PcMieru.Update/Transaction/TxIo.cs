using System.Security.Cryptography;
using PcMieru.Update.Feed;

namespace PcMieru.Update.Transaction;

/// <summary>A file that was read for a copy did not have the length or the SHA-256 it was supposed to have (the source changed, or the list is wrong).</summary>
public sealed class TxSourceMismatchException(string path, string detail) : IOException($"'{path}' is not what was expected: {detail}")
{
    public string Path { get; } = path;
}

/// <summary>A file that was just written did not read back with the SHA-256 it was written with.</summary>
public sealed class TxVerifyException(string path, string detail) : IOException($"'{path}' did not read back as written: {detail}")
{
    public string Path { get; } = path;
}

/// <summary>
/// THE ONLY PLACE THE TRANSACTION CHANGES THE FILE SYSTEM. Every write, rename, replace and delete of Transaction/ goes through a method here, and each one says which <see cref="TxStep"/> it is, calls
/// <see cref="IFaultInjector.Reach"/> right before (<see cref="TxEdge.Before"/>), in the middle of a write (<see cref="TxEdge.Mid"/>) and right after (<see cref="TxEdge.After"/>). So a cut point cannot be forgotten:
/// a test reads the source of Transaction/ and fails when a file change is made anywhere but here.
///
/// <b>Durability.</b> A file's data is written and then flushed to the disk (<see cref="FileStream.Flush(bool)"/> with true) BEFORE the rename that makes the name point to it; the rename is
/// <see cref="TxNative.Move"/> (write-through). JSON that must be replaced atomically (the journal, installed.json, last-result.json, blocked.json) is written to "&lt;name&gt;.tmp" in the SAME directory, flushed, and
/// moved over the target: the target is always either the old file or the new one, never half of one.
/// </summary>
internal sealed class TxIo(IFaultInjector fault)
{
    private const int MaxChunk = 1 << 16;

    public IFaultInjector Fault { get; } = fault;

    /// <summary>Calls the injector (a step with no file change of its own: the seams).</summary>
    public void Reach(TxStep step, TxEdge edge, string? item = null) => Fault.Reach(step, edge, item);

    // ---------------------------------------------------------------------------------------------------------------- copy

    /// <summary>
    /// Copies <paramref name="source"/> to the NEW file <paramref name="destination"/> (an existing one is an error, unless <paramref name="overwrite"/> is set: a leftover of a cut copy): reads it with the hash running and checks length and SHA-256 against what is expected
    /// (<see cref="TxSourceMismatchException"/> otherwise), flushes the copy to the disk, reads it back and checks it again (<see cref="TxVerifyException"/> otherwise). The parent folder is made if it is missing.
    /// </summary>
    public void CopyNew(TxStep step, string source, string destination, long expectedBytes, string expectedSha256, bool overwrite = false)
    {
        Reach(step, TxEdge.Before, destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        var chunk = (int)Math.Clamp(expectedBytes / 2, 1, MaxChunk); // at least two writes for a file of two bytes or more, so a half-written copy can be left behind
        var buffer = new byte[chunk];
        using (var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, chunk, FileOptions.SequentialScan))
        using (var to = new FileStream(destination, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None, chunk))
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            long total = 0;
            var midDone = false;
            int read;
            while ((read = from.Read(buffer, 0, buffer.Length)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                to.Write(buffer, 0, read);
                total += read;
                if (!midDone)
                {
                    midDone = true;
                    to.Flush(); // what is written so far is in the file when the cut comes
                    Reach(step, TxEdge.Mid, destination);
                }
            }

            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (total != expectedBytes || !Sha256List.Same(actual, expectedSha256))
            {
                throw new TxSourceMismatchException(source, total != expectedBytes ? $"{total} bytes, expected {expectedBytes}" : "the SHA-256 differs");
            }

            to.Flush(flushToDisk: true);
        }

        VerifyFile(destination, expectedBytes, expectedSha256);
        Reach(step, TxEdge.After, destination);
    }

    private static void VerifyFile(string path, long expectedBytes, string expectedSha256)
    {
        using var back = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, MaxChunk, FileOptions.SequentialScan);
        if (back.Length != expectedBytes)
        {
            throw new TxVerifyException(path, $"{back.Length} bytes, expected {expectedBytes}");
        }

        var actual = Convert.ToHexStringLower(SHA256.HashData(back));
        if (!Sha256List.Same(actual, expectedSha256))
        {
            throw new TxVerifyException(path, "the SHA-256 differs");
        }
    }

    // ---------------------------------------------------------------------------------------------------------------- write

    /// <summary>Writes the NEW file <paramref name="destination"/> (an existing one is an error), flushed to the disk.</summary>
    public void WriteNew(TxStep step, string destination, ReadOnlySpan<byte> bytes)
    {
        Reach(step, TxEdge.Before, destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using (var to = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, MaxChunk))
        {
            WriteHalves(step, to, bytes, destination);
            to.Flush(flushToDisk: true);
        }

        Reach(step, TxEdge.After, destination);
    }

    /// <summary>
    /// Puts <paramref name="bytes"/> at <paramref name="destination"/> atomically: they are written to "&lt;destination&gt;.tmp" in the same folder (a leftover one is overwritten), flushed to the disk, and moved over
    /// the target with a write-through rename. The target is the old file or the new file, whatever happens.
    /// </summary>
    public void WriteReplace(TxStep step, string destination, ReadOnlySpan<byte> bytes)
    {
        Reach(step, TxEdge.Before, destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".tmp";
        using (var to = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, MaxChunk))
        {
            WriteHalves(step, to, bytes, destination);
            to.Flush(flushToDisk: true);
        }

        TxNative.Move(temporary, destination, replace: true);
        Reach(step, TxEdge.After, destination);
    }

    private void WriteHalves(TxStep step, FileStream to, ReadOnlySpan<byte> bytes, string item)
    {
        var half = bytes.Length / 2;
        to.Write(bytes[..half]);
        to.Flush();
        Reach(step, TxEdge.Mid, item);
        to.Write(bytes[half..]);
    }

    // ---------------------------------------------------------------------------------------------------------------- rename, delete

    /// <summary>Renames <paramref name="source"/> to <paramref name="destination"/> (write-through; an existing target is replaced only when <paramref name="replace"/>). The target's folder is made if it is missing.</summary>
    public void Move(TxStep step, string source, string destination, bool replace)
    {
        Reach(step, TxEdge.Before, destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        TxNative.Move(source, destination, replace);
        Reach(step, TxEdge.After, destination);
    }

    /// <summary>Deletes a file (a file that is not there is not an error).</summary>
    public void DeleteFile(TxStep step, string path)
    {
        Reach(step, TxEdge.Before, path);
        File.Delete(path);
        Reach(step, TxEdge.After, path);
    }

    /// <summary>
    /// Deletes a file the transaction put there, and then the folders that this leaves EMPTY, up to (not including) <paramref name="stopAt"/>. A folder with anything in it (a user's file) stays: only
    /// the folders that the added file alone was keeping are removed, and a plain rmdir never removes a non-empty folder.
    /// </summary>
    public void DeleteFileAndEmptyParents(TxStep step, string path, string stopAt)
    {
        Reach(step, TxEdge.Before, path);
        File.Delete(path);
        var stop = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stopAt));
        var parent = Path.GetDirectoryName(Path.GetFullPath(path));
        while (parent is not null && !string.Equals(parent, stop, StringComparison.OrdinalIgnoreCase)
            && parent.StartsWith(stop + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
        {
            Directory.Delete(parent, recursive: false);
            parent = Path.GetDirectoryName(parent);
        }

        Reach(step, TxEdge.After, path);
    }

    /// <summary>
    /// Deletes a folder and everything in it (a folder that is not there is not an error), and then its parent when that is left empty (work/ or prev-&lt;old&gt;/). A link is removed as a link, never followed.
    /// </summary>
    public void DeleteTree(TxStep step, string directory)
    {
        Reach(step, TxEdge.Before, directory);
        if (Directory.Exists(directory))
        {
            var isLink = new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint);
            Directory.Delete(directory, recursive: !isLink);
        }

        var parent = Path.GetDirectoryName(Path.GetFullPath(directory));
        if (parent is not null && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
        {
            Directory.Delete(parent, recursive: false);
        }

        Reach(step, TxEdge.After, directory);
    }
}
