using System.Diagnostics;
using PcMieru.Core.Collection;
using PcMieru.Core.Hardware.Collectors;
using PcMieru.Core.Hardware.Model;
using PcMieru.Core.Hardware.Native;
using PcMieru.Core.Hardware.Sensors;
using PcMieru.Core.Tests.Hardware;
using CollectionMerge = PcMieru.Core.Collection.GpuSensorMerge;

namespace PcMieru.Core.Tests.Collection;

/// <summary>
/// The CPU package power as a value (DESIGN-B 6): watts when the Energy Meter gave a package reading, otherwise missing with the reason.
/// The byte-level rules (milliwatts to watts, the <c>_PKG</c> instances only) are in <see cref="PdhReadRuleTests"/>; this is the step after.
/// </summary>
public sealed class PackagePowerValueTests
{
    [Fact]
    public void A_valid_reading_is_the_watts_with_the_source_named()
    {
        var v = PdhSystemSampler.PackagePowerValue(new PackagePowerReading(PackagePowerState.Valid, 65.5), primed: true);

        Assert.True(v.IsOk);
        Assert.Equal(65.5, v.Data);
        Assert.Equal("PDH Energy Meter", v.Source);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_Windows_with_no_Energy_Meter_counter_is_unsupported_with_the_reason_and_never_zero_watts(bool primed)
    {
        var v = PdhSystemSampler.PackagePowerValue(new PackagePowerReading(PackagePowerState.NoCounter, 0), primed);

        Assert.False(v.IsOk);
        Assert.Equal(ValueStatus.Unsupported, v.Status);
        Assert.Equal(0.0, v.Data);
        Assert.Contains("Energy Meter", v.Detail);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_meter_with_no_package_instance_is_unsupported_with_the_reason_and_never_zero_watts(bool primed)
    {
        var v = PdhSystemSampler.PackagePowerValue(new PackagePowerReading(PackagePowerState.NoPackageInstance, 0), primed);

        Assert.False(v.IsOk);
        Assert.Equal(ValueStatus.Unsupported, v.Status);
        Assert.Contains("_PKG", v.Detail);
    }

    [Fact]
    public void A_sample_that_did_not_come_is_not_collected_and_the_first_one_says_it_has_no_baseline_yet()
    {
        var later = PdhSystemSampler.PackagePowerValue(new PackagePowerReading(PackagePowerState.NoValidSample, 0), primed: true);
        var first = PdhSystemSampler.PackagePowerValue(new PackagePowerReading(PackagePowerState.NoValidSample, 0), primed: false);

        Assert.Equal(ValueStatus.NotCollected, later.Status);
        Assert.Equal(ValueStatus.NotCollected, first.Status);
        Assert.False(later.IsOk);
        Assert.False(first.IsOk);
        Assert.Contains("not usable", later.Detail);
        Assert.Contains("no baseline", first.Detail);
    }

    [Fact]
    public void No_missing_state_is_ever_an_ok_value()
    {
        foreach (var state in Enum.GetValues<PackagePowerState>().Where(static s => s != PackagePowerState.Valid))
        {
            foreach (var primed in new[] { true, false })
            {
                var v = PdhSystemSampler.PackagePowerValue(new PackagePowerReading(state, 123.0), primed);

                Assert.False(v.IsOk, $"{state} (primed {primed}) must not be a reading");
                Assert.False(string.IsNullOrWhiteSpace(v.Detail), $"{state} (primed {primed}) must say why");
            }
        }
    }

    [Fact]
    public void Milliwatts_of_the_package_instance_reach_the_value_as_watts()
    {
        var bytes = PdhReadRuleTests.Build(
            PdhReadRuleTests.Valid("RAPL_Package0_CORE", 20_000),
            PdhReadRuleTests.Valid("RAPL_Package0_PKG", 65_500));

        var v = PdhSystemSampler.PackagePowerValue(PdhReadRule.PackagePower(bytes, PdhReadRuleTests.Base, 2), primed: true);

        Assert.True(v.IsOk);
        Assert.Equal(65.5, v.Data, 9);
    }

    [Fact]
    public void An_instance_list_with_no_package_is_unsupported_through_the_whole_path()
    {
        var bytes = PdhReadRuleTests.Build(PdhReadRuleTests.Valid("RAPL_Package0_CORE", 20_000));

        var v = PdhSystemSampler.PackagePowerValue(PdhReadRule.PackagePower(bytes, PdhReadRuleTests.Base, 1), primed: true);

        Assert.Equal(ValueStatus.Unsupported, v.Status);
        Assert.False(v.IsOk);
    }
}

/// <summary>The NVML clocks event reason bits: the names are the header's, and a bit with no name is called unknown, never explained.</summary>
public sealed class ClocksEventReasonsTests
{
    [Theory]
    [InlineData(0x1UL, "gpuIdle")]
    [InlineData(0x2UL, "applicationsClocksSetting")]
    [InlineData(0x4UL, "swPowerCap")]
    [InlineData(0x8UL, "hwSlowdown")]
    [InlineData(0x10UL, "syncBoost")]
    [InlineData(0x20UL, "swThermalSlowdown")]
    [InlineData(0x40UL, "hwThermalSlowdown")]
    [InlineData(0x80UL, "hwPowerBrakeSlowdown")]
    [InlineData(0x100UL, "displayClockSetting")]
    public void Each_documented_bit_has_its_own_stable_id(ulong bit, string id)
    {
        Assert.Equal([id], ClocksEventReasons.Ids(bit));
        Assert.False(ClocksEventReasons.HasUnknown(bit));
    }

    [Fact]
    public void Nothing_set_is_no_reason_at_all_and_not_unknown()
    {
        Assert.Empty(ClocksEventReasons.Ids(0));
        Assert.False(ClocksEventReasons.HasUnknown(0));
    }

    [Fact]
    public void Several_bits_come_out_lowest_bit_first()
    {
        Assert.Equal(["swPowerCap", "swThermalSlowdown"], ClocksEventReasons.Ids(ClocksEventReasons.SwThermalSlowdown | ClocksEventReasons.SwPowerCap));
        Assert.Equal(9, ClocksEventReasons.Ids(ClocksEventReasons.Known).Count);
    }

    [Theory]
    [InlineData(0x200UL)]
    [InlineData(0x8000_0000_0000_0000UL)]
    [InlineData(ulong.MaxValue)]
    public void A_bit_nobody_named_is_unknown_and_is_not_given_a_name(ulong mask)
    {
        Assert.True(ClocksEventReasons.HasUnknown(mask));
        Assert.DoesNotContain(string.Empty, ClocksEventReasons.Ids(mask));
        Assert.All(ClocksEventReasons.Ids(mask), static id => Assert.False(string.IsNullOrWhiteSpace(id)));
    }

    [Fact]
    public void Only_an_unknown_bit_names_nothing_and_a_known_bit_next_to_it_is_still_named()
    {
        Assert.Empty(ClocksEventReasons.Ids(0x200));
        Assert.Equal(["swPowerCap"], ClocksEventReasons.Ids(0x204));
        Assert.True(ClocksEventReasons.HasUnknown(0x204));
    }
}

/// <summary>The mask as a value: the result code decides, never the bits (0 is a real answer, "nothing holds the clocks back").</summary>
public sealed class LimitReasonMaskTests
{
    [Fact]
    public void A_zero_mask_that_was_read_is_ok_and_means_no_limit()
    {
        var v = SensorMapping.Mask(VendorApi.Nvml, 0, 0, "nvml:clocksEventReasons");

        Assert.True(v.IsOk);
        Assert.Equal(0UL, v.Data);
        Assert.Equal("nvml:clocksEventReasons", v.Source);
    }

    [Fact]
    public void A_mask_that_was_read_keeps_every_bit()
    {
        var v = SensorMapping.Mask(VendorApi.Nvml, 0x4 | 0x200, 0, "nvml");

        Assert.True(v.IsOk);
        Assert.Equal(0x204UL, v.Data);
    }

    [Theory]
    [InlineData(3, ValueStatus.Unsupported)]
    [InlineData(SensorMapping.FunctionMissing, ValueStatus.Unsupported)]
    [InlineData(4, ValueStatus.Denied)]
    [InlineData(10, ValueStatus.Timeout)]
    [InlineData(15, ValueStatus.Gone)]
    [InlineData(999, ValueStatus.Error)]
    public void A_failed_read_is_missing_with_the_reason_and_carries_no_bits(int code, ValueStatus expected)
    {
        // NVML may leave stale bits in the out-parameter; they must not reach the value.
        var v = SensorMapping.Mask(VendorApi.Nvml, 0x4, code, "nvml");

        Assert.False(v.IsOk);
        Assert.Equal(expected, v.Status);
        Assert.Equal(0UL, v.Data);
        Assert.False(string.IsNullOrWhiteSpace(v.Detail));
    }

    [Fact]
    public void A_driver_without_the_function_says_so()
    {
        var v = SensorMapping.Mask(VendorApi.Nvml, 0, SensorMapping.FunctionMissing, "nvml");

        Assert.Equal("this driver does not provide the function", v.Detail);
    }

    [Fact]
    public void A_missing_limit_reason_is_not_worked_out_from_a_hot_or_power_capped_gpu()
    {
        // A very hot GPU at its power limit, and the driver gives no reasons: the reasons stay missing. Nothing is inferred.
        var hot = SensorFixtures.Reading("luid_a", temp: 99, extras: SensorFixtures.Extras(reasons: null, slowdownC: 88)) with
        {
            PowerWatts = Value.Ok(300.0, "NVML"),
        };

        var merged = Assert.Single(CollectionMerge.Apply([Gpu("luid_a")], SensorFixtures.Readings(hot)));

        Assert.Equal(99, merged.TemperatureC.Data);
        Assert.Equal(ValueStatus.Unsupported, merged.LimitReasons.Status);
        Assert.False(merged.LimitReasons.IsOk);
        Assert.Equal(0UL, merged.LimitReasons.Data);
    }

    private static GpuInfo Gpu(string id) => new(
        id,
        "NVIDIA GeForce RTX 5070",
        Value.Ok(10.0, "PDH"),
        [],
        Value.Ok(1L, "PDH"),
        Value.Ok(2L, "DXGI"),
        Value.Ok(3L, "PDH"),
        Value.NotCollected<double>("SensorHost"),
        Value.NotCollected<double>("SensorHost"),
        Value.NotCollected<double>("SensorHost"),
        Value.NotCollected<double>("SensorHost"));
}

/// <summary>After the GPU merge every adapter still knows which SensorHost frame its vendor values are from (DESIGN-B 6).</summary>
public sealed class GpuProvenanceTests
{
    private static readonly SourceOrigin Frame = new(new FrameId(4, 77), 9_000_000, 1000);

    private static GpuInfo Gpu(string id, string name = "NVIDIA GeForce RTX 5070") => new(
        id,
        name,
        Value.Ok(10.0, "PDH GPU Engine"),
        [],
        Value.Ok(1L, "PDH GPU Adapter Memory"),
        Value.Ok(2L, "DXGI"),
        Value.Ok(3L, "PDH GPU Adapter Memory"),
        Value.NotCollected<double>("SensorHost"),
        Value.NotCollected<double>("SensorHost"),
        Value.NotCollected<double>("SensorHost"),
        Value.NotCollected<double>("SensorHost"));

    [Fact]
    public void A_matched_adapter_keeps_the_frame_its_values_came_from_and_its_limit_reasons()
    {
        var reading = SensorFixtures.Reading("luid_a", extras: SensorFixtures.Extras(reasons: 0x4));

        var merged = Assert.Single(CollectionMerge.Apply([Gpu("luid_a")], SensorFixtures.Readings(reading), Frame));

        Assert.Same(Frame, merged.VendorOrigin);
        Assert.Equal(new FrameId(4, 77), merged.VendorOrigin!.Frame);
        Assert.Equal(1000, merged.VendorOrigin.PeriodMs);
        Assert.True(merged.LimitReasons.IsOk);
        Assert.Equal(0x4UL, merged.LimitReasons.Data);
    }

    [Fact]
    public void Every_adapter_keeps_the_origin_also_the_one_the_frame_had_no_reading_for()
    {
        var reading = SensorFixtures.Reading("luid_a", extras: SensorFixtures.Extras(reasons: 0));

        var merged = CollectionMerge.Apply([Gpu("luid_a"), Gpu("luid_b", "AMD Radeon(TM) Graphics")], SensorFixtures.Readings(reading), Frame);

        Assert.Equal(2, merged.Count);
        Assert.All(merged, g => Assert.Same(Frame, g.VendorOrigin));
        Assert.True(merged[0].LimitReasons.IsOk);
        Assert.Equal(ValueStatus.Unsupported, merged[1].LimitReasons.Status);
    }

    [Fact]
    public void A_failed_answer_keeps_the_gap_it_was_given_next_to_the_reason()
    {
        var gap = SourceOrigin.Missing(SourceGap.HostRestart, 1000);
        var answer = Value.Missing<IReadOnlyList<GpuSensorReading>>(ValueStatus.Timeout, "SensorHost", "restarting");

        var merged = Assert.Single(CollectionMerge.Apply([Gpu("luid_a")], answer, gap));

        Assert.Same(gap, merged.VendorOrigin);
        Assert.Null(merged.VendorOrigin!.Frame);
        Assert.Equal(SourceGap.HostRestart, merged.VendorOrigin.Gap);
        Assert.Equal(ValueStatus.Timeout, merged.LimitReasons.Status);
        Assert.Equal("restarting", merged.LimitReasons.Detail);
    }

    [Fact]
    public void A_two_second_frame_is_passed_as_a_two_second_frame()
    {
        var slow = new SourceOrigin(new FrameId(4, 5), 3_000_000, 2000);

        var merged = Assert.Single(CollectionMerge.Apply([Gpu("luid_a")], SensorFixtures.Readings(SensorFixtures.Reading("luid_a")), slow));

        Assert.Equal(2000, merged.VendorOrigin!.PeriodMs);
    }

    [Fact]
    public void A_reading_without_extras_leaves_the_limit_reasons_not_collected_and_still_has_its_origin()
    {
        var merged = Assert.Single(CollectionMerge.Apply([Gpu("luid_a")], SensorFixtures.Readings(SensorFixtures.Reading("luid_a")), Frame));

        Assert.Equal(ValueStatus.NotCollected, merged.LimitReasons.Status);
        Assert.Same(Frame, merged.VendorOrigin);
    }

    [Fact]
    public void Without_an_origin_the_adapter_has_none_and_the_old_call_is_unchanged()
    {
        var merged = Assert.Single(CollectionMerge.Apply([Gpu("luid_a")], SensorFixtures.Readings(SensorFixtures.Reading("luid_a"))));

        Assert.Null(merged.VendorOrigin);
    }

    [Fact]
    public void Two_merges_with_two_frames_do_not_share_an_origin()
    {
        var other = new SourceOrigin(new FrameId(4, 78), 10_000_000, 1000);
        var input = new[] { Gpu("luid_a") };
        var readings = SensorFixtures.Readings(SensorFixtures.Reading("luid_a"));

        var first = Assert.Single(CollectionMerge.Apply(input, readings, Frame));
        var second = Assert.Single(CollectionMerge.Apply(input, readings, other));

        Assert.Equal(77, first.VendorOrigin!.Frame!.Value.Sequence);
        Assert.Equal(78, second.VendorOrigin!.Frame!.Value.Sequence);
        Assert.Null(input[0].VendorOrigin);
    }
}

/// <summary>
/// The GPU sensor cache next to the display's needs: it keeps the frame an answer is from (for the recorder) while its display behaviour
/// (never blocks, MaxAge, two-second refresh) stays as it was.
/// </summary>
public sealed class GpuSensorCacheProvenanceTests
{
    private sealed class FramedFake(Func<GpuSensorView> view) : IFramedSensorClient
    {
        private int _requested = 2000;
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public int RequestedPeriodMs
        {
            get => Volatile.Read(ref _requested);
            set => Volatile.Write(ref _requested, value);
        }

        public bool IsHostRestarting { get; set; }

        public Task<Value<IReadOnlyList<GpuSensorReading>>> ReadGpuAsync(CancellationToken ct = default) => Task.FromResult(view().Gpus);

        public Task<GpuSensorView> ReadGpuFramedAsync(CancellationToken ct = default)
        {
            _ = Interlocked.Increment(ref _calls);
            return Task.FromResult(view());
        }

        public void Dispose()
        {
        }
    }

    private sealed class PlainFake(Func<Value<IReadOnlyList<GpuSensorReading>>> answer) : ISensorClient
    {
        public Task<Value<IReadOnlyList<GpuSensorReading>>> ReadGpuAsync(CancellationToken ct = default) => Task.FromResult(answer());

        public void Dispose()
        {
        }
    }

    private static GpuSensorView Ok(SourceOrigin origin) =>
        new(SensorFixtures.Readings(SensorFixtures.Reading("luid_a", temp: 61)), origin);

    private static async Task<GpuSensorView> SettledAsync(GpuSensorCache cache)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            var now = cache.CurrentView();
            if (now.Gpus.Status != ValueStatus.NotCollected || deadline.ElapsedMilliseconds > 10_000)
            {
                return now;
            }

            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task The_frame_a_reading_is_from_is_kept_next_to_it()
    {
        var origin = new SourceOrigin(new FrameId(3, 7), 123_456, 1000);
        using var client = new FramedFake(() => Ok(origin));
        var cache = new GpuSensorCache(() => client);

        var view = await SettledAsync(cache);

        Assert.True(view.Gpus.IsOk);
        Assert.Equal(61, Assert.Single(view.Gpus.Data!).TemperatureC.Data);
        Assert.Equal(origin, view.Origin);
        Assert.True(cache.Current().IsOk);
    }

    [Fact]
    public async Task A_two_second_frame_stays_a_two_second_frame_after_the_client_asks_for_one_second()
    {
        var origin = new SourceOrigin(new FrameId(3, 8), 200_000, 2000);
        using var client = new FramedFake(() => Ok(origin)) { RequestedPeriodMs = 1000 };
        var cache = new GpuSensorCache(() => client);

        var view = await SettledAsync(cache);

        Assert.Equal(2000, view.Origin.PeriodMs);
        Assert.Equal(new FrameId(3, 8), view.Origin.Frame);
    }

    [Theory]
    [InlineData(SourceGap.HostRestart)]
    [InlineData(SourceGap.ReadFailed)]
    public async Task A_missing_answer_keeps_the_gap_the_client_gave(SourceGap gap)
    {
        var missing = Value.Missing<IReadOnlyList<GpuSensorReading>>(ValueStatus.Timeout, "SensorHost", "no frame");
        using var client = new FramedFake(() => new GpuSensorView(missing, SourceOrigin.Missing(gap, 2000)));
        var cache = new GpuSensorCache(() => client);

        var view = await SettledAsync(cache);

        Assert.Equal(ValueStatus.Timeout, view.Gpus.Status);
        Assert.Null(view.Origin.Frame);
        Assert.Equal(gap, view.Origin.Gap);
    }

    [Theory]
    [InlineData(true, SourceGap.HostRestart)]
    [InlineData(false, SourceGap.ReadFailed)]
    public async Task A_reading_that_is_too_old_is_a_host_restart_when_the_client_knows_it_is_restarting_and_a_failed_read_otherwise(bool restarting, SourceGap expected)
    {
        var clock = new[] { Stopwatch.GetTimestamp() };
        using var client = new FramedFake(() => Ok(new SourceOrigin(new FrameId(3, 7), 1, 1000))) { IsHostRestarting = restarting };
        var cache = new GpuSensorCache(() => client, () => Volatile.Read(ref clock[0]));
        Assert.True((await SettledAsync(cache)).Gpus.IsOk);

        Volatile.Write(ref clock[0], clock[0] + (11 * Stopwatch.Frequency)); // past MaxAge (ten seconds) on the cache's own clock
        var stale = cache.CurrentView(); // the refresh this starts cannot publish before this call has returned

        Assert.False(stale.Gpus.IsOk);
        Assert.Null(stale.Origin.Frame);
        Assert.Equal(expected, stale.Origin.Gap);
    }

    [Fact]
    public async Task No_client_is_the_gap_unsupported()
    {
        var cache = new GpuSensorCache(static () => null);

        var view = await SettledAsync(cache);

        Assert.Equal(ValueStatus.Unsupported, view.Gpus.Status);
        Assert.Equal(SourceGap.Unsupported, view.Origin.Gap);
    }

    [Fact]
    public async Task A_client_that_throws_is_the_gap_read_failed()
    {
        using var client = new FramedFake(static () => throw new InvalidOperationException("pipe closed"));
        var cache = new GpuSensorCache(() => client);

        var view = await SettledAsync(cache);

        Assert.Equal(ValueStatus.Error, view.Gpus.Status);
        Assert.Equal(SourceGap.ReadFailed, view.Origin.Gap);
    }

    [Fact]
    public void Before_the_first_answer_the_gap_is_not_started()
    {
        using var gate = new ManualResetEventSlim();
        using var client = new PlainFake(() =>
        {
            _ = gate.Wait(TimeSpan.FromSeconds(10));
            return SensorFixtures.Readings();
        });
        var cache = new GpuSensorCache(() => client);

        var first = cache.CurrentView();
        gate.Set();

        Assert.Equal(ValueStatus.NotCollected, first.Gpus.Status);
        Assert.Equal(SourceGap.NotStarted, first.Origin.Gap);
    }

    [Fact]
    public async Task A_client_that_does_not_number_its_frames_is_stamped_as_generation_zero_at_two_seconds()
    {
        using var client = new PlainFake(static () => SensorFixtures.Readings(SensorFixtures.Reading("luid_a")));
        var cache = new GpuSensorCache(() => client);

        var view = await SettledAsync(cache);

        Assert.Equal(new FrameId(0, 1), view.Origin.Frame);
        Assert.Equal(2000, view.Origin.PeriodMs);
        Assert.True(view.Origin.AcquiredUs > 0);
    }

    [Fact]
    public async Task Clear_forgets_the_frame_as_well_as_the_reading()
    {
        using var client = new FramedFake(() => Ok(new SourceOrigin(new FrameId(3, 7), 1, 1000)));
        var cache = new GpuSensorCache(() => client);
        Assert.True((await SettledAsync(cache)).Gpus.IsOk);

        cache.Clear();
        var after = cache.CurrentView();

        Assert.Equal(ValueStatus.NotCollected, after.Gpus.Status);
        Assert.Null(after.Origin.Frame);
        Assert.Equal(SourceGap.NotStarted, after.Origin.Gap);
    }

    [Fact]
    public void The_refresh_is_every_two_seconds_as_before_and_just_under_one_second_while_one_second_frames_are_asked_for()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), GpuSensorCache.RefreshIntervalFor(2000));
        Assert.Equal(GpuSensorCache.RefreshEvery, GpuSensorCache.RefreshIntervalFor(2000));
        Assert.True(GpuSensorCache.RefreshIntervalFor(1000) < TimeSpan.FromSeconds(1));
        Assert.True(GpuSensorCache.RefreshIntervalFor(1000) >= TimeSpan.FromMilliseconds(500));
        Assert.Equal(GpuSensorCache.RefreshIntervalFor(1000), GpuSensorCache.RefreshIntervalFor(500));
    }

    [Fact]
    public void The_display_freshness_limit_is_still_ten_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), GpuSensorCache.MaxAge);
    }
}

/// <summary>AC connected and battery from the existing battery reader's rules (DESIGN-B 6): a missing value says why and is never "AC off" or 0 %.</summary>
public sealed class PowerInfoTests
{
    private static RawPowerStatus Power(byte ac = 1, byte flag = 0, byte percent = 80, uint life = uint.MaxValue) => new(ac, flag, percent, life);

    [Fact]
    public void A_desktop_on_the_wall_says_ac_connected_and_that_it_has_no_battery()
    {
        var p = BatteryBuilder.BuildPower(Power(ac: 1, flag: 128, percent: 255), null);

        Assert.True(p.AcConnected.IsOk);
        Assert.True(p.AcConnected.Data);
        Assert.Equal(ValueStatus.Unsupported, p.BatteryPercent.Status);
        Assert.Equal(ValueStatus.Unsupported, p.BatteryState.Status);
        Assert.Contains("no battery", p.BatteryPercent.Detail);
        Assert.False(p.BatteryPercent.IsOk);
    }

    [Fact]
    public void A_laptop_on_battery_says_ac_off_and_its_charge()
    {
        var p = BatteryBuilder.BuildPower(Power(ac: 0, flag: 1, percent: 63), null);

        Assert.True(p.AcConnected.IsOk);
        Assert.False(p.AcConnected.Data);
        Assert.Equal(63, p.BatteryPercent.Data);
        Assert.Equal("on battery", p.BatteryState.Data);
    }

    [Fact]
    public void A_laptop_on_the_charger_says_charging_when_windows_does_and_plugged_in_otherwise()
    {
        var charging = BatteryBuilder.BuildPower(Power(ac: 1, flag: 8 | 1, percent: 40), null);
        var full = BatteryBuilder.BuildPower(Power(ac: 1, flag: 1, percent: 100), null);

        Assert.Equal("charging", charging.BatteryState.Data);
        Assert.Equal("plugged in", full.BatteryState.Data);
        Assert.True(charging.AcConnected.Data);
    }

    [Fact]
    public void An_unknown_power_source_is_unsupported_and_never_ac_off()
    {
        var p = BatteryBuilder.BuildPower(Power(ac: 255, flag: 128, percent: 255), null);

        Assert.Equal(ValueStatus.Unsupported, p.AcConnected.Status);
        Assert.False(p.AcConnected.IsOk);
        Assert.Contains("not reported", p.AcConnected.Detail);
    }

    [Fact]
    public void An_unknown_charge_is_unsupported_and_never_zero_percent()
    {
        var p = BatteryBuilder.BuildPower(Power(ac: 0, flag: 1, percent: 255), null);

        Assert.Equal(ValueStatus.Unsupported, p.BatteryPercent.Status);
        Assert.Equal(0, p.BatteryPercent.Data);
        Assert.False(p.BatteryPercent.IsOk);
    }

    [Fact]
    public void A_failed_call_is_a_failure_with_the_reason_on_every_value_and_never_ac_off()
    {
        var p = BatteryBuilder.BuildPower(null, "GetSystemPowerStatus failed (Win32 error 5)");

        foreach (var detail in new[] { p.AcConnected.Detail, p.BatteryPercent.Detail, p.BatteryState.Detail })
        {
            Assert.Equal("GetSystemPowerStatus failed (Win32 error 5)", detail);
        }

        Assert.Equal(ValueStatus.Error, p.AcConnected.Status);
        Assert.False(p.AcConnected.IsOk);
    }

    [Theory]
    [InlineData(0, 1, 63)]
    [InlineData(1, 1, 100)]
    [InlineData(1, 9, 40)]
    [InlineData(255, 1, 50)]
    [InlineData(1, 1, 255)]
    public void The_sampler_and_the_hardware_page_read_the_same_power_status_the_same_way(int ac, int flag, int percent)
    {
        var raw = Power((byte)ac, (byte)flag, (byte)percent);
        var page = BatteryBuilder.Build(raw, [new RawBattery(BatteryNative.CapSystemBattery, "LION", 50_000, 45_000, 100, 4, 12_300)]).Data!;

        var sample = BatteryBuilder.BuildPower(raw, null);

        Assert.Equal(page.OnAc.Status, sample.AcConnected.Status);
        Assert.Equal(page.OnAc.Data, sample.AcConnected.Data);
        Assert.Equal(page.OnAc.Source, sample.AcConnected.Source);
        Assert.Equal(page.ChargePercent.Status, sample.BatteryPercent.Status);
        Assert.Equal(page.ChargePercent.Data, sample.BatteryPercent.Data);
    }
}

/// <summary>One snapshot as the recorder's DTO: a pure mapping, with and without the stage timestamps the real source takes.</summary>
public sealed class RecordingSampleBuilderTests
{
    private static GpuInfo Gpu(string id, SourceOrigin? origin, ulong? reasons = 4) => new(
        id,
        "NVIDIA GeForce RTX 5070",
        Value.Ok(30.0, "PDH GPU Engine"),
        [],
        Value.Ok(1_000L, "PDH GPU Adapter Memory"),
        Value.Ok(2_000L, "DXGI"),
        Value.Ok(0L, "PDH GPU Adapter Memory"),
        Value.Ok(61.0, "NVML"),
        Value.Ok(120.0, "NVML"),
        Value.Unsupported<double>("NVML", "no fan"),
        Value.Ok(2500.0, "NVML"))
    {
        TemperatureLimitC = Value.Ok(88.0, "NVML"),
        LimitReasons = reasons is { } r ? Value.Ok(r, "NVML") : Value.Unsupported<ulong>("NVML", "not supported by this GPU"),
        VendorOrigin = origin,
    };

    private static MemoryInfo Memory() => new(
        32_000_000_000,
        16_000_000_000,
        Value.Ok(1L, "t"),
        Value.Ok(2L, "t"),
        Value.Ok(3L, "t"),
        Value.Ok(4L, "t"),
        Value.Ok(5L, "t"));

    private static Snapshot Snap(IReadOnlyList<GpuInfo> gpus, SnapshotProvenance? provenance, Value<double>? power = null)
    {
        var taken = new DateTime(2026, 11, 22, 9, 0, 0, DateTimeKind.Utc);
        var cpu = new CpuInfo(32, Value.Ok(12.5, "t"), [], Value.Ok(4500.0, "t"))
        {
            PackagePowerWatts = power ?? Value.Ok(88.25, "PDH Energy Meter"),
        };
        var system = new SystemInfo(cpu, Memory(), [], [], gpus, 100)
        {
            Power = new PowerInfo(Value.Ok(true, "k"), Value.Unsupported<int>("k", "no battery in this PC"), Value.Unsupported<string>("k", "no battery in this PC")),
        };
        return new Snapshot(Snapshot.CurrentSchemaVersion, taken, taken.AddSeconds(-1), 1000, system, []) { Provenance = provenance };
    }

    [Fact]
    public void The_stage_times_become_monotonic_microseconds_and_the_pass_is_numbered_in_generation_zero()
    {
        var f = Stopwatch.Frequency;
        var snapshot = Snap([], new SnapshotProvenance(f * 10, f * 11, f * 12, f * 13, f * 14));

        var s = RecordingSampleBuilder.Build(snapshot, 42, 1000);

        Assert.Equal(new FrameId(0, 42), s.Core.Frame);
        Assert.Equal(10_000_000, s.Core.AcquiredUs);
        Assert.Equal(1000, s.Core.PeriodMs);
        Assert.Equal(SourceGap.None, s.Core.Gap);
        Assert.Equal(new CoreStageTimes(11_000_000, 12_000_000, 13_000_000, 14_000_000), s.Stages);
    }

    [Fact]
    public void A_stage_that_gave_nothing_is_zero_and_not_a_time()
    {
        var f = Stopwatch.Frequency;

        var s = RecordingSampleBuilder.Build(Snap([], new SnapshotProvenance(f * 10, f * 11, f * 12, 0, 0)), 1, 1000);

        Assert.Equal(0, s.Stages.PdhUs);
        Assert.Equal(0, s.Stages.GpuCounterUs);
        Assert.Equal(11_000_000, s.Stages.CpuUs);
    }

    [Fact]
    public void A_source_that_does_not_time_its_stages_gets_no_stage_times_and_a_handover_time_instead()
    {
        var s = RecordingSampleBuilder.Build(Snap([], null), 5, 1000);

        Assert.Equal(new CoreStageTimes(0, 0, 0, 0), s.Stages);
        Assert.True(s.Core.AcquiredUs > 0);
        Assert.Equal(new FrameId(0, 5), s.Core.Frame);
    }

    [Fact]
    public void The_values_come_across_as_they_are()
    {
        var snapshot = Snap([], null);

        var s = RecordingSampleBuilder.Build(snapshot, 1, 1000);

        Assert.Equal(snapshot.TakenAtUtc, s.TakenAtUtc);
        Assert.Equal(1000, s.IntervalMs);
        Assert.Equal(12.5, s.CpuUsagePercent.Data);
        Assert.Equal(4500.0, s.CpuMhz.Data);
        Assert.Equal(88.25, s.CpuPackageWatts.Data);
        Assert.Same(snapshot.System.Memory, s.Memory);
        Assert.Same(snapshot.System.Power, s.Power);
        Assert.True(s.Power.AcConnected.Data);
    }

    [Fact]
    public void A_missing_cpu_power_stays_missing_with_its_reason()
    {
        var missing = Value.Unsupported<double>("PDH Energy Meter", "this Windows has no Energy Meter counters");

        var s = RecordingSampleBuilder.Build(Snap([], null, missing), 1, 1000);

        Assert.Equal(ValueStatus.Unsupported, s.CpuPackageWatts.Status);
        Assert.False(s.CpuPackageWatts.IsOk);
        Assert.Contains("Energy Meter", s.CpuPackageWatts.Detail);
    }

    [Fact]
    public void Each_adapter_carries_its_vendor_values_limit_reasons_and_origin()
    {
        var origin = new SourceOrigin(new FrameId(2, 9), 8_000_000, 1000);

        var s = RecordingSampleBuilder.Build(Snap([Gpu("luid_a", origin), Gpu("luid_b", origin, reasons: null)], null), 1, 1000);

        Assert.Equal(2, s.Gpus.Count);
        var a = s.Gpus[0];
        Assert.Equal("luid_a", a.Id);
        Assert.Equal(30.0, a.UtilizationPercent.Data);
        Assert.Equal(61.0, a.TemperatureC.Data);
        Assert.Equal(120.0, a.PowerWatts.Data);
        Assert.Equal(88.0, a.TemperatureLimitC.Data);
        Assert.Equal(4UL, a.LimitReasons.Data);
        Assert.Same(origin, a.Vendor);
        Assert.Equal(ValueStatus.Unsupported, s.Gpus[1].LimitReasons.Status);
    }

    [Fact]
    public void An_adapter_with_no_vendor_origin_is_marked_not_started_never_given_a_frame()
    {
        var s = RecordingSampleBuilder.Build(Snap([Gpu("luid_a", null)], null), 1, 1000);

        var vendor = Assert.Single(s.Gpus).Vendor;
        Assert.Null(vendor.Frame);
        Assert.Equal(SourceGap.NotStarted, vendor.Gap);
    }

    [Fact]
    public void A_GPU_stage_that_timed_out_is_stamped_as_a_failed_read_with_the_period_and_no_frame()
    {
        var known = new[] { Gpu("luid_a", new SourceOrigin(new FrameId(2, 9), 8_000_000, 1000)) };

        var timedOut = GpuSampler.TimedOut(known, ValueStatus.Timeout, "GPU counters: the stage did not finish in time", 1000);
        var s = RecordingSampleBuilder.Build(Snap(timedOut, null), 1, 1000);

        var gpu = Assert.Single(s.Gpus);
        Assert.Equal(ValueStatus.Timeout, gpu.TemperatureC.Status);
        Assert.Null(gpu.Vendor.Frame); // the frame the last good tick had is not claimed for this one
        Assert.Equal(SourceGap.ReadFailed, gpu.Vendor.Gap); // not NotStarted: the stage did start, it did not answer
        Assert.Equal(1000, gpu.Vendor.PeriodMs);
    }

    [Fact]
    public void The_sample_does_not_change_when_the_snapshots_list_does()
    {
        var list = new List<GpuInfo> { Gpu("luid_a", null) };
        var snapshot = Snap(list, null);

        var s = RecordingSampleBuilder.Build(snapshot, 1, 1000);
        list.Add(Gpu("luid_b", null));

        Assert.Single(s.Gpus);
        Assert.True(((IList<RecordedGpu>)s.Gpus).IsReadOnly);
    }
}

/// <summary>
/// The SensorHost child's cadence control without a child: a request is only recorded when it arrives and is applied at the next sampling
/// boundary, so a frame is never half at one period and the frame says the period it was taken at.
/// </summary>
public sealed class SensorCadenceTests
{
    [Fact]
    public void It_starts_at_the_period_the_child_was_started_with()
    {
        Assert.Equal(2000, new SensorCadence(2000).PeriodMs);
        Assert.Equal(1000, new SensorCadence(1000).PeriodMs);
    }

    [Fact]
    public void A_request_changes_nothing_until_the_next_boundary()
    {
        var cadence = new SensorCadence(2000);

        Assert.True(cadence.TryRequest("1000"));

        Assert.Equal(2000, cadence.PeriodMs);
        Assert.Equal(1000, cadence.ApplyPending());
        Assert.Equal(1000, cadence.PeriodMs);
    }

    [Fact]
    public void A_fake_child_takes_each_frame_at_the_period_of_its_own_boundary()
    {
        var cadence = new SensorCadence(2000);
        var frames = new List<int>();

        // The boundary is where the child reads its period; a request lands between two boundaries, from the reader thread.
        frames.Add(cadence.ApplyPending());
        _ = cadence.TryRequest("1000");
        frames.Add(cadence.ApplyPending());
        frames.Add(cadence.ApplyPending());
        _ = cadence.TryRequest("2000");
        frames.Add(cadence.ApplyPending());

        Assert.Equal([2000, 1000, 1000, 2000], frames);
    }

    [Fact]
    public void The_last_request_before_a_boundary_wins()
    {
        var cadence = new SensorCadence(2000);

        _ = cadence.TryRequest("1000");
        _ = cadence.TryRequest("2000");

        Assert.Equal(2000, cadence.ApplyPending());
    }

    [Fact]
    public void A_boundary_with_nothing_pending_keeps_the_period_and_a_request_is_applied_once()
    {
        var cadence = new SensorCadence(1000);
        _ = cadence.TryRequest("2000");

        Assert.Equal(2000, cadence.ApplyPending());
        Assert.Equal(2000, cadence.ApplyPending());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("1500")]
    [InlineData("0")]
    [InlineData("500")]
    [InlineData("-1000")]
    [InlineData("+1000")]
    [InlineData("1000ms")]
    [InlineData("1e3")]
    [InlineData("10000")]
    public void Anything_but_the_two_exact_values_is_ignored(string? line)
    {
        var cadence = new SensorCadence(2000);

        Assert.False(cadence.TryRequest(line));

        Assert.Equal(2000, cadence.ApplyPending());
    }

    [Theory]
    [InlineData("1000", 1000)]
    [InlineData("2000", 2000)]
    [InlineData(" 1000 ", 1000)]
    [InlineData("1000\r", 1000)]
    public void The_two_values_are_accepted_with_surrounding_white_space(string line, int expected)
    {
        var cadence = new SensorCadence(expected == 1000 ? 2000 : 1000);

        Assert.True(cadence.TryRequest(line));

        Assert.Equal(expected, cadence.ApplyPending());
    }

    [Fact]
    public void The_wire_form_is_the_number_of_milliseconds_and_the_parent_can_only_write_what_the_child_accepts()
    {
        Assert.Equal("1000", SensorCadence.Line(SensorCadence.FastMs));
        Assert.Equal("2000", SensorCadence.Line(SensorCadence.SlowMs));
        Assert.True(SensorCadence.IsAllowed(1000));
        Assert.True(SensorCadence.IsAllowed(2000));
        Assert.False(SensorCadence.IsAllowed(1500));
        Assert.False(SensorCadence.IsAllowed(0));
        Assert.True(new SensorCadence(2000).TryRequest(SensorCadence.Line(1000)));
    }
}
