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
/// A managed file could not be replaced or deleted AND could not even be renamed aside: a running program has it open in a way that forbids it (a sharing violation, or access denied on the rename). Nothing was
/// changed for that file. The transaction answers <see cref="TxOutcome.Busy"/> (journal kept, no sentinel), not <see cref="TxOutcome.RecoveryFailed"/>: closing the program and trying again is all it takes.
/// </summary>
public sealed class TxInUseException(string path, Exception inner) : IOException($"'{path}' is in use by a running program and cannot be moved aside: {inner.Message}", inner)
{
    public string Path { get; } = path;
}

/// <summary>
/// THE ONLY PLACE THE TRANSACTION CHANGES THE FILE SYSTEM. Every write, rename, replace and delete of Transaction/ goes through a method here, and each one says which <see cref="TxStep"/> it is, calls
/// <see cref="IFaultInjector.Reach"/> right before (<see cref="TxEdge.Before"/>), in the middle of a write (<see cref="TxEdge.Mid"/>) and right after (<see cref="TxEdge.After"/>). So a cut point cannot be forgotten:
/// a test reads the source of Transaction/ and fails when a file change is made anywhere but here.
///
/// <b>What is promised about durability, and what is not.</b> A file's data is written and then flushed to the disk (<see cref="FileStream.Flush(bool)"/> with true) BEFORE the rename that makes the name point to it. The
/// rename is <see cref="TxNative.Move"/> (MoveFileEx with the write-through flag), but for a rename inside one volume that flag is NOT a promise that the new name is on the disk when the call returns. What the
/// transaction stands on is the ORDER (data first, then the name; NTFS and ReFS log their metadata in order, which is why a swap is refused on any other file system) and the journal's readers, which decide by the
/// real hashes of the files and never assume that the last rename survived. JSON that must be replaced in one step (the journal, installed.json, last-result.json, blocked.json) is written to "&lt;name&gt;.tmp" in the
/// SAME directory, flushed, and moved over the target: the target is always either the old file or the new one, never half of one.
/// </summary>
internal sealed class TxIo(IFaultInjector fault, bool durable = true)
{
    private const int MaxChunk = 1 << 16;

    public IFaultInjector Fault { get; } = fault;

    /// <summary>True in the product (every flush goes to the disk and every rename is written through). A test may turn it off (<see cref="TxSeams.Durable"/>): a simulated power cut or a killed process does not depend on it, and on a slow disk it is most of the time of a test.</summary>
    public bool Durable { get; } = durable;

    /// <summary>Null in the product. A test sets it to act between the steps of the tree walk of <see cref="DeleteTree"/> (see <see cref="TxNative.RemoveTree"/>): "open" before a node is opened, "opened" after it was looked at.</summary>
    internal Action<string, string>? TreeHook { get; init; }

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

            to.Flush(flushToDisk: Durable);
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
            to.Flush(flushToDisk: Durable);
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
            to.Flush(flushToDisk: Durable);
        }

        TxNative.Move(temporary, destination, replace: true, Durable);
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
        TxNative.Move(source, destination, replace, Durable);
        Reach(step, TxEdge.After, destination);
    }

    /// <summary>
    /// Puts <paramref name="source"/> at <paramref name="target"/> over a file that is there. The replace is tried first (one atomic rename: the target is the old file or the new one). When it fails the way a file in
    /// use fails (access denied, a sharing violation) the file at <paramref name="target"/> is renamed ASIDE to "<paramref name="asideBase"/>.&lt;random&gt;" (a running program's mapped image cannot be overwritten or
    /// deleted, but its name can be changed) and the source is put at the free name. If even the rename fails the same way, <see cref="TxInUseException"/> is thrown and nothing was changed. The name is free for a
    /// moment in between; every reader of the journal decides from the real files and treats a missing one as "restore it from the backup".
    /// </summary>
    public void MoveOver(TxStep place, TxStep aside, string source, string target, string asideBase)
    {
        Reach(place, TxEdge.Before, target);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try
        {
            TxNative.Move(source, target, replace: true, Durable);
        }
        catch (Exception ex) when (TxNative.IsLockedFailure(ex) && ManagedSet.Look(target) == Presence.File)
        {
            SetAside(aside, target, asideBase);
            TxNative.Move(source, target, replace: false, Durable);
        }

        Reach(place, TxEdge.After, target);
    }

    private void SetAside(TxStep step, string target, string asideBase)
    {
        Reach(step, TxEdge.Before, target);
        var destination = asideBase + "." + Guid.NewGuid().ToString("N")[..8];
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try
        {
            TxNative.Move(target, destination, replace: false, Durable);
        }
        catch (Exception ex) when (TxNative.IsLockedFailure(ex))
        {
            throw new TxInUseException(target, ex);
        }

        Reach(step, TxEdge.After, target);
    }

    /// <summary>Deletes a file (a file that is not there is not an error).</summary>
    public void DeleteFile(TxStep step, string path)
    {
        Reach(step, TxEdge.Before, path);
        File.Delete(path);
        Reach(step, TxEdge.After, path);
    }

    /// <summary>
    /// Deletes a file the transaction put there, and then the folders that this leaves EMPTY, up to (not including) <paramref name="stopAt"/>, but only those <paramref name="mayRemove"/> names (full paths): the folders the
    /// journal records this transaction CREATED. A folder that was there before the swap (even an empty one a person made) stays, and so does a folder with anything in it; a plain rmdir never removes a non-empty folder.
    /// A file that cannot be deleted because a program has it mapped (a running image) is renamed ASIDE to "<paramref name="asideBase"/>.&lt;random&gt;" (<paramref name="aside"/>) instead: its name is then free, which is
    /// all the rollback needs. If even the rename fails the same way, <see cref="TxInUseException"/> is thrown and nothing was changed.
    /// </summary>
    public void DeleteFileAndEmptyParents(TxStep step, string path, string stopAt, IReadOnlyCollection<string> mayRemove, TxStep aside, string asideBase)
    {
        Reach(step, TxEdge.Before, path);
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (TxNative.IsLockedFailure(ex) && ManagedSet.Look(path) == Presence.File)
        {
            // A mapped image cannot be deleted; its name can be changed. The name is then free, which is all the rollback needs.
            SetAside(aside, path, asideBase);
        }

        var stop = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stopAt));
        var parent = Path.GetDirectoryName(Path.GetFullPath(path));
        while (parent is not null && !string.Equals(parent, stop, StringComparison.OrdinalIgnoreCase)
            && parent.StartsWith(stop + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && mayRemove.Contains(parent, StringComparer.OrdinalIgnoreCase)
            && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
        {
            Directory.Delete(parent, recursive: false);
            parent = Path.GetDirectoryName(parent);
        }

        Reach(step, TxEdge.After, path);
    }

    /// <summary>
    /// Deletes a folder and everything in it (a folder that is not there is not an error), and then its parent when that is work/ or prev-&lt;old&gt;/ and is left empty. The tree is walked ON HANDLES (a folder is opened
    /// once, without sharing delete, and its children are opened relative to that handle, so a name that is swapped for a link after it was looked at is never followed) and no link is ever entered: a reparse point
    /// (a junction or a symbolic link, to a folder or to a file) is removed as itself, so what it points to is never touched. When the folder's own parent (work/ or
    /// prev-&lt;old&gt;/) has become a link, that link is what is unlinked and nothing is deleted through it. A <paramref name="directory"/> that is a plain file is left alone.
    /// </summary>
    public void DeleteTree(TxStep step, string directory)
    {
        Reach(step, TxEdge.Before, directory);
        var full = Path.GetFullPath(directory);
        var parent = Path.GetDirectoryName(full);
        var parentIsContainer = parent is not null && TxPaths.IsContainerName(Path.GetFileName(parent));
        if (parentIsContainer && ManagedSet.Look(parent!) == Presence.Link)
        {
            RemoveLink(parent!);
        }
        else
        {
            RemoveTree(full);
            if (parentIsContainer && Directory.Exists(parent!) && ManagedSet.Look(parent!) == Presence.Other && !Directory.EnumerateFileSystemEntries(parent!).Any())
            {
                Directory.Delete(parent!, recursive: false);
            }
        }

        Reach(step, TxEdge.After, directory);
    }

    private void RemoveTree(string path) => TxNative.RemoveTree(path, linkOnly: false, TreeHook);

    /// <summary>Removes the link itself (a directory link with a non-recursive rmdir, a file link with a delete) and nothing it points to. A path that is not a link when it is opened is left alone.</summary>
    private void RemoveLink(string path) => TxNative.RemoveTree(path, linkOnly: true, TreeHook);
}
