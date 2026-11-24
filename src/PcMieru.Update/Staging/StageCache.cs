using System.Security.Cryptography;
using PcMieru.Update.Bundle;
using PcMieru.Update.Feed;
using PcMieru.Update.Transaction;

namespace PcMieru.Update.Staging;

/// <summary>
/// The folders a stage leaves in <c>L\update\&lt;id&gt;\cache</c> (DESIGN-B 3.3): looking at one, and removing them. Removing goes through the handle-based, reparse-safe walker of the transaction
/// (<c>TxNative.RemoveTree</c>): a junction a user (or something else) put in the cache is removed as itself and never entered, so what it points to is never touched.
/// </summary>
public static class StageCache
{
    /// <summary>
    /// Removes <paramref name="folder"/> and everything in it. Never throws: a folder that is still held by something (a scanner, an open file) stays and is removed by the next stage or the next tidy.
    /// </summary>
    /// <returns>True when nothing of it is left.</returns>
    public static bool Remove(string folder)
    {
        try
        {
            TxNative.RemoveTree(folder, linkOnly: false, hook: null);
            return !Directory.Exists(folder) && !File.Exists(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Removes every stage folder of <paramref name="stateFolder"/> (all of <c>cache\*</c>) except <paramref name="keep"/> (the folder of a stage that is still wanted); a link at <c>cache</c> itself is
    /// unlinked. Never throws. A plain file in <c>cache</c> is left alone.
    /// </summary>
    public static void Clear(string stateFolder, string? keep = null)
    {
        var cache = StageLayout.CacheOf(stateFolder);
        try
        {
            if (TxPaths.IsLink(cache))
            {
                TxNative.RemoveTree(cache, linkOnly: true, hook: null);
                return;
            }

            if (!Directory.Exists(cache))
            {
                return;
            }

            var kept = keep is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(keep));
            foreach (var entry in Directory.EnumerateFileSystemEntries(cache).ToList())
            {
                if (kept is not null && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry)), kept, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Remove(entry);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // What is left is taken by the next stage.
        }
    }

    /// <summary>
    /// Whether the folder of <paramref name="staged"/> is still exactly what a stage made: no link anywhere in it, <c>release.manifest.json</c> is the file whose hash the record names and is a valid
    /// manifest of that version and build, and <c>files\</c> holds exactly the files of that manifest, each with its length and SHA-256 (the whole is read once). Never throws.
    /// </summary>
    public static bool IsIntact(string stateFolder, StagedUpdate staged, CancellationToken ct = default)
    {
        try
        {
            if (!staged.IsWellFormedFor(stateFolder) || TxPaths.IsLink(staged.Folder) || TxPaths.IsLink(StageLayout.CacheOf(stateFolder)))
            {
                return false;
            }

            var manifestPath = Path.Combine(staged.Folder, StageLayout.ManifestFileName);
            var manifestBytes = TxEngine.ReadSmall(manifestPath, BundleManifest.MaxBytes);
            if (manifestBytes is null || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(manifestBytes)), staged.ManifestSha256, StringComparison.Ordinal)
                || !BundleManifest.TryParse(manifestBytes, out var outer, out _)
                || outer.Version != staged.Version || outer.BuildId != staged.BuildId)
            {
                return false;
            }

            return FilesMatch(staged.FilesFolder(), outer, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the inner manifest when the stage belongs to this install, its path contains no reparse points, and both manifests match the release details and one another.
    /// Preparation uses the returned bytes to check each staged file's length and SHA-256.
    /// </summary>
    internal static byte[]? ReadBoundManifest(string stateFolder, StagedUpdate staged)
    {
        try
        {
            if (!staged.IsWellFormedFor(stateFolder))
            {
                return null;
            }

            for (var folder = new DirectoryInfo(staged.FilesFolder()); folder is not null; folder = folder.Parent)
            {
                if (ManagedSet.Look(folder.FullName) != Presence.Other)
                {
                    return null;
                }
            }

            var manifestBytes = TxEngine.ReadSmall(Path.Combine(staged.Folder, StageLayout.ManifestFileName), BundleManifest.MaxBytes);
            if (manifestBytes is null || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(manifestBytes)), staged.ManifestSha256, StringComparison.Ordinal)
                || !BundleManifest.TryParse(manifestBytes, out var outer, out _)
                || outer.Version != staged.Version || outer.BuildId != staged.BuildId)
            {
                return null;
            }

            var listed = outer.Files.FirstOrDefault(f => f.Path == BundleFormat.InnerManifestName);
            if (listed is null)
            {
                return null;
            }

            var inner = TxEngine.ReadSmall(Path.Combine(staged.FilesFolder(), BundleFormat.InnerManifestName), BundleManifest.MaxBytes);
            if (inner is null || inner.Length != listed.Bytes || Convert.ToHexStringLower(SHA256.HashData(inner)) != listed.Sha256
                || !BundleManifest.TryParse(inner, out var manifest, out _) || manifest.Version != outer.Version || manifest.BuildId != outer.BuildId
                || !manifest.Files.OrderBy(f => f.Path, StringComparer.Ordinal)
                    .SequenceEqual(outer.Files.Where(f => f != listed).OrderBy(f => f.Path, StringComparer.Ordinal)))
            {
                return null;
            }

            return inner;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool FilesMatch(string filesFolder, BundleManifest outer, CancellationToken ct)
    {
        if (TxPaths.IsLink(filesFolder) || !Directory.Exists(filesFolder))
        {
            return false;
        }

        var found = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries(filesFolder, "*", SearchOption.AllDirectories))
        {
            if (new FileInfo(entry).Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return false;
            }

            if (File.Exists(entry))
            {
                found++;
            }
        }

        if (found != outer.Files.Count)
        {
            return false;
        }

        foreach (var file in outer.Files)
        {
            ct.ThrowIfCancellationRequested();
            var path = TxPaths.Under(filesFolder, file.Path);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != file.Bytes)
            {
                return false;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(stream)), file.Sha256, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
