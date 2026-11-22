namespace PcMieru.Update.Feed;

/// <summary>
/// An update that is on this PC, unpacked and checked, waiting for the GUI to hand it to the runner (DESIGN-B 3.3): its version, its build id, its tag, the folder
/// <c>L\update\&lt;id&gt;\cache\&lt;tx&gt;</c> it was unpacked into and the SHA-256 of the outer manifest that is in that folder. It is written to state.json by the stage and read back by the
/// GUI. <b>The runner never trusts this record</b> (nor anything else the GUI says): it re-reads the folder from disk and binds it to the hash it was sent (DESIGN-B 3.3, SwapCli --apply).
/// </summary>
public sealed record StagedUpdate
{
    /// <summary>"1.2.3".</summary>
    public required string Version { get; init; }

    /// <summary>A lower-case GUID: the build id of the bundle.</summary>
    public required string BuildId { get; init; }

    /// <summary>"v1.2.3".</summary>
    public required string Tag { get; init; }

    /// <summary>The full path of the stage folder (<c>cache\&lt;tx&gt;</c>).</summary>
    public required string Folder { get; init; }

    /// <summary>The SHA-256 of <c>release.manifest.json</c> in <see cref="Folder"/> (64 lower-case hex digits).</summary>
    public required string ManifestSha256 { get; init; }

    /// <summary>The folder the swap transaction takes its files from (<c>PCMieru.bundle.json</c> is in it): <c>&lt;stage&gt;\files</c>.</summary>
    public string FilesFolder() => Path.Combine(Folder, StageLayout.FilesFolderName);

    /// <summary>
    /// Whether this record is well formed AND belongs to <paramref name="stateFolder"/>: the version is a plain X.Y.Z, the tag is "v" + it, the build id is a lower-case GUID, the hash is 64 lower-case
    /// hex digits and the folder is exactly <c>stateFolder\cache\&lt;12 hex&gt;</c>. A state file is a plain file on a disk the user can edit; what it names is checked before it is shown or used.
    /// </summary>
    internal bool IsWellFormedFor(string stateFolder)
    {
        if (!AppVersion.TryParse(Version, out var version) || version.ToString() != Version || Tag != "v" + Version)
        {
            return false;
        }

        if (!Guid.TryParseExact(BuildId, "D", out var guid) || guid.ToString("D") != BuildId || !Sha256List.IsHex64(ManifestSha256) || ManifestSha256 != ManifestSha256.ToLowerInvariant())
        {
            return false;
        }

        try
        {
            var expectedParent = Path.TrimEndingDirectorySeparator(Path.Combine(Path.GetFullPath(stateFolder), StageLayout.CacheFolderName));
            var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Folder));
            var parent = Path.GetDirectoryName(folder);
            return parent is not null
                && string.Equals(parent, expectedParent, StringComparison.OrdinalIgnoreCase)
                && IsTxName(Path.GetFileName(folder));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsTxName(string name) => name.Length == 12 && name.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');
}

/// <summary>The names inside <c>L\update\&lt;id&gt;</c> that the stage uses.</summary>
public static class StageLayout
{
    public const string CacheFolderName = "cache";
    public const string FilesFolderName = "files";
    public const string HashListFileName = "SHA256.txt";
    public const string ManifestFileName = "release.manifest.json";
    public const string ZipFileName = "bundle.zip";
    public const string ZipPartName = "bundle.zip.part";

    public static string CacheOf(string stateFolder) => Path.Combine(stateFolder, CacheFolderName);
}
