using PcMieru.Update.Bundle;
using PcMieru.Update.Feed;
using PcMieru.Update.Transaction;

namespace PcMieru.Update.Staging;

/// <summary>How one stage run ended: the stage, or the reason there is none.</summary>
internal sealed record StageRun(StagedUpdate? Staged, UpdateRefusal Reason, string Detail)
{
    public static StageRun Done(StagedUpdate staged) => new(staged, UpdateRefusal.None, string.Empty);

    public static StageRun Fail(UpdateRefusal reason, string detail) => new(null, reason, detail);
}

/// <summary>
/// Makes one stage (DESIGN-B 3.3): downloads the zip of a release that a check validated and unpacks it into <c>L\update\&lt;id&gt;\cache\&lt;tx&gt;</c>.
///
/// <code>
/// cache\&lt;tx&gt;\SHA256.txt  release.manifest.json     the two small files exactly as the check read them (their hashes were checked against each other and against the API's digests)
/// cache\&lt;tx&gt;\bundle.zip.part                       the download (counted and hashed on the bytes that arrive), then renamed to bundle.zip
/// cache\&lt;tx&gt;\files\...                             the unpacked bundle, every file made with CreateNew, its length and hash checked while it is written
/// </code>
/// then the zip is removed (the files are what the swap uses). The order of the checks, each of which removes the whole folder when it fails: the byte limit (256 MiB) on the real stream, the SHA-256
/// of the zip against SHA256.txt and the API's digest, the zip itself against the outer manifest (<see cref="BundleZip.Extract"/>: every rule of the bundle checker, then each file written is counted
/// and hashed again), and the Authenticode signature of every program file that is new or changed.
///
/// This class is the only code of the feed that writes files; the transaction (<c>Transaction/</c>) is the only code that changes the install, and does not live here.
/// </summary>
internal sealed class UpdateStager(IFeedTransport transport, ISignatureInspector inspector, Limits limits, IDiskSpace disk)
{
    /// <summary>Room that is asked for beyond the zip and the unpacked files (the state file, the small files, the zip's rename).</summary>
    internal const long SpaceMargin = 32L * 1024 * 1024;

    /// <param name="installed">What is installed now (the record, or the install's own manifest); null when it cannot be read, and then every program file is looked at.</param>
    public async Task<StageRun> RunAsync(ValidatedRelease release, string stateFolder, InstalledRecord? installed, string tx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (!TxPaths.IsValidTx(tx))
        {
            throw new ArgumentException("not a transaction id", nameof(tx));
        }

        var cache = StageLayout.CacheOf(stateFolder);
        var folder = Path.Combine(cache, tx);
        var finished = false;
        try
        {
            var run = await StageAsync(release, stateFolder, cache, folder, installed, ct).ConfigureAwait(false);
            finished = run.Staged is not null;
            return run;
        }
        finally
        {
            if (!finished)
            {
                // Whatever went wrong, and a cancellation too: a half-made stage is never left behind (a folder that cannot be removed now is taken by the next stage).
                StageCache.Remove(folder);
            }
        }
    }

    private async Task<StageRun> StageAsync(ValidatedRelease release, string stateFolder, string cache, string folder, InstalledRecord? installed, CancellationToken ct)
    {
        // The two small files and the numbers must be what the check made them (a ValidatedRelease is built in one place, but a stage does not rest on that).
        if (!string.Equals(Sha256Of(release.HashListBytes), release.HashListSha256, StringComparison.Ordinal)
            || !string.Equals(Sha256Of(release.ManifestBytes), release.ManifestSha256, StringComparison.Ordinal)
            || release.Manifest.Version != release.Version.ToString())
        {
            return StageRun.Fail(UpdateRefusal.ManifestHashMismatch, "the held small files are not what the check validated");
        }

        try
        {
            if (TxPaths.IsLink(stateFolder) || TxPaths.IsLink(cache))
            {
                return StageRun.Fail(UpdateRefusal.StageFailed, "the state folder or its cache folder is a link");
            }

            Directory.CreateDirectory(folder);
            if (TxPaths.IsLink(folder))
            {
                return StageRun.Fail(UpdateRefusal.StageFailed, "the stage folder is a link");
            }

            var need = checked(release.ZipDeclaredBytes + release.Manifest.Files.Sum(f => f.Bytes) + SpaceMargin);
            var free = disk.FreeBytes(folder);
            if (free < need)
            {
                return StageRun.Fail(UpdateRefusal.StageFailed, $"not enough room: {free} bytes are free, {need} are needed");
            }

            WriteNew(Path.Combine(folder, StageLayout.HashListFileName), release.HashListBytes);
            WriteNew(Path.Combine(folder, StageLayout.ManifestFileName), release.ManifestBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StageRun.Fail(UpdateRefusal.StageFailed, "the stage folder could not be made: " + ex.Message);
        }

        // The zip: the only request of the stage. It goes into a .part file, is counted and hashed as it arrives, and is renamed only when it is whole and is the one that was named.
        var part = Path.Combine(folder, StageLayout.ZipPartName);
        var zip = Path.Combine(folder, StageLayout.ZipFileName);
        AssetDownload download;
        try
        {
            await using (var destination = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 80 * 1024, FileOptions.Asynchronous))
            {
                download = await transport.DownloadAssetAsync(release.ZipUrl, release.ZipDeclaredBytes, limits.ZipBytes, destination, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StageRun.Fail(UpdateRefusal.StageFailed, "the download could not be written: " + ex.Message);
        }

        if (!string.Equals(download.Sha256, release.ZipSha256, StringComparison.Ordinal)
            || !ReleaseParser.DigestAgrees(release.ZipApiDigest, download.Sha256))
        {
            return StageRun.Fail(UpdateRefusal.ZipHashMismatch, "the zip is not the one SHA256.txt (or the release's digest) names");
        }

        try
        {
            File.Move(part, zip);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StageRun.Fail(UpdateRefusal.StageFailed, "the download could not be renamed: " + ex.Message);
        }

        // Unpacking and the signatures are file work and CPU: off the caller's thread (the caller may be the window's).
        var unpacked = await Task.Run(() => Unpack(release, folder, zip, installed, ct), ct).ConfigureAwait(false);
        if (unpacked is not null)
        {
            return unpacked;
        }

        return StageRun.Done(new StagedUpdate
        {
            Version = release.Version.ToString(),
            BuildId = release.Manifest.BuildId,
            Tag = release.Tag,
            Folder = folder,
            ManifestSha256 = release.ManifestSha256,
        });
    }

    /// <returns>Null when the files are unpacked and their signatures are acceptable; otherwise why not.</returns>
    private StageRun? Unpack(ValidatedRelease release, string folder, string zip, InstalledRecord? installed, CancellationToken ct)
    {
        var files = Path.Combine(folder, StageLayout.FilesFolderName);
        try
        {
            Directory.CreateDirectory(files);
            var root = Path.GetFullPath(files) + Path.DirectorySeparatorChar;

            using (var source = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var result = BundleZip.Extract(source, release.Version.ToString(), release.Manifest, limits, name =>
                {
                    ct.ThrowIfCancellationRequested();
                    var target = Path.GetFullPath(TxPaths.Under(files, name));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new IOException($"'{name}' would be written outside the stage");
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    return new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 80 * 1024);
                }, BundleFloor.ForThisBuild);
                if (!result.Ok)
                {
                    return StageRun.Fail(UpdateRefusal.BundleInvalid, $"{result.Fault}: {result.Detail}");
                }
            }

            File.Delete(zip); // the unpacked files are what the swap takes; the zip is not kept
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StageRun.Fail(UpdateRefusal.StageFailed, "the files could not be written: " + ex.Message);
        }

        // A link anywhere in what was just written would be a way out of the stage folder: refuse, as the check of an old stage does.
        if (Directory.EnumerateFileSystemEntries(files, "*", SearchOption.AllDirectories).Any(e => new FileInfo(e).Attributes.HasFlag(FileAttributes.ReparsePoint)))
        {
            return StageRun.Fail(UpdateRefusal.StageFailed, "the unpacked folder contains a link");
        }

        return CheckSignatures(release, files, installed, ct);
    }

    /// <summary>
    /// Authenticode of every program file whose hash differs from the installed record (a file that did not change is the one that is installed). Valid and Unsigned pass; one Invalid refuses the whole
    /// candidate. The identity of a signer is not compared yet: see <see cref="SignaturePolicy"/> (DESIGN-B 12).
    /// </summary>
    private StageRun? CheckSignatures(ValidatedRelease release, string files, InstalledRecord? installed, CancellationToken ct)
    {
        foreach (var file in release.Manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            if (!SignaturePolicy.IsProgramFile(file.Path) || (installed is not null && installed.TryGet(file.Path, out var current) && string.Equals(current.Sha256, file.Sha256, StringComparison.Ordinal)))
            {
                continue;
            }

            if (SignaturePolicy.Check(file.Path, inspector.Inspect(TxPaths.Under(files, file.Path))) is { } why)
            {
                return StageRun.Fail(UpdateRefusal.SignatureInvalid, why);
            }
        }

        return null;
    }

    private static void WriteNew(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static string Sha256Of(byte[] bytes) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
}
