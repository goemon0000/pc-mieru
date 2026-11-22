// New in this repository (no old counterpart: the old readers talked to WMI directly and could not be tested without it).
// They show, with a scripted WMI, the questions, the fall-backs and the deadline of the Windows readers of the configuration code.
using PcMieru.Checks.Build;
using PcMieru.Checks.SystemInfo;
using PcMieru.Checks.Tests.SystemInfo;
using static PcMieru.Checks.Tests.Build.ScriptedWmi;

namespace PcMieru.Checks.Tests.Build;

public class WindowsBuildReadersTests
{
    private static WindowsBuildReaders Readers(ScriptedWmi wmi, ScriptedGpu? gpu = null, ManualTimeProvider? clock = null) =>
        new(wmi, gpu ?? ScriptedGpu.Ok(), clock ?? new ManualTimeProvider());

    private static string MemoryText(int form) => WindowsBuildReaders.MemoryQueries[form].Text;

    /// <summary>The reader says the category could not be read, and why (it is not an empty list).</summary>
    private static async Task<CheckReason> UnreadAsync(Func<Task> read) => (await Assert.ThrowsAsync<BuildCategoryUnreadException>(read)).Reason;

    /// <summary>
    /// What a real timeout looks like to the readers: the question ran the whole of its deadline of 5 s. (A Timeout answer with the clock
    /// left where it was is the other timeout, a lane with no free slot, which refuses at once.)
    /// </summary>
    private static Action PastTheDeadline(ManualTimeProvider clock) => () => clock.Advance(SystemWmiFacts.Budget + TimeSpan.FromSeconds(1));

    // --- The questions are the old ones ---

    [Fact]
    public void The_questions_are_the_old_ones_word_for_word()
    {
        Assert.Equal("SELECT Name FROM Win32_Processor", WindowsBuildReaders.CpuQuery.Text);
        Assert.Equal("SELECT Manufacturer, Product FROM Win32_BaseBoard", WindowsBuildReaders.BoardQuery.Text);
        Assert.Equal(
            new[]
            {
                "SELECT PartNumber, Manufacturer, Capacity, Speed, MemoryType, SMBIOSMemoryType, ConfiguredClockSpeed FROM Win32_PhysicalMemory",
                "SELECT PartNumber, Manufacturer, Capacity, Speed, MemoryType, SMBIOSMemoryType FROM Win32_PhysicalMemory",
                "SELECT PartNumber, Manufacturer, Capacity, Speed, MemoryType FROM Win32_PhysicalMemory",
            },
            WindowsBuildReaders.MemoryQueries.Select(q => q.Text).ToArray());
        Assert.Equal("SELECT Name, PNPDeviceID FROM Win32_VideoController", WindowsBuildReaders.VideoControllerQuery.Text);
        Assert.Equal("SELECT FriendlyName, Model, BusType, MediaType, Size FROM MSFT_PhysicalDisk", WindowsBuildReaders.PhysicalDiskQuery.Text);
        Assert.Equal("SELECT Model, Size, InterfaceType, PNPDeviceID, MediaType FROM Win32_DiskDrive", WindowsBuildReaders.DiskDriveQuery.Text);
        Assert.Equal("SELECT UserFriendlyName, Active FROM WmiMonitorID", WindowsBuildReaders.MonitorQuery.Text);
        Assert.Equal("SELECT Name, PNPDeviceID FROM Win32_CDROMDrive", WindowsBuildReaders.OpticalQuery.Text);
    }

    [Fact]
    public void The_namespaces_are_the_old_ones()
    {
        Assert.Equal(@"root\cimv2", WindowsBuildReaders.CpuQuery.Scope);
        Assert.Equal(@"root\cimv2", WindowsBuildReaders.BoardQuery.Scope);
        Assert.All(WindowsBuildReaders.MemoryQueries, q => Assert.Equal(@"root\cimv2", q.Scope));
        Assert.Equal(@"root\cimv2", WindowsBuildReaders.VideoControllerQuery.Scope);
        Assert.Equal(@"\\.\root\microsoft\windows\storage", WindowsBuildReaders.PhysicalDiskQuery.Scope);
        Assert.Equal(@"root\cimv2", WindowsBuildReaders.DiskDriveQuery.Scope);
        Assert.Equal(@"\\.\root\wmi", WindowsBuildReaders.MonitorQuery.Scope);
        Assert.Equal(@"root\cimv2", WindowsBuildReaders.OpticalQuery.Scope);
    }

    [Fact]
    public void No_question_asks_for_a_serial_number_or_an_identifier_of_the_machine()
    {
        var all = new[]
        {
            WindowsBuildReaders.CpuQuery.Text, WindowsBuildReaders.BoardQuery.Text, WindowsBuildReaders.VideoControllerQuery.Text,
            WindowsBuildReaders.PhysicalDiskQuery.Text, WindowsBuildReaders.DiskDriveQuery.Text, WindowsBuildReaders.MonitorQuery.Text,
            WindowsBuildReaders.OpticalQuery.Text,
        }.Concat(WindowsBuildReaders.MemoryQueries.Select(q => q.Text));
        foreach (var text in all)
        {
            Assert.DoesNotContain("Serial", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("UniqueId", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("UUID", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    // --- CPU and board ---

    [Fact]
    public async Task Cpu_names_are_trimmed_and_a_value_that_is_not_text_is_an_empty_name()
    {
        var wmi = new ScriptedWmi().Rows(
            WindowsBuildReaders.CpuQuery.Text,
            Row(("Name", "  AMD Ryzen 7 5800X 8-Core Processor   ")),
            Row(("Name", 42)),
            Row());
        var cpus = await Readers(wmi).CpusAsync(default);
        Assert.Equal(new[] { "AMD Ryzen 7 5800X 8-Core Processor", "", "" }, cpus.ToArray());
    }

    [Fact]
    public async Task Board_is_the_first_row_or_nothing()
    {
        var one = new ScriptedWmi().Rows(
            WindowsBuildReaders.BoardQuery.Text,
            Row(("Manufacturer", "ASUSTeK COMPUTER INC."), ("Product", "TUF GAMING X670E-PLUS WIFI")),
            Row(("Manufacturer", "Other"), ("Product", "Second row")));
        Assert.Equal(("ASUSTeK COMPUTER INC.", "TUF GAMING X670E-PLUS WIFI"), await Readers(one).BoardAsync(default));

        var none = new ScriptedWmi().Rows(WindowsBuildReaders.BoardQuery.Text);
        Assert.Equal((null, null), await Readers(none).BoardAsync(default));

        var broken = new ScriptedWmi().Reason(WindowsBuildReaders.BoardQuery.Text, CheckReason.Denied);
        Assert.Equal(CheckReason.Denied, await UnreadAsync(() => Readers(broken).BoardAsync(default))); // not "no board": it could not be read
    }

    [Theory]
    [InlineData(CheckReason.Timeout)]
    [InlineData(CheckReason.Denied)]
    [InlineData(CheckReason.Unsupported)]
    [InlineData(CheckReason.Error)]
    [InlineData(CheckReason.Partial)]
    public async Task A_question_with_no_fall_back_passes_its_reason_up(CheckReason reason)
    {
        var wmi = new ScriptedWmi()
            .Reason(WindowsBuildReaders.CpuQuery.Text, reason)
            .Reason(WindowsBuildReaders.MonitorQuery.Text, reason)
            .Reason(WindowsBuildReaders.OpticalQuery.Text, reason);
        var r = Readers(wmi);
        Assert.Equal(reason, await UnreadAsync(() => r.CpusAsync(default)));
        Assert.Equal(reason, await UnreadAsync(() => r.MonitorsAsync(default)));
        Assert.Equal(reason, await UnreadAsync(() => r.OpticalsAsync(default)));
    }

    // --- Memory: three forms, newest first ---

    private static Dictionary<string, object?> StickRow(bool withNewColumns) => withNewColumns
        ? Row(("PartNumber", "CMK64GX5M2B5600Z40"), ("Manufacturer", "Corsair"), ("Capacity", 34359738368UL), ("Speed", 4800u), ("MemoryType", (ushort)0),
              ("SMBIOSMemoryType", 34u), ("ConfiguredClockSpeed", 5600u))
        : Row(("PartNumber", "F4-3200C16-16GVK"), ("Manufacturer", "G Skill Intl"), ("Capacity", 17179869184UL), ("Speed", 3200u), ("MemoryType", (ushort)24));

    [Fact]
    public async Task Memory_uses_the_newest_form_when_windows_has_the_columns()
    {
        var wmi = new ScriptedWmi().Rows(MemoryText(0), StickRow(true));
        var mem = await Readers(wmi).MemoryAsync(default);
        Assert.Equal(new RawMemory("CMK64GX5M2B5600Z40", "Corsair", 34359738368UL, 4800, 34, 0, 5600), Assert.Single(mem));
        Assert.Equal(new[] { MemoryText(0) }, wmi.Asked.Select(a => a.Text).ToArray());
    }

    [Fact]
    public async Task Memory_drops_the_columns_an_old_windows_refuses_one_form_at_a_time()
    {
        var wmi = new ScriptedWmi()
            .Reason(MemoryText(0), CheckReason.Error)
            .Reason(MemoryText(1), CheckReason.Unsupported)
            .Rows(MemoryText(2), StickRow(false));
        var mem = await Readers(wmi).MemoryAsync(default);
        Assert.Equal(new RawMemory("F4-3200C16-16GVK", "G Skill Intl", 17179869184UL, 3200, 0, 24, 0), Assert.Single(mem));
        Assert.Equal(new[] { MemoryText(0), MemoryText(1), MemoryText(2) }, wmi.Asked.Select(a => a.Text).ToArray());
    }

    [Fact]
    public async Task Memory_form_without_the_new_columns_ignores_them_even_if_a_row_has_them()
    {
        // The old reader read only the columns it asked for, so the last form gives 0 for the SMBIOS type and the configured speed.
        var wmi = new ScriptedWmi()
            .Reason(MemoryText(0), CheckReason.Error)
            .Reason(MemoryText(1), CheckReason.Error)
            .Rows(MemoryText(2), StickRow(true));
        var mem = Assert.Single(await Readers(wmi).MemoryAsync(default));
        Assert.Equal(0, mem.SmbiosType);
        Assert.Equal(0u, mem.ConfiguredSpeed);
    }

    [Fact]
    public async Task Memory_an_empty_answer_is_an_answer_and_does_not_ask_the_older_forms()
    {
        var wmi = new ScriptedWmi().Rows(MemoryText(0));
        Assert.Empty(await Readers(wmi).MemoryAsync(default));
        Assert.Single(wmi.Asked);
    }

    [Fact]
    public async Task Memory_no_form_answering_is_unread_with_the_reason_of_the_last_form()
    {
        var wmi = new ScriptedWmi() // nothing is scripted: each question fails
            .Reason(MemoryText(0), CheckReason.Denied)
            .Reason(MemoryText(1), CheckReason.Unsupported);
        Assert.Equal(CheckReason.Error, await UnreadAsync(() => Readers(wmi).MemoryAsync(default)));
        Assert.Equal(3, wmi.Asked.Count);
    }

    [Fact]
    public async Task Memory_out_of_time_is_unread_for_timeout_and_no_older_form_is_tried()
    {
        var clock = new ManualTimeProvider();
        var wmi = new ScriptedWmi().TimesOut(MemoryText(0), PastTheDeadline(clock)).Rows(MemoryText(1), StickRow(false));
        Assert.Equal(CheckReason.Timeout, await UnreadAsync(() => Readers(wmi, clock: clock).MemoryAsync(default)));
        Assert.Single(wmi.Asked);
    }

    [Fact]
    public async Task Memory_stopped_is_unread_for_partial_and_no_older_form_is_tried()
    {
        var wmi = new ScriptedWmi().Reason(MemoryText(0), CheckReason.Partial).Rows(MemoryText(1), StickRow(false));
        Assert.Equal(CheckReason.Partial, await UnreadAsync(() => Readers(wmi).MemoryAsync(default)));
        Assert.Single(wmi.Asked);
    }

    [Fact]
    public async Task Memory_a_lane_with_no_free_slot_while_time_is_left_still_tries_the_older_form()
    {
        // The lane refuses at once (Timeout) and the clock has not moved: the 5 s are still there, so the next form is tried (DESIGN-A 6.1).
        var wmi = new ScriptedWmi().Reason(MemoryText(0), CheckReason.Timeout).Rows(MemoryText(1), StickRow(false));
        Assert.Single(await Readers(wmi).MemoryAsync(default));
        Assert.Equal(new[] { MemoryText(0), MemoryText(1) }, wmi.Asked.Select(a => a.Text).ToArray());
    }

    [Fact]
    public async Task Memory_when_every_form_is_refused_for_no_free_slot_it_is_unread_for_timeout()
    {
        var wmi = new ScriptedWmi()
            .Reason(MemoryText(0), CheckReason.Timeout)
            .Reason(MemoryText(1), CheckReason.Timeout)
            .Reason(MemoryText(2), CheckReason.Timeout);
        Assert.Equal(CheckReason.Timeout, await UnreadAsync(() => Readers(wmi).MemoryAsync(default)));
        Assert.Equal(3, wmi.Asked.Count);
    }

    [Fact]
    public async Task Memory_a_value_that_does_not_convert_is_zero_as_before()
    {
        var wmi = new ScriptedWmi().Rows(
            MemoryText(0),
            Row(("PartNumber", "X"), ("Manufacturer", 5), ("Capacity", "not a number"), ("Speed", "3200"), ("MemoryType", (object?)null),
                ("SMBIOSMemoryType", -1L), ("ConfiguredClockSpeed", 99999999999L)));
        var m = Assert.Single(await Readers(wmi).MemoryAsync(default));
        Assert.Equal(new RawMemory("X", "", 0UL, 3200, -1, 0, 0), m); // a manufacturer that is not text is ""; "3200" converts; -1 stays; too big is 0
    }

    // --- Graphics: DXGI, then the video controller ---

    private static readonly RawGpu Card = new("NVIDIA GeForce RTX 5070", 12_817_793_024, true);

    [Fact]
    public async Task Gpu_takes_dxgi_when_it_lists_an_adapter_and_asks_nothing_else()
    {
        var wmi = new ScriptedWmi();
        var gpu = ScriptedGpu.Ok(Card);
        Assert.Equal(new[] { Card }, (await Readers(wmi, gpu).GpusAsync(default)).ToArray());
        Assert.Equal(1, gpu.Asked);
        Assert.Empty(wmi.Asked);
    }

    [Theory]
    [InlineData(CheckReason.Error)]
    [InlineData(CheckReason.Denied)]
    [InlineData(CheckReason.Unsupported)]
    public async Task Gpu_falls_back_to_the_video_controller_when_dxgi_cannot_be_read(CheckReason reason)
    {
        var wmi = new ScriptedWmi().Rows(
            WindowsBuildReaders.VideoControllerQuery.Text,
            Row(("Name", "NVIDIA GeForce RTX 3080 Ti"), ("PNPDeviceID", @"PCI\VEN_10DE&DEV_2208&SUBSYS_00000000&REV_A1\4&2")),
            Row(("Name", "Meta Virtual Monitor"), ("PNPDeviceID", @"ROOT\DISPLAY\0000")));
        var gpus = await Readers(wmi, ScriptedGpu.Reason(reason)).GpusAsync(default);
        Assert.Equal(new[] { new RawGpu("NVIDIA GeForce RTX 3080 Ti", 0, true), new RawGpu("Meta Virtual Monitor", 0, false) }, gpus.ToArray());
    }

    [Fact]
    public async Task Gpu_falls_back_when_dxgi_lists_nothing_and_when_it_throws()
    {
        var rows = Row(("Name", "AMD Radeon RX 7900 XTX"), ("PNPDeviceID", @"pci\ven_1002&dev_744c\4&1")); // the PCI prefix is not case sensitive
        var wmi = new ScriptedWmi().Rows(WindowsBuildReaders.VideoControllerQuery.Text, rows);

        var nothing = await Readers(wmi, ScriptedGpu.Ok()).GpusAsync(default);
        Assert.Equal(new RawGpu("AMD Radeon RX 7900 XTX", 0, true), Assert.Single(nothing));

        var thrown = await Readers(wmi, ScriptedGpu.Throws(new DllNotFoundException("dxgi.dll"))).GpusAsync(default);
        Assert.Equal(new RawGpu("AMD Radeon RX 7900 XTX", 0, true), Assert.Single(thrown));
    }

    [Fact]
    public async Task Gpu_out_of_time_is_unread_for_timeout_and_the_video_controller_is_not_asked()
    {
        var clock = new ManualTimeProvider();
        var wmi = new ScriptedWmi().Rows(WindowsBuildReaders.VideoControllerQuery.Text, Row(("Name", "X"), ("PNPDeviceID", @"PCI\X")));
        var gpu = ScriptedGpu.TimesOut(PastTheDeadline(clock));
        Assert.Equal(CheckReason.Timeout, await UnreadAsync(() => Readers(wmi, gpu, clock).GpusAsync(default)));
        Assert.Empty(wmi.Asked);
    }

    [Fact]
    public async Task Gpu_stopped_is_unread_for_partial_and_the_video_controller_is_not_asked()
    {
        var wmi = new ScriptedWmi().Rows(WindowsBuildReaders.VideoControllerQuery.Text, Row(("Name", "X"), ("PNPDeviceID", @"PCI\X")));
        Assert.Equal(CheckReason.Partial, await UnreadAsync(() => Readers(wmi, ScriptedGpu.Reason(CheckReason.Partial)).GpusAsync(default)));
        Assert.Empty(wmi.Asked);
    }

    [Fact]
    public async Task Gpu_a_lane_with_no_free_slot_while_time_is_left_still_tries_the_video_controller()
    {
        // DXGI's lane refused at once (Timeout) and the clock has not moved: the deadline is not expired, so Win32_VideoController is asked.
        var wmi = new ScriptedWmi().Rows(WindowsBuildReaders.VideoControllerQuery.Text, Row(("Name", "NVIDIA GeForce RTX 3080 Ti"), ("PNPDeviceID", @"PCI\VEN_10DE&DEV_2208\4&2")));
        var gpu = ScriptedGpu.Reason(CheckReason.Timeout);
        var gpus = await Readers(wmi, gpu).GpusAsync(default);
        Assert.Equal(new RawGpu("NVIDIA GeForce RTX 3080 Ti", 0, true), Assert.Single(gpus));
        Assert.Equal(1, gpu.Asked);
        Assert.Single(wmi.Asked);
    }

    [Fact]
    public async Task Gpu_when_both_lanes_have_no_free_slot_it_is_unread_for_timeout()
    {
        var wmi = new ScriptedWmi().Reason(WindowsBuildReaders.VideoControllerQuery.Text, CheckReason.Timeout);
        Assert.Equal(CheckReason.Timeout, await UnreadAsync(() => Readers(wmi, ScriptedGpu.Reason(CheckReason.Timeout)).GpusAsync(default)));
        Assert.Single(wmi.Asked);
    }

    [Fact]
    public async Task Gpu_the_fall_back_that_cannot_be_read_gives_its_own_reason()
    {
        var wmi = new ScriptedWmi().Reason(WindowsBuildReaders.VideoControllerQuery.Text, CheckReason.Denied);
        Assert.Equal(CheckReason.Denied, await UnreadAsync(() => Readers(wmi, ScriptedGpu.Reason(CheckReason.Error)).GpusAsync(default)));
    }

    // --- Disks: the storage class, then Win32_DiskDrive ---

    private static Dictionary<string, object?> PhysicalDisk(string name, int bus, int media, ulong size) =>
        Row(("FriendlyName", name), ("Model", "ignored when FriendlyName is there"), ("BusType", (ushort)bus), ("MediaType", (ushort)media), ("Size", size));

    [Fact]
    public async Task Disks_use_the_storage_class_and_ask_nothing_else_when_it_has_rows()
    {
        var wmi = new ScriptedWmi().Rows(
            WindowsBuildReaders.PhysicalDiskQuery.Text,
            PhysicalDisk("Samsung SSD 980 PRO 1TB", 17, 4, 1000204886016UL),
            Row(("Model", "Only a model"), ("BusType", (ushort)11), ("MediaType", (ushort)3), ("Size", 2000398934016UL)));
        var disks = await Readers(wmi).DisksAsync(default);
        Assert.Equal(
            new[] { new RawDisk("Samsung SSD 980 PRO 1TB", 17, 4, 1000204886016UL), new RawDisk("Only a model", 11, 3, 2000398934016UL) },
            disks.ToArray());
        Assert.Single(wmi.Asked);
    }

    [Theory]
    [InlineData(CheckReason.Error)]
    [InlineData(CheckReason.Denied)]
    [InlineData(CheckReason.Unsupported)]
    public async Task Disks_fall_back_to_win32_diskdrive_when_the_storage_class_cannot_be_read(CheckReason reason)
    {
        var wmi = new ScriptedWmi()
            .Reason(WindowsBuildReaders.PhysicalDiskQuery.Text, reason)
            .Rows(
                WindowsBuildReaders.DiskDriveQuery.Text,
                Row(("Model", "TOSHIBA MG04ACA200E"), ("Size", 2000396321280UL), ("InterfaceType", "IDE"), ("PNPDeviceID", @"IDE\DISKTOSHIBA_MG04ACA200E\1"), ("MediaType", "Fixed hard disk media")),
                Row(("Model", "SanDisk Cruzer Blade"), ("Size", 16005464064UL), ("InterfaceType", "USB"), ("PNPDeviceID", @"USBSTOR\DISK&VEN_SANDISK\4C53"), ("MediaType", "Removable Media")));
        var disks = await Readers(wmi).DisksAsync(default);
        Assert.Equal(
            new[] { new RawDisk("TOSHIBA MG04ACA200E", 11, 0, 2000396321280UL), new RawDisk("SanDisk Cruzer Blade", 7, 0, 16005464064UL) },
            disks.ToArray());
    }

    [Fact]
    public async Task Disks_fall_back_when_the_storage_class_has_no_row()
    {
        var wmi = new ScriptedWmi()
            .Rows(WindowsBuildReaders.PhysicalDiskQuery.Text)
            .Rows(WindowsBuildReaders.DiskDriveQuery.Text, Row(("Model", "WDC WD20EZAZ-00GGJB0"), ("Size", 2000396321280UL), ("InterfaceType", "IDE"), ("PNPDeviceID", @"IDE\DISK\1"), ("MediaType", "Fixed hard disk media")));
        Assert.Single(await Readers(wmi).DisksAsync(default));
    }

    [Fact]
    public async Task Disks_out_of_time_is_unread_for_timeout_and_win32_diskdrive_is_not_asked()
    {
        var clock = new ManualTimeProvider();
        var wmi = new ScriptedWmi()
            .TimesOut(WindowsBuildReaders.PhysicalDiskQuery.Text, PastTheDeadline(clock))
            .Rows(WindowsBuildReaders.DiskDriveQuery.Text, Row(("Model", "X")));
        Assert.Equal(CheckReason.Timeout, await UnreadAsync(() => Readers(wmi, clock: clock).DisksAsync(default)));
        Assert.Single(wmi.Asked);
    }

    [Fact]
    public async Task Disks_stopped_is_unread_for_partial_and_win32_diskdrive_is_not_asked()
    {
        var wmi = new ScriptedWmi()
            .Reason(WindowsBuildReaders.PhysicalDiskQuery.Text, CheckReason.Partial)
            .Rows(WindowsBuildReaders.DiskDriveQuery.Text, Row(("Model", "X")));
        Assert.Equal(CheckReason.Partial, await UnreadAsync(() => Readers(wmi).DisksAsync(default)));
        Assert.Single(wmi.Asked);
    }

    [Fact]
    public async Task Disks_a_lane_with_no_free_slot_while_time_is_left_still_tries_win32_diskdrive()
    {
        var wmi = new ScriptedWmi()
            .Reason(WindowsBuildReaders.PhysicalDiskQuery.Text, CheckReason.Timeout)
            .Rows(WindowsBuildReaders.DiskDriveQuery.Text, Row(("Model", "WDC WD20EZAZ-00GGJB0"), ("Size", 2000396321280UL), ("InterfaceType", "IDE"), ("PNPDeviceID", @"IDE\DISK\1"), ("MediaType", "Fixed hard disk media")));
        Assert.Single(await Readers(wmi).DisksAsync(default));
        Assert.Equal(2, wmi.Asked.Count);
    }

    [Fact]
    public async Task Disks_when_the_fall_back_runs_out_of_the_shared_time_it_is_unread_for_timeout()
    {
        // The storage class refused at once, then Win32_DiskDrive ran the whole deadline of the category: the category is unread, Timeout.
        var clock = new ManualTimeProvider();
        var wmi = new ScriptedWmi()
            .Reason(WindowsBuildReaders.PhysicalDiskQuery.Text, CheckReason.Timeout)
            .TimesOut(WindowsBuildReaders.DiskDriveQuery.Text, PastTheDeadline(clock));
        Assert.Equal(CheckReason.Timeout, await UnreadAsync(() => Readers(wmi, clock: clock).DisksAsync(default)));
        Assert.Equal(2, wmi.Asked.Count);
    }

    [Fact]
    public async Task Disks_when_the_storage_class_has_no_row_and_the_fall_back_cannot_be_read_it_is_unread()
    {
        var wmi = new ScriptedWmi().Rows(WindowsBuildReaders.PhysicalDiskQuery.Text); // nothing is scripted for Win32_DiskDrive: it fails
        Assert.Equal(CheckReason.Error, await UnreadAsync(() => Readers(wmi).DisksAsync(default)));
    }

    // --- Monitors, optical drives ---

    [Fact]
    public async Task Monitors_read_the_edid_name_and_a_missing_active_is_active()
    {
        ushort[] Name(string s) => [.. s.Select(ch => (ushort)ch), 0, 0];
        var wmi = new ScriptedWmi().Rows(
            WindowsBuildReaders.MonitorQuery.Text,
            Row(("UserFriendlyName", Name("UG27PJ")), ("Active", true)),
            Row(("UserFriendlyName", Name("OLD")), ("Active", false)),
            Row(("UserFriendlyName", Name("NOACTIVE"))),
            Row(("UserFriendlyName", "not an array"), ("Active", true)));
        var monitors = await Readers(wmi).MonitorsAsync(default);
        Assert.Equal(
            new[] { new RawMonitor("UG27PJ", true), new RawMonitor("OLD", false), new RawMonitor("NOACTIVE", true), new RawMonitor("", true) },
            monitors.ToArray());
    }

    [Fact]
    public async Task Opticals_read_name_and_pnp_id()
    {
        var wmi = new ScriptedWmi().Rows(
            WindowsBuildReaders.OpticalQuery.Text,
            Row(("Name", "HL-DT-ST BD-RE WH16NS60"), ("PNPDeviceID", @"SCSI\CDROM&VEN_HL-DT-ST\4&1")),
            Row());
        Assert.Equal(
            new[] { new RawOptical("HL-DT-ST BD-RE WH16NS60", @"SCSI\CDROM&VEN_HL-DT-ST\4&1"), new RawOptical("", "") },
            (await Readers(wmi).OpticalsAsync(default)).ToArray());
    }

    // --- The deadline ---

    [Fact]
    public async Task Each_category_has_its_own_five_second_deadline_and_the_fall_backs_of_one_category_share_it()
    {
        var clock = new ManualTimeProvider();
        var wmi = new ScriptedWmi(); // nothing is scripted: every question fails, so every fall-back is asked
        var readers = Readers(wmi, ScriptedGpu.Reason(CheckReason.Error), clock);

        await Assert.ThrowsAsync<BuildCategoryUnreadException>(() => readers.MemoryAsync(default));
        var memory = wmi.Deadlines.ToArray();
        Assert.Equal(3, memory.Length);
        Assert.Same(memory[0], memory[1]);
        Assert.Same(memory[0], memory[2]);
        Assert.Equal(SystemWmiFacts.Budget, memory[0].Duration);

        await Assert.ThrowsAsync<BuildCategoryUnreadException>(() => readers.DisksAsync(default));
        var all = wmi.Deadlines.ToArray();
        Assert.Equal(5, all.Length);
        Assert.Same(all[3], all[4]); // MSFT_PhysicalDisk and Win32_DiskDrive
        Assert.NotSame(all[0], all[3]);
    }

    [Fact]
    public void The_budget_of_a_category_is_five_seconds() => Assert.Equal(TimeSpan.FromSeconds(5), SystemWmiFacts.Budget);

    // --- Failure and cancellation ---

    [Fact]
    public async Task A_reader_that_throws_is_an_unread_category_for_every_source()
    {
        var wmi = new ScriptedWmi();
        foreach (var text in new[]
                 {
                     WindowsBuildReaders.CpuQuery.Text, WindowsBuildReaders.BoardQuery.Text, WindowsBuildReaders.VideoControllerQuery.Text,
                     WindowsBuildReaders.PhysicalDiskQuery.Text, WindowsBuildReaders.DiskDriveQuery.Text, WindowsBuildReaders.MonitorQuery.Text,
                     WindowsBuildReaders.OpticalQuery.Text, MemoryText(0), MemoryText(1), MemoryText(2),
                 })
        {
            wmi.Throws(text, new InvalidOperationException("a bug below"));
        }

        var r = Readers(wmi, ScriptedGpu.Throws(new InvalidOperationException("a bug below")));
        Assert.Equal(CheckReason.Error, await UnreadAsync(() => r.CpusAsync(default)));
        Assert.Equal(CheckReason.Error, await UnreadAsync(() => r.BoardAsync(default)));
        Assert.Equal(CheckReason.Error, await UnreadAsync(() => r.MemoryAsync(default)));
        Assert.Equal(CheckReason.Error, await UnreadAsync(() => r.GpusAsync(default)));
        Assert.Equal(CheckReason.Error, await UnreadAsync(() => r.DisksAsync(default)));
        Assert.Equal(CheckReason.Error, await UnreadAsync(() => r.MonitorsAsync(default)));
        Assert.Equal(CheckReason.Error, await UnreadAsync(() => r.OpticalsAsync(default)));
    }

    [Fact]
    public async Task Cancellation_while_a_question_is_asked_is_passed_on()
    {
        var wmi = new ScriptedWmi().Hangs(WindowsBuildReaders.CpuQuery.Text).Hangs(MemoryText(0));
        var r = Readers(wmi);
        using var cts = new CancellationTokenSource();
        var cpus = r.CpusAsync(cts.Token);
        var mem = r.MemoryAsync(cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cpus);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mem);
    }

    [Fact]
    public async Task Cancellation_is_passed_on_even_when_the_answer_came_back_anyway()
    {
        var wmi = new ScriptedWmi().Rows(WindowsBuildReaders.CpuQuery.Text, Row(("Name", "CPU")));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Readers(wmi).CpusAsync(cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Readers(wmi, ScriptedGpu.Ok(Card)).GpusAsync(cts.Token));
    }

    // --- From the readers to a code ---

    [Fact]
    public async Task The_windows_sources_collect_into_the_same_code_as_the_same_material_given_by_hand()
    {
        var wmi = new ScriptedWmi()
            .Rows(WindowsBuildReaders.CpuQuery.Text, Row(("Name", "AMD Ryzen 7 5800X 8-Core Processor ")))
            .Rows(WindowsBuildReaders.BoardQuery.Text, Row(("Manufacturer", "ASUSTeK COMPUTER INC."), ("Product", "TUF GAMING X670E-PLUS WIFI")))
            .Rows(MemoryText(0), StickRow(true), StickRow(true))
            .Rows(WindowsBuildReaders.PhysicalDiskQuery.Text, PhysicalDisk("Samsung SSD 980 PRO 1TB", 17, 4, 1000204886016UL))
            .Rows(WindowsBuildReaders.OpticalQuery.Text);
        var viaWindows = await BuildCollector.CollectAsync(BuildSources.Windows(Readers(wmi, ScriptedGpu.Ok(Card))));

        var byHand = await BuildCollector.CollectAsync(new BuildSources
        {
            Cpus = _ => Task.FromResult<IReadOnlyList<string>>(["AMD Ryzen 7 5800X 8-Core Processor"]),
            Board = _ => Task.FromResult<(string?, string?)>(("ASUSTeK COMPUTER INC.", "TUF GAMING X670E-PLUS WIFI")),
            Memory = _ => Task.FromResult<IReadOnlyList<RawMemory>>([new RawMemory("CMK64GX5M2B5600Z40", "Corsair", 34359738368UL, 4800, 34, 0, 5600), new RawMemory("CMK64GX5M2B5600Z40", "Corsair", 34359738368UL, 4800, 34, 0, 5600)]),
            Gpus = _ => Task.FromResult<IReadOnlyList<RawGpu>>([Card]),
            Disks = _ => Task.FromResult<IReadOnlyList<RawDisk>>([new RawDisk("Samsung SSD 980 PRO 1TB", 17, 4, 1000204886016UL)]),
        });
        var made = BuildCode.Make(viaWindows);
        Assert.Equal(BuildCode.Make(byHand).Code, made.Code);
        Assert.Equal(2, made.Config["ram"].Single().Q); // the two equal sticks are merged by the code builder
        Assert.Equal(5600, made.Config["ram"].Single().Speed);
    }
}
