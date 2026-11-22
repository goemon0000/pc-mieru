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
        if (!ZipDirectory.TryRead(zip, checked(limits.Files + 3), out var central, out var directoryFault, out var directoryDetail))
        {
            return BundleResult.Refuse(directoryFault, directoryDetail);
        }

        var root = BundleFormat.RootFolder(expectedVersion);
        var classified = Classify(central, root, limits, out var refusal);
        if (classified is null)
        {
            return refusal;
        }

        try
        {
            using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count != central.Count)
            {
                return BundleResult.Refuse(BundleFault.ZipAmbiguous, $"the central directory has {central.Count} entries, the zip library reads {archive.Entries.Count}");
            }

            var entries = new List<BundleEntry>(classified.Files.Count);
            for (var i = 0; i < central.Count; i++)
            {
                var seen = archive.Entries[i];
                var declared = central[i];
                if (seen.FullName != declared.Name || seen.Length != declared.UncompressedSize || seen.CompressedLength != declared.CompressedSize)
                {
                    return BundleResult.Refuse(BundleFault.ZipAmbiguous, $"entry {i} is read differently by the central directory and by the zip library");
                }

                if (classified.Files.TryGetValue(i, out var relative))
                {
                    entries.Add(new BundleEntry(relative, declared.UncompressedSize, () => seen.Open()));
                }
            }

            return BundleChecker.Check(entries, expectedVersion, outer, limits, floor);
        }
        catch (InvalidDataException ex)
        {
            return BundleResult.Refuse(BundleFault.NotZip, "the zip could not be read: " + ex.Message);
        }
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
    /// Why an entry is not a plain file or directory, or null when it is. A Unix mode (the high half of the attributes, when the zip was made on Unix)
    /// that is not a regular file or a directory (a symbolic link is 0120000, a device or a pipe is another type) is refused; so is a Windows reparse point
    /// or device flag; and so is a kind that the name contradicts (a directory mode on a file name, a file mode on a directory name).
    /// </summary>
    private static string? LinkReason(ZipCentralEntry entry, bool isDirectory)
    {
        var host = entry.VersionMadeBy >> 8;
        var attributes = entry.ExternalAttributes;
        if (host == 3)
        {
            var type = (attributes >> 16) & 0xF000;
            if (type == 0xA000)
            {
                return "a symbolic link";
            }

            if (type is not (0 or 0x8000 or 0x4000))
            {
                return "neither a plain file nor a directory";
            }

            if ((type == 0x4000 && !isDirectory) || (type == 0x8000 && isDirectory))
            {
                return "a kind that its name contradicts";
            }

            return null;
        }

        const uint DirectoryFlag = 0x10, Device = 0x40, ReparsePoint = 0x400;
        if ((attributes & ReparsePoint) != 0)
        {
            return "a reparse point";
        }

        if ((attributes & Device) != 0)
        {
            return "a device";
        }

        if ((attributes & DirectoryFlag) != 0 && !isDirectory)
        {
            return "a kind that its name contradicts";
        }

        return null;
    }
}
