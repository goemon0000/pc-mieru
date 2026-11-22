using PcMieru.Core;

namespace PcMieru.Gui.Tests;

/// <summary>
/// The detail lines the recording work added to the Performance page: CPU package power, AC power and battery (CPU), the clock limit reasons
/// (GPU). A value Windows or the driver does not give is a dim dash with the reason as its tip, never a made-up number or a guessed reason.
/// </summary>
public class PerfRecordingLinesTests
{
    private const string PdhSource = "PDH Energy Meter";
    private const string PowerSource = "kernel32:GetSystemPowerStatus";

    private static readonly PowerInfo DesktopOnAc = new(
        Value.Ok(true, PowerSource),
        Value.Unsupported<int>(PowerSource, "no battery in this PC"),
        Value.Unsupported<string>(PowerSource, "no battery in this PC"));

    private static async Task<(Snapshot Snapshot, PerfPanel Panel)> CpuAsync(Value<double> watts, PowerInfo power)
    {
        var snapshot = await Harness.FakeSnapshotAsync();
        var system = snapshot.System with { Cpu = snapshot.System.Cpu with { PackagePowerWatts = watts }, Power = power };
        return (snapshot with { System = system }, new PerfPanel(PerfKind.Cpu, string.Empty, "CPU"));
    }

    private static async Task<(Snapshot Snapshot, PerfPanel Panel)> GpuAsync(Func<GpuInfo, GpuInfo> change)
    {
        var snapshot = await Harness.FakeSnapshotAsync();
        var gpus = snapshot.System.Gpus.Select(change).ToArray();
        return (snapshot with { System = snapshot.System with { Gpus = gpus } }, new PerfPanel(PerfKind.Gpu, gpus[0].Id, gpus[0].Name));
    }

    private static StatLine Line(IEnumerable<StatLine> lines, string label) => Assert.Single(lines, l => l.Label == label);

    [Theory]
    [InlineData(Language.En, "CPU package power")]
    [InlineData(Language.Ja, "CPU パッケージ電力")]
    public async Task The_cpu_package_power_is_a_watt_line_with_its_source_as_the_tip(Language language, string caption)
    {
        using var _ = Harness.Locale(language);
        var (snapshot, panel) = await CpuAsync(Value.Ok(65.5, PdhSource), DesktopOnAc);

        var line = Line(PerfMetrics.Stats(panel, snapshot, PerfFacts.Empty), caption);

        Assert.Equal("65.5 W", line.Value);
        Assert.False(line.Dim);
        Assert.Contains(PdhSource, line.Tip, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Language.En, "CPU package power")]
    [InlineData(Language.Ja, "CPU パッケージ電力")]
    public async Task A_cpu_power_this_windows_does_not_give_is_a_dim_dash_with_the_reason_never_zero_watts(Language language, string caption)
    {
        using var _ = Harness.Locale(language);
        var missing = Value.Unsupported<double>(PdhSource, "this Windows has no Energy Meter counters");
        var (snapshot, panel) = await CpuAsync(missing, DesktopOnAc);

        var line = Line(PerfMetrics.Stats(panel, snapshot, PerfFacts.Empty), caption);

        Assert.Equal(Fmt.Dash, line.Value);
        Assert.True(line.Dim);
        Assert.Equal(Fmt.MissingTip(missing), line.Tip);
        Assert.Contains("no Energy Meter counters", line.Tip, StringComparison.Ordinal);
        Assert.DoesNotContain("0.0 W", line.Value, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Language.En, "AC power", "Plugged in", "On battery")]
    [InlineData(Language.Ja, "AC 電源", "接続中", "バッテリー駆動")]
    public async Task The_ac_state_says_plugged_in_or_on_battery_and_a_missing_one_is_a_dim_dash(Language language, string caption, string on, string off)
    {
        using var _ = Harness.Locale(language);
        var cpu = Value.Ok(40.0, PdhSource);

        var (onSnapshot, panel) = await CpuAsync(cpu, DesktopOnAc);
        var onLine = Line(PerfMetrics.Stats(panel, onSnapshot, PerfFacts.Empty), caption);
        Assert.Equal(on, onLine.Value);
        Assert.False(onLine.Dim);

        var (offSnapshot, _) = await CpuAsync(cpu, DesktopOnAc with { AcConnected = Value.Ok(false, PowerSource) });
        Assert.Equal(off, Line(PerfMetrics.Stats(panel, offSnapshot, PerfFacts.Empty), caption).Value);

        var unknown = Value.Unsupported<bool>(PowerSource, "power source not reported");
        var (unknownSnapshot, _) = await CpuAsync(cpu, DesktopOnAc with { AcConnected = unknown });
        var unknownLine = Line(PerfMetrics.Stats(panel, unknownSnapshot, PerfFacts.Empty), caption);
        Assert.Equal(Fmt.Dash, unknownLine.Value);
        Assert.True(unknownLine.Dim);
        Assert.Equal(Fmt.MissingTip(unknown), unknownLine.Tip);
    }

    [Theory]
    [InlineData(Language.En, "Battery", "80 % (charging)", "80 % (on battery)", "80 %", "80 % (state unknown)")]
    [InlineData(Language.Ja, "バッテリー", "80 % (充電中)", "80 % (放電中)", "80 %", "80 % (状態不明)")]
    public async Task A_desktop_has_no_battery_line_and_a_readable_battery_shows_its_charge_and_state(
        Language language,
        string caption,
        string charging,
        string discharging,
        string percentOnly,
        string unknownState)
    {
        using var _ = Harness.Locale(language);
        var cpu = Value.Ok(40.0, PdhSource);
        var (desktop, panel) = await CpuAsync(cpu, DesktopOnAc);
        Assert.DoesNotContain(PerfMetrics.Stats(panel, desktop, PerfFacts.Empty), l => l.Label == caption);

        async Task<string> ShownAsync(Value<string> state)
        {
            var laptop = new PowerInfo(Value.Ok(true, PowerSource), Value.Ok(80, PowerSource), state);
            var (snapshot, _) = await CpuAsync(cpu, laptop);
            return Line(PerfMetrics.Stats(panel, snapshot, PerfFacts.Empty), caption).Value;
        }

        Assert.Equal(charging, await ShownAsync(Value.Ok("charging", PowerSource)));
        Assert.Equal(discharging, await ShownAsync(Value.Ok("on battery", PowerSource)));
        Assert.Equal(percentOnly, await ShownAsync(Value.Unsupported<string>(PowerSource, "charging state not reported")));
        Assert.Equal(unknownState, await ShownAsync(Value.Ok("something new", PowerSource)));
    }

    [Theory]
    [InlineData(Language.En, "Clock limit reasons", 0UL, "None")]
    [InlineData(Language.En, "Clock limit reasons", 0x4UL, "Power cap (software)")]
    [InlineData(Language.En, "Clock limit reasons", 0x24UL, "Power cap (software), Thermal slowdown (software)")]
    [InlineData(Language.En, "Clock limit reasons", 0x200UL, "Other (a reason newer than this app knows)")]
    [InlineData(Language.En, "Clock limit reasons", 0x404UL, "Power cap (software), Other (a reason newer than this app knows)")]
    [InlineData(Language.Ja, "クロックの制限理由", 0UL, "なし")]
    [InlineData(Language.Ja, "クロックの制限理由", 0x4UL, "電力上限（ソフトウェア）")]
    [InlineData(Language.Ja, "クロックの制限理由", 0x24UL, "電力上限（ソフトウェア）, 温度による減速（ソフトウェア）")]
    [InlineData(Language.Ja, "クロックの制限理由", 0x200UL, "その他（ドライバーの新しい理由）")]
    public async Task The_gpu_limit_reasons_are_named_and_zero_means_nothing_holds_the_clocks_back(Language language, string caption, ulong mask, string expected)
    {
        using var _ = Harness.Locale(language);
        var (snapshot, panel) = await GpuAsync(g => g with { LimitReasons = Value.Ok(mask, "nvml:nvmlDeviceGetCurrentClocksEventReasons") });

        var line = Line(PerfMetrics.Stats(panel, snapshot, PerfFacts.Empty), caption);

        Assert.Equal(expected, line.Value);
        Assert.False(line.Dim);
    }

    [Theory]
    [InlineData(Language.En, "Clock limit reasons")]
    [InlineData(Language.Ja, "クロックの制限理由")]
    public async Task An_unknown_limit_reason_is_a_dim_dash_with_the_reason_and_is_never_worked_out_from_a_hot_or_power_capped_gpu(Language language, string caption)
    {
        using var _ = Harness.Locale(language);
        var unsupported = Value.Unsupported<ulong>("nvml:nvmlDeviceGetCurrentClocksEventReasons", "this driver does not provide the function");
        var notAsked = Value.NotCollected<ulong>("none");

        foreach (var reasons in new[] { unsupported, notAsked })
        {
            // Hot and drawing a lot: a guess from these would say "thermal" or "power cap". The line must say nothing instead.
            var (snapshot, panel) = await GpuAsync(g => g with
            {
                TemperatureC = Value.Ok(92.0, "nvml:nvmlDeviceGetTemperature"),
                PowerWatts = Value.Ok(285.0, "nvml:nvmlDeviceGetPowerUsage"),
                LimitReasons = reasons,
            });

            var line = Line(PerfMetrics.Stats(panel, snapshot, PerfFacts.Empty), caption);

            Assert.Equal(Fmt.Dash, line.Value);
            Assert.True(line.Dim);
            Assert.Equal(Fmt.MissingTip(reasons), line.Tip);
        }
    }

    [Theory]
    [InlineData(Language.En)]
    [InlineData(Language.Ja)]
    public void Every_bit_the_app_names_has_a_text_in_both_languages_and_none_shows_a_raw_key(Language language)
    {
        using var _ = Harness.Locale(language);

        for (var bit = 0; bit < 9; bit++)
        {
            var text = PerfMetrics.LimitReasonText(1UL << bit);
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.DoesNotContain("gui.perf", text, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("gui.perf", PerfMetrics.LimitReasonText(ulong.MaxValue), StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_panel_lists_more_lines_than_the_page_has_rows_for()
    {
        using var _ = Harness.Locale(Language.En);
        var snapshot = await Harness.FakeSnapshotAsync();
        var battery = new PowerInfo(Value.Ok(true, PowerSource), Value.Ok(80, PowerSource), Value.Ok("charging", PowerSource));
        snapshot = snapshot with { System = snapshot.System with { Power = battery } };

        Assert.All(PerfMetrics.Panels(snapshot.System), panel =>
            Assert.True(PerfMetrics.Stats(panel, snapshot, PerfFacts.Empty).Count <= PerfMetrics.MaxStats, panel.Kind.ToString()));
    }
}
