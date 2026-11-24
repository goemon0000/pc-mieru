namespace PcMieru.Update.Bundle;

/// <summary>
/// Why a name, a manifest or a bundle is refused. One value per rule of DESIGN-B 3.2, so a test (and a message) can say exactly which rule held.
/// </summary>
public enum BundleFault
{
    None = 0,

    // --- one name (BundleNames) ---
    /// <summary>The name is empty.</summary>
    EmptyName,

    /// <summary>A backslash. A ZIP made by .NET Framework's CreateFromDirectory (Windows PowerShell 5.1) names its entries this way, so the v0.2.0 zip is refused.</summary>
    Backslash,

    /// <summary>A leading "/" (an absolute path).</summary>
    LeadingSlash,

    /// <summary>A drive name ("C:").</summary>
    DriveName,

    /// <summary>A ":" anywhere else (an NTFS alternate data stream "a.dll:x").</summary>
    Colon,

    /// <summary>An empty path element ("a//b", or a trailing "/" on a file).</summary>
    EmptySegment,

    /// <summary>A "." or ".." element.</summary>
    DotSegment,

    /// <summary>A control character (below U+0020, U+007F, U+0080..U+009F).</summary>
    ControlCharacter,

    /// <summary>An element that ends with a dot or a space (Windows drops it, so the name on disk would not be the name in the list).</summary>
    TrailingDotOrSpace,

    /// <summary>A DOS device name (CON, PRN, AUX, NUL, COM0..9, LPT0..9), with or without an extension, in any case.</summary>
    DeviceName,

    /// <summary>A name that is none of the allowed shapes of the 3.2 table (a sub folder other than ja/ and en/, an exe or dll named some other way, ...).</summary>
    NotAllowed,

    // --- the manifest (BundleManifest.TryParse) ---
    /// <summary>The manifest is not JSON, or not an object, or a value has the wrong type.</summary>
    ManifestSyntax,

    /// <summary>The manifest starts with a byte order mark (the file must be UTF-8 without one).</summary>
    ManifestBom,

    /// <summary>A JSON key appears twice in one object.</summary>
    DuplicateKey,

    /// <summary>A required key is missing.</summary>
    ManifestMissingField,

    /// <summary>The format is not "pcm-bundle/1".</summary>
    UnknownFormat,

    /// <summary>The rid is not "win-x64".</summary>
    UnknownRid,

    /// <summary>The gate protocol is not the integer 1.</summary>
    UnknownGateProtocol,

    /// <summary>The version is not a plain X.Y.Z (no "v", no suffix, no leading zero).</summary>
    BadVersion,

    /// <summary>The build id is not a lower-case GUID.</summary>
    BadBuildId,

    /// <summary>A file entry is not an object with a string path and an integer size.</summary>
    BadFileEntry,

    /// <summary>A hash is missing or is not 64 lower-case hex digits.</summary>
    BadHash,

    /// <summary>A size below zero.</summary>
    NegativeSize,

    /// <summary>The same path twice (compared without regard to case: Windows would treat them as one file).</summary>
    DuplicatePath,

    // --- the bundle (BundleZip / BundleFolder) ---
    /// <summary>The zip has no readable central directory (or cannot be read at all).</summary>
    NotZip,

    /// <summary>The zip uses ZIP64 (a bundle is far below 4 GiB and 65,535 entries; the limits make it unnecessary).</summary>
    Zip64,

    /// <summary>The zip is longer than <see cref="Limits.ZipBytes"/>.</summary>
    ZipTooLarge,

    /// <summary>The total size of the files is over the limit (declared or really read).</summary>
    TotalTooLarge,

    /// <summary>More files than the limit.</summary>
    TooManyFiles,

    /// <summary>A manifest is over the manifest limit (declared or really read).</summary>
    ManifestTooLarge,

    /// <summary>An entry is outside the root folder (a file at the top of the zip).</summary>
    OutsideRoot,

    /// <summary>The top folder is not PCMieru-X.Y.Z-win-x64 for the expected version.</summary>
    WrongRoot,

    /// <summary>An explicit directory entry that is not the root, ja/ or en/, or one that carries data.</summary>
    DirectoryNotAllowed,

    /// <summary>An explicit ja/ or en/ directory entry with nothing in it.</summary>
    EmptyDirectory,

    /// <summary>A symbolic link, a reparse point or any entry that is not a plain file or directory.</summary>
    Link,

    /// <summary>An encrypted entry.</summary>
    Encrypted,

    /// <summary>A compression method other than stored or deflate.</summary>
    UnsupportedMethod,

    /// <summary>The same name twice (compared without regard to case).</summary>
    DuplicateName,

    /// <summary>A file and a directory with the same name.</summary>
    FileDirectoryCollision,

    /// <summary>The zip can be read two ways: a second end record, a local header that names another file, or the central directory and the zip library disagreeing about an entry (name, sizes, attributes). It is refused.</summary>
    ZipAmbiguous,

    /// <summary>A file on the required floor is not in the bundle.</summary>
    FloorMissing,

    /// <summary>The inner PCMieru.bundle.json is not in the bundle (whatever floor was asked for).</summary>
    InnerManifestMissing,

    /// <summary>The set of files differs from the manifest's (a file missing from the manifest, a file missing from the zip, or a different spelling).</summary>
    FileSetMismatch,

    /// <summary>The version of the manifest is not the version asked for.</summary>
    VersionMismatch,

    /// <summary>The outer and the inner manifest differ in format, version, rid, build id or gate protocol.</summary>
    OuterInnerMismatch,

    /// <summary>The inner manifest does not list exactly the files of the outer one minus itself (or lists itself).</summary>
    InnerFilesMismatch,

    /// <summary>The declared length, the length really read and the manifest's length are not all equal.</summary>
    LengthMismatch,

    /// <summary>The SHA-256 of the bytes really read is not the manifest's.</summary>
    HashMismatch,

    /// <summary>The bundle could not be read (I/O error while reading an entry or the folder).</summary>
    ReadFailed,

    /// <summary>The gate file is in the bundle and the manifests agree about it, but its bytes are not the text fixed in the client (<c>GateFile.Sha256Hex</c>, DESIGN-B 4).</summary>
    GateFileChanged,
}

/// <summary>The answer of a pure check (a name, a manifest): ok, or the rule that held and a short text.</summary>
public readonly record struct BundleVerdict(BundleFault Fault, string Detail)
{
    public static BundleVerdict Pass { get; } = new(BundleFault.None, string.Empty);

    public bool Ok => Fault == BundleFault.None;

    public static BundleVerdict Refuse(BundleFault fault, string detail) => new(fault, detail);

    public override string ToString() => Ok ? "ok" : $"{Fault}: {Detail}";
}

/// <summary>The answer of the bundle validators: ok (with what was read), or the rule that held and a short text.</summary>
public readonly record struct BundleResult(BundleFault Fault, string Detail, int FileCount, long TotalBytes)
{
    public bool Ok => Fault == BundleFault.None;

    public static BundleResult Pass(int files, long bytes) => new(BundleFault.None, string.Empty, files, bytes);

    public static BundleResult Refuse(BundleFault fault, string detail) => new(fault, detail, 0, 0);

    public override string ToString() => Ok ? $"ok: {FileCount} files, {TotalBytes} bytes" : $"{Fault}: {Detail}";
}
