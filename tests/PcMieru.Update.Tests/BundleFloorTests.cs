using PcMieru.Update.Bundle;

namespace PcMieru.Update.Tests;

/// <summary>The required floor and its split for step 1: the full floor of DESIGN-B 3.2 and what this build's publish can already meet.</summary>
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
    public void What_is_not_built_yet_is_three_names_and_all_of_them_are_on_the_full_floor()
    {
        Assert.Equal(
            new[] { "PCMieru.Swap.exe", "PCMieru.gate.txt", "PcMieru.Update.dll" }.OrderBy(n => n, StringComparer.Ordinal),
            BundleFloor.NotYetBuilt.OrderBy(n => n, StringComparer.Ordinal));
        foreach (var name in BundleFloor.NotYetBuilt)
        {
            Assert.Contains(name, BundleFloor.Full);
        }
    }

    [Fact]
    public void The_floor_for_this_build_is_the_full_floor_without_what_is_not_built_yet()
    {
        Assert.Equal(BundleFloor.Full.Count - BundleFloor.NotYetBuilt.Count, BundleFloor.ForThisBuild.Count);
        Assert.Equal(14, BundleFloor.ForThisBuild.Count);
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
