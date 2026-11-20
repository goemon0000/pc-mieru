using PcMieru.Checks.SystemInfo;
using PcMieru.Core;
using PcMieru.Core.Hardware.Model;

namespace PcMieru.Checks.Tests.SystemInfo;

/// <summary>
/// How what was read is put into the readings of the judge: the fixed order of the subjects (the number is the place in it), the joins (the
/// GPU to its controller by PCI ids, the disk to its health by drive number, the volume to its disk), and what stays missing when a list or a
/// leaf did not come. No Windows: Core's values are written by hand.
/// </summary>
public sealed class SystemInputsBuilderTests
{
    private static HardwareFacts Facts(HardwareInfo machine) => new(
        CoreReadings.Of(machine.Os),
        CoreReadings.Of(machine.Cpu),
        CoreReadings.Of(machine.Memory),
        CoreReadings.Of(machine.Gpus),
        CoreReadings.Of(machine.Storage));

    private static VideoControllerRow Nvidia(int? code = 0) => new(@"PCI\VEN_10DE&DEV_2704&SUBSYS_00000000&REV_A1\4&1&0&0019", code);

    private static SystemWmiFacts Wmi(
        IReadOnlyList<PageFileRow>? files = null,
        IReadOnlyList<VideoControllerRow>? video = null,
        DiskHealthReading? health = null) =>
        new(
            CheckReading<IReadOnlyList<PageFileRow>>.Ok(files ?? [new PageFileRow(@"C:\pagefile.sys", 4096, 100, 500)]),
            CheckReading<bool>.Ok(true),
            CheckReading<IReadOnlyList<VideoControllerRow>>.Ok(video ?? [Nvidia()]),
            health ?? Health(new PhysicalDiskRow("0", Hw.SerialOfDisk0, 0, [2]), new PhysicalDiskRow("1", Hw.SerialOfDisk1, 0, [2])));

    private static DiskHealthReading Health(params PhysicalDiskRow[] rows) => new(CheckReading<IReadOnlyList<PhysicalDiskRow>>.Ok(rows));

    private static SystemInputs Build(HardwareInfo? machine = null, SystemWmiFacts? wmi = null) =>
        SystemInputsBuilder.Build(Facts(machine ?? Hw.Machine()), wmi ?? Wmi(), SI.NoEvents);

    private static void IsMissing<T>(CheckReading<T> reading, CheckReason reason)
    {
        Assert.False(reading.HasValue);
        Assert.Equal(reason, reading.Reason);
    }

    // ---- the PCI key ----

    [Theory]
    [InlineData(@"PCI\VEN_10DE&DEV_2704&SUBSYS_00000000&REV_A1\4&1&0&0019", "10DE:2704")]
    [InlineData(@"pci\ven_10de&dev_2704&subsys_00000000", "10DE:2704")]
    [InlineData(@"PCI\VEN_1002&DEV_164E", "1002:164E")]
    [InlineData(@"  PCI\VEN_8086&DEV_46A6&SUBSYS_1  ", "8086:46A6")]
    public void A_pci_id_of_wmi_becomes_the_vendor_and_device_of_core(string pnp, string key)
    {
        Assert.Equal(key, SystemInputsBuilder.PciKey(pnp));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"ROOT\BASICDISPLAY\0000")]
    [InlineData(@"USB\VID_10DE&PID_2704")]
    [InlineData(@"PCI\VEN_10DE")]
    [InlineData(@"PCI\VEN_10DE&DEV_27")]
    [InlineData(@"PCI\VEN_10GE&DEV_2704")]
    [InlineData(@"PCI\VEN_10DE&DEV_27ZZ")]
    [InlineData(@"PCI\VEN_10DE&XXX_2704")]
    public void An_id_that_is_not_a_pci_vendor_and_device_has_no_key(string? pnp)
    {
        Assert.Null(SystemInputsBuilder.PciKey(pnp));
    }

    // ---- the OS ----

    [Fact]
    public void The_os_version_is_the_name_the_build_number_after_the_last_dot_and_the_two_optional_parts()
    {
        var inputs = Build();

        var os = inputs.OsVersion;
        Assert.True(os.HasValue);
        Assert.Equal(new OsVersionValue("Windows 11 Pro", "25H2", 26300, 7171), os.Value);
        Assert.Equal(3600d, inputs.UptimeSeconds.Value);
    }

    [Fact]
    public void A_feature_update_name_that_is_not_there_is_null_and_so_is_a_revision_below_zero()
    {
        var withoutName = Build(Hw.Machine(os: Hw.Os(displayVersion: null)));
        var negativeRevision = Build(Hw.Machine(os: Hw.Os(ubr: -1)));

        Assert.Null(withoutName.OsVersion.Value.DisplayVersion);
        Assert.Equal(7171, withoutName.OsVersion.Value.Ubr);
        Assert.Equal("25H2", negativeRevision.OsVersion.Value.DisplayVersion);
        Assert.Null(negativeRevision.OsVersion.Value.Ubr);
    }

    [Fact]
    public void A_build_that_is_not_a_number_is_an_error_and_an_empty_name_is_unsupported()
    {
        IsMissing(Build(Hw.Machine(os: Hw.Os(build: "10.0.garbage"))).OsVersion, CheckReason.Error);
        IsMissing(Build(Hw.Machine(os: Hw.Os(name: "   "))).OsVersion, CheckReason.Unsupported);
    }

    [Fact]
    public void An_uptime_below_zero_is_an_error()
    {
        IsMissing(Build(Hw.Machine(os: Hw.Os(uptimeSec: -5))).UptimeSeconds, CheckReason.Error);
        Assert.Equal(0d, Build(Hw.Machine(os: Hw.Os(uptimeSec: 0))).UptimeSeconds.Value);
    }

    [Fact]
    public void When_the_os_did_not_come_the_version_and_the_uptime_say_why()
    {
        var facts = Facts(Hw.Machine()) with { Os = CheckReading<OsHardware>.Missing(CheckReason.Timeout) };

        var inputs = SystemInputsBuilder.Build(facts, Wmi(), SI.NoEvents);

        IsMissing(inputs.OsVersion, CheckReason.Timeout);
        IsMissing(inputs.UptimeSeconds, CheckReason.Timeout);
    }

    // ---- the CPU ----

    [Fact]
    public void The_cpu_readings_are_the_names_and_numbers_of_core()
    {
        var inputs = Build();

        Assert.Equal("AMD Ryzen 9 7950X 16-Core Processor", inputs.CpuModel.Value);
        Assert.Equal(16, inputs.CpuCores.Value);
        Assert.Equal(32, inputs.CpuLogical.Value);
        Assert.Equal(4500d, inputs.CpuRatedMhz.Value);
    }

    [Fact]
    public void A_core_count_or_a_clock_of_zero_is_windows_not_telling()
    {
        var cpu = Hw.Cpu() with { PhysicalCores = Hw.V(0), LogicalProcessors = Hw.V(0), BaseClockMhz = Hw.V(0) };

        var inputs = Build(Hw.Machine(cpu: cpu));

        IsMissing(inputs.CpuCores, CheckReason.Unsupported);
        IsMissing(inputs.CpuLogical, CheckReason.Unsupported);
        IsMissing(inputs.CpuRatedMhz, CheckReason.Unsupported);
    }

    [Fact]
    public void A_cpu_leaf_that_was_denied_and_a_cpu_that_did_not_come_say_why()
    {
        var cpu = Hw.Cpu() with { Name = Value.Denied<string>("test") };
        var inputs = Build(Hw.Machine(cpu: cpu));
        IsMissing(inputs.CpuModel, CheckReason.Denied);
        Assert.True(inputs.CpuCores.HasValue);

        var none = SystemInputsBuilder.Build(Facts(Hw.Machine()) with { Cpu = CheckReading<CpuHardware>.TimedOut() }, Wmi(), SI.NoEvents);
        IsMissing(none.CpuModel, CheckReason.Timeout);
        IsMissing(none.CpuCores, CheckReason.Timeout);
        IsMissing(none.CpuLogical, CheckReason.Timeout);
        IsMissing(none.CpuRatedMhz, CheckReason.Timeout);
    }

    // ---- memory ----

    [Fact]
    public void Only_the_installed_modules_are_listed_an_empty_slot_is_not_a_module()
    {
        var memory = Hw.Memory(Hw.Module(gib: 16), Hw.Module(gib: 0), Hw.Module(gib: 32));

        var modules = Build(Hw.Machine(memory: memory)).Memory.Value;

        Assert.Equal(2, modules.Count);
        Assert.Equal(16L * 1024 * 1024 * 1024, modules[0].CapacityBytes.Value);
        Assert.Equal(32L * 1024 * 1024 * 1024, modules[1].CapacityBytes.Value);
    }

    [Fact]
    public void A_slot_whose_capacity_could_not_be_read_is_kept_with_the_capacity_missing()
    {
        var unknown = Hw.Module() with { CapacityBytes = Value.Denied<long>("test") };

        var modules = Build(Hw.Machine(memory: Hw.Memory(Hw.Module(), unknown))).Memory.Value;

        Assert.Equal(2, modules.Count);
        IsMissing(modules[1].CapacityBytes, CheckReason.Denied);
        Assert.True(modules[1].RatedMtPerSec.HasValue);
    }

    [Fact]
    public void The_two_speeds_are_the_modules_own_and_a_memory_that_did_not_come_has_no_list()
    {
        var modules = Build(Hw.Machine(memory: Hw.Memory(Hw.Module(rated: 5600, configured: 4800)))).Memory.Value;
        Assert.Equal(5600, modules[0].RatedMtPerSec.Value);
        Assert.Equal(4800, modules[0].ConfiguredMtPerSec.Value);

        var none = SystemInputsBuilder.Build(Facts(Hw.Machine()) with { Memory = CheckReading<MemoryHardware>.Failed() }, Wmi(), SI.NoEvents);
        IsMissing(none.Memory, CheckReason.Error);
    }

    // ---- GPUs ----

    [Fact]
    public void A_gpu_takes_its_status_from_the_controller_of_the_same_pci_vendor_and_device()
    {
        var wmi = Wmi(video: [new VideoControllerRow(@"PCI\VEN_1002&DEV_164E&SUBSYS_1", 0), Nvidia(43)]);

        var gpu = Build(wmi: wmi).Gpus.Value.Single();

        Assert.Equal("NVIDIA GeForce RTX 5070", gpu.Name.Value);
        Assert.Equal("32.0.15.9999", gpu.DriverVersion.Value);
        Assert.Equal(new DateOnly(2026, 10, 1), gpu.DriverDate.Value);
        Assert.Equal(43, gpu.DeviceStatus.Value);
    }

    [Fact]
    public void The_pci_ids_are_compared_whatever_their_case()
    {
        var wmi = Wmi(video: [new VideoControllerRow(@"pci\ven_10de&dev_2704&subsys_0", 22)]);

        var gpu = Build(Hw.Machine(gpus: [Hw.Gpu(pci: " 10de:2704 ")]), wmi).Gpus.Value.Single();

        Assert.Equal(22, gpu.DeviceStatus.Value);
    }

    [Fact]
    public void An_adapter_with_no_controller_of_its_ids_has_no_status_and_never_a_guess()
    {
        IsMissing(Build(wmi: Wmi(video: [])).Gpus.Value.Single().DeviceStatus, CheckReason.Error);
        IsMissing(Build(wmi: Wmi(video: [new VideoControllerRow(@"PCI\VEN_1002&DEV_164E", 0)])).Gpus.Value.Single().DeviceStatus, CheckReason.Error);
        IsMissing(Build(wmi: Wmi(video: [new VideoControllerRow(@"ROOT\BASICDISPLAY\0000", 0)])).Gpus.Value.Single().DeviceStatus, CheckReason.Error);
    }

    [Fact]
    public void A_controller_whose_code_did_not_come_leaves_the_status_an_error()
    {
        IsMissing(Build(wmi: Wmi(video: [Nvidia(null)])).Gpus.Value.Single().DeviceStatus, CheckReason.Error);
    }

    [Fact]
    public void Two_identical_adapters_need_two_controllers_that_agree()
    {
        var twins = Hw.Machine(gpus: [Hw.Gpu(), Hw.Gpu()]);

        var both = Build(twins, Wmi(video: [Nvidia(0), Nvidia(0)])).Gpus.Value;
        Assert.All(both, static g => Assert.Equal(0, g.DeviceStatus.Value));

        // One fewer controller than adapters, or two that disagree: which adapter has the error is not known.
        Assert.All(Build(twins, Wmi(video: [Nvidia(43)])).Gpus.Value, static g => IsMissing(g.DeviceStatus, CheckReason.Error));
        Assert.All(Build(twins, Wmi(video: [Nvidia(0), Nvidia(43)])).Gpus.Value, static g => IsMissing(g.DeviceStatus, CheckReason.Error));
        Assert.All(Build(twins, Wmi(video: [Nvidia(0), Nvidia(0), Nvidia(0)])).Gpus.Value, static g => IsMissing(g.DeviceStatus, CheckReason.Error));
    }

    [Fact]
    public void An_adapter_whose_pci_id_is_not_known_has_no_status()
    {
        var gpu = Hw.Gpu() with { PciId = Value.Unsupported<string>("test") };

        IsMissing(Build(Hw.Machine(gpus: [gpu])).Gpus.Value.Single().DeviceStatus, CheckReason.Error);
    }

    [Fact]
    public void When_the_controllers_did_not_come_every_status_says_why_and_the_other_readings_stay()
    {
        var wmi = Wmi() with { VideoControllers = CheckReading<IReadOnlyList<VideoControllerRow>>.Missing(CheckReason.Denied) };

        var gpu = Build(wmi: wmi).Gpus.Value.Single();

        IsMissing(gpu.DeviceStatus, CheckReason.Denied);
        Assert.True(gpu.Name.HasValue);
        Assert.True(gpu.DriverDate.HasValue);
    }

    [Theory]
    [InlineData("2026-10-01", true)]
    [InlineData("2024-02-29", true)]
    [InlineData("2026-13-01", false)]
    [InlineData("10/01/2026", false)]
    [InlineData("garbage", false)]
    public void The_driver_date_is_a_day_in_the_form_core_writes_it_and_anything_else_is_an_error(string text, bool parsed)
    {
        var gpu = Build(Hw.Machine(gpus: [Hw.Gpu(driverDate: text)])).Gpus.Value.Single();

        Assert.Equal(parsed, gpu.DriverDate.HasValue);
        if (!parsed)
        {
            Assert.Equal(CheckReason.Error, gpu.DriverDate.Reason);
        }
    }

    [Fact]
    public void A_gpu_list_that_did_not_come_has_no_gpus()
    {
        var none = SystemInputsBuilder.Build(Facts(Hw.Machine()) with { Gpus = CheckReading<IReadOnlyList<GpuHardware>>.Missing(CheckReason.Unsupported) }, Wmi(), SI.NoEvents);

        IsMissing(none.Gpus, CheckReason.Unsupported);
    }

    // ---- disks and volumes ----

    [Fact]
    public void Disks_are_numbered_in_the_order_of_their_drive_numbers_and_each_has_the_health_of_its_own_row()
    {
        var machine = Hw.Machine(storage:
        [
            Hw.Disk(5, "Third Model", "SERIAL-5"),
            Hw.Disk(0, "First Model", Hw.SerialOfDisk0),
            Hw.Disk(2, "Second Model", "SERIAL-2"),
        ]);
        var wmi = Wmi(health: Health(
            new PhysicalDiskRow("0", Hw.SerialOfDisk0, 2, [2]),
            new PhysicalDiskRow("2", "SERIAL-2", 1, [2]),
            new PhysicalDiskRow("5", "SERIAL-5", 0, [2])));

        var disks = Build(machine, wmi).Disks.Value;

        Assert.Equal(["First Model", "Second Model", "Third Model"], disks.Select(static d => d.Model!));
        Assert.Equal(["critical", "warning", "healthy"], disks.Select(static d => d.Health.Text!));
        Assert.All(disks, static d => Assert.False(d.Unreadable));
    }

    [Fact]
    public void A_row_whose_serial_is_another_disks_leaves_that_disk_unavailable_with_an_error()
    {
        var wmi = Wmi(health: Health(new PhysicalDiskRow("0", "NOT-THE-SERIAL-OF-DISK-0", 0, [2]), new PhysicalDiskRow("1", Hw.SerialOfDisk1, 0, [2])));

        var disks = Build(wmi: wmi).Disks.Value;

        Assert.Null(disks[0].Health.Text);
        Assert.Equal(CheckReason.Error, disks[0].Health.Reason);
        Assert.Equal("healthy", disks[1].Health.Text);
    }

    [Fact]
    public void A_slot_core_could_not_look_at_is_listed_unreadable_and_asks_for_the_health_of_its_number()
    {
        var machine = Hw.Machine(storage: [Hw.Disk(0, "Model", Hw.SerialOfDisk0), Hw.UnreadableDisk(1)]);
        var wmi = Wmi(health: Health(new PhysicalDiskRow("0", Hw.SerialOfDisk0, 0, [2]), new PhysicalDiskRow("1", null, 1, [2])));

        var disks = Build(machine, wmi).Disks.Value;

        Assert.False(disks[0].Unreadable);
        Assert.True(disks[1].Unreadable);
        Assert.Null(disks[1].Model);
        Assert.Equal("warning", disks[1].Health.Text); // Windows's row for that number needs no serial of ours
    }

    [Fact]
    public void A_disk_with_a_model_but_no_capacity_is_still_readable()
    {
        var disk = Hw.Disk(0, "Model", Hw.SerialOfDisk0) with { CapacityBytes = Value.Failed<long>("test") };

        Assert.False(Build(Hw.Machine(storage: [disk])).Disks.Value.Single().Unreadable);
    }

    [Fact]
    public void A_disk_whose_model_is_blank_has_no_model()
    {
        var disk = Hw.Disk(0, "   ", Hw.SerialOfDisk0);

        Assert.Null(Build(Hw.Machine(storage: [disk])).Disks.Value.Single().Model);
    }

    [Fact]
    public void The_disks_say_no_serial_no_number_and_no_label_anywhere()
    {
        var disks = Build().Disks.Value;

        var text = string.Join(" ", disks.Select(static d => d.ToString()));
        Assert.DoesNotContain(Hw.SerialOfDisk0, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Hw.SerialOfDisk1, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Volumes_are_in_the_order_of_their_letters_and_each_names_the_one_disk_it_lives_on()
    {
        var machine = Hw.Machine(storage:
        [
            Hw.Disk(7, "Data Model", "S7", Hw.Volume("D:", 2000, 1000)),
            Hw.Disk(3, "System Model", "S3", Hw.Volume("C:", 1000, 500)),
        ]);

        var volumes = Build(machine, Wmi(health: Health())).Volumes.Value;

        Assert.Equal([1000L, 2000L], volumes.Select(static v => v.TotalBytes.Value));
        Assert.Equal([1, 2], volumes.Select(static v => v.DiskOrdinal!.Value)); // the disk of drive number 3 is disk-1
    }

    [Fact]
    public void A_volume_on_two_disks_is_listed_once_and_says_no_disk()
    {
        var machine = Hw.Machine(storage:
        [
            Hw.Disk(0, "A", "SA", Hw.Volume("E:", 3000, 1000)),
            Hw.Disk(1, "B", "SB", Hw.Volume("E:", 3000, 1000)),
        ]);

        var volumes = Build(machine, Wmi(health: Health())).Volumes.Value;

        var only = Assert.Single(volumes);
        Assert.Null(only.DiskOrdinal);
    }

    [Fact]
    public void Letters_that_differ_only_in_case_are_one_volume()
    {
        var machine = Hw.Machine(storage: [Hw.Disk(0, "A", "SA", Hw.Volume("c:", 1000, 500), Hw.Volume("C:", 1000, 500))]);

        Assert.Single(Build(machine, Wmi(health: Health())).Volumes.Value);
    }

    [Fact]
    public void A_volume_whose_size_or_free_bytes_were_denied_keeps_the_reading_that_came()
    {
        var volume = Hw.Volume("C:", 1000, 500) with { TotalBytes = Value.Denied<long>("test") };

        var item = Build(Hw.Machine(storage: [Hw.Disk(0, "A", "SA", volume)]), Wmi(health: Health())).Volumes.Value.Single();

        IsMissing(item.TotalBytes, CheckReason.Denied);
        Assert.Equal(500L, item.FreeBytes.Value);
    }

    [Fact]
    public void When_the_drives_did_not_come_there_are_no_disks_and_no_volumes()
    {
        var facts = Facts(Hw.Machine()) with { Storage = CheckReading<IReadOnlyList<StorageDevice>>.TimedOut() };

        var inputs = SystemInputsBuilder.Build(facts, Wmi(), SI.NoEvents);

        IsMissing(inputs.Disks, CheckReason.Timeout);
        IsMissing(inputs.Volumes, CheckReason.Timeout);
    }

    // ---- page files and events ----

    [Fact]
    public void Page_files_are_numbered_by_name_with_the_unnamed_last_and_the_name_is_dropped()
    {
        var wmi = Wmi(files:
        [
            new PageFileRow(@"D:\pagefile.sys", 2, 20, 200),
            new PageFileRow(null, 9, 90, 900),
            new PageFileRow(@"c:\pagefile.sys", 1, 10, 100),
        ]);

        var files = Build(wmi: wmi).PageFiles.Value;

        Assert.Equal([1L, 2L, 9L], files.Select(static f => f.AllocatedMb!.Value));
        Assert.Equal([10L, 20L, 90L], files.Select(static f => f.CurrentMb!.Value));
        Assert.Equal([100L, 200L, 900L], files.Select(static f => f.PeakMb!.Value));
    }

    [Fact]
    public void A_page_file_size_that_did_not_come_stays_null_and_a_list_that_did_not_come_is_missing()
    {
        var one = Build(wmi: Wmi(files: [new PageFileRow(@"C:\pagefile.sys", null, 1, null)])).PageFiles.Value.Single();
        Assert.Null(one.AllocatedMb);
        Assert.Equal(1L, one.CurrentMb);
        Assert.Null(one.PeakMb);

        var wmi = Wmi() with { PageFiles = CheckReading<IReadOnlyList<PageFileRow>>.Missing(CheckReason.Denied), PageFileAutomatic = CheckReading<bool>.Missing(CheckReason.Denied) };
        var none = Build(wmi: wmi);
        IsMissing(none.PageFiles, CheckReason.Denied);
        IsMissing(none.PageFileAutomatic, CheckReason.Denied);
    }

    [Fact]
    public void The_event_counts_are_passed_on_as_they_are()
    {
        var events = SI.NoEvents.ToDictionary(static p => p.Key, static p => p.Value, StringComparer.Ordinal);
        events["system.eventlog.disk7d"] = CheckReading<long>.Ok(4);

        var inputs = SystemInputsBuilder.Build(Facts(Hw.Machine()), Wmi(), events);

        Assert.Same(events, inputs.Events);
    }

    [Fact]
    public void Nothing_that_is_missing_is_refused_by_the_builder()
    {
        Assert.Throws<ArgumentNullException>(() => SystemInputsBuilder.Build(null!, Wmi(), SI.NoEvents));
        Assert.Throws<ArgumentNullException>(() => SystemInputsBuilder.Build(Facts(Hw.Machine()), null!, SI.NoEvents));
        Assert.Throws<ArgumentNullException>(() => SystemInputsBuilder.Build(Facts(Hw.Machine()), Wmi(), null!));
    }
}
