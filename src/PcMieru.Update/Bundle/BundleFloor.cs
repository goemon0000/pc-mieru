namespace PcMieru.Update.Bundle;

/// <summary>
/// The required floor of a v0.5 bundle (DESIGN-B 3.2): files that must be in every bundle, whatever else the manifest lists. The names are compared
/// without regard to case. One list, shared by the validator and by tools/publish.ps1 (through tools/BundleCheck).
///
/// <b>The split.</b> <see cref="Full"/> is the floor as designed. Steps 1 to 3 shipped without some of its files (the gate file, the Update library, the swapper); every one of them is in
/// every published folder now (PCMieru.Swap.exe since step 4), so <see cref="NotYetBuilt"/> is EMPTY, <see cref="ForThisBuild"/> is <see cref="Full"/>, and tools/publish.ps1 and the client both ask for
/// <see cref="Full"/>. The two properties stay as the place where a floor file of a later step can wait until its step ships (name it in <see cref="NotYetBuilt"/> and publish with the
/// "this-build" floor of tools/BundleCheck again, in the same change), and so that the tests that build a bundle of "this build" keep saying so.
/// The client's own check (BundleZip.Validate with no floor argument) uses <see cref="Full"/> from the start.
/// </summary>
public static class BundleFloor
{
    /// <summary>The floor of v0.5, as designed (17 names).</summary>
    public static IReadOnlyList<string> Full { get; } =
    [
        // The five product exes and the Core library.
        "PCMieru.exe",
        "pcmi.exe",
        "PCMieru.SensorHost.exe",
        "PCMieru.Helper.exe",
        "PCMieru.NetMeter.exe",
        "PcMieru.Core.dll",

        // New in v0.5: the swapper, the manifest of the folder, the gate file (every program refuses to start without it), and the Checks and Update libraries.
        "PCMieru.Swap.exe",
        BundleFormat.InnerManifestName,
        BundleFormat.GateFileName,
        "PcMieru.Checks.dll",
        "PcMieru.Update.dll",

        // The three known managed exes need their runtime configuration (a folder without them does not start).
        "PCMieru.deps.json",
        "PCMieru.runtimeconfig.json",
        "pcmi.deps.json",
        "pcmi.runtimeconfig.json",
        "PCMieru.SensorHost.deps.json",
        "PCMieru.SensorHost.runtimeconfig.json",
    ];

    /// <summary>
    /// The floor files that a published folder does not carry yet: none (step 4 shipped PCMieru.Swap.exe, and ci.yml no longer passes -PartialFloor; publish.ps1 has no such switch any more).
    /// </summary>
    public static IReadOnlyList<string> NotYetBuilt { get; } = [];

    /// <summary><see cref="Full"/> without <see cref="NotYetBuilt"/>: what this build's publish can already meet.</summary>
    public static IReadOnlyList<string> ForThisBuild { get; } =
        Full.Where(name => !NotYetBuilt.Contains(name, StringComparer.OrdinalIgnoreCase)).ToArray();
}
