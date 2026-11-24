using PcMieru.Update.Bundle;

namespace PcMieru.Update.Feed;

/// <summary>
/// What a check validated about one release and the stage needs to go on (DESIGN-B 3.3): built only by <see cref="UpdateService"/> after SHA256.txt, the outer manifest and every
/// digest of the API agreed, and carried on the <see cref="UpdateCheck"/> it returns. Nothing outside this assembly can make one, so a candidate that a fake or a file produced can never reach
/// the download. It holds the two small files as they were read (the stage writes those exact bytes into its folder) and the numbers the zip is checked against.
/// </summary>
internal sealed class ValidatedRelease
{
    public required string Tag { get; init; }

    public required AppVersion Version { get; init; }

    public required Uri ZipUrl { get; init; }

    /// <summary>The zip's SHA-256 as SHA256.txt names it (lower-case hex).</summary>
    public required string ZipSha256 { get; init; }

    /// <summary>What the release says the zip weighs (the API's size).</summary>
    public required long ZipDeclaredBytes { get; init; }

    /// <summary>The API's "digest" of the zip ("sha256:..."), or null when the API gave none.</summary>
    public string? ZipApiDigest { get; init; }

    public required byte[] HashListBytes { get; init; }

    /// <summary>SHA-256 of <see cref="HashListBytes"/> (lower-case hex).</summary>
    public required string HashListSha256 { get; init; }

    public required byte[] ManifestBytes { get; init; }

    /// <summary>SHA-256 of <see cref="ManifestBytes"/>, which SHA256.txt names for the manifest (lower-case hex).</summary>
    public required string ManifestSha256 { get; init; }

    public required BundleManifest Manifest { get; init; }
}
