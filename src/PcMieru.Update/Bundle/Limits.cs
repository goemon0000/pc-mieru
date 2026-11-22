namespace PcMieru.Update.Bundle;

/// <summary>
/// The size limits of a bundle (DESIGN-B 3.2 item 4: ZIP 256 MiB, 768 MiB of files, 4,096 files, 2 MiB of manifest). They are applied to what is
/// <b>really read</b> (the bytes of the zip, the bytes of every decompressed entry, the bytes of a manifest), not only to what a header says.
/// A test builds a small <see cref="Limits"/> to cross a limit with a few bytes.
/// </summary>
public sealed record Limits
{
    /// <summary>The longest zip, in bytes.</summary>
    public long ZipBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>The most bytes of all files together, after decompression.</summary>
    public long TotalBytes { get; init; } = 768L * 1024 * 1024;

    /// <summary>The most files.</summary>
    public int Files { get; init; } = 4096;

    /// <summary>The longest manifest (outer or inner), in bytes.</summary>
    public int ManifestBytes { get; init; } = 2 * 1024 * 1024;

    /// <summary>The proposed v0.5 limits.</summary>
    public static Limits Default { get; } = new();
}
