using PcMieru.Update.Bundle;

namespace PcMieru.Update.Tests;

/// <summary>The required floor and its split (step 1): the full floor of DESIGN-B 3.2 and what this build's publish can meet (all of it since step 4).</summary>
public class BundleFloorTests
{
    [Fact]
    public void The_full_floor_is_the_seventeen_names_of_the_design()
    {
        Assert.Equal(17, BundleFloor.Full.Count);
        Assert.Equal(BundleFloor.Full.Count, BundleFloor.Full.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        string[] designed =
        [
            "PCMieru.exe", "pcmi.exe", "PCMieru.SensorHost.exe", "PCMieru.Helper.exe", "PCMieru.NetMeter.exe", "PcMieru.Core.dll",
            "PCMieru.Swap.exe", "PCMieru.bundle.json", "PCMieru.gate.txt", "PcMieru.Checks.dll", "PcMieru.Update.dll",
            "PCMieru.deps.json", "PCMieru.runtimeconfig.json", "pcmi.deps.json", "pcmi.runtimeconfig.json",
            "PCMieru.SensorHost.deps.json", "PCMieru.SensorHost.runtimeconfig.json",
        ];
        Assert.Equal(designed.OrderBy(n => n, StringComparer.Ordinal), BundleFloor.Full.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void Nothing_is_left_that_is_not_built_yet_and_the_swapper_is_on_the_floor_of_this_build()
    {
        // Step 3 built the gate file and ships PcMieru.Update.dll; step 4 ships the swapper, the last name that was waiting.
        Assert.Empty(BundleFloor.NotYetBuilt);
        Assert.Contains(BundleFormat.GateFileName, BundleFloor.ForThisBuild);
        Assert.Contains("PcMieru.Update.dll", BundleFloor.ForThisBuild);
        Assert.Contains("PCMieru.Swap.exe", BundleFloor.ForThisBuild);
        foreach (var name in BundleFloor.NotYetBuilt)
        {
            Assert.Contains(name, BundleFloor.Full);
        }
    }

    [Fact]
    public void The_floor_for_this_build_is_the_full_floor_without_what_is_not_built_yet()
    {
        Assert.Equal(BundleFloor.Full.Count - BundleFloor.NotYetBuilt.Count, BundleFloor.ForThisBuild.Count);
        Assert.Equal(17, BundleFloor.ForThisBuild.Count);
        Assert.Equal(BundleFloor.Full.OrderBy(n => n, StringComparer.Ordinal), BundleFloor.ForThisBuild.OrderBy(n => n, StringComparer.Ordinal));
        foreach (var name in BundleFloor.NotYetBuilt)
        {
            Assert.DoesNotContain(name, BundleFloor.ForThisBuild);
        }

        foreach (var name in BundleFloor.ForThisBuild)
        {
            Assert.Contains(name, BundleFloor.Full);
        }

        // The inner manifest is on both: a published bundle always has it.
        Assert.Contains(BundleFormat.InnerManifestName, BundleFloor.ForThisBuild);
    }

    [Fact]
    public void Every_floor_name_obeys_the_name_rules()
    {
        foreach (var name in BundleFloor.Full)
        {
            Assert.True(BundleNames.Check(name).Ok, name);
        }
    }
}
