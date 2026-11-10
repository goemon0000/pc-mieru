using System.Diagnostics;
using System.Globalization;
using PcMieru.Core.Collection;

namespace PcMieru.Gui.Tests;

/// <summary>
/// Per-process network in the window (SPEC 5.1): the unit setting and how a rate is written, the meter button for each
/// state of the meter, the Network column and the saved columns of an older version.
/// </summary>
public class NetworkUiTests
{
    // ---- the formatter ----

    [Theory]
    [InlineData(0.0, "0 bps")]
    [InlineData(1.0, "8 bps")]
    [InlineData(124.9, "999 bps")]
    [InlineData(125.0, "1.00 kbps")]
    [InlineData(1_000.0, "8.00 kbps")]
    [InlineData(1_249.5, "10.0 kbps")]
    [InlineData(12_495.0, "100 kbps")]
    [InlineData(42_000.0, "336 kbps")]
    [InlineData(124_999.0, "1.00 Mbps")]
    [InlineData(1_234_567.0, "9.88 Mbps")]
    [InlineData(1_250_000.0, "10.0 Mbps")]
    [InlineData(12_500_000.0, "100 Mbps")]
    [InlineData(312_500_000.0, "2.50 Gbps")]
    public void Auto_bits_keep_three_digits_and_roll_up_at_a_thousand(double bytesPerSecond, string expected)
    {
        using var _ = Harness.Locale();
        Assert.Equal(expected, Fmt.NetRate(bytesPerSecond, NetworkUnit.AutoBits));
    }

    [Theory]
    [InlineData(0.0, "0 B/s")]
    [InlineData(1.0, "1 B/s")]
    [InlineData(999.0, "999 B/s")]
    [InlineData(999.4, "999 B/s")]
    [InlineData(1_000.0, "1.00 kB/s")]
    [InlineData(42_000.0, "42.0 kB/s")]
    [InlineData(999_999.0, "1.00 MB/s")]
    [InlineData(5_800_000.0, "5.80 MB/s")]
    [InlineData(999_999_999.0, "1.00 GB/s")]
    public void Auto_bytes_are_decimal_not_1024_based(double bytesPerSecond, string expected)
    {
        using var _ = Harness.Locale();
        Assert.Equal(expected, Fmt.NetRate(bytesPerSecond, NetworkUnit.AutoBytes));
    }

    [Theory]
    [InlineData((int)NetworkUnit.FixedMegabits, 0.0, "0.00 Mbps")]
    [InlineData((int)NetworkUnit.FixedMegabits, 500.0, "<0.01 Mbps")] // 0.004 Mbps: traffic that exists is not shown as zero
    [InlineData((int)NetworkUnit.FixedMegabits, 625.0, "0.01 Mbps")]
    [InlineData((int)NetworkUnit.FixedMegabits, 1_250_000.0, "10.00 Mbps")]
    [InlineData((int)NetworkUnit.FixedMegabits, 1_234_567_890.0, "9,876.54 Mbps")]
    [InlineData((int)NetworkUnit.FixedMegabytes, 0.0, "0.00 MB/s")]
    [InlineData((int)NetworkUnit.FixedMegabytes, 4_000.0, "<0.01 MB/s")]
    [InlineData((int)NetworkUnit.FixedMegabytes, 4_999.0, "<0.01 MB/s")]
    [InlineData((int)NetworkUnit.FixedMegabytes, 5_000.0, "0.01 MB/s")]
    [InlineData((int)NetworkUnit.FixedMegabytes, 5_800_000.0, "5.80 MB/s")]
    public void Fixed_units_never_change_their_prefix_and_keep_two_decimals(int unitValue, double bytesPerSecond, string expected)
    {
        var unit = (NetworkUnit)unitValue;
        using var _ = Harness.Locale();
        Assert.Equal(expected, Fmt.NetRate(bytesPerSecond, unit));
    }

    [Theory]
    [InlineData((int)NetworkUnit.AutoBits)]
    [InlineData((int)NetworkUnit.AutoBytes)]
    public void An_automatic_number_never_has_more_than_three_digits_so_a_column_keeps_its_width(int unitValue)
    {
        var unit = (NetworkUnit)unitValue;
        using var _ = Harness.Locale();
        for (var rate = 1.0; rate < 1e13; rate *= 1.0731)
        {
            var text = Fmt.NetRate(rate, unit);
            var number = text[..text.IndexOf(' ', StringComparison.Ordinal)];
            Assert.True(number.Count(char.IsDigit) <= 3, text);
            Assert.True(double.Parse(number, CultureInfo.InvariantCulture) < 1000, text);
        }
    }

    [Theory]
    [InlineData((int)NetworkUnit.AutoBits)]
    [InlineData((int)NetworkUnit.FixedMegabits)]
    [InlineData((int)NetworkUnit.AutoBytes)]
    [InlineData((int)NetworkUnit.FixedMegabytes)]
    public void A_negative_or_not_a_number_rate_counts_as_zero(int unitValue)
    {
        var unit = (NetworkUnit)unitValue;
        using var _ = Harness.Locale();
        var zero = Fmt.NetRate(0, unit);
        Assert.Equal(zero, Fmt.NetRate(-5, unit));
        Assert.Equal(zero, Fmt.NetRate(double.NaN, unit));
    }

    [Fact]
    public void A_rate_beyond_the_largest_prefix_stays_in_it_instead_of_failing()
    {
        using var _ = Harness.Locale();
        Assert.EndsWith(" Tbps", Fmt.NetRate(1e15, NetworkUnit.AutoBits), StringComparison.Ordinal);
        Assert.EndsWith(" TB/s", Fmt.NetRate(double.MaxValue, NetworkUnit.AutoBytes), StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_rate_reads_eight_times_larger_in_bits_than_in_bytes()
    {
        using var _ = Harness.Locale();
        Assert.Equal("5.00 MB/s", Fmt.NetRate(5_000_000, NetworkUnit.AutoBytes));
        Assert.Equal("40.0 Mbps", Fmt.NetRate(5_000_000, NetworkUnit.AutoBits));
    }

    [Fact]
    public void A_value_that_was_not_collected_is_a_dash_and_a_missing_side_counts_as_zero_in_the_sum()
    {
        using var _ = Harness.Locale();
        var ok = Value.Ok(125_000.0, "t");
        var missing = Value.NotCollected<double>("t");
        Assert.Equal("-", Fmt.NetRate(missing, NetworkUnit.AutoBits));
        Assert.Equal("-", Fmt.NetRate(Value.Denied<double>("t"), Value.Failed<double>("t"), NetworkUnit.FixedMegabits));
        Assert.Equal("1.00 Mbps", Fmt.NetRate(ok, NetworkUnit.AutoBits));
        Assert.Equal("1.00 Mbps", Fmt.NetRate(ok, missing, NetworkUnit.AutoBits));
        Assert.Equal("2.00 Mbps", Fmt.NetRate(ok, ok, NetworkUnit.AutoBits));
        Assert.Equal("0 bps", Fmt.NetRate(Value.Ok(0.0, "t"), NetworkUnit.AutoBits));
    }

    [Fact]
    public void The_unit_follows_the_language_names_and_the_numbers_follow_the_culture()
    {
        using var _ = Harness.Locale(Language.Ja);
        Assert.Equal("自動（bps）", NetworkUnits.Label(NetworkUnit.AutoBits));
        Assert.Equal("Mbps 固定", NetworkUnits.Label(NetworkUnit.FixedMegabits));
        Assert.Equal("自動（B/s）", NetworkUnits.Label(NetworkUnit.AutoBytes));
        Assert.Equal("MB/s 固定", NetworkUnits.Label(NetworkUnit.FixedMegabytes));
        Strings.Current = Language.En;
        Assert.Equal("Auto (bps)", NetworkUnits.Label(NetworkUnit.AutoBits));
        Assert.Equal("MB/s (fixed)", NetworkUnits.Label(NetworkUnit.FixedMegabytes));
    }

    [Fact]
    public void Every_unit_is_offered_once_and_a_bits_axis_steps_in_eighths_of_a_byte()
    {
        Assert.Equal(Enum.GetValues<NetworkUnit>().OrderBy(u => u), NetworkUnits.All.OrderBy(u => u));
        Assert.Equal(0.125, NetworkUnits.AxisStep(NetworkUnit.AutoBits));
        Assert.Equal(0.125, NetworkUnits.AxisStep(NetworkUnit.FixedMegabits));
        Assert.Equal(1.0, NetworkUnits.AxisStep(NetworkUnit.AutoBytes));
        Assert.Equal(1.0, NetworkUnits.AxisStep(NetworkUnit.FixedMegabytes));
    }

    [Fact]
    public void The_disk_and_memory_units_are_still_1024_based()
    {
        using var _ = Harness.Locale();
        Assert.Equal("1.0 KB", Fmt.Bytes(1024));
        Assert.Equal("1.0 MB", Fmt.Bytes(1_048_576));
    }

    // ---- the setting ----

    [Fact]
    public void The_unit_and_the_start_with_the_app_choice_survive_a_restart_and_default_to_auto_bits_and_off()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pcmieru-gui-tests-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "gui.ini");
        try
        {
            var fresh = new GuiSettings();
            Assert.Equal(NetworkUnit.AutoBits, fresh.NetworkUnit);
            Assert.False(fresh.NetMeterAutostart);

            foreach (var unit in NetworkUnits.All)
            {
                new GuiSettings(path) { NetworkUnit = unit, NetMeterAutostart = true }.Save();
                var loaded = GuiSettings.Load(path);
                Assert.Equal(unit, loaded.NetworkUnit);
                Assert.True(loaded.NetMeterAutostart);
            }

            new GuiSettings(path) { NetworkUnit = NetworkUnit.AutoBytes, NetMeterAutostart = false }.Save();
            Assert.False(GuiSettings.Load(path).NetMeterAutostart);
            Assert.Contains("net.unit=autobytes", File.ReadAllLines(path));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("net.unit=klingon")]
    [InlineData("net.unit=99")]
    [InlineData("net.unit=-1")]
    [InlineData("net.unit=")]
    public void An_unknown_unit_in_the_file_leaves_the_default_and_is_not_kept_as_a_free_value(string line)
    {
        var settings = new GuiSettings();
        settings.Apply(line);
        Assert.Equal(NetworkUnit.AutoBits, settings.NetworkUnit);
        Assert.Null(settings.GetValue("net.unit"));
    }

    [Fact]
    public void A_file_without_the_network_lines_gives_the_defaults()
    {
        var settings = new GuiSettings();
        settings.Apply("language=ja");
        Assert.Equal(NetworkUnit.AutoBits, settings.NetworkUnit);
        Assert.False(settings.NetMeterAutostart);
    }

    // ---- the meter button and the tips of a missing value ----

    [Theory]
    [InlineData(NetMeterStatus.Off, "gui.proc.net.start", true, true, true)]
    [InlineData(NetMeterStatus.Declined, "gui.proc.net.start", true, true, true)]
    [InlineData(NetMeterStatus.Failed, "gui.proc.net.start", true, true, true)]
    [InlineData(NetMeterStatus.Starting, "gui.proc.net.starting", true, false, false)]
    [InlineData(NetMeterStatus.Running, "gui.proc.net.stop", true, true, false)]
    [InlineData(NetMeterStatus.Unsupported, "gui.proc.net.start", false, false, true)]
    public void The_button_offers_what_the_meter_can_do_next(NetMeterStatus status, string text, bool visible, bool enabled, bool shield)
    {
        var button = NetMeterUi.Button(status);
        Assert.Equal(text, button.TextKey);
        Assert.Equal(visible, button.Visible);
        Assert.Equal(enabled, button.Enabled);
        Assert.Equal(shield, button.Shield);
    }

    [Fact]
    public void Every_status_has_a_button_and_a_tip_in_both_languages()
    {
        foreach (var language in new[] { Language.Ja, Language.En })
        {
            using var _ = Harness.Locale(language);
            foreach (var status in Enum.GetValues<NetMeterStatus>())
            {
                var button = NetMeterUi.Button(status);
                Assert.False(Strings.Get(button.TextKey).StartsWith('['), status + " text");
                Assert.False(NetMeterUi.ButtonTip(new NetMeterState(status)).StartsWith('['), status + " tip");
                Assert.False(NetMeterUi.MissingTip(new NetMeterState(status), Value.NotCollected<double>("t")).StartsWith('['), status + " missing");
            }
        }
    }

    [Fact]
    public void The_button_reads_start_getting_ready_and_stop_in_both_languages()
    {
        using (Harness.Locale(Language.Ja))
        {
            Assert.Equal("通信量を計測", Strings.Get(NetMeterUi.Button(NetMeterStatus.Off).TextKey));
            Assert.Equal("計測を準備中…", Strings.Get(NetMeterUi.Button(NetMeterStatus.Starting).TextKey));
            Assert.Equal("計測を止める", Strings.Get(NetMeterUi.Button(NetMeterStatus.Running).TextKey));
        }

        using (Harness.Locale(Language.En))
        {
            Assert.Equal("Measure network", Strings.Get(NetMeterUi.Button(NetMeterStatus.Off).TextKey));
            Assert.Equal("Getting ready…", Strings.Get(NetMeterUi.Button(NetMeterStatus.Starting).TextKey));
            Assert.Equal("Stop measuring", Strings.Get(NetMeterUi.Button(NetMeterStatus.Running).TextKey));
        }
    }

    [Fact]
    public void A_dash_says_how_to_turn_the_meter_on_and_that_one_administrator_prompt_follows()
    {
        using var _ = Harness.Locale(Language.Ja);
        var tip = NetMeterUi.MissingTip(new NetMeterState(NetMeterStatus.Off), Value.NotCollected<double>("t"));
        Assert.Contains("通信量を計測", tip, StringComparison.Ordinal);
        Assert.Contains("管理者の確認が 1 回", tip, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_or_unsupported_meter_adds_its_own_words_after_the_fixed_ones()
    {
        using var _ = Harness.Locale();
        var failed = NetMeterUi.MissingTip(new NetMeterState(NetMeterStatus.Failed, "the session was refused"), Value.Failed<double>("t"));
        Assert.StartsWith(Strings.Get("gui.proc.net.tip.failed"), failed, StringComparison.Ordinal);
        Assert.EndsWith("the session was refused", failed, StringComparison.Ordinal);
        var unsupported = NetMeterUi.MissingTip(new NetMeterState(NetMeterStatus.Unsupported, "no ETW here"), Value.Unsupported<double>("t"));
        Assert.EndsWith("no ETW here", unsupported, StringComparison.Ordinal);
        Assert.Equal(Strings.Get("gui.proc.net.tip.declined"), NetMeterUi.MissingTip(new NetMeterState(NetMeterStatus.Declined), Value.Denied<double>("t")));
        Assert.Equal(NetMeterUi.ButtonTip(new NetMeterState(NetMeterStatus.Failed)), Strings.Get("gui.proc.net.tip.failed"));
    }

    [Fact]
    public void A_running_meter_leaves_a_missing_value_to_its_ordinary_reason()
    {
        using var _ = Harness.Locale();
        var gone = Value.Gone<double>("t");
        Assert.Equal(Fmt.MissingTip(gone), NetMeterUi.MissingTip(new NetMeterState(NetMeterStatus.Running), gone));
    }

    // ---- the pages, on sample data with a fake meter ----

    private sealed class FakeMeter(NetMeterStatus status) : INetMeterControl
    {
        private int _starts;
        private int _stops;

        public NetMeterState State { get; private set; } = new(status);

        public NetMeterState Next { get; set; } = new(NetMeterStatus.Running);

        public int Starts => Volatile.Read(ref _starts);

        public int Stops => Volatile.Read(ref _stops);

        public int StartThreadId { get; private set; }

        public event EventHandler? StateChanged;

        public Task<NetMeterState> StartAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _starts);
            StartThreadId = Environment.CurrentManagedThreadId;
            Set(Next);
            return Task.FromResult(State);
        }

        public void Stop()
        {
            Interlocked.Increment(ref _stops);
            Set(new NetMeterState(NetMeterStatus.Off));
        }

        public void Set(NetMeterState state)
        {
            State = state;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class Rig : IDisposable
    {
        private readonly IDisposable _locale;
        private readonly ThemeService _theme = new(ThemeMode.Light);

        public Rig(NetMeterStatus status, string? savedColumns = null, int? columnVersion = null, Language language = Language.En)
        {
            _locale = Harness.Locale(language);
            Meter = new FakeMeter(status);
            // The pump reads the sampler loop, so the loop is built on the source that has this meter (the default one has its own).
            Services = PcMieruServices.Create(
                new PcMieruOptions { ForceFakes = true },
                registry =>
                {
                    var source = new FakeSnapshotSource(null, Meter);
                    registry.Set<ISnapshotSource>(source);
                    registry.Set(new SamplerLoop(source, registry.Get<ISamplerControl>()));
                });
            Pump = new SnapshotPump(Services);
            Settings = new GuiSettings();
            if (savedColumns is not null)
            {
                Settings.SetValue("proc.cols", savedColumns);
            }

            if (columnVersion is { } version)
            {
                Settings.SetInt("proc.cols.v", version);
            }

            Context = new GuiContext(Services, Pump, _theme, Settings, Meter);
            Context.StatusMessage += Status.Add;
            Page = new ProcessesPage(Context);
            _ = Page.Handle; // the page ignores a sample, and a meter change, until it has a window to draw on
        }

        public FakeMeter Meter { get; }

        public PcMieruServices Services { get; }

        public SnapshotPump Pump { get; }

        public GuiSettings Settings { get; }

        public GuiContext Context { get; }

        public ProcessesPage Page { get; }

        public List<string> Status { get; } = [];

        /// <summary>Lets the page take one sample the way the window does, and waits (running the message loop) until it has.</summary>
        public void Fill(bool withRates = false)
        {
            Page.OnActivated();
            Pump.Start();
            Wait(() => Page.VisibleRowCount > 0);
            if (withRates)
            {
                // A rate needs two samples to compare, so the page has numbers once the second one is in.
                var tick = Pump.TickAsync();
                Wait(() => tick.IsCompleted && Pump.History.Count >= 2);
                if (Meter.State.Status == NetMeterStatus.Running)
                {
                    Wait(() => ProcessRows().Any(i => Page.Model[i].Process!.NetSendBytesPerSec.TryGet(out _)));
                }
            }
        }

        /// <summary>The rows of single processes (not the group headers), top to bottom.</summary>
        public List<int> ProcessRows() => [.. Enumerable.Range(0, Page.Model.Count).Where(i => !Page.Model[i].IsGroup)];

        public void Dispose()
        {
            Page.Dispose();
            Pump.Dispose();
            Services.Dispose();
            _theme.Dispose();
            _locale.Dispose();
        }
    }

    // The page's continuations come back through the message loop, so the test thread has to run it while it waits.
    private static void Wait(Func<bool> done)
    {
        var clock = Stopwatch.StartNew();
        while (!done() && clock.ElapsedMilliseconds < 10_000)
        {
            Application.DoEvents();
            Thread.Sleep(2);
        }

        Assert.True(done(), "the page did not get there");
    }

    [Theory]
    [InlineData(NetMeterStatus.Off, "Measure network", true, true, true)]
    [InlineData(NetMeterStatus.Starting, "Getting ready…", true, false, false)]
    [InlineData(NetMeterStatus.Running, "Stop measuring", true, true, false)]
    [InlineData(NetMeterStatus.Declined, "Measure network", true, true, true)]
    [InlineData(NetMeterStatus.Failed, "Measure network", true, true, true)]
    [InlineData(NetMeterStatus.Unsupported, "Measure network", false, false, true)]
    public void The_button_on_the_processes_page_follows_the_state_of_the_meter(NetMeterStatus status, string text, bool visible, bool enabled, bool shield)
    {
        using var rig = new Rig(status);
        var button = rig.Page.MeterButton;
        Assert.Equal(text, button.Text);
        Assert.Equal(visible, button.Visible);
        Assert.Equal(enabled, button.Enabled);
        Assert.Equal(shield, button.Image is not null);

        // The same button, the meter then changing under it (from any thread; here the state is applied directly).
        rig.Meter.Set(new NetMeterState(NetMeterStatus.Running));
        rig.Page.ApplyMeterState();
        Assert.Equal("Stop measuring", button.Text);
        Assert.True(button.Visible && button.Enabled);
        Assert.Null(button.Image);
        rig.Meter.Set(new NetMeterState(NetMeterStatus.Declined));
        rig.Page.ApplyMeterState();
        Assert.Equal("Measure network", button.Text);
        Assert.NotNull(button.Image);
    }

    [Fact]
    public void The_button_text_follows_the_language()
    {
        using var rig = new Rig(NetMeterStatus.Off, language: Language.Ja);
        Assert.Equal("通信量を計測", rig.Page.MeterButton.Text);
        Strings.Current = Language.En;
        rig.Page.ApplyLanguage();
        Assert.Equal("Measure network", rig.Page.MeterButton.Text);
        Strings.Current = Language.Ja;
        rig.Page.ApplyLanguage();
        Assert.Equal("通信量を計測", rig.Page.MeterButton.Text);
    }

    [Fact]
    public void Pressing_start_calls_the_meter_off_the_ui_thread_and_pressing_stop_stops_it()
    {
        using var rig = new Rig(NetMeterStatus.Off);
        rig.Page.MeterButton.PerformClick();
        Wait(() => rig.Meter.Starts == 1 && rig.Page.MeterButton.Text == "Stop measuring");
        Assert.NotEqual(Environment.CurrentManagedThreadId, rig.Meter.StartThreadId);

        rig.Page.MeterButton.PerformClick();
        Wait(() => rig.Meter.Stops == 1 && rig.Page.MeterButton.Text == "Measure network");
        Assert.Equal(1, rig.Meter.Starts);
    }

    [Fact]
    public void A_declined_prompt_goes_to_the_status_bar_in_plain_words_and_the_button_offers_to_try_again()
    {
        using var rig = new Rig(NetMeterStatus.Off);
        rig.Meter.Next = new NetMeterState(NetMeterStatus.Declined);
        rig.Page.MeterButton.PerformClick();
        Wait(() => rig.Status.Count > 0);
        Assert.Equal(Strings.Get("gui.proc.net.tip.declined"), rig.Status[^1]);
        Assert.Equal("Measure network", rig.Page.MeterButton.Text);
        Assert.True(rig.Page.MeterButton.Enabled);
    }

    [Fact]
    public void A_button_that_is_getting_ready_cannot_be_pressed_again()
    {
        using var rig = new Rig(NetMeterStatus.Starting);
        rig.Page.MeterButton.PerformClick();
        Application.DoEvents();
        Assert.Equal(0, rig.Meter.Starts);
    }

    [Fact]
    public void The_network_column_is_on_by_default_after_disk_and_the_send_and_receive_split_is_not()
    {
        using var rig = new Rig(NetMeterStatus.Running);
        var columns = rig.Page.VisibleColumns.ToList();
        Assert.Contains(ProcessColumn.Network, columns);
        Assert.DoesNotContain(ProcessColumn.NetSend, columns);
        Assert.DoesNotContain(ProcessColumn.NetReceive, columns);
        Assert.True(columns.IndexOf(ProcessColumn.Disk) < columns.IndexOf(ProcessColumn.Network));
        Assert.True(columns.IndexOf(ProcessColumn.Network) < columns.IndexOf(ProcessColumn.Gpu));
    }

    [Fact]
    public void A_cell_shows_the_rate_in_the_chosen_unit_and_the_unit_changes_it_at_once()
    {
        using var rig = new Rig(NetMeterStatus.Running);
        rig.Fill(withRates: true);
        var rows = rig.ProcessRows();

        foreach (var (unit, suffix) in new[] { (NetworkUnit.AutoBits, "bps"), (NetworkUnit.AutoBytes, "B/s"), (NetworkUnit.FixedMegabits, " Mbps"), (NetworkUnit.FixedMegabytes, " MB/s") })
        {
            rig.Context.SetNetworkUnit(unit);
            foreach (var row in rows)
            {
                var info = rig.Page.Model[row].Process!;
                var cell = Net(rig, row, ProcessColumn.Network);
                Assert.Equal(Fmt.NetRate(info.NetSendBytesPerSec, info.NetReceiveBytesPerSec, unit), cell.Text);
                Assert.EndsWith(suffix, cell.Text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void The_split_columns_give_send_and_receive_each_on_its_own()
    {
        using var rig = new Rig(NetMeterStatus.Running, savedColumns: "Name,Cpu,NetSend,NetReceive", columnVersion: 1);
        rig.Context.SetNetworkUnit(NetworkUnit.AutoBytes);
        rig.Fill(withRates: true);
        Assert.Equal(string.Empty, rig.Page.CellAt(rig.ProcessRows()[0], ProcessColumn.Network).Text); // hidden here: no cell
        var busy = rig.ProcessRows().First(i => rig.Page.Model[i].Process!.NetReceiveBytesPerSec.TryGet(out var v) && v > 0);
        var process = rig.Page.Model[busy].Process!;
        Assert.Equal(Fmt.NetRate(process.NetSendBytesPerSec, NetworkUnit.AutoBytes), Net(rig, busy, ProcessColumn.NetSend).Text);
        Assert.Equal(Fmt.NetRate(process.NetReceiveBytesPerSec, NetworkUnit.AutoBytes), Net(rig, busy, ProcessColumn.NetReceive).Text);
        Assert.NotEqual(Net(rig, busy, ProcessColumn.NetSend).Text, Net(rig, busy, ProcessColumn.NetReceive).Text);
    }


    private static GridCell Net(Rig rig, int row, ProcessColumn column) => rig.Page.CellAt(row, column);

    [Fact]
    public void A_busy_process_has_a_bar_and_an_idle_one_has_none()
    {
        using var rig = new Rig(NetMeterStatus.Running);
        rig.Fill(withRates: true);
        var rows = rig.ProcessRows();
        var bars = rows.Select(i => (Info: rig.Page.Model[i].Process!, Cell: Net(rig, i, ProcessColumn.Network)))
            .Where(x => x.Info.NetSendBytesPerSec.TryGet(out _))
            .ToList();
        Assert.NotEmpty(bars);
        Assert.All(bars.Where(x => ProcessTableModel.NetRate(x.Info) <= 0), x => Assert.Equal(0, x.Cell.Bar));
        Assert.Contains(bars, x => ProcessTableModel.NetRate(x.Info) > 0 && x.Cell.Bar > 0);
    }

    [Fact]
    public void While_the_meter_is_off_the_cell_is_a_dash_whose_tip_says_how_to_start()
    {
        using var rig = new Rig(NetMeterStatus.Off);
        rig.Fill(withRates: true);
        var row = rig.ProcessRows().First();
        var cell = Net(rig, row, ProcessColumn.Network);
        Assert.Equal("-", cell.Text);
        Assert.Equal(0, cell.Bar);
        var column = rig.Page.VisibleColumns.ToList().IndexOf(ProcessColumn.Network);
        Assert.Equal(Strings.Get("gui.proc.net.tip.off"), rig.Page.List.CellTip?.Invoke(row, column));
    }

    [Theory]
    [InlineData(NetMeterStatus.Starting, "gui.proc.net.tip.starting")]
    [InlineData(NetMeterStatus.Declined, "gui.proc.net.tip.declined")]
    public void Each_state_of_the_meter_puts_its_own_reason_in_the_cell_tip(NetMeterStatus status, string key)
    {
        using var rig = new Rig(status);
        rig.Fill(withRates: true);
        var row = rig.ProcessRows().First();
        var column = rig.Page.VisibleColumns.ToList().IndexOf(ProcessColumn.Network);
        Assert.Equal("-", Net(rig, row, ProcessColumn.Network).Text);
        Assert.Equal(Strings.Get(key), rig.Page.List.CellTip?.Invoke(row, column));
    }

    [Fact]
    public void A_cell_with_a_value_has_the_column_hint_as_its_tip()
    {
        using var rig = new Rig(NetMeterStatus.Running);
        rig.Fill(withRates: true);
        var row = rig.ProcessRows().First(i => rig.Page.Model[i].Process!.NetSendBytesPerSec.TryGet(out _));
        var column = rig.Page.VisibleColumns.ToList().IndexOf(ProcessColumn.Network);
        var tip = rig.Page.List.CellTip?.Invoke(row, column) ?? string.Empty;
        Assert.Equal(Strings.Get("gui.proc.col.network.hint"), tip);
        Assert.Contains("loopback", tip, StringComparison.Ordinal);
    }

    [Fact]
    public void The_detail_pane_writes_send_and_receive_in_the_unit_and_a_dash_with_a_reason_when_missing()
    {
        using var _ = Harness.Locale();
        using var pane = new ProcessDetailPane { NetworkUnit = NetworkUnit.AutoBits };
        var process = Sample(42_000, 5_800_000);
        pane.UpdateNumbers(process);
        Assert.Equal("336 kbps", pane.NetSendText);
        Assert.Equal("46.4 Mbps", pane.NetReceiveText);

        pane.NetworkUnit = NetworkUnit.AutoBytes;
        pane.UpdateNumbers(process);
        Assert.Equal("42.0 kB/s", pane.NetSendText);
        Assert.Equal("5.80 MB/s", pane.NetReceiveText);

        pane.NetworkUnit = NetworkUnit.FixedMegabits;
        pane.UpdateNumbers(process);
        Assert.Equal("0.34 Mbps", pane.NetSendText);

        var asked = new List<Value<double>>();
        pane.NetworkMissingTip = value =>
        {
            asked.Add(value);
            return "why";
        };
        pane.UpdateNumbers(process with { NetSendBytesPerSec = Value.NotCollected<double>("t"), NetReceiveBytesPerSec = Value.Denied<double>("t") });
        Assert.Equal("-", pane.NetSendText);
        Assert.Equal("-", pane.NetReceiveText);
        Assert.Equal(2, asked.Count);
    }

    private static ProcessInfo Sample(double? send, double? receive) =>
        new(
            new ProcessKey(5, 1005),
            "net.exe",
            0,
            null,
            1,
            1,
            0,
            8,
            0,
            0,
            0,
            0,
            0,
            Value.Ok(1.0, "t"),
            Value.NotCollected<double>("t"),
            Value.NotCollected<double>("t"),
            Value.NotCollected<double>("t"),
            Value.Ok(ProcessState.Running, "t"),
            send is { } s ? Value.Ok(s, "t") : Value.NotCollected<double>("t"),
            receive is { } r ? Value.Ok(r, "t") : Value.NotCollected<double>("t"));

    // ---- columns saved by an older version ----

    [Fact]
    public void A_saved_set_of_columns_gets_the_network_column_once_and_the_marker_is_kept()
    {
        using var rig = new Rig(NetMeterStatus.Running, savedColumns: "Name,Cpu,Memory,Gpu");
        var columns = rig.Page.VisibleColumns;
        Assert.Contains(ProcessColumn.Network, columns);
        Assert.Contains(ProcessColumn.Cpu, columns);
        Assert.DoesNotContain(ProcessColumn.Disk, columns); // what the user left out stays out
        Assert.Equal(1, rig.Settings.GetInt("proc.cols.v", 0));
    }

    [Fact]
    public void A_set_saved_after_the_network_column_existed_keeps_a_hide()
    {
        using var rig = new Rig(NetMeterStatus.Running, savedColumns: "Name,Cpu,Memory,Gpu", columnVersion: 1);
        Assert.DoesNotContain(ProcessColumn.Network, rig.Page.VisibleColumns);
        Assert.Contains(ProcessColumn.Cpu, rig.Page.VisibleColumns);
    }

    [Fact]
    public void Hiding_the_network_column_survives_the_next_start()
    {
        using var first = new Rig(NetMeterStatus.Running, savedColumns: "Name,Cpu,Memory,Gpu");
        Assert.Contains(ProcessColumn.Network, first.Page.VisibleColumns);
        var lines = first.Settings.ToLines().ToList();

        // The user hides it: the page saves the set without it, with the marker already in the file.
        var hidden = new GuiSettings();
        foreach (var line in lines.Where(l => !l.StartsWith("proc.cols=", StringComparison.Ordinal)))
        {
            hidden.Apply(line);
        }

        hidden.SetValue("proc.cols", "Name,Cpu,Memory,Gpu");
        using var second = new Rig(NetMeterStatus.Running, savedColumns: "Name,Cpu,Memory,Gpu", columnVersion: hidden.GetInt("proc.cols.v", 0));
        Assert.DoesNotContain(ProcessColumn.Network, second.Page.VisibleColumns);
    }

    [Fact]
    public void The_old_factory_default_moves_to_the_new_default_which_has_network()
    {
        using var rig = new Rig(NetMeterStatus.Running, savedColumns: "Name,Pid,Cpu,Memory,IoRead,IoWrite,Gpu,State");
        var columns = rig.Page.VisibleColumns;
        Assert.Contains(ProcessColumn.Network, columns);
        Assert.Contains(ProcessColumn.Disk, columns);
        Assert.DoesNotContain(ProcessColumn.Pid, columns);
    }

    [Fact]
    public void A_first_run_has_the_network_column_and_marks_the_layout()
    {
        using var rig = new Rig(NetMeterStatus.Running);
        Assert.Contains(ProcessColumn.Network, rig.Page.VisibleColumns);
        Assert.Equal(1, rig.Settings.GetInt("proc.cols.v", 0));
    }

    // ---- the other pages ----

    [Fact]
    public void The_settings_page_lists_four_units_shows_the_current_one_and_follows_a_change_made_elsewhere()
    {
        using var _ = Harness.Locale(Language.En);
        using var services = PcMieruServices.Create(new PcMieruOptions { ForceFakes = true });
        using var pump = new SnapshotPump(services);
        using var theme = new ThemeService(ThemeMode.Light);
        var context = new GuiContext(services, pump, theme, new GuiSettings());
        using var page = new SettingsPage(context);
        Assert.Equal(["Auto (bps)", "Mbps (fixed)", "Auto (B/s)", "MB/s (fixed)"], page.NetUnitBox.Items.Cast<string>());
        Assert.Equal(0, page.NetUnitBox.SelectedIndex);

        page.NetUnitBox.SelectedIndex = 3;
        Assert.Equal(NetworkUnit.FixedMegabytes, context.Settings.NetworkUnit);
        context.SetNetworkUnit(NetworkUnit.AutoBytes);
        Assert.Equal(2, page.NetUnitBox.SelectedIndex);

        Assert.False(page.NetAutostartBox.Checked);
        page.NetAutostartBox.Checked = true;
        Assert.True(context.Settings.NetMeterAutostart);
        page.NetAutostartBox.Checked = false;
        Assert.False(context.Settings.NetMeterAutostart);
    }

    [Fact]
    public async Task The_graph_headline_and_the_fact_list_write_the_network_in_the_unit()
    {
        using var _ = Harness.Locale(Language.En);
        var snapshot = await Harness.FakeSnapshotAsync();
        var up = snapshot.System.Nics.First(n => n.IsUp);
        // The first sample of a rate has no earlier one to compare with, so the numbers are set here.
        var nic = up with { SendBytesPerSec = Value.Ok(42_000.0, "t"), ReceiveBytesPerSec = Value.Ok(5_800_000.0, "t") };
        var system = snapshot.System with { Nics = [nic] };
        snapshot = snapshot with { System = system };
        var panel = new PerfPanel(PerfKind.Network, nic.Id, nic.Name);

        Assert.Equal("46.7 Mbps", PerfMetrics.Headline(panel, system, NetworkUnit.AutoBits)); // the two sides added
        Assert.Equal("5.84 MB/s", PerfMetrics.Headline(panel, system, NetworkUnit.AutoBytes));
        Assert.Equal("46.74 Mbps", PerfMetrics.Headline(panel, system, NetworkUnit.FixedMegabits));
        var bits = PerfMetrics.Stats(panel, snapshot, PerfFacts.Empty, NetworkUnit.AutoBits).Select(s => s.Value).ToList();
        var bytes = PerfMetrics.Stats(panel, snapshot, PerfFacts.Empty, NetworkUnit.AutoBytes).Select(s => s.Value).ToList();
        Assert.Contains("336 kbps", bits);
        Assert.Contains("46.4 Mbps", bits);
        Assert.Contains("42.0 kB/s", bytes);
        Assert.Contains("5.80 MB/s", bytes);
    }

    [Fact]
    public void The_performance_graph_axis_follows_the_unit_and_a_change_applies_at_once()
    {
        using var _ = Harness.Locale(Language.En);
        using var services = PcMieruServices.Create(new PcMieruOptions { ForceFakes = true });
        using var pump = new SnapshotPump(services);
        using var theme = new ThemeService(ThemeMode.Light);
        var context = new GuiContext(services, pump, theme, new GuiSettings());
        using var performance = new PerformancePage(context);
        Assert.NotEqual(IntPtr.Zero, performance.Handle);
        performance.OnActivated();
        pump.Start();
        Wait(() => performance.List.Items.Count >= 3);

        performance.Select("network");
        Assert.True(performance.Current is { Kind: PerfKind.Network }, "the network panel was not selected");
        var graph = performance.Graphs[0];
        Assert.Equal(0.125, graph.AxisStep);
        Assert.EndsWith("Mbps", graph.AxisFormatter!(1_250_000), StringComparison.Ordinal);

        context.SetNetworkUnit(NetworkUnit.AutoBytes);
        Assert.Equal(1.0, performance.Graphs[0].AxisStep);
        Assert.Equal("1.25 MB/s", performance.Graphs[0].AxisFormatter!(1_250_000));
        context.SetNetworkUnit(NetworkUnit.FixedMegabits);
        Assert.Equal(0.125, performance.Graphs[0].AxisStep);
        Assert.Equal("10.00 Mbps", performance.Graphs[0].AxisFormatter!(1_250_000));
    }
}
