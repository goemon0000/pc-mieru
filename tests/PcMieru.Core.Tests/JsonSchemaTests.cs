using PcMieru.Core.Collection;
using PcMieru.Core.Hardware;
using PcMieru.Core.Hardware.Model;
using PcMieru.Core.Reporting;

namespace PcMieru.Core.Tests;

public class JsonSchemaTests
{
    [Fact]
    public void Snapshot_matches_the_v1_fixture()
    {
        var snapshot = FakeSnapshots.Create(Fixture.FixedTime, tick: 2);
        Assert.Equal("1", snapshot.SchemaVersion);
        Fixture.AssertMatches("snapshot.v1.json", PcMieruJson.Serialize(snapshot, indented: true));
    }

    [Fact]
    public void Hardware_matches_the_v1_fixture()
    {
        var hardware = FakeHardwareData.Create(Fixture.FixedTime);
        Fixture.AssertMatches("hardware.v1.json", PcMieruJson.Serialize(hardware, indented: true));
    }

    [Fact]
    public async Task Storage_health_matches_the_v1_fixture()
    {
        var drives = await new FakeStorageHealthProvider().ReadAsync(false);
        var report = new StorageHealthReport(StorageHealthReport.CurrentSchemaVersion, drives);
        Fixture.AssertMatches("storage-health.v1.json", PcMieruJson.Serialize(report, indented: true));
    }

    [Fact]
    public void Summary_matches_the_v1_fixture()
    {
        var snapshot = FakeSnapshots.Create(Fixture.FixedTime, tick: 2);
        var summary = ObservationRun.Summarize(snapshot, [snapshot], new FakeObservationEngine(), null);
        Assert.NotEmpty(summary.Observations);
        Fixture.AssertMatches("summary.v1.json", PcMieruJson.Serialize(summary, indented: true));
    }

    [Fact]
    public void Snapshot_round_trips()
    {
        var snapshot = FakeSnapshots.Create(Fixture.FixedTime, tick: 2);
        var json = PcMieruJson.Serialize(snapshot);
        var back = PcMieruJson.Deserialize<Snapshot>(json);
        Assert.NotNull(back);
        Assert.Equal(json, PcMieruJson.Serialize(back));
        Assert.Equal(snapshot.Processes.Count, back.Processes.Count);
        Assert.Equal(snapshot.Processes[3].Key, back.Processes[3].Key);
    }

    [Fact]
    public void Hardware_round_trips()
    {
        var hardware = FakeHardwareData.Create(Fixture.FixedTime);
        var json = PcMieruJson.Serialize(hardware);
        var back = PcMieruJson.Deserialize<HardwareInfo>(json);
        Assert.NotNull(back);
        Assert.Equal(json, PcMieruJson.Serialize(back));
    }

    [Fact]
    public void Big_counters_are_strings_so_other_tools_do_not_lose_precision()
    {
        var node = PcMieruJson.ToNode(new U128(UInt128.MaxValue));
        Assert.Equal("340282366920938463463374607431768211455", node!.GetValue<string>());
    }
}
