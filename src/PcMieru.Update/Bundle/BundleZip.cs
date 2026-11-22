using System.IO.Compression;
using PcMieru.Update.Feed;

namespace PcMieru.Update.Bundle;

/// <summary>
/// The validator of a release zip (DESIGN-B 3.2). It reads the streams only: nothing is extracted to disk, and no file is held in memory except the
/// inner manifest (at most <see cref="Limits.ManifestBytes"/>). tools/publish.ps1 -Verify runs this same code through tools/BundleCheck, so a zip
/// that publish makes is judged by the rules the client will apply.
///
/// The order of the checks: the version asked for; the zip size; the central directory (read by hand: <see cref="ZipDirectory"/>); every entry's name,
/// place, kind and flags; the zip library's view of the same entries; then <see cref="BundleChecker"/> (floor, inner manifest, file set against the outer
/// manifest, lengths, hashes, limits).
/// </summary>
public static class BundleZip
{
    /// <summary>
    /// Judges <paramref name="zip"/> (seekable) as the release zip of <paramref name="expectedVersion"/> ("X.Y.Z") described by <paramref name="outer"/>.
    /// <paramref name="floor"/> is the list of required files (names compared without regard to case); null means <see cref="BundleFloor.Full"/>, which
    /// is what the client uses. <see cref="BundleFloor.ForThisBuild"/> is what publish asks for until the later steps ship the missing files.
    /// </summary>
    public static BundleResult Validate(Stream zip, string expectedVersion, BundleManifest outer, Limits limits, IReadOnlyCollection<string>? floor = null)
    {
        ArgumentNullException.ThrowIfNull(zip);
        ArgumentNullException.ThrowIfNull(outer);
        ArgumentNullException.ThrowIfNull(limits);
        floor ??= BundleFloor.Full;

        if (!AppVersion.TryParse(expectedVersion, out var parsed) || parsed.ToString() != expectedVersion)
        {
            return BundleResult.Refuse(BundleFault.BadVersion, "the expected version is not a plain X.Y.Z");
        }

        if (!zip.CanSeek || !zip.CanRead)
        {
            return BundleResult.Refuse(BundleFault.NotZip, "the zip stream cannot be read and sought");
        }

        if (zip.Length > limits.ZipBytes)
        {
            return BundleResult.Refuse(BundleFault.ZipTooLarge, $"the zip is {zip.Length} bytes, more than {limits.ZipBytes}");
        }

        // A bundle holds the files, the root folder and at most ja/ and en/ as explicit entries.
        if (!ZipDirectory.TryRead(zip, checked(limits.Files + 3), out var central, out var bodyLength, out var directoryFault, out var directoryDetail))
        {
            return BundleResult.Refuse(directoryFault, directoryDetail);
        }

        var root = BundleFormat.RootFolder(expectedVersion);
        var classified = Classify(central, root, limits, out var refusal);
        if (classified is null)
        {
            return refusal;
        }

        // Each central record must point at a local header of its own name; the data starts after it.
        if (!ZipDirectory.TryReadLocalHeaders(zip, central, bodyLength, out var dataStarts, out var localFault, out var localDetail))
        {
            return BundleResult.Refuse(localFault, localDetail);
        }

        var disagreement = CrossCheck(zip, central);
        if (disagreement is not null)
        {
            return disagreement.Value;
        }

        // The data is read by this validator and not by the zip library: the library stops an entry at the size its header gives, so a deflate stream that
        // goes on past that size would be read as exactly the manifest's bytes and the rest never seen. Read here, the stream is as long as it is, and the
        // check that never takes more than the manifest's length plus one byte (BundleChecker.Hash) sees the excess.
        var entries = new List<BundleEntry>(classified.Files.Count);
        for (var i = 0; i < central.Count; i++)
        {
            if (classified.Files.TryGetValue(i, out var relative))
            {
                var (method, start, compressed) = (central[i].Method, dataStarts[i], central[i].CompressedSize);
                entries.Add(new BundleEntry(relative, central[i].UncompressedSize, () => OpenData(zip, method, start, compressed)));
            }
        }

        return BundleChecker.Check(entries, expectedVersion, outer, limits, floor);
    }

    /// <summary>
    /// The names of the entries of a zip, as the central directory gives them (one char per byte), in the order of the file. Nothing is judged: not the names,
    /// not the kinds, not the floor. It is the same hand reader as <see cref="Validate"/> (so a zip with two end records has no list either), and it is
    /// what CI compares between the zips of two PowerShells.
    /// </summary>
    public static bool TryListNames(Stream zip, Limits limits, out IReadOnlyList<string> names, out BundleFault fault, out string detail)
    {
        ArgumentNullException.ThrowIfNull(zip);
        ArgumentNullException.ThrowIfNull(limits);
        names = [];
        if (!zip.CanSeek || !zip.CanRead)
        {
            fault = BundleFault.NotZip;
            detail = "the zip stream cannot be read and sought";
            return false;
        }

        if (zip.Length > limits.ZipBytes)
        {
            fault = BundleFault.ZipTooLarge;
            detail = $"the zip is {zip.Length} bytes, more than {limits.ZipBytes}";
            return false;
        }

        if (!ZipDirectory.TryRead(zip, checked(limits.Files + 3), out var central, out _, out fault, out detail))
        {
            return false;
        }

        names = central.Select(e => e.Name).ToArray();
        return true;
    }

    /// <summary>
    /// Makes the zip library's reading of the zip agree with the hand reader's: the same entries in the same order with the same names, sizes and attributes.
    /// Returns the refusal, or null when they agree. A zip the library cannot read at all is <see cref="BundleFault.NotZip"/>, and a read that fails on the
    /// way (a disk or a share that goes away) is <see cref="BundleFault.ReadFailed"/>.
    /// </summary>
    private static BundleResult? CrossCheck(Stream zip, IReadOnlyList<ZipCentralEntry> central)
    {
        try
        {
            using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count != central.Count)
            {
                return BundleResult.Refuse(BundleFault.ZipAmbiguous, $"the central directory has {central.Count} entries, the zip library reads {archive.Entries.Count}");
            }

            for (var i = 0; i < central.Count; i++)
            {
                var seen = archive.Entries[i];
                var declared = central[i];
                if (seen.FullName != declared.Name
                    || seen.Length != declared.UncompressedSize
                    || seen.CompressedLength != declared.CompressedSize
                    || (uint)seen.ExternalAttributes != declared.ExternalAttributes)
                {
                    return BundleResult.Refuse(BundleFault.ZipAmbiguous, $"entry {i} is read differently by the central directory and by the zip library");
                }
            }

            return null;
        }
        catch (InvalidDataException ex)
        {
            return BundleResult.Refuse(BundleFault.NotZip, "the zip could not be read: " + ex.Message);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ObjectDisposedException or UnauthorizedAccessException)
        {
            return BundleResult.Refuse(BundleFault.ReadFailed, "the zip could not be read: " + ex.Message);
        }
    }

    /// <summary>The bytes of one entry as they are in the zip: a window of the compressed bytes, inflated unless the entry is stored.</summary>
    private static Stream OpenData(Stream zip, ushort method, long start, uint compressedBytes)
    {
        var window = new WindowStream(zip, start, compressedBytes);
        return method == 0 ? window : new DeflateStream(window, CompressionMode.Decompress);
    }

    /// <summary>A read-only view of <c>length</c> bytes of a seekable stream from <c>start</c>. It seeks before every read, and never disposes the stream it views.</summary>
    private sealed class WindowStream(Stream inner, long start, long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var left = length - _position;
            if (left <= 0 || buffer.IsEmpty)
            {
                return 0;
            }

            inner.Seek(start + _position, SeekOrigin.Begin);
            var read = inner.Read(buffer[..(int)Math.Min(buffer.Length, left)]);
            _position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>What <see cref="Classify"/> found: for each central directory index of a file, its name below the root folder.</summary>
    private sealed record Classified(Dictionary<int, string> Files);

    /// <summary>
    /// The per-entry rules: the name's character rules, the place (inside the root folder), the kind (a plain file, or the root, ja/ or en/ directory),
    /// the flags (no encryption, stored or deflate), links, duplicates, collisions, empty directories and the number of files.
    /// Returns null (and the refusal) at the first entry that breaks a rule.
    /// </summary>
    private static Classified? Classify(IReadOnlyList<ZipCentralEntry> central, string root, Limits limits, out BundleResult refusal)
    {
        var files = new Dictionary<int, string>();
        var fileKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directoryKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // explicit and implied
        var explicitDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sawRoot = false;

        foreach (var entry in central)
        {
            var shown = BundleNames.Show(entry.Name);
            var isDirectory = entry.IsDirectory;
            var bare = isDirectory ? entry.Name[..^1] : entry.Name;

            // The name first, so the reason is the same whatever else the entry is.
            var syntax = BundleNames.CheckSyntax(bare);
            if (!syntax.Ok)
            {
                refusal = BundleResult.Refuse(syntax.Fault, syntax.Detail);
                return null;
            }

            if (entry.IsEncrypted)
            {
                refusal = BundleResult.Refuse(BundleFault.Encrypted, $"'{shown}' is encrypted");
                return null;
            }

            if (entry.Method is not (0 or 8))
            {
                refusal = BundleResult.Refuse(BundleFault.UnsupportedMethod, $"'{shown}' uses compression method {entry.Method} (only stored and deflate are read)");
                return null;
            }

            var linkReason = LinkReason(entry, isDirectory);
            if (linkReason is not null)
            {
                refusal = BundleResult.Refuse(BundleFault.Link, $"'{shown}' is {linkReason}");
                return null;
            }

            // The place: inside the root folder.
            var slash = bare.IndexOf('/');
            string relative;
            if (slash < 0)
            {
                if (!isDirectory)
                {
                    refusal = BundleResult.Refuse(BundleFault.OutsideRoot, $"'{shown}' is outside the folder '{root}/'");
                    return null;
                }

                if (bare != root)
                {
                    refusal = BundleResult.Refuse(BundleFault.WrongRoot, $"'{shown}' is not the folder '{root}/'");
                    return null;
                }

                // The root folder's own entry (optional, once, no data).
                if (sawRoot)
                {
                    refusal = BundleResult.Refuse(BundleFault.DuplicateName, $"'{shown}' is listed twice");
                    return null;
                }

                if (entry.UncompressedSize != 0)
                {
                    refusal = BundleResult.Refuse(BundleFault.DirectoryNotAllowed, $"the directory '{shown}' carries data");
                    return null;
                }

                sawRoot = true;
                continue;
            }

            if (bare[..slash] != root)
            {
                refusal = BundleResult.Refuse(BundleFault.WrongRoot, $"'{shown}' is not inside the folder '{root}/'");
                return null;
            }

            relative = bare[(slash + 1)..];

            if (isDirectory)
            {
                var allowed = BundleNames.CheckDirectory(relative);
                if (!allowed.Ok)
                {
                    refusal = BundleResult.Refuse(allowed.Fault, allowed.Detail);
                    return null;
                }

                if (entry.UncompressedSize != 0)
                {
                    refusal = BundleResult.Refuse(BundleFault.DirectoryNotAllowed, $"the directory '{shown}' carries data");
                    return null;
                }

                if (fileKeys.Contains(relative))
                {
                    refusal = BundleResult.Refuse(BundleFault.FileDirectoryCollision, $"'{relative}' is both a file and a directory");
                    return null;
                }

                if (!explicitDirectories.Add(relative))
                {
                    refusal = BundleResult.Refuse(BundleFault.DuplicateName, $"the directory '{relative}/' is listed twice");
                    return null;
                }

                directoryKeys.Add(relative);
                continue;
            }

            // A file. Duplicates and collisions are judged before the table, so they are not hidden by it.
            if (fileKeys.Contains(relative))
            {
                refusal = BundleResult.Refuse(BundleFault.DuplicateName, $"'{relative}' is listed twice (ignoring case)");
                return null;
            }

            if (directoryKeys.Contains(relative))
            {
                refusal = BundleResult.Refuse(BundleFault.FileDirectoryCollision, $"'{relative}' is both a file and a directory");
                return null;
            }

            var allowedFile = BundleNames.Check(relative);
            if (!allowedFile.Ok)
            {
                refusal = BundleResult.Refuse(allowedFile.Fault, allowedFile.Detail);
                return null;
            }

            var parentEnd = relative.IndexOf('/');
            if (parentEnd > 0)
            {
                var parent = relative[..parentEnd];
                if (fileKeys.Contains(parent))
                {
                    refusal = BundleResult.Refuse(BundleFault.FileDirectoryCollision, $"'{parent}' is both a file and a directory");
                    return null;
                }

                directoryKeys.Add(parent);
            }

            fileKeys.Add(relative);
            files[entry.Index] = relative;
            if (files.Count > limits.Files)
            {
                refusal = BundleResult.Refuse(BundleFault.TooManyFiles, $"more than {limits.Files} files");
                return null;
            }
        }

        foreach (var directory in explicitDirectories)
        {
            if (!fileKeys.Any(f => f.StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase)))
            {
                refusal = BundleResult.Refuse(BundleFault.EmptyDirectory, $"the directory '{directory}/' is empty");
                return null;
            }
        }

        refusal = default;
        return new Classified(files);
    }

    /// <summary>
    /// Why an entry is not a plain file or directory, or null when it is. The same test for every made-by host: the host byte says who wrote the zip, not
    /// which half of the 32-bit attributes is true, and a zip that claims MS-DOS or NTFS can carry a Unix mode while one that claims Unix can carry DOS bits.
    /// The high half is a Unix mode: a file type (S_IFMT) other than none, a regular file or a directory (a symbolic link is 0120000, a device, a pipe and a
    /// socket are other types) is refused. The low half is the DOS attributes: a reparse point (0x400, a junction or a symbolic link) and a device (0x40)
    /// are refused. And so is a kind that the name contradicts: a directory mode or the directory bit on a file name, a regular file mode on a directory name.
    /// </summary>
    private static string? LinkReason(ZipCentralEntry entry, bool isDirectory)
    {
        const uint TypeMask = 0xF000, RegularFile = 0x8000, Directory = 0x4000, SymbolicLink = 0xA000;
        const uint DirectoryBit = 0x10, DeviceBit = 0x40, ReparsePointBit = 0x400;

        var type = (entry.ExternalAttributes >> 16) & TypeMask;
        var dos = entry.ExternalAttributes & 0xFFFF;

        if ((dos & ReparsePointBit) != 0)
        {
            return "a reparse point";
        }

        if ((dos & DeviceBit) != 0)
        {
            return "a device";
        }

        if (type == SymbolicLink)
        {
            return "a symbolic link";
        }

        if (type is not (0 or RegularFile or Directory))
        {
            return "neither a plain file nor a directory";
        }

        if ((type == Directory && !isDirectory) || (type == RegularFile && isDirectory) || ((dos & DirectoryBit) != 0 && !isDirectory))
        {
            return "a kind that its name contradicts";
        }

        return null;
    }
}
