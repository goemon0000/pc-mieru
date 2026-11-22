using PcMieru.Update.Feed;

namespace PcMieru.Update.Bundle;

/// <summary>
/// The validator of a published folder (the same rules as the zip, applied to real files). tools/publish.ps1 runs it on the folder before the zip is made
/// (and in <c>-SkipZip</c> mode, where there is no zip): the folder is judged against its own inner PCMieru.bundle.json, and against an outer manifest when
/// one is given. The folder is only read. A link (a reparse point) is refused and never followed, whether it is the folder itself or anything inside it.
/// </summary>
public static class BundleFolder
{
    /// <summary>
    /// Judges <paramref name="folder"/> as the folder of <paramref name="expectedVersion"/> ("X.Y.Z"). <paramref name="outer"/> may be null.
    /// <paramref name="floor"/> null means <see cref="BundleFloor.Full"/>.
    /// </summary>
    public static BundleResult Validate(string folder, string expectedVersion, BundleManifest? outer, Limits limits, IReadOnlyCollection<string>? floor = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        ArgumentNullException.ThrowIfNull(limits);
        floor ??= BundleFloor.Full;

        if (!AppVersion.TryParse(expectedVersion, out var parsed) || parsed.ToString() != expectedVersion)
        {
            return BundleResult.Refuse(BundleFault.BadVersion, "the expected version is not a plain X.Y.Z");
        }

        var top = new DirectoryInfo(folder);
        if (!top.Exists)
        {
            return BundleResult.Refuse(BundleFault.ReadFailed, $"'{folder}' is not a folder");
        }

        // The folder itself may be the link (a junction to a good-looking bundle elsewhere): the children of a junction are plain files, so only this says it.
        if (top.Attributes.HasFlag(FileAttributes.ReparsePoint) || top.LinkTarget is not null)
        {
            return BundleResult.Refuse(BundleFault.Link, $"the folder '{folder}' is itself a link");
        }

        var entries = new List<BundleEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var info in top.EnumerateFileSystemInfos().OrderBy(i => i.Name, StringComparer.Ordinal))
            {
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null)
                {
                    return BundleResult.Refuse(BundleFault.Link, $"'{BundleNames.Show(info.Name)}' is a link");
                }

                if (info is DirectoryInfo directory)
                {
                    var verdict = BundleNames.CheckDirectory(directory.Name);
                    if (!verdict.Ok)
                    {
                        return BundleResult.Refuse(verdict.Fault, verdict.Detail);
                    }

                    var before = entries.Count;
                    foreach (var child in directory.EnumerateFileSystemInfos().OrderBy(i => i.Name, StringComparer.Ordinal))
                    {
                        if (child.Attributes.HasFlag(FileAttributes.ReparsePoint) || child.LinkTarget is not null)
                        {
                            return BundleResult.Refuse(BundleFault.Link, $"'{directory.Name}/{BundleNames.Show(child.Name)}' is a link");
                        }

                        if (child is DirectoryInfo)
                        {
                            return BundleResult.Refuse(BundleFault.DirectoryNotAllowed, $"'{directory.Name}/{BundleNames.Show(child.Name)}/' is a directory below ja/ or en/");
                        }

                        var refusal = Add(entries, seen, directory.Name + "/" + child.Name, (FileInfo)child, limits);
                        if (refusal is not null)
                        {
                            return refusal.Value;
                        }
                    }

                    if (entries.Count == before)
                    {
                        return BundleResult.Refuse(BundleFault.EmptyDirectory, $"the directory '{directory.Name}/' is empty");
                    }

                    continue;
                }

                var added = Add(entries, seen, info.Name, (FileInfo)info, limits);
                if (added is not null)
                {
                    return added.Value;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return BundleResult.Refuse(BundleFault.ReadFailed, $"the folder could not be read: {ex.Message}");
        }

        return BundleChecker.Check(entries, expectedVersion, outer, limits, floor);
    }

    /// <summary>Judges one file's relative name and adds it; returns a refusal, or null.</summary>
    private static BundleResult? Add(List<BundleEntry> entries, HashSet<string> seen, string relative, FileInfo file, Limits limits)
    {
        var verdict = BundleNames.Check(relative);
        if (!verdict.Ok)
        {
            return BundleResult.Refuse(verdict.Fault, verdict.Detail);
        }

        if (!seen.Add(relative))
        {
            return BundleResult.Refuse(BundleFault.DuplicateName, $"'{relative}' appears twice (ignoring case)");
        }

        if (entries.Count >= limits.Files)
        {
            return BundleResult.Refuse(BundleFault.TooManyFiles, $"more than {limits.Files} files");
        }

        var path = file.FullName;
        entries.Add(new BundleEntry(relative, file.Length, () => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan)));
        return null;
    }
}
