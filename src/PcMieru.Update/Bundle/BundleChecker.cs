using System.Security.Cryptography;
using PcMieru.Wire.Gate;

namespace PcMieru.Update.Bundle;

/// <summary>
/// One file of a bundle as a source (a zip or a folder) offers it: the name below the root folder, the length the source declares, and a way to
/// read its bytes. The checker never trusts the declared length; it counts what it reads.
/// </summary>
internal sealed record BundleEntry(string Name, long DeclaredBytes, Func<Stream> Open);

/// <summary>
/// The checks that a zip and a folder have in common (DESIGN-B 3.2 items 3 and 4): the required floor, the inner PCMieru.bundle.json, the file set,
/// the outer manifest, the lengths, the hashes and the limits. The sources (<see cref="BundleZip"/>, <see cref="BundleFolder"/>) have already judged
/// every name, so what arrives here is a list of unique, well-formed relative names.
///
/// With an outer manifest (the zip case) the authority is the outer one; the inner one must say the same (minus itself). Without one (the folder case,
/// as tools/publish.ps1 -SkipZip runs it) the inner manifest is the authority.
/// </summary>
internal static class BundleChecker
{
    private const int ReadChunk = 80 * 1024;

    public static BundleResult Check(
        IReadOnlyList<BundleEntry> files,
        string expectedVersion,
        BundleManifest? outer,
        Limits limits,
        IReadOnlyCollection<string> floor)
    {
        if (files.Count > limits.Files)
        {
            return BundleResult.Refuse(BundleFault.TooManyFiles, $"{files.Count} files, more than {limits.Files}");
        }

        // The required floor (names are compared without regard to case). The inner manifest is judged below with a fault of its own.
        var names = new HashSet<string>(files.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var required in floor)
        {
            if (required.Equals(BundleFormat.InnerManifestName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!names.Contains(required))
            {
                return BundleResult.Refuse(BundleFault.FloorMissing, $"'{required}' is required and is not in the bundle");
            }
        }

        var inner = files.FirstOrDefault(f => f.Name == BundleFormat.InnerManifestName);
        if (inner is null)
        {
            return BundleResult.Refuse(BundleFault.InnerManifestMissing, $"'{BundleFormat.InnerManifestName}' is not in the bundle");
        }

        // The outer manifest: the version, the file set (exact spelling) and the declared lengths.
        if (outer is not null)
        {
            if (outer.Version != expectedVersion)
            {
                return BundleResult.Refuse(BundleFault.VersionMismatch, $"the outer manifest is for {outer.Version}, not {expectedVersion}");
            }

            var different = FirstSetDifference(files.Select(f => f.Name), outer.Files.Select(f => f.Path), "the bundle", "the outer manifest");
            if (different is not null)
            {
                return BundleResult.Refuse(BundleFault.FileSetMismatch, different);
            }

            var outerByPath = outer.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
            foreach (var file in files.OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                if (file.DeclaredBytes != outerByPath[file.Name].Bytes)
                {
                    return BundleResult.Refuse(BundleFault.LengthMismatch, $"'{file.Name}': the bundle says {file.DeclaredBytes} bytes, the outer manifest says {outerByPath[file.Name].Bytes}");
                }
            }
        }

        // The declared total (what is really read is counted again below).
        long declaredTotal = 0;
        foreach (var file in files)
        {
            declaredTotal += file.DeclaredBytes;
            if (declaredTotal > limits.TotalBytes)
            {
                return BundleResult.Refuse(BundleFault.TotalTooLarge, $"the files add up to more than {limits.TotalBytes} bytes");
            }
        }

        // The inner manifest: read (bounded), hashed, compared with the outer's line for it, then parsed.
        if (inner.DeclaredBytes > limits.ManifestBytes)
        {
            return BundleResult.Refuse(BundleFault.ManifestTooLarge, $"'{inner.Name}' is {inner.DeclaredBytes} bytes, more than {limits.ManifestBytes}");
        }

        byte[] innerBytes;
        try
        {
            using var stream = inner.Open();
            innerBytes = BundleManifest.ReadAtMost(stream, limits.ManifestBytes, out var tooLong);
            if (tooLong)
            {
                return BundleResult.Refuse(BundleFault.ManifestTooLarge, $"'{inner.Name}' is longer than {limits.ManifestBytes} bytes");
            }
        }
        catch (Exception ex) when (IsReadError(ex))
        {
            return BundleResult.Refuse(BundleFault.ReadFailed, $"'{inner.Name}' could not be read: {ex.Message}");
        }

        if (innerBytes.Length != inner.DeclaredBytes)
        {
            return BundleResult.Refuse(BundleFault.LengthMismatch, $"'{inner.Name}': {inner.DeclaredBytes} bytes declared, {innerBytes.Length} read");
        }

        var innerSha = Convert.ToHexStringLower(SHA256.HashData(innerBytes));
        if (outer is not null)
        {
            var outerInner = outer.Files.First(f => f.Path == BundleFormat.InnerManifestName);
            if (innerSha != outerInner.Sha256)
            {
                return BundleResult.Refuse(BundleFault.HashMismatch, $"'{inner.Name}': the SHA-256 is not the outer manifest's");
            }
        }

        if (!BundleManifest.TryParse(innerBytes, out var innerManifest, out var verdict))
        {
            return BundleResult.Refuse(verdict.Fault, $"'{inner.Name}': {verdict.Detail}");
        }

        if (innerManifest.Files.Any(f => f.Path.Equals(BundleFormat.InnerManifestName, StringComparison.OrdinalIgnoreCase)))
        {
            return BundleResult.Refuse(BundleFault.InnerFilesMismatch, $"'{inner.Name}' lists itself");
        }

        if (outer is not null)
        {
            // Format, rid and gate protocol are the same by construction (the reader refuses any other value); the version and the build id can differ.
            if (innerManifest.Version != outer.Version)
            {
                return BundleResult.Refuse(BundleFault.OuterInnerMismatch, $"the version is {innerManifest.Version} in '{inner.Name}' and {outer.Version} in the outer manifest");
            }

            if (innerManifest.BuildId != outer.BuildId)
            {
                return BundleResult.Refuse(BundleFault.OuterInnerMismatch, "the build id of the inner manifest is not the outer manifest's");
            }

            var outerWithoutInner = outer.Files.Where(f => f.Path != BundleFormat.InnerManifestName).ToArray();
            var different = FirstSetDifference(innerManifest.Files.Select(f => f.Path), outerWithoutInner.Select(f => f.Path), "the inner manifest", "the outer manifest");
            if (different is not null)
            {
                return BundleResult.Refuse(BundleFault.InnerFilesMismatch, different);
            }

            var outerByPath = outerWithoutInner.ToDictionary(f => f.Path, StringComparer.Ordinal);
            foreach (var file in innerManifest.Files)
            {
                var other = outerByPath[file.Path];
                if (file.Bytes != other.Bytes || file.Sha256 != other.Sha256)
                {
                    return BundleResult.Refuse(BundleFault.InnerFilesMismatch, $"'{file.Path}' has another length or hash in the inner manifest than in the outer one");
                }
            }
        }
        else
        {
            var different = FirstSetDifference(
                files.Where(f => f.Name != BundleFormat.InnerManifestName).Select(f => f.Name),
                innerManifest.Files.Select(f => f.Path),
                "the folder",
                "the inner manifest");
            if (different is not null)
            {
                return BundleResult.Refuse(BundleFault.FileSetMismatch, different);
            }
        }

        if (innerManifest.Version != expectedVersion)
        {
            return BundleResult.Refuse(BundleFault.VersionMismatch, $"'{inner.Name}' is for {innerManifest.Version}, not {expectedVersion}");
        }

        // Every other file: the length it declares, the length really read and the manifest's length are one number, and the hash is the manifest's.
        var reference = innerManifest.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        long total = innerBytes.Length;
        foreach (var file in files.Where(f => f.Name != BundleFormat.InnerManifestName).OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            var expected = reference[file.Name];
            if (file.DeclaredBytes != expected.Bytes)
            {
                return BundleResult.Refuse(BundleFault.LengthMismatch, $"'{file.Name}': the bundle says {file.DeclaredBytes} bytes, the manifest says {expected.Bytes}");
            }

            var remaining = limits.TotalBytes - total;
            var cap = Math.Min(expected.Bytes, remaining);
            Digest digest;
            try
            {
                using var stream = file.Open();
                digest = Hash(stream, cap);
            }
            catch (Exception ex) when (IsReadError(ex))
            {
                return BundleResult.Refuse(BundleFault.ReadFailed, $"'{file.Name}' could not be read: {ex.Message}");
            }

            if (digest.Exceeded)
            {
                return cap < expected.Bytes
                    ? BundleResult.Refuse(BundleFault.TotalTooLarge, $"more than {limits.TotalBytes} bytes were read")
                    : BundleResult.Refuse(BundleFault.LengthMismatch, $"'{file.Name}': more than the {expected.Bytes} bytes of the manifest were read");
            }

            if (digest.Bytes != expected.Bytes)
            {
                return BundleResult.Refuse(BundleFault.LengthMismatch, $"'{file.Name}': {digest.Bytes} bytes were read, the manifest says {expected.Bytes}");
            }

            if (digest.Sha256 != expected.Sha256)
            {
                return BundleResult.Refuse(BundleFault.HashMismatch, $"'{file.Name}': the SHA-256 is not the manifest's");
            }

            // The gate file is the one file whose bytes the client fixes (DESIGN-B 4): a bundle whose manifests agree with a gate file of other bytes is refused all the same.
            if (file.Name.Equals(GateFile.FileName, StringComparison.OrdinalIgnoreCase) && digest.Sha256 != GateFile.Sha256Hex)
            {
                return BundleResult.Refuse(BundleFault.GateFileChanged, $"'{file.Name}': the SHA-256 is not the one fixed in this program");
            }

            total += digest.Bytes;
        }

        return BundleResult.Pass(files.Count, total);
    }

    /// <summary>What <see cref="Hash"/> saw.</summary>
    internal readonly record struct Digest(long Bytes, string Sha256, bool Exceeded);

    /// <summary>
    /// Reads a stream to its end and returns the length and the SHA-256 (lower-case hex). It never reads more than <paramref name="cap"/> bytes plus one:
    /// as soon as there are more than <paramref name="cap"/>, it stops and says so (<see cref="Digest.Exceeded"/>), so a stream that lies about its
    /// length cannot make the check read without end. Nothing is buffered beyond one chunk.
    /// </summary>
    internal static Digest Hash(Stream stream, long cap)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[ReadChunk];
        long total = 0;
        while (true)
        {
            var want = (int)Math.Min(buffer.Length, cap - total + 1);
            var read = stream.Read(buffer, 0, want);
            if (read <= 0)
            {
                break;
            }

            total += read;
            if (total > cap)
            {
                return new Digest(total, string.Empty, Exceeded: true);
            }

            hash.AppendData(buffer, 0, read);
        }

        return new Digest(total, Convert.ToHexStringLower(hash.GetHashAndReset()), Exceeded: false);
    }

    /// <summary>The first difference between two sets of names (exact spelling), described, or null when they are equal.</summary>
    internal static string? FirstSetDifference(IEnumerable<string> left, IEnumerable<string> right, string leftName, string rightName)
    {
        var a = new HashSet<string>(left, StringComparer.Ordinal);
        var b = new HashSet<string>(right, StringComparer.Ordinal);
        var onlyLeft = a.Where(n => !b.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        var onlyRight = b.Where(n => !a.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        if (onlyLeft.Length == 0 && onlyRight.Length == 0)
        {
            return null;
        }

        // The same name spelled with other letters on the two sides is the one thing worth saying apart.
        foreach (var name in onlyLeft)
        {
            var spelled = onlyRight.FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (spelled is not null)
            {
                return $"'{BundleNames.Show(name)}' in {leftName} is spelled '{BundleNames.Show(spelled)}' in {rightName}";
            }
        }

        return onlyLeft.Length > 0
            ? $"'{BundleNames.Show(onlyLeft[0])}' is in {leftName} but not in {rightName} ({onlyLeft.Length} such, {onlyRight.Length} the other way)"
            : $"'{BundleNames.Show(onlyRight[0])}' is in {rightName} but not in {leftName} ({onlyRight.Length} such, {onlyLeft.Length} the other way)";
    }

    private static bool IsReadError(Exception ex) =>
        ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException or ObjectDisposedException;
}
