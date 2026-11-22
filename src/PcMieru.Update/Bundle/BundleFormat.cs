namespace PcMieru.Update.Bundle;

/// <summary>The fixed words of the pcm-bundle/1 contract (DESIGN-B 3.1) and the names of the files of a release, in one place.</summary>
public static class BundleFormat
{
    /// <summary>The "format" value of a manifest.</summary>
    public const string Format = "pcm-bundle/1";

    /// <summary>The only runtime identifier of v0.5.</summary>
    public const string Rid = "win-x64";

    /// <summary>The only gate protocol of v0.5 (an integer in the manifest).</summary>
    public const int GateProtocol = 1;

    /// <summary>The manifest inside the folder. It lists every file but itself.</summary>
    public const string InnerManifestName = "PCMieru.bundle.json";

    /// <summary>The gate file (DESIGN-B 4). Not produced by this step.</summary>
    public const string GateFileName = "PCMieru.gate.txt";

    /// <summary>"PCMieru-X.Y.Z-win-x64": the top folder of the zip and of a manual unzip.</summary>
    public static string RootFolder(string version) => $"PCMieru-{version}-{Rid}";

    /// <summary>"PCMieru-X.Y.Z-win-x64.zip".</summary>
    public static string ZipName(string version) => RootFolder(version) + ".zip";

    /// <summary>"PCMieru-X.Y.Z-win-x64.manifest.json": the outer manifest next to the zip.</summary>
    public static string OuterManifestName(string version) => RootFolder(version) + ".manifest.json";

    /// <summary>"SHA256.txt": the hashes of the zip and of the outer manifest.</summary>
    public const string HashListName = "SHA256.txt";
}
