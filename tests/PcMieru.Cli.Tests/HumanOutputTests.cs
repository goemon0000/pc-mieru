using System.Text.Json.Nodes;
using PcMieru.Cli.Commands;
using PcMieru.Core.Hardware.Model;
using PcMieru.Core.Hardware.Sensors;

namespace PcMieru.Cli.Tests;

/// <summary>
/// The text output of perf, proc, health and hw. These used to be a flat "path: value" dump (about 250 lines for perf,
/// with 32 per-core numbers); the checks here are about what a person reads: units, labels, missing values,
/// widths, hostile text.
/// </summary>
[Collection("Global string table")]
public sealed class HumanOutputTests
{
    private static readonly string Esc = new((char)27, 1);

    private static CliContext Context(Language language = Language.En, int width = 120)
    {
        _ = CliApp.Commands;
        var env = new CliEnvironment(TextReader.Null, new StringWriter(), new StringWriter(), o => CliRun.Services(o), width, language, null);
        var context = new CliContext(env, language, CancellationToken.None);

        // The hardware category names ("hw.category.cpu") are registered by the hardware module, which is created with the services.
        _ = context.Services();
        return context;
    }

    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static Value<double> Ok(double v) => Value.Ok(v, "test");

    private static Value<double> NoDouble(ValueStatus status = ValueStatus.NotCollected) => Value.Missing<double>(status, "test");

    private static Value<long> OkLong(long v) => Value.Ok(v, "test");

    private static Value<long> NoLong(ValueStatus status = ValueStatus.NotCollected) => Value.Missing<long>(status, "test");

    private static SystemSummary Summary(
        CpuInfo? cpu = null,
        MemoryInfo? memory = null,
        DiskInfo[]? disks = null,
        NicInfo[]? nics = null,
        GpuInfo[]? gpus = null,
        Observation[]? observations = null,
        ProcessInfo[]? top = null,
        int intervalMs = 1000,
        long uptime = 93_784) =>
        new(
            SystemSummary.CurrentSchemaVersion,
            DateTime.UtcNow,
            intervalMs,
            new SystemInfo(
                cpu ?? new CpuInfo(32, Ok(12.34), Enumerable.Range(0, 32).Select(i => Ok(i * 3.0)).ToArray(), Ok(4500)),
                memory ?? new MemoryInfo(64L << 30, 40L << 30, OkLong(30L << 30), OkLong(96L << 30), NoLong(), NoLong(), NoLong()),
                disks ?? [],
                nics ?? [],
                gpus ?? [],
                uptime),
            observations ?? [],
            top ?? []);

    private static GpuInfo Gpu(string name, Value<double>? usage = null, Value<double>? temperature = null) =>
        new("gpu0", name, usage ?? Ok(5), [], OkLong(2L << 30), OkLong(12L << 30), NoLong(), temperature ?? NoDouble(), NoDouble(), NoDouble(), NoDouble());

    // ----- perf -----

    [Fact]
    public async Task Perf_on_the_fakes_is_a_short_readable_summary_not_a_dump_of_every_field()
    {
        var r = await CliRun.RunAsync(["perf", "--sample", "0s"]);

        Assert.Equal(0, r.ExitCode);
        Assert.True(r.OutLines.Length < 45, "perf printed " + r.OutLines.Length + " lines");
        Assert.DoesNotContain("perLogicalPercent", r.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("schemaVersion", r.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("Not collected", r.Out, StringComparison.Ordinal);
        Assert.Contains("CPU", r.Out, StringComparison.Ordinal);
        Assert.Contains("Memory", r.Out, StringComparison.Ordinal);
        Assert.Contains("Observations", r.Out, StringComparison.Ordinal);
        Assert.Matches(@"\d+(\.\d+)? (GB|MB)", r.Out);
    }

    [Fact]
    public void Perf_shows_round_numbers_with_units_and_leaves_the_per_core_list_to_json()
    {
        var text = PerfCommand.Render(Context(), Summary(gpus: [Gpu("Test GPU")]));

        Assert.Contains("12.3%", text, StringComparison.Ordinal);
        Assert.Contains("32 logical processors", text, StringComparison.Ordinal);
        Assert.Contains("4500 MHz", text, StringComparison.Ordinal);
        Assert.Contains("24.0 GB of 64.0 GB in use (37.5%), 40.0 GB free", text, StringComparison.Ordinal);
        Assert.Contains("commit 30.0 GB of 96.0 GB", text, StringComparison.Ordinal);
        Assert.Contains("dedicated memory 2.0 GB of 12.0 GB", text, StringComparison.Ordinal);
        Assert.Contains("1d 2h 3m", text, StringComparison.Ordinal);
        Assert.DoesNotContain("12.3400", text, StringComparison.Ordinal);
        Assert.DoesNotContain("69.0", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Perf_summary_leaves_out_the_gpu_power_the_amd_driver_only_reports()
    {
        // The driver's "GPU power" for an AMD integrated GPU is not a measurement; no summary line carries it.
        var driverOnly = Gpu("AMD Radeon(TM) Graphics") with { PowerWatts = Value.Ok(17.3, GpuPowerSource.DriverReport) };

        foreach (var language in new[] { Language.En, Language.Ja })
        {
            var text = PerfCommand.Render(Context(language), Summary(gpus: [driverOnly]));

            Assert.Contains("AMD Radeon", text, StringComparison.Ordinal);
            Assert.DoesNotContain("17.3", text, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(Language.En, "Failed: GetIfTable2 returned 5")]
    [InlineData(Language.Ja, "取得に失敗: GetIfTable2 returned 5")]
    public void Perf_says_why_a_list_is_empty_when_its_state_is_not_ok_instead_of_leaving_it_out(Language language, string expectedNics)
    {
        var summary = Summary();
        summary = summary with
        {
            System = summary.System with
            {
                DisksState = Value.Failed<int>("PDH PhysicalDisk", "PDH could not be opened"),
                NicsState = Value.Failed<int>("GetIfTable2", "GetIfTable2 returned 5"),
                GpusState = Value.NotCollected<int>("PDH GPU Engine", "GPU sampling is off (no GPU view is open)"),
            },
        };

        var text = PerfCommand.Render(Context(language), summary);

        Assert.Contains(expectedNics, text, StringComparison.Ordinal);
        Assert.Contains("PDH could not be opened", text, StringComparison.Ordinal);
        Assert.Contains("GPU sampling is off", Lines(text).Single(l => l.StartsWith("GPU", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.DoesNotContain(language == Language.En ? "none found" : "見つかりません", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Perf_still_says_none_for_a_list_that_was_read_fine_and_for_a_state_nobody_filled()
    {
        var read = Summary();
        read = read with { System = read.System with { DisksState = Value.Ok(0, "t"), NicsState = Value.Ok(0, "t"), GpusState = Value.Ok(0, "t") } };

        foreach (var summary in new[] { read, Summary() })
        {
            var text = PerfCommand.Render(Context(), summary);

            Assert.Contains("none found", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Error", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Not collected", Lines(text).Single(l => l.StartsWith("GPU", StringComparison.Ordinal)), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Perf_json_carries_the_three_states()
    {
        var summary = Summary();
        summary = summary with { System = summary.System with { NicsState = Value.Failed<int>("GetIfTable2", "GetIfTable2 returned 5") } };

        var json = System.Text.Json.Nodes.JsonNode.Parse(PcMieruJson.Serialize(summary))!["system"]!;

        Assert.Equal("error", (string?)json["nicsState"]!["status"]);
        Assert.NotNull(json["disksState"]);
        Assert.NotNull(json["gpusState"]);
    }

    [Fact]
    public void Perf_never_turns_a_missing_value_into_zero()
    {
        var cpu = new CpuInfo(8, NoDouble(), [], NoDouble());
        var memory = new MemoryInfo(0, 0, NoLong(), NoLong(), NoLong(), NoLong(), NoLong());
        var disk = new DiskInfo("d0", "Disk X", NoDouble(ValueStatus.Denied), NoDouble(), NoDouble(), NoDouble(ValueStatus.Unsupported));
        var nic = new NicInfo("n0", "Nic X", true, NoDouble(), NoDouble(), NoLong());
        var text = PerfCommand.Render(Context(), Summary(cpu: cpu, memory: memory, disks: [disk], nics: [nic], gpus: [Gpu("G", usage: NoDouble(ValueStatus.Unsupported), temperature: NoDouble(ValueStatus.Denied))]));

        var lines = Lines(text);
        Assert.StartsWith("CPU", lines[0], StringComparison.Ordinal);
        Assert.Contains("Not collected", lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain("0.0%", lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain("MHz", lines[0], StringComparison.Ordinal);
        Assert.Contains("Not collected", lines[1], StringComparison.Ordinal);
        Assert.Contains("usage Unsupported", lines.Single(l => l.StartsWith("GPU", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.DoesNotContain("temperature", text, StringComparison.Ordinal);
        var diskRow = lines.Single(l => l.StartsWith("Disk X", StringComparison.Ordinal));
        Assert.Equal(["Disk", "X", "-", "-", "-", "-"], diskRow.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var nicRow = lines.Single(l => l.StartsWith("Nic X", StringComparison.Ordinal));
        Assert.Equal(["Nic", "X", "Up", "-", "-", "-"], nicRow.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void Perf_without_a_measured_window_says_so_instead_of_showing_a_duration()
    {
        var text = PerfCommand.Render(Context(), Summary(intervalMs: 0));

        Assert.Matches(@"Measured over\s+-", text);
    }

    [Fact]
    public void Perf_prints_each_observation_on_its_own_marked_line_and_says_when_there_is_none()
    {
        var none = PerfCommand.Render(Context(), Summary());
        var some = PerfCommand.Render(
            Context(),
            Summary(observations: [new Observation(
                "memory.dominant",
                ObservationSeverity.Notice,
                ObservationCertainty.Fact,
                "obs.memory.processDominant",
                new Dictionary<string, string> { ["name"] = "big.exe", ["percent"] = "61", ["usedMb"] = "40000" },
                [],
                [])]));

        Assert.Contains("Nothing matched in this observation", none, StringComparison.Ordinal);
        Assert.DoesNotContain("  ! ", none, StringComparison.Ordinal);
        Assert.Single(Lines(some), l => l.StartsWith("  ! big.exe is using 61% of the memory", StringComparison.Ordinal));
        Assert.DoesNotContain("Nothing matched", some, StringComparison.Ordinal);
    }

    [Fact]
    public void Perf_tables_line_up_by_display_width_with_japanese_names_and_fit_a_narrow_terminal()
    {
        var disks = new[]
        {
            new DiskInfo("d0", "システムディスク（とても長い名前のディスクです）", Ok(12), Ok(5_000_000), Ok(1_000_000), Ok(1.5)),
            new DiskInfo("d1", "Data", Ok(0), Ok(0), Ok(0), Ok(0.5)),
        };
        var text = PerfCommand.Render(Context(Language.Ja, width: 70), Summary(disks: disks));

        var block = text.Split("\n\n")[1];
        var tableLines = Lines(block);
        Assert.Equal(3, tableLines.Length);
        Assert.Single(tableLines.Select(CliText.Width).Distinct());
        Assert.All(tableLines, l => Assert.True(CliText.Width(l) <= 70, l));
        Assert.Contains("…", tableLines[1], StringComparison.Ordinal);
        Assert.StartsWith("ディスク", tableLines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Perf_text_cannot_carry_escape_codes_from_a_device_or_process_name()
    {
        var hostile = "evil" + Esc + "[31mred\nnewline";
        var disk = new DiskInfo("d0", hostile, Ok(1), Ok(1), Ok(1), Ok(1));
        var nic = new NicInfo("n0", hostile, true, Ok(1), Ok(1), OkLong(1_000_000_000));
        var process = Samples.Process(7, hostile);
        var text = PerfCommand.Render(Context(), Summary(disks: [disk], nics: [nic], gpus: [Gpu(hostile)], top: [process]));

        Assert.DoesNotContain(Esc, text, StringComparison.Ordinal);
        Assert.DoesNotContain("newline\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Perf_json_is_unchanged_by_the_text_rendering()
    {
        var text = PerfCommand.Render(Context(), Summary());
        var json = PcMieruJson.Serialize(Summary());

        Assert.NotNull(JsonNode.Parse(json)!["system"]!["cpu"]!["perLogicalPercent"]);
        Assert.NotEqual(text, json);
    }

    // ----- proc -----

    [Fact]
    public void Proc_without_details_says_they_are_not_collected_and_never_prints_a_null()
    {
        var detail = new ProcessDetail(Samples.Process(4242, "tool.exe", parent: 10), null);
        var text = ProcCommand.Render(Context(), detail);

        Assert.Contains("4242 (parent 10)", text, StringComparison.Ordinal);
        Assert.Contains("Details (path, signature ...) have not been collected", text, StringComparison.Ordinal);
        Assert.DoesNotContain("null", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Path", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Proc_shows_why_a_detail_is_missing_and_the_signature_note()
    {
        var process = Samples.Process(300, "svc.exe");
        var details = Samples.Details(process.Key, "C:\\Windows\\svc.exe", "svc.exe -k x") with
        {
            Owner = Value.Denied<string>("test"),
            Signature = Value.Ok(new SignatureInfo(SignatureState.SignedEmbedded, "Example Corp"), "test"),
            ServiceNames = ["Alpha", "Beta"],
            Known = new KnownProcess("svc", "サービス", "A service host", true, true),
        };
        var text = ProcCommand.Render(Context(), new ProcessDetail(process, details));

        Assert.Matches(@"Owner\s+Access denied", text);
        Assert.Matches(@"Path\s+C:\\Windows\\svc\.exe", text);
        Assert.Contains("Signed (embedded in the file): Example Corp", text, StringComparison.Ordinal);
        Assert.Contains("revocation was not checked", text, StringComparison.Ordinal);
        Assert.Matches(@"Services\s+Alpha, Beta", text);
        Assert.Matches(@"What is this\?\s+A service host", text);
        Assert.Matches(@"Memory\s+\d", text);
    }

    [Fact]
    public void Proc_in_japanese_uses_the_japanese_description_and_labels_line_up_by_display_width()
    {
        var process = Samples.Process(300, "svc.exe");
        var details = Samples.Details(process.Key, "C:\\x.exe", "x") with { Known = new KnownProcess("svc", "サービス用の仕組み", "A service host", true, true) };
        var lines = Lines(ProcCommand.Render(Context(Language.Ja), new ProcessDetail(process, details)));

        Assert.Contains(lines, l => l.EndsWith("サービス用の仕組み", StringComparison.Ordinal));
        string[] values = ["svc.exe", "C:\\x.exe", "サービス用の仕組み", "動作中"];
        var columns = values
            .Select(v =>
            {
                var line = lines.Single(l => l.Contains(v, StringComparison.Ordinal));
                return CliText.Width(line[..line.IndexOf(v, StringComparison.Ordinal)]);
            })
            .Distinct()
            .ToArray();
        Assert.Single(columns);
    }

    [Fact]
    public void Proc_command_line_with_a_newline_or_escape_code_stays_on_one_line()
    {
        var process = Samples.Process(300, "svc.exe");
        var details = Samples.Details(process.Key, "C:\\x.exe", "x\ny" + Esc + "[2J");
        var text = ProcCommand.Render(Context(), new ProcessDetail(process, details));

        Assert.DoesNotContain(Esc, text, StringComparison.Ordinal);
        Assert.Single(Lines(text), l => l.StartsWith("Command line", StringComparison.Ordinal));
        Assert.Contains("x?y?[2J", text, StringComparison.Ordinal);
    }

    // ----- health -----

    private static StorageHealth Nvme(Value<int>? warning = null) =>
        new(
            StorageHealthKind.Nvme,
            Ok(41),
            Ok(7),
            Ok(100),
            Ok(10),
            warning ?? Value.Ok(0, "test"),
            Value.Ok(new U128(48_200_000_000_000UL), "test"),
            Value.Ok(new U128(91_500_000_000_000UL), "test"),
            Value.Ok(new U128(2210UL), "test"),
            Value.Ok(new U128(612UL), "test"),
            Value.Ok(new U128(18UL), "test"),
            Value.Ok(new U128(0UL), "test"),
            []);

    [Fact]
    public void Health_prints_one_block_per_drive_with_units()
    {
        var report = new StorageHealthReport("1", [new DriveHealth(0, "Disk A", Value.Ok(Nvme(), "test"))]);
        var text = HealthCommand.Render(Context(), report, elevated: false);

        Assert.Contains("Drive 0: Disk A (NVMe)", text, StringComparison.Ordinal);
        Assert.Matches(@"Temperature\s+41 °C", text);
        Assert.Matches(@"Spare area\s+100\.0% \(threshold 10\.0%\)", text);
        Assert.Matches(@"Data written\s+43\.8 TB", text);
        Assert.Matches(@"Power-on time\s+2210 h \(about 92 days\)", text);
        Assert.Matches(@"Critical warning\s+none", text);
        Assert.DoesNotContain("48200000000000", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Health_shows_a_critical_warning_and_never_shows_zero_for_a_value_it_could_not_read()
    {
        var health = Nvme(warning: Value.Ok(4, "test")) with { DataWrittenBytes = Value.Denied<U128>("test"), TemperatureC = NoDouble(ValueStatus.Unsupported) };
        var text = HealthCommand.Render(Context(), new StorageHealthReport("1", [new DriveHealth(0, "Disk A", Value.Ok(health, "test"))]), elevated: false);

        Assert.Matches(@"Critical warning\s+yes \(value 4\)", text);
        Assert.Matches(@"Data written\s+Access denied", text);
        Assert.Matches(@"Temperature\s+Unsupported", text);
    }

    [Fact]
    public void Health_suggests_elevate_only_for_a_denied_drive_and_only_when_not_already_elevated()
    {
        var denied = new StorageHealthReport("1", [new DriveHealth(1, "Disk B", Value.Denied<StorageHealth>("test"))]);
        var unsupported = new StorageHealthReport("1", [new DriveHealth(1, "Disk B", Value.Unsupported<StorageHealth>("test"))]);

        var first = HealthCommand.Render(Context(), denied, elevated: false);
        var elevated = HealthCommand.Render(Context(), denied, elevated: true);
        var other = HealthCommand.Render(Context(), unsupported, elevated: false);

        Assert.Contains("Access denied", first, StringComparison.Ordinal);
        Assert.Contains("--elevate", first, StringComparison.Ordinal);
        Assert.Contains("Access denied", elevated, StringComparison.Ordinal);
        Assert.DoesNotContain("--elevate", elevated, StringComparison.Ordinal);
        Assert.Contains("Unsupported", other, StringComparison.Ordinal);
        Assert.DoesNotContain("--elevate", other, StringComparison.Ordinal);
    }

    [Fact]
    public void Health_sata_attributes_are_a_table_that_keeps_the_drives_own_numbers()
    {
        var health = Nvme() with
        {
            Kind = StorageHealthKind.Sata,
            Attributes = [new SmartAttribute(5, "Reallocated_Sector_Ct", 100, 100, 10, new U128(0UL), true), new SmartAttribute(194, "Temperature_Celsius", 36, 40, 0, new U128(36UL), false)],
        };
        var text = HealthCommand.Render(Context(), new StorageHealthReport("1", [new DriveHealth(2, "Disk C", Value.Ok(health, "test"))]), elevated: false);

        var rows = Lines(text).Where(l => l.TrimStart().StartsWith("5 ", StringComparison.Ordinal) || l.TrimStart().StartsWith("194 ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Contains("Reallocated_Sector_Ct", rows[0], StringComparison.Ordinal);
        Assert.Single(rows.Select(CliText.Width).Distinct());
    }

    [Fact]
    public void Health_with_no_drives_says_so_and_a_hostile_model_name_is_neutralised()
    {
        var empty = HealthCommand.Render(Context(), new StorageHealthReport("1", []), elevated: false);
        var hostile = HealthCommand.Render(Context(), new StorageHealthReport("1", [new DriveHealth(0, "M" + Esc + "[0mX\nY", Value.Denied<StorageHealth>("test"))]), elevated: true);

        Assert.Equal("No drives were found\n", empty);
        Assert.DoesNotContain(Esc, hostile, StringComparison.Ordinal);
        Assert.Single(Lines(hostile), l => l.StartsWith("Drive 0:", StringComparison.Ordinal));
    }

    // ----- hw -----

    private static HardwareInfo Hw(
        Value<CpuHardware>? cpu = null,
        Value<MemoryHardware>? memory = null,
        Value<IReadOnlyList<StorageDevice>>? storage = null,
        Value<BatteryHardware>? battery = null) =>
        new(
            HardwareInfo.CurrentSchemaVersion,
            DateTime.UtcNow,
            cpu ?? Value.NotCollected<CpuHardware>("test"),
            memory ?? Value.NotCollected<MemoryHardware>("test"),
            Value.NotCollected<BoardHardware>("test"),
            Value.NotCollected<IReadOnlyList<GpuHardware>>("test"),
            storage ?? Value.NotCollected<IReadOnlyList<StorageDevice>>("test"),
            Value.NotCollected<IReadOnlyList<NetworkAdapter>>("test"),
            Value.NotCollected<IReadOnlyList<DisplayDevice>>("test"),
            Value.NotCollected<IReadOnlyList<AudioEndpoint>>("test"),
            Value.NotCollected<IReadOnlyList<UsbDevice>>("test"),
            battery ?? Value.NotCollected<BatteryHardware>("test"),
            Value.NotCollected<OsHardware>("test"));

    private static CpuHardware Cpu() =>
        new(
            Value.Ok("Test CPU", "test"),
            Value.Denied<string>("test"),
            Value.Ok(25, "test"),
            Value.Ok(97, "test"),
            Value.Ok(2, "test"),
            Value.Ok(16, "test"),
            Value.Ok(32, "test"),
            Value.Unsupported<int>("test"),
            Value.Unsupported<int>("test"),
            Value.Ok(4500, "test"),
            Value.Ok("AM5", "test"),
            Value.Ok(1_048_576L, "test"),
            Value.Ok(16_777_216L, "test"),
            Value.Ok(67_108_864L, "test"),
            Value.Ok<IReadOnlyList<string>>(["AVX2", "AES"], "test"));

    [Fact]
    public void Hw_uses_plain_names_and_units_and_shows_why_a_value_is_missing()
    {
        var text = HwCommand.Render(Context(), Hw(cpu: Value.Ok(Cpu(), "test")));

        Assert.StartsWith("[CPU]\n", text, StringComparison.Ordinal);
        Assert.Matches(@"  Physical cores: 16\n", text);
        Assert.Contains("  Base clock: 4500 MHz\n", text, StringComparison.Ordinal);
        Assert.Contains("  L1 cache: 1.0 MB\n", text, StringComparison.Ordinal);
        Assert.Contains("  L3 cache: 64.0 MB\n", text, StringComparison.Ordinal);
        Assert.Contains("  Vendor: Access denied\n", text, StringComparison.Ordinal);
        Assert.Contains("  Performance cores: Unsupported\n", text, StringComparison.Ordinal);
        Assert.Contains("  Instruction sets: AVX2, AES\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("1048576", text, StringComparison.Ordinal);
        Assert.DoesNotContain("l1CacheBytes", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Hw_in_japanese_uses_japanese_names()
    {
        var text = HwCommand.Render(Context(Language.Ja), Hw(cpu: Value.Ok(Cpu(), "test")));

        Assert.Contains("  物理コア数: 16\n", text, StringComparison.Ordinal);
        Assert.Contains("  製造元: " + Strings.Status(ValueStatus.Denied, Language.Ja) + "\n", text, StringComparison.Ordinal);
        Assert.Contains("  基本クロック: 4500 MHz\n", text, StringComparison.Ordinal);
        Assert.Contains("  高性能コア数: " + Strings.Status(ValueStatus.Unsupported, Language.Ja) + "\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Hw_lists_nested_items_under_a_title_and_a_whole_unsupported_category_says_so()
    {
        var volume = new StorageVolume("C:", "System", "NTFS", Value.Ok(2_000_000_000_000L, "test"), Value.Ok(1_000_000_000_000L, "test"));
        var drive = new StorageDevice(
            0,
            Value.Ok("Disk A", "test"),
            Value.Denied<string>("test"),
            Value.Ok("FW1", "test"),
            Value.Ok(StorageBus.Nvme, "test"),
            Value.Ok(2_000_000_000_000L, "test"),
            Value.Ok(true, "test"),
            Value.Ok("GPT", "test"),
            [volume]);
        var text = HwCommand.Render(Context(), Hw(storage: Value.Ok<IReadOnlyList<StorageDevice>>([drive], "test"), battery: Value.Unsupported<BatteryHardware>("test")));

        Assert.Contains("[Storage]\n  Storage 1\n    Drive number: 0\n", text, StringComparison.Ordinal);
        Assert.Contains("    Bus: NVMe\n", text, StringComparison.Ordinal);
        Assert.Contains("    TRIM supported: yes\n", text, StringComparison.Ordinal);
        Assert.Contains("    Capacity: 1.8 TB\n", text, StringComparison.Ordinal);
        Assert.Contains("    Volume 1\n      Letter: C:\n", text, StringComparison.Ordinal);
        Assert.Contains("[Battery]\n  Unsupported\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[CPU]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_field_of_the_hardware_model_has_a_plain_name_so_nothing_shows_up_as_camelCase()
    {
        // Walks the model types themselves (not one sample), so a field added to a record later fails here
        // until it has a name in CliLabels. Details records hung off the model are covered the same way.
        var names = new SortedSet<string>(StringComparer.Ordinal);
        Collect(typeof(HardwareInfo), names, new HashSet<Type>());
        names.ExceptWith(["schemaVersion", "collectedAtUtc", "cpu", "memory", "board", "gpus", "storage", "network", "displays", "audio", "usb", "battery", "os"]);

        Assert.True(names.Count > 60, "expected the model to have many fields, found " + names.Count);
        var missing = names.Where(n => !CliLabels.Has(n)).ToArray();
        Assert.True(missing.Length == 0, "add a label to CliLabels for: " + string.Join(", ", missing));
        Assert.All(names, n => Assert.NotEqual(n, CliLabels.Label(n, Language.En)));
        Assert.All(names, n => Assert.NotEqual(CliLabels.Label(n, Language.En), "[cli.field." + n + "]"));
    }

    private static void Collect(Type type, ISet<string> names, ISet<Type> seen)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsGenericType)
        {
            // Value<T>, IReadOnlyList<T>: the name belongs to the property, the shape to the type argument.
            foreach (var argument in type.GetGenericArguments())
            {
                Collect(argument, names, seen);
            }

            return;
        }

        if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(DateTime) || type == typeof(U128) || type.Namespace?.StartsWith("PcMieru", StringComparison.Ordinal) != true)
        {
            return;
        }

        if (!seen.Add(type))
        {
            return;
        }

        foreach (var property in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            names.Add(char.ToLowerInvariant(property.Name[0]) + property.Name[1..]);
            Collect(property.PropertyType, names, seen);
        }
    }

    private static JsonObject HwJson(string json) => JsonNode.Parse(json)!.AsObject();

    private const string DetailsJson = """
        {
          "cpu": { "status": "ok", "source": "t", "value": {
            "name": { "status": "ok", "source": "t", "value": "Test CPU" },
            "physicalCores": { "status": "ok", "source": "t", "value": 8 },
            "details": {
              "currentClockMhz": { "status": "ok", "source": "t", "value": 4500 },
              "threadsPerCore": { "status": "ok", "source": "t", "value": 2 },
              "caches": [
                { "level": 1, "type": "instruction", "sizeBytes": 32768, "associativity": 8, "lineSizeBytes": 64, "instances": 8 }
              ]
            }
          } },
          "display": { "status": "notCollected", "source": "t" }
        }
        """;

    [Fact]
    public void Hw_leaves_the_details_blocks_out_unless_asked_and_says_how_to_get_them()
    {
        var plain = HwCommand.Render(Context(), HwJson(DetailsJson), details: false);
        var full = HwCommand.Render(Context(), HwJson(DetailsJson), details: true);

        Assert.Contains("  Name: Test CPU\n", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("Current clock", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("Details", plain.Split("Add --details")[0], StringComparison.Ordinal);
        Assert.EndsWith("Add --details to show the fine print (--json always includes everything)\n", plain, StringComparison.Ordinal);
        Assert.Equal(1, plain.Split("--details").Length - 1);

        Assert.Contains("  Details\n    Current clock: 4500 MHz\n    Threads per core: 2\n", full, StringComparison.Ordinal);
        Assert.Contains("    Cache 1\n      Level: 1\n      Type: Instruction\n      Size: 32.0 KB\n      Associativity: 8\n      Line size: 64 B\n      Instances: 8\n", full, StringComparison.Ordinal);
        Assert.DoesNotContain("--details", full, StringComparison.Ordinal);
    }

    [Fact]
    public void Hw_prints_no_hint_when_there_were_no_details_to_hide()
    {
        var text = HwCommand.Render(Context(), HwJson("""{ "cpu": { "status": "ok", "source": "t", "value": { "name": { "status": "ok", "source": "t", "value": "X" } } } }"""), details: false);

        Assert.Equal("[CPU]\n  Name: X\n", text);
    }

    [Fact]
    public void Hw_shows_an_empty_memory_socket_as_empty_not_as_a_module_with_unknown_properties()
    {
        var json = """
            { "memory": { "status": "ok", "source": "t", "value": {
              "modules": [
                { "slot": { "status": "ok", "source": "t", "value": "DIMM 0" }, "capacityBytes": { "status": "ok", "source": "t", "value": 0 },
                  "type": { "status": "unsupported", "source": "t" } },
                { "slot": { "status": "ok", "source": "t", "value": "DIMM 1" }, "capacityBytes": { "status": "ok", "source": "t", "value": 34359738368 },
                  "type": { "status": "ok", "source": "t", "value": "ddr5" } },
                { "slot": { "status": "ok", "source": "t", "value": "DIMM 2" }, "capacityBytes": { "status": "unsupported", "source": "t" } }
              ] } } }
            """;
        var text = HwCommand.Render(Context(), HwJson(json), details: false);

        Assert.Contains("  Memory module 1: DIMM 0 (empty slot)\n", text, StringComparison.Ordinal);
        Assert.Contains("  Memory module 2\n    Slot: DIMM 1\n    Capacity: 32.0 GB\n    Type: DDR5\n", text, StringComparison.Ordinal);

        // A capacity that could not be read is not "empty": it stays a module, with the reason.
        Assert.Contains("  Memory module 3\n    Slot: DIMM 2\n    Capacity: Unsupported\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Hw_shows_an_empty_string_as_none_and_translates_known_words_only()
    {
        var json = """
            { "storage": { "status": "ok", "source": "t", "value": [
              { "label": { "status": "ok", "source": "t", "value": "" },
                "kind": { "status": "ok", "source": "t", "value": "ethernet" },
                "type": { "status": "ok", "source": "t", "value": "Basic data" },
                "bitsPerSample": { "status": "ok", "source": "t", "value": 16 },
                "dataWidthBits": { "status": "ok", "source": "t", "value": 64 },
                "maxLuminanceNits": { "status": "ok", "source": "t", "value": 400 },
                "diagonalInches": { "status": "ok", "source": "t", "value": 27 } }
            ] } }
            """;
        var en = HwCommand.Render(Context(), HwJson(json), details: false);
        var ja = HwCommand.Render(Context(Language.Ja), HwJson(json), details: false);

        Assert.Contains("    Label: none\n", en, StringComparison.Ordinal);
        Assert.Contains("    Kind: Ethernet\n", en, StringComparison.Ordinal);
        Assert.Contains("    Type: Basic data\n", en, StringComparison.Ordinal);
        Assert.Contains("    Data width: 64 bit\n", en, StringComparison.Ordinal);
        Assert.Contains("    Max luminance: 400 nit\n", en, StringComparison.Ordinal);
        Assert.Contains("    Diagonal size: 27 inch\n", en, StringComparison.Ordinal);
        Assert.Contains("    ラベル: なし\n", ja, StringComparison.Ordinal);
        Assert.Contains("    種類: 有線 LAN\n", ja, StringComparison.Ordinal);
        Assert.Contains("    対角のサイズ: 27 インチ\n", ja, StringComparison.Ordinal);
    }

    [Fact]
    public void Health_for_a_sata_drive_leaves_out_the_rows_it_can_never_have_but_nvme_keeps_every_row()
    {
        Value<T> Gone<T>() => Value.Unsupported<T>("test");
        var sata = Nvme() with
        {
            Kind = StorageHealthKind.Sata,
            PercentageUsed = Gone<double>(),
            AvailableSparePercent = Gone<double>(),
            AvailableSpareThresholdPercent = Gone<double>(),
            CriticalWarning = Gone<int>(),
            DataWrittenBytes = Gone<U128>(),
            DataReadBytes = Gone<U128>(),
        };
        var nvme = sata with { Kind = StorageHealthKind.Nvme };
        var sataText = HealthCommand.Render(Context(), new StorageHealthReport("1", [new DriveHealth(0, "S", Value.Ok(sata, "test"))]), elevated: false);
        var nvmeText = HealthCommand.Render(Context(), new StorageHealthReport("1", [new DriveHealth(0, "N", Value.Ok(nvme, "test"))]), elevated: false);

        Assert.DoesNotContain("Unsupported", sataText, StringComparison.Ordinal);
        Assert.DoesNotContain("Life used", sataText, StringComparison.Ordinal);
        Assert.Contains("Temperature", sataText, StringComparison.Ordinal);
        Assert.Contains("Power-on time", sataText, StringComparison.Ordinal);
        // Life used, spare area (its threshold is folded into the same row), critical warning, written, read.
        Assert.Equal(5, Lines(nvmeText).Count(l => l.Contains("Unsupported", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_field_without_a_label_is_still_shown_under_its_own_name()
    {
        Assert.Equal("someFutureField", CliLabels.Label("someFutureField", Language.Ja));
        Assert.False(CliLabels.Has("someFutureField"));
    }

    // ----- shared formatting -----

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(150L * 1024 * 1024, "150 MB")]
    [InlineData(68_719_476_736L, "64.0 GB")]
    [InlineData(48_200_000_000_000L, "43.8 TB")]
    public void Bytes_use_binary_units_with_one_decimal_below_100(long bytes, string expected) =>
        Assert.Equal(expected, CliFormat.Bytes(bytes));

    [Fact]
    public void Bytes_of_a_non_number_is_a_dash_and_rates_end_in_per_second()
    {
        Assert.Equal("-", CliFormat.Bytes(double.NaN));
        Assert.Equal("1.0 KB/s", CliFormat.Rate(1024));
        Assert.Equal("0 B/s", CliFormat.Rate(0));
    }

    [Theory]
    [InlineData(2_500_000_000L, "2.5 Gbit/s")]
    [InlineData(1_000_000_000L, "1 Gbit/s")]
    [InlineData(100_000_000L, "100 Mbit/s")]
    [InlineData(54_000_000L, "54 Mbit/s")]
    [InlineData(64_000L, "64 kbit/s")]
    public void Link_speed_is_shown_in_the_largest_whole_unit(long bits, string expected) =>
        Assert.Equal(expected, CliFormat.LinkSpeed(bits));

    [Theory]
    [InlineData(59, Language.En, "0m")]
    [InlineData(3600 + 120, Language.En, "1h 2m")]
    [InlineData(93_784, Language.En, "1d 2h 3m")]
    [InlineData(93_784, Language.Ja, "1 日 2 時間 3 分")]
    [InlineData(-5, Language.En, "0m")]
    public void Durations_are_days_hours_minutes(long seconds, Language language, string expected) =>
        Assert.Equal(expected, CliFormat.Duration(Context(language), seconds));
}
