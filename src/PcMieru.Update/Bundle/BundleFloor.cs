namespace PcMieru.Update.Bundle;

/// <summary>
/// The required floor of a v0.5 bundle (DESIGN-B 3.2): files that must be in every bundle, whatever else the manifest lists. The names are compared
/// without regard to case. One list, shared by the validator and by tools/publish.ps1 (through tools/BundleCheck).
///
/// <b>The split for step 1.</b> <see cref="Full"/> is the floor as designed. Three of its files do not exist in a published folder yet:
/// PCMieru.Swap.exe (step 4), PCMieru.gate.txt (step 3) and PcMieru.Update.dll (the Gui is not wired to it before step 2). <see cref="NotYetBuilt"/> names
/// them and <see cref="ForThisBuild"/> is Full without them: that is what tools/publish.ps1 asks for now. The later steps delete a name from
/// <see cref="NotYetBuilt"/> when the file starts to ship; when the list is empty publish asks for <see cref="Full"/> and the client always does.
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

        // New in v0.5: the swapper, the manifest of the folder, the gate file, and the Checks and Update libraries.
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

    /// <summary>The floor files that a published folder does not carry yet. Shrinks as the steps of DESIGN-B 11 land.</summary>
    public static IReadOnlyList<string> NotYetBuilt { get; } =
    [
        "PCMieru.Swap.exe",
        BundleFormat.GateFileName,
        "PcMieru.Update.dll",
    ];

    /// <summary><see cref="Full"/> without <see cref="NotYetBuilt"/>: what this build's publish can already meet.</summary>
    public static IReadOnlyList<string> ForThisBuild { get; } =
        Full.Where(name => !NotYetBuilt.Contains(name, StringComparer.OrdinalIgnoreCase)).ToArray();
}
