using System.Buffers.Binary;
using System.Text;
using PcMieru.Core.Collection;
using PcMieru.Core.Hardware.Model;

namespace PcMieru.Core.Tests.Collection;

internal static class GpuNames
{
    public static string Engine(uint pid, ulong luid, uint phys, uint eng, string type) =>
        $"pid_{pid}_luid_0x{(uint)(luid >> 32):x8}_0x{(uint)luid:x8}_phys_{phys}_eng_{eng}_engtype_{type}";

    public static string Adapter(ulong luid, uint phys = 0) =>
        $"luid_0x{(uint)(luid >> 32):x8}_0x{(uint)luid:x8}_phys_{phys}";
}

public sealed class GpuCounterNameTests
{
    [Fact]
    public void A_real_engine_instance_name_is_taken_apart_by_its_tokens()
    {
        Assert.True(GpuCounterName.TryParseEngine("pid_1234_luid_0x00000000_0x000166CA_phys_0_eng_3_engtype_Copy", out var pid, out var key, out var type));

        Assert.Equal(1234u, pid);
        Assert.Equal(new GpuEngineKey(0x166CA, 0, 3), key);
        Assert.Equal("Copy", type.ToString());
    }

    [Fact]
    public void The_luid_is_high_part_then_low_part()
    {
        Assert.True(GpuCounterName.TryParseEngine("pid_1_luid_0x00000001_0x00000002_phys_1_eng_7_engtype_3D", out _, out var key, out _));

        Assert.Equal(new GpuEngineKey(0x1_0000_0002UL, 1, 7), key);
    }

    [Theory]
    [InlineData("JPEG_Decode_0")]
    [InlineData("VideoDecode")]
    [InlineData("Compute_1")]
    [InlineData("3D")]
    public void An_engine_type_is_the_rest_of_the_name_even_when_it_contains_underscores(string engineType)
    {
        Assert.True(GpuCounterName.TryParseEngine($"pid_4_luid_0x00000000_0x00000001_phys_0_eng_0_engtype_{engineType}", out _, out _, out var type));

        Assert.Equal(engineType, type.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("pid")]
    [InlineData("luid_0x00000000_0x00000001_phys_0_eng_0_engtype_3D")] // no pid
    [InlineData("pid_1_phys_0_eng_0_engtype_3D")] // no luid
    [InlineData("pid_1_luid_0x00000000_0x00000001_eng_0_engtype_3D")] // no phys
    [InlineData("pid_1_luid_0x00000000_0x00000001_phys_0_engtype_3D")] // no eng
    [InlineData("pid_1_luid_0x00000000_0x00000001_phys_0_eng_0")] // no engtype
    [InlineData("pid_1_luid_0x00000000_0x00000001_phys_0_eng_0_engtype")] // engtype without a value
    [InlineData("pid_1_luid_0x00000000_0x00000001_phys_0_eng_0_engtype_")] // empty value
    [InlineData("pid_1_luid_0x00000000_0x00000001_phys_0_eng_0_bogus_5_engtype_3D")] // a key this parser does not know
    [InlineData("pid_1_pid_2_luid_0x00000000_0x00000001_phys_0_eng_0_engtype_3D")] // duplicate key
    [InlineData("pid_1_luid_0x00000000_0x00000001_luid_0x00000000_0x00000002_phys_0_eng_0_engtype_3D")]
    [InlineData("pid_-1_luid_0x00000000_0x00000001_phys_0_eng_0_engtype_3D")]
    [InlineData("pid_99999999999_luid_0x00000000_0x00000001_phys_0_eng_0_engtype_3D")]
    [InlineData("pid_abc_luid_0x00000000_0x00000001_phys_0_eng_0_engtype_3D")]
    [InlineData("pid_1_luid_0xZZ_0x00000001_phys_0_eng_0_engtype_3D")]
    [InlineData("pid_1_luid_00000000_0x00000001_phys_0_eng_0_engtype_3D")] // no 0x
    [InlineData("pid_1_luid_0x_0x00000001_phys_0_eng_0_engtype_3D")] // 0x with no digits
    [InlineData("pid_1_luid_0x1FFFFFFFF_0x00000001_phys_0_eng_0_engtype_3D")] // does not fit 32 bits
    [InlineData("pid_1_luid_0x00000000")] // luid cut short
    public void A_name_that_is_not_exactly_the_known_format_is_rejected_whole(string name)
    {
        Assert.False(GpuCounterName.TryParseEngine(name, out _, out _, out _));
    }

    [Fact]
    public void The_adapter_memory_instance_gives_luid_and_phys()
    {
        Assert.True(GpuCounterName.TryParseAdapter("luid_0x00000000_0x000166CA_phys_2", out var luid, out var phys));

        Assert.Equal(0x166CAUL, luid);
        Assert.Equal(2u, phys);
    }

    [Theory]
    [InlineData("")]
    [InlineData("luid_0x00000000_0x000166CA")] // no phys
    [InlineData("phys_0")] // no luid
    [InlineData("luid_0x00000000_0x000166CA_phys_0_eng_1")] // an engine key where only an adapter belongs
    [InlineData("luid_0x00000000_0x000166CA_phys_0_phys_1")]
    [InlineData("luid_0xQQ_0x000166CA_phys_0")]
    public void An_adapter_instance_in_any_other_shape_is_rejected(string name)
    {
        Assert.False(GpuCounterName.TryParseAdapter(name, out _, out _));
    }

    [Theory]
    [InlineData(0x166CAUL, "luid_0x00000000_0x000166ca")]
    [InlineData(0x1_0000_00ABUL, "luid_0x00000001_0x000000ab")]
    [InlineData(0UL, "luid_0x00000000_0x00000000")]
    [InlineData(0xFFFF_FFFF_FFFF_FFFFUL, "luid_0xffffffff_0xffffffff")]
    public void The_adapter_id_is_the_lowercase_luid_text_SensorHost_uses(ulong luid, string expected)
    {
        Assert.Equal(expected, GpuCounterName.AdapterId(luid));
    }

    [Fact]
    public void The_id_round_trips_through_the_adapter_parser_for_a_high_luid()
    {
        // GpuNames.Adapter is the PDH spelling (upper or lower case hex both occur in the wild).
        Assert.True(GpuCounterName.TryParseAdapter(GpuNames.Adapter(0x24557_0000_1234UL), out var luid, out _));
        Assert.Equal(0x24557_0000_1234UL, luid);
    }
}

public sealed class GpuAggregatorTests
{
    private const uint Valid = Pdh.CStatusValidData;
    private const ulong Gpu0 = 0x166CA;
    private const ulong Gpu1 = 0x17DDA;

    private static GpuAggregator Fill(params (string Name, double Value)[] engines)
    {
        var aggregator = new GpuAggregator();
        aggregator.Begin();
        foreach (var (name, value) in engines)
        {
            aggregator.AddEngine(name, value, Valid);
        }

        aggregator.Finish();
        return aggregator;
    }

    [Fact]
    public void Two_processes_on_the_same_engine_are_summed()
    {
        var a = Fill((GpuNames.Engine(10, Gpu0, 0, 0, "3D"), 30), (GpuNames.Engine(11, Gpu0, 0, 0, "3D"), 40));

        Assert.Equal(70, a.Adapters[Gpu0].Busiest);
        Assert.Equal(30, a.ProcessBusiest[10]);
        Assert.Equal(40, a.ProcessBusiest[11]);
        Assert.Equal(1, a.Adapters[Gpu0].EngineInstances);
    }

    [Fact]
    public void Different_engines_are_never_added_together()
    {
        // The Task Manager bug this guards against: 60 % 3D plus 50 % Copy is not 110 % of anything.
        var a = Fill((GpuNames.Engine(10, Gpu0, 0, 0, "3D"), 60), (GpuNames.Engine(10, Gpu0, 0, 1, "Copy"), 50));

        Assert.Equal(60, a.Adapters[Gpu0].Busiest);
        Assert.Equal(60, a.Adapters[Gpu0].ByType["3D"]);
        Assert.Equal(50, a.Adapters[Gpu0].ByType["Copy"]);
        Assert.Equal(60, a.ProcessBusiest[10]);
        Assert.Equal(2, a.Adapters[Gpu0].EngineInstances);
    }

    [Fact]
    public void An_engine_cannot_be_busier_than_one_hundred_percent_but_each_process_keeps_its_own_share()
    {
        var a = Fill((GpuNames.Engine(10, Gpu0, 0, 0, "VideoEncode"), 80), (GpuNames.Engine(11, Gpu0, 0, 0, "VideoEncode"), 70));

        Assert.Equal(100, a.Adapters[Gpu0].Busiest);
        Assert.Equal(80, a.ProcessBusiest[10]);
        Assert.Equal(70, a.ProcessBusiest[11]);
    }

    [Fact]
    public void A_process_on_two_engines_reads_as_its_busiest_engine_not_the_sum()
    {
        var a = Fill((GpuNames.Engine(10, Gpu0, 0, 0, "3D"), 20), (GpuNames.Engine(10, Gpu0, 0, 1, "Compute_0"), 35));

        Assert.Equal(35, a.ProcessBusiest[10]);
    }

    [Fact]
    public void A_process_with_two_instances_on_the_same_engine_is_summed_for_that_engine()
    {
        var a = Fill((GpuNames.Engine(10, Gpu0, 0, 0, "3D"), 30), (GpuNames.Engine(10, Gpu0, 0, 0, "3D"), 25));

        Assert.Equal(55, a.ProcessBusiest[10]);
    }

    [Fact]
    public void The_same_engine_index_on_another_adapter_or_another_physical_adapter_is_a_different_engine()
    {
        var a = Fill(
            (GpuNames.Engine(10, Gpu0, 0, 0, "3D"), 50),
            (GpuNames.Engine(10, Gpu1, 0, 0, "3D"), 10),
            (GpuNames.Engine(10, Gpu0, 1, 0, "3D"), 20));

        Assert.Equal(50, a.Adapters[Gpu0].Busiest);
        Assert.Equal(2, a.Adapters[Gpu0].EngineInstances);
        Assert.Equal(10, a.Adapters[Gpu1].Busiest);
    }

    [Fact]
    public void The_per_type_value_is_the_busiest_engine_of_that_type()
    {
        var a = Fill((GpuNames.Engine(10, Gpu0, 0, 0, "3D"), 10), (GpuNames.Engine(11, Gpu0, 0, 4, "3D"), 25));

        Assert.Equal(25, a.Adapters[Gpu0].ByType["3D"]);
        Assert.Equal(25, a.Adapters[Gpu0].Busiest);
    }

    [Fact]
    public void An_instance_in_an_unknown_format_is_counted_and_left_out()
    {
        var a = new GpuAggregator();
        a.Begin();
        a.AddEngine("something_new_in_a_later_windows", 50, Valid);
        a.AddEngine(GpuNames.Engine(10, Gpu0, 0, 0, "3D"), 5, Valid);
        a.AddAdapterMemory("not an adapter", 1, Valid, dedicated: true);
        a.Finish();

        Assert.Equal(2, a.Unrecognized);
        Assert.Equal(5, a.Adapters[Gpu0].Busiest);
    }

    [Theory]
    [InlineData(0xC0000BBAu, 10.0)]
    [InlineData(Valid, double.NaN)]
    [InlineData(Valid, double.PositiveInfinity)]
    [InlineData(Valid, -1.0)]
    public void A_value_PDH_could_not_compute_is_counted_as_invalid_and_never_summed(uint cstatus, double value)
    {
        var a = new GpuAggregator();
        a.Begin();
        a.AddEngine(GpuNames.Engine(10, Gpu0, 0, 0, "3D"), value, cstatus);
        a.AddAdapterMemory(GpuNames.Adapter(Gpu0), value, cstatus, dedicated: false);
        a.Finish();

        Assert.Equal(2, a.Invalid);
        Assert.Empty(a.Adapters);
        Assert.Empty(a.ProcessBusiest);
    }

    [Fact]
    public void Memory_is_summed_over_the_physical_adapters_of_one_luid_and_dedicated_is_kept_apart_from_shared()
    {
        var a = new GpuAggregator();
        a.Begin();
        a.AddAdapterMemory(GpuNames.Adapter(Gpu0, 0), 1000, Valid, dedicated: true);
        a.AddAdapterMemory(GpuNames.Adapter(Gpu0, 1), 500, Valid, dedicated: true);
        a.AddAdapterMemory(GpuNames.Adapter(Gpu0, 0), 64, Valid, dedicated: false);
        a.AddAdapterMemory(GpuNames.Adapter(Gpu1), 7, Valid, dedicated: true);
        a.Finish();

        Assert.Equal(1500, a.Adapters[Gpu0].DedicatedUsed);
        Assert.Equal(64, a.Adapters[Gpu0].SharedUsed);
        Assert.Equal(0, a.Adapters[Gpu0].EngineInstances);
        Assert.Equal(7, a.Adapters[Gpu1].DedicatedUsed);
        Assert.Null(a.Adapters[Gpu1].SharedUsed);
    }

    [Fact]
    public void A_huge_memory_reading_saturates_instead_of_overflowing()
    {
        var a = new GpuAggregator();
        a.Begin();
        a.AddAdapterMemory(GpuNames.Adapter(Gpu0), 1e30, Valid, dedicated: true);
        a.Finish();

        Assert.Equal(long.MaxValue, a.Adapters[Gpu0].DedicatedUsed);
    }

    [Fact]
    public void Begin_forgets_everything_from_the_previous_tick()
    {
        var a = Fill((GpuNames.Engine(10, Gpu0, 0, 0, "3D"), 30));
        a.AddEngine("garbage", 1, Valid);
        a.Begin();
        a.Finish();

        Assert.Empty(a.Adapters);
        Assert.Empty(a.ProcessBusiest);
        Assert.Equal(0, a.Unrecognized);
        Assert.Equal(0, a.Invalid);
    }
}

public sealed class GpuDescribeTests
{
    private const uint Valid = Pdh.CStatusValidData;
    private const long GiB = 1L << 30;

    private static readonly GpuAdapter Real = new(0x166CA, "NVIDIA GeForce RTX 5070", 12 * GiB, 0x10DE, false);
    private static readonly GpuAdapter Duplicate = new(0x24557, "NVIDIA GeForce RTX 5070", 12 * GiB, 0x10DE, false);
    private static readonly GpuAdapter Software = new(0x1, "Microsoft Basic Render Driver", 0, 0x1414, true);

    private static GpuAggregator Data(Action<GpuAggregator> fill)
    {
        var a = new GpuAggregator();
        a.Begin();
        fill(a);
        a.Finish();
        return a;
    }

    [Fact]
    public void Before_the_first_window_every_adapter_DXGI_lists_is_there_with_not_collected_numbers_and_a_real_name_and_total()
    {
        var gpus = GpuSampler.Describe([Real, Duplicate, Software], null, primed: false, "no baseline yet (first GPU sample)", ValueStatus.NotCollected);

        Assert.Equal(2, gpus.Count); // the software adapter is never a GPU a user looks at
        var gpu = gpus[0];
        Assert.Equal("luid_0x00000000_0x000166ca", gpu.Id);
        Assert.Equal("NVIDIA GeForce RTX 5070", gpu.Name);
        Assert.Equal(ValueStatus.NotCollected, gpu.UtilizationPercent.Status);
        Assert.Equal("no baseline yet (first GPU sample)", gpu.UtilizationPercent.Detail);
        Assert.Empty(gpu.Engines);
        Assert.Equal(12 * GiB, gpu.DedicatedTotalBytes.Data);
        Assert.Equal(ValueStatus.NotCollected, gpu.DedicatedUsedBytes.Status);
        Assert.Equal("SensorHost", gpu.TemperatureC.Source);
        Assert.Equal(ValueStatus.NotCollected, gpu.TemperatureC.Status);
    }

    [Fact]
    public void Once_counters_were_read_an_adapter_without_any_instance_is_not_listed()
    {
        var data = Data(a =>
        {
            a.AddEngine(GpuNames.Engine(10, Real.Luid, 0, 0, "3D"), 60, Valid);
            a.AddEngine(GpuNames.Engine(10, Real.Luid, 0, 1, "Copy"), 20, Valid);
            a.AddAdapterMemory(GpuNames.Adapter(Real.Luid), 1 * GiB, Valid, dedicated: true);
            a.AddAdapterMemory(GpuNames.Adapter(Real.Luid), 5_000_000, Valid, dedicated: false);
        });

        var gpus = GpuSampler.Describe([Real, Duplicate, Software], data, primed: true, null, ValueStatus.NotCollected);

        var gpu = Assert.Single(gpus);
        Assert.Equal(Real.Id, gpu.Id);
        Assert.True(gpu.UtilizationPercent.IsOk);
        Assert.Equal(60, gpu.UtilizationPercent.Data);
        Assert.Equal(new[] { "3D", "Copy" }, gpu.Engines.Select(e => e.Type));
        Assert.Equal(new[] { 60.0, 20.0 }, gpu.Engines.Select(e => e.Percent.Data));
        Assert.Equal(1 * GiB, gpu.DedicatedUsedBytes.Data);
        Assert.Equal(5_000_000, gpu.SharedUsedBytes.Data);
        Assert.Equal(12 * GiB, gpu.DedicatedTotalBytes.Data);
    }

    [Fact]
    public void Engines_list_3D_first_then_the_rest_in_name_order()
    {
        var data = Data(a =>
        {
            a.AddEngine(GpuNames.Engine(1, Real.Luid, 0, 0, "VideoDecode"), 1, Valid);
            a.AddEngine(GpuNames.Engine(1, Real.Luid, 0, 1, "Copy"), 2, Valid);
            a.AddEngine(GpuNames.Engine(1, Real.Luid, 0, 2, "3D"), 3, Valid);
            a.AddEngine(GpuNames.Engine(1, Real.Luid, 0, 3, "Compute_0"), 4, Valid);
        });

        var gpu = Assert.Single(GpuSampler.Describe([Real], data, primed: true, null, ValueStatus.NotCollected));

        Assert.Equal(new[] { "3D", "Compute_0", "Copy", "VideoDecode" }, gpu.Engines.Select(e => e.Type));
        Assert.Equal(4, gpu.UtilizationPercent.Data); // the busiest engine, not their sum (10)
    }

    [Fact]
    public void An_adapter_with_working_memory_counters_and_no_engine_context_is_idle_not_unknown()
    {
        var data = Data(a => a.AddAdapterMemory(GpuNames.Adapter(Real.Luid), 100, Valid, dedicated: true));

        var gpu = Assert.Single(GpuSampler.Describe([Real], data, primed: true, null, ValueStatus.NotCollected));

        Assert.True(gpu.UtilizationPercent.IsOk);
        Assert.Equal(0, gpu.UtilizationPercent.Data);
        Assert.NotNull(gpu.UtilizationPercent.Detail);
        Assert.Empty(gpu.Engines);
        Assert.Equal(ValueStatus.NotCollected, gpu.SharedUsedBytes.Status);
    }

    [Fact]
    public void An_adapter_only_PDH_knows_is_listed_under_its_luid_when_it_has_engine_activity_and_not_when_it_has_only_memory()
    {
        const ulong virtualMonitor = 0x17D96;
        const ulong stranger = 0x99999;
        var data = Data(a =>
        {
            a.AddEngine(GpuNames.Engine(5, stranger, 0, 0, "3D"), 12, Valid);
            a.AddAdapterMemory(GpuNames.Adapter(virtualMonitor), 0, Valid, dedicated: true);
            a.AddEngine(GpuNames.Engine(5, Real.Luid, 0, 0, "3D"), 7, Valid);
        });

        var gpus = GpuSampler.Describe([Real], data, primed: true, null, ValueStatus.NotCollected);

        Assert.Equal(2, gpus.Count);
        Assert.Equal(Real.Id, gpus[0].Id);
        Assert.Equal(GpuCounterName.AdapterId(stranger), gpus[1].Id);
        Assert.StartsWith("GPU luid_", gpus[1].Name);
        Assert.Equal(12, gpus[1].UtilizationPercent.Data);
        Assert.Equal(ValueStatus.Unsupported, gpus[1].DedicatedTotalBytes.Status);
    }

    [Fact]
    public void A_failed_read_reports_the_failure_status_and_reason_on_every_live_number_and_keeps_name_and_total()
    {
        var gpu = Assert.Single(GpuSampler.Describe([Real], null, primed: false, "PdhCollectQueryData 0x800007D2", ValueStatus.Error));

        Assert.Equal(ValueStatus.Error, gpu.UtilizationPercent.Status);
        Assert.Equal("PdhCollectQueryData 0x800007D2", gpu.UtilizationPercent.Detail);
        Assert.Equal(ValueStatus.Error, gpu.DedicatedUsedBytes.Status);
        Assert.Equal(ValueStatus.Error, gpu.SharedUsedBytes.Status);
        Assert.Equal("NVIDIA GeForce RTX 5070", gpu.Name);
        Assert.True(gpu.DedicatedTotalBytes.IsOk);
    }

    [Fact]
    public void An_unknown_name_format_leaves_a_note_on_the_utilization_instead_of_a_silent_gap()
    {
        var data = Data(a =>
        {
            a.AddEngine(GpuNames.Engine(10, Real.Luid, 0, 0, "3D"), 5, Valid);
            a.AddEngine("future_format", 1, Valid);
        });

        var gpu = Assert.Single(GpuSampler.Describe([Real], data, primed: true, null, ValueStatus.NotCollected));

        Assert.True(gpu.UtilizationPercent.IsOk);
        Assert.Contains("ignored", gpu.UtilizationPercent.Detail);
    }

    [Fact]
    public void An_adapter_that_reports_no_dedicated_memory_has_an_unsupported_total_not_zero()
    {
        var integrated = new GpuAdapter(0x17DDA, "AMD Radeon(TM) Graphics", 0, 0x1002, false);

        var gpu = Assert.Single(GpuSampler.Describe([integrated], null, primed: false, "x", ValueStatus.NotCollected));

        Assert.Equal(ValueStatus.Unsupported, gpu.DedicatedTotalBytes.Status);
    }

    [Fact]
    public void Failed_replaces_every_live_number_by_one_status_and_keeps_identity()
    {
        var known = GpuSampler.Describe([Real], null, primed: false, "x", ValueStatus.NotCollected);

        var off = GpuSampler.Failed(known, ValueStatus.Timeout, "did not answer within 3 s");

        var gpu = Assert.Single(off);
        Assert.Equal(Real.Id, gpu.Id);
        Assert.Equal(Real.Name, gpu.Name);
        Assert.Equal(12 * GiB, gpu.DedicatedTotalBytes.Data);
        foreach (var v in new[] { gpu.UtilizationPercent, gpu.TemperatureC, gpu.PowerWatts, gpu.FanPercent, gpu.ClockMhz })
        {
            Assert.Equal(ValueStatus.Timeout, v.Status);
            Assert.Equal("did not answer within 3 s", v.Detail);
        }

        Assert.Equal(ValueStatus.Timeout, gpu.DedicatedUsedBytes.Status);
        Assert.Equal(ValueStatus.Timeout, gpu.SharedUsedBytes.Status);
        Assert.Empty(gpu.Engines);
    }
}

public sealed class GpuSensorMergeTests
{
    private static GpuInfo Gpu(string id, string name) => new(
        id,
        name,
        Value.Ok(10.0, "PDH GPU Engine"),
        [new GpuEngineInfo("3D", Value.Ok(10.0, "PDH GPU Engine"))],
        Value.Ok(1L, "PDH GPU Adapter Memory"),
        Value.Ok(2L, "DXGI"),
        Value.Ok(3L, "PDH GPU Adapter Memory"),
        Value.NotCollected<double>("SensorHost", "not read yet"),
        Value.NotCollected<double>("SensorHost", "not read yet"),
        Value.NotCollected<double>("SensorHost", "not read yet"),
        Value.NotCollected<double>("SensorHost", "not read yet"));

    private static GpuSensorReading Reading(string id, string name) => new(
        id,
        name,
        Value.Ok(65.0, "NVML"),
        Value.Ok(120.0, "NVML"),
        Value.Ok(250.0, "NVML"),
        Value.Ok(40.0, "NVML"),
        Value.Ok(2500.0, "NVML"));

    private static Value<IReadOnlyList<GpuSensorReading>> Ok(params GpuSensorReading[] readings) =>
        Value.Ok<IReadOnlyList<GpuSensorReading>>(readings, "SensorHost");

    [Fact]
    public void A_reading_with_the_same_id_fills_temperature_power_fan_and_clock_and_nothing_else()
    {
        var gpu = Gpu("luid_0x00000000_0x000166ca", "NVIDIA GeForce RTX 5070");

        var merged = Assert.Single(GpuSensorMerge.Apply([gpu], Ok(Reading("LUID_0X00000000_0X000166CA", "something else"))));

        Assert.Equal(65.0, merged.TemperatureC.Data);
        Assert.Equal(120.0, merged.PowerWatts.Data);
        Assert.Equal(40.0, merged.FanPercent.Data);
        Assert.Equal(2500.0, merged.ClockMhz.Data);
        Assert.Equal(gpu.UtilizationPercent, merged.UtilizationPercent);
        Assert.Equal(gpu.DedicatedUsedBytes, merged.DedicatedUsedBytes);
        Assert.Equal(gpu.Name, merged.Name);
    }

    [Fact]
    public void Without_an_id_match_the_adapter_name_is_the_fallback_ignoring_case_and_padding()
    {
        var gpu = Gpu("luid_0x00000000_0x000166ca", "NVIDIA GeForce RTX 5070");

        var merged = Assert.Single(GpuSensorMerge.Apply([gpu], Ok(Reading("0", "  nvidia geforce rtx 5070 "))));

        Assert.Equal(65.0, merged.TemperatureC.Data);
    }

    [Fact]
    public void An_id_match_wins_over_an_earlier_name_match()
    {
        var gpu = Gpu("luid_0x00000000_0x000166ca", "NVIDIA GeForce RTX 5070");
        var byName = Reading("other", "NVIDIA GeForce RTX 5070") with { TemperatureC = Value.Ok(11.0, "NVML") };
        var byId = Reading("luid_0x00000000_0x000166ca", "x") with { TemperatureC = Value.Ok(22.0, "NVML") };

        var merged = Assert.Single(GpuSensorMerge.Apply([gpu], Ok(byName, byId)));

        Assert.Equal(22.0, merged.TemperatureC.Data);
    }

    [Fact]
    public void An_answer_with_no_reading_for_this_adapter_is_unsupported_not_zero()
    {
        var gpu = Gpu("luid_0x00000000_0x00017dda", "AMD Radeon(TM) Graphics");

        var merged = Assert.Single(GpuSensorMerge.Apply([gpu], Ok(Reading("luid_0x00000000_0x000166ca", "NVIDIA GeForce RTX 5070"))));

        Assert.Equal(ValueStatus.Unsupported, merged.TemperatureC.Status);
        Assert.Equal(ValueStatus.Unsupported, merged.PowerWatts.Status);
        Assert.Equal(ValueStatus.Unsupported, merged.FanPercent.Status);
        Assert.Equal(ValueStatus.Unsupported, merged.ClockMhz.Status);
        Assert.NotNull(merged.TemperatureC.Detail);
    }

    [Theory]
    [InlineData(ValueStatus.Unsupported)]
    [InlineData(ValueStatus.Timeout)]
    [InlineData(ValueStatus.Error)]
    [InlineData(ValueStatus.NotCollected)]
    public void A_failed_answer_from_the_sensor_client_is_passed_on_with_its_own_status_and_reason(ValueStatus status)
    {
        var gpu = Gpu("luid_0x00000000_0x000166ca", "NVIDIA GeForce RTX 5070");
        var answer = Value.Missing<IReadOnlyList<GpuSensorReading>>(status, "SensorHost", "NVML.dll was not found");

        var merged = Assert.Single(GpuSensorMerge.Apply([gpu], answer));

        foreach (var v in new[] { merged.TemperatureC, merged.PowerWatts, merged.FanPercent, merged.ClockMhz })
        {
            Assert.Equal(status, v.Status);
            Assert.Equal("NVML.dll was not found", v.Detail);
        }

        Assert.Equal(gpu.UtilizationPercent, merged.UtilizationPercent);
    }

    [Fact]
    public void No_adapters_is_returned_as_it_is()
    {
        IReadOnlyList<GpuInfo> none = [];

        Assert.Same(none, GpuSensorMerge.Apply(none, Ok(Reading("a", "b"))));
    }

    [Fact]
    public void The_input_list_is_not_modified()
    {
        var gpu = Gpu("luid_0x00000000_0x000166ca", "NVIDIA GeForce RTX 5070");
        IReadOnlyList<GpuInfo> input = [gpu];

        _ = GpuSensorMerge.Apply(input, Ok(Reading(gpu.Id, gpu.Name)));

        Assert.Equal(ValueStatus.NotCollected, input[0].TemperatureC.Status);
    }
}

public sealed class GpuAdaptersTests
{
    private static byte[] Desc(string name, uint vendor, ulong dedicated, uint luidLow, uint luidHigh, uint flags)
    {
        var d = new byte[312];
        Encoding.Unicode.GetBytes(name).CopyTo(d, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(256), vendor);
        BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(272), dedicated);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(296), luidLow);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(300), luidHigh);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(304), flags);
        return d;
    }

    [Fact]
    public void A_descriptor_is_read_at_the_documented_offsets()
    {
        var a = GpuAdapters.ParseDesc1(Desc("NVIDIA GeForce RTX 5070", 0x10DE, 12UL << 30, 0x166CA, 0, 0));

        Assert.Equal("NVIDIA GeForce RTX 5070", a.Name);
        Assert.Equal(0x10DEu, a.VendorId);
        Assert.Equal(12L << 30, a.DedicatedBytes);
        Assert.Equal(0x166CAUL, a.Luid);
        Assert.False(a.IsSoftware);
        Assert.Equal("luid_0x00000000_0x000166ca", a.Id);
    }

    [Fact]
    public void The_luid_high_part_is_the_upper_half_and_the_software_flag_is_bit_one()
    {
        var a = GpuAdapters.ParseDesc1(Desc("Microsoft Basic Render Driver", 0x1414, 0, 0xAB, 1, 2));

        Assert.Equal(0x1_0000_00ABUL, a.Luid);
        Assert.True(a.IsSoftware);
        Assert.Equal(0, a.DedicatedBytes);
    }

    [Fact]
    public void A_name_that_fills_all_128_characters_with_no_terminator_stays_inside_the_name_field()
    {
        var a = GpuAdapters.ParseDesc1(Desc(new string('G', 128), 1, 0, 1, 0, 0));

        Assert.Equal(128, a.Name.Length);
        Assert.Equal(1u, a.VendorId);
    }

    [Fact]
    public void A_dedicated_size_beyond_the_long_range_is_clamped()
    {
        var a = GpuAdapters.ParseDesc1(Desc("x", 1, ulong.MaxValue, 1, 0, 0));

        Assert.Equal(long.MaxValue, a.DedicatedBytes);
    }

    [Fact]
    public void Enumerate_never_throws_and_every_adapter_has_a_name_and_a_distinct_luid()
    {
        var adapters = GpuAdapters.Enumerate();

        Assert.All(adapters, a => Assert.False(string.IsNullOrWhiteSpace(a.Name)));
        Assert.Equal(adapters.Count, adapters.Select(a => a.Luid).Distinct().Count());
    }
}
