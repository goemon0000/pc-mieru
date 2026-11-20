using PcMieru.Checks.SystemInfo;

namespace PcMieru.Checks.Tests.SystemInfo;

/// <summary>
/// What Windows says of a disk (DESIGN-A 14.1, 14.4): the verdict rule row by row, the join of a row to a disk by its drive number with the
/// serial check, and the one read with its 5 s deadline. No Windows: the rows are scripted, and go through the mapper of the real question.
/// </summary>
public sealed class DiskHealthTests
{
    private static async Task<DiskHealthReading> Read(FakeWmiReader wmi, ManualTimeProvider? clock = null) =>
        await DiskHealthReader.ReadAsync(wmi, clock ?? new ManualTimeProvider(), CancellationToken.None);

    // The rows go through the mapper of the real question, then the join: no read, no clock.
    private static DiskHealthVerdict Of(int driveNumber, string? serial, params IReadOnlyDictionary<string, object?>[] rows) =>
        new DiskHealthReading(CheckReading<IReadOnlyList<PhysicalDiskRow>>.Ok([.. rows.Select(static row => DiskHealthReader.Map(new FakeRow(row)))])).Of(driveNumber, serial);

    // ---- the verdict rule ----

    [Theory]
    [InlineData(0, ItemVerdict.Ok, "healthy")]
    [InlineData(1, ItemVerdict.Caution, "warning")]
    [InlineData(2, ItemVerdict.Check, "critical")]
    public void The_health_status_alone_gives_healthy_warning_or_critical(int status, ItemVerdict verdict, string text)
    {
        var result = DiskHealthReader.Judge(status, []);

        Assert.Equal(text, result.Text);
        Assert.Equal(verdict, result.Verdict);
        Assert.Null(result.Reason);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void An_operational_status_of_degraded_predictive_failure_error_or_non_recoverable_is_critical_whatever_the_health_says(int code)
    {
        Assert.Equal("critical", DiskHealthReader.Judge(0, [2, code]).Text);
        Assert.Equal("critical", DiskHealthReader.Judge(1, [code]).Text);
        Assert.Equal("critical", DiskHealthReader.Judge(null, [code]).Text);
        Assert.Equal(ItemVerdict.Check, DiskHealthReader.Judge(0, [code]).Verdict);
    }

    [Theory]
    [InlineData(0)] // unknown
    [InlineData(1)] // other
    [InlineData(2)] // ok
    [InlineData(4)] // stressed
    [InlineData(8)]
    [InlineData(22)]
    public void The_other_operational_codes_do_not_change_the_verdict(int code)
    {
        Assert.Equal("healthy", DiskHealthReader.Judge(0, [code]).Text);
        Assert.Equal("warning", DiskHealthReader.Judge(1, [code]).Text);
    }

    [Fact]
    public void Two_codes_where_one_is_critical_are_critical()
    {
        Assert.Equal("critical", DiskHealthReader.Judge(0, [2, 4, 6]).Text);
        Assert.Equal("healthy", DiskHealthReader.Judge(0, [2, 4]).Text);
    }

    [Fact]
    public void An_unknown_health_status_is_unsupported_and_none_at_all_is_an_error()
    {
        foreach (var status in new[] { 5, 3, 4, 99, -1 })
        {
            var unknown = DiskHealthReader.Judge(status, []);
            Assert.Null(unknown.Text);
            Assert.Equal(CheckReason.Unsupported, unknown.Reason);
            Assert.Equal(ItemVerdict.Unavailable, unknown.Verdict);
        }

        var none = DiskHealthReader.Judge(null, [2]);
        Assert.Null(none.Text);
        Assert.Equal(CheckReason.Error, none.Reason);
    }

    [Fact]
    public void A_verdict_nobody_made_is_an_unavailable_row_with_the_reason_error()
    {
        var item = default(DiskHealthVerdict).ToItem("system.storage.health", new CheckSubject("disk-1"));

        Assert.Null(item.Value);
        Assert.Equal(ItemVerdict.Unavailable, item.Verdict);
        Assert.Equal(CheckReason.Error, item.Reason);
        Assert.Equal(CheckSource.Wmi, item.Source);
    }

    [Fact]
    public void The_row_of_a_verdict_carries_its_text_and_verdict_and_the_source_wmi()
    {
        var subject = new CheckSubject("disk-2", "Some Model");

        var item = DiskHealthVerdict.Warning.ToItem("storage.health", subject);

        Assert.Equal("storage.health", item.Id);
        Assert.Equal(CheckValue.Text("warning"), item.Value);
        Assert.Equal(ItemVerdict.Caution, item.Verdict);
        Assert.Equal(CheckSource.Wmi, item.Source);
        Assert.Null(item.Reason);
        Assert.Equal(subject, item.Subject);
    }

    // ---- the join: a row is a disk's by its drive number, and its serial must agree ----

    [Fact]
    public void The_row_whose_device_id_is_the_drive_number_is_the_disks()
    {
        var health = Of(
            1,
            Hw.SerialOfDisk1,
            FakeWmiReader.Disk("0", Hw.SerialOfDisk0, health: 2),
            FakeWmiReader.Disk("1", Hw.SerialOfDisk1, health: 1),
            FakeWmiReader.Disk("2", "OTHER", health: 0));

        Assert.Equal("warning", health.Text);
    }

    [Fact]
    public void A_drive_number_is_matched_as_a_whole_not_by_a_prefix()
    {
        var rows = new[] { FakeWmiReader.Disk("10", null, health: 2), FakeWmiReader.Disk("1", null, health: 0) };

        Assert.Equal("healthy", Of(1, null, rows).Text);
        Assert.Equal("critical", Of(10, null, rows).Text);
        Assert.Equal(CheckReason.Error, Of(11, null, rows).Reason);
    }

    [Fact]
    public void A_serial_that_differs_means_the_row_is_another_disks_and_nothing_is_said()
    {
        var health = Of(0, Hw.SerialOfDisk0, FakeWmiReader.Disk("0", "SOMEONE-ELSES-SERIAL", health: 0));

        Assert.Null(health.Text);
        Assert.Equal(CheckReason.Error, health.Reason);
        Assert.Equal(ItemVerdict.Unavailable, health.Verdict);
    }

    [Theory]
    [InlineData("S6B0NG0R000000X", "S6B0NG0R000000X")]
    [InlineData("S6B0NG0R000000X", "  S6B0NG0R000000X  ")]
    [InlineData("S6B0NG0R000000X", "s6b0ng0r000000x")] // the case is not a difference
    [InlineData("S6B0NG0R000000X", "S6B0NG0R000000X....")] // the dots Windows pads NVMe serials with
    [InlineData("S6B0NG0R000000X..", "S6B0NG0R000000X")]
    public void The_same_serial_in_the_forms_the_two_sides_write_it_is_the_same(string ours, string theirs)
    {
        var health = Of(0, ours, FakeWmiReader.Disk("0", theirs, health: 0));

        Assert.Equal("healthy", health.Text);
    }

    [Theory]
    [InlineData(null, "S6B0NG0R000000X")]
    [InlineData("S6B0NG0R000000X", null)]
    [InlineData("", "S6B0NG0R000000X")]
    [InlineData("S6B0NG0R000000X", "   ")]
    [InlineData("....", "S6B0NG0R000000X")]
    [InlineData(null, null)]
    public void When_one_side_has_no_serial_the_drive_number_alone_decides(string? ours, string? theirs)
    {
        var health = Of(0, ours, FakeWmiReader.Disk("0", theirs, health: 1));

        Assert.Equal("warning", health.Text);
    }

    [Fact]
    public void No_row_for_the_drive_number_is_an_error()
    {
        var none = Of(3, Hw.SerialOfDisk0, FakeWmiReader.Disk("0", Hw.SerialOfDisk0));
        var empty = Of(0, Hw.SerialOfDisk0);

        Assert.Equal(CheckReason.Error, none.Reason);
        Assert.Null(none.Text);
        Assert.Equal(CheckReason.Error, empty.Reason);
        Assert.Null(empty.Text);
    }

    [Fact]
    public void Two_rows_for_one_number_are_an_error_because_which_is_the_disk_is_not_known()
    {
        var health = Of(0, null, FakeWmiReader.Disk("0", null, health: 0), FakeWmiReader.Disk("0", null, health: 2));

        Assert.Equal(CheckReason.Error, health.Reason);
        Assert.Null(health.Text);
    }

    [Fact]
    public void A_row_with_no_device_id_is_nobodys()
    {
        var health = Of(0, null, FakeWmiReader.Row(("SerialNumber", "X"), ("HealthStatus", (ushort)0)));

        Assert.Equal(CheckReason.Error, health.Reason);
    }

    // ---- the mapper: the operational status as WMI gives it ----

    [Fact]
    public void The_operational_status_array_of_uint16_is_read_as_the_codes_it_holds()
    {
        var health = Of(0, null, FakeWmiReader.Disk("0", null, health: 0, 2, 5));

        Assert.Equal("critical", health.Text);
    }

    [Fact]
    public void An_operational_status_of_one_number_instead_of_an_array_is_one_code()
    {
        var row = FakeWmiReader.Row(("DeviceId", "0"), ("SerialNumber", null), ("HealthStatus", (ushort)0), ("OperationalStatus", (ushort)6));

        Assert.Equal("critical", Of(0, null, row).Text);
    }

    [Fact]
    public void An_operational_status_that_is_missing_or_odd_is_no_code_and_the_health_status_decides()
    {
        var missing = FakeWmiReader.Row(("DeviceId", "0"), ("HealthStatus", (ushort)1));
        var odd = FakeWmiReader.Row(("DeviceId", "1"), ("HealthStatus", (ushort)0), ("OperationalStatus", new object?[] { "x", null, 6 }));
        var empty = FakeWmiReader.Row(("DeviceId", "2"), ("HealthStatus", (ushort)0), ("OperationalStatus", Array.Empty<ushort>()));

        Assert.Equal("warning", Of(0, null, missing, odd, empty).Text);
        Assert.Equal("critical", Of(1, null, missing, odd, empty).Text); // the 6 among the odd ones still counts
        Assert.Equal("healthy", Of(2, null, missing, odd, empty).Text);
    }

    [Fact]
    public void A_health_status_that_is_missing_or_not_a_number_is_an_error_and_a_number_as_text_is_a_number()
    {
        var missing = FakeWmiReader.Row(("DeviceId", "0"));
        var text = FakeWmiReader.Row(("DeviceId", "1"), ("HealthStatus", "garbage"));
        var asText = FakeWmiReader.Row(("DeviceId", "2"), ("HealthStatus", "2"));

        Assert.Equal(CheckReason.Error, Of(0, null, missing, text, asText).Reason);
        Assert.Equal(CheckReason.Error, Of(1, null, missing, text, asText).Reason);
        Assert.Equal("critical", Of(2, null, missing, text, asText).Text);
    }

    [Fact]
    public void A_device_id_that_is_a_number_is_matched_as_its_text()
    {
        var row = FakeWmiReader.Row(("DeviceId", 4u), ("HealthStatus", (ushort)0));

        Assert.Equal("healthy", Of(4, null, row).Text);
    }

    // ---- the read ----

    [Fact]
    public async Task The_read_asks_the_storage_scope_for_the_four_fixed_properties_and_nothing_else()
    {
        var wmi = new FakeWmiReader().Rows("MSFT_PhysicalDisk");

        await Read(wmi);

        var call = Assert.Single(wmi.Calls);
        Assert.Equal(WmiScopes.Storage, call.Scope);
        Assert.Equal("SELECT DeviceId, SerialNumber, HealthStatus, OperationalStatus FROM MSFT_PhysicalDisk", DiskHealthReader.Query.Text);
        Assert.DoesNotContain("WHERE", DiskHealthReader.Query.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Model", DiskHealthReader.Query.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UniqueId", DiskHealthReader.Query.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_read_has_its_own_deadline_of_five_seconds_from_now()
    {
        var clock = new ManualTimeProvider();
        var wmi = new FakeWmiReader().Rows("MSFT_PhysicalDisk");

        await Read(wmi, clock);

        var deadline = Assert.Single(wmi.Calls).Deadline;
        Assert.Equal(TimeSpan.FromSeconds(5), DiskHealthReader.Budget);
        Assert.Equal(TimeSpan.FromSeconds(5), deadline.Duration);
        Assert.Equal(TimeSpan.FromSeconds(5), deadline.Remaining);
        Assert.False(deadline.IsExpired);
    }

    [Theory]
    [InlineData(CheckReason.Denied)]
    [InlineData(CheckReason.Timeout)]
    [InlineData(CheckReason.Unsupported)]
    public async Task When_the_read_does_not_come_every_disk_is_unavailable_with_the_reason_of_the_read(CheckReason reason)
    {
        var reading = await Read(new FakeWmiReader().Fails("MSFT_PhysicalDisk", reason));

        foreach (var number in new[] { 0, 1, 7 })
        {
            var health = reading.Of(number, Hw.SerialOfDisk0);
            Assert.Null(health.Text);
            Assert.Equal(reason, health.Reason);
            Assert.Equal(ItemVerdict.Unavailable, health.Verdict);
        }
    }

    [Fact]
    public async Task A_read_that_throws_is_an_error_for_every_disk_and_never_throws_out()
    {
        var reading = await Read(new FakeWmiReader().Throws("MSFT_PhysicalDisk", new InvalidOperationException("boom")));

        Assert.Equal(CheckReason.Error, reading.Of(0, null).Reason);
    }

    [Fact]
    public async Task A_stop_by_the_caller_is_passed_on()
    {
        using var cts = new CancellationTokenSource();
        var wmi = new FakeWmiReader().Then("MSFT_PhysicalDisk", (_, _) => Task.FromCanceled<WmiScript>(new CancellationToken(true)));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DiskHealthReader.ReadAsync(wmi, new ManualTimeProvider(), cts.Token));
    }

    [Fact]
    public async Task A_cancellation_that_is_not_the_callers_is_an_error_of_the_read()
    {
        var wmi = new FakeWmiReader().Throws("MSFT_PhysicalDisk", new OperationCanceledException());

        var reading = await Read(wmi);

        Assert.Equal(CheckReason.Error, reading.Of(0, null).Reason);
    }

    [Fact]
    public void A_reading_that_was_not_made_is_unavailable_with_the_reason_it_was_given()
    {
        var reading = DiskHealthReading.Missing(CheckReason.Timeout);

        Assert.Equal(CheckReason.Timeout, reading.Of(0, null).Reason);
    }

    [Fact]
    public async Task One_read_serves_every_disk_of_the_run()
    {
        var wmi = new FakeWmiReader().Rows("MSFT_PhysicalDisk", FakeWmiReader.MachineDisks());

        var reading = await Read(wmi);
        _ = reading.Of(0, Hw.SerialOfDisk0);
        _ = reading.Of(1, Hw.SerialOfDisk1);

        Assert.Single(wmi.Calls);
    }

    [Fact]
    public async Task The_serial_of_a_row_is_never_in_what_a_verdict_writes()
    {
        var reading = await Read(new FakeWmiReader().Rows("MSFT_PhysicalDisk", FakeWmiReader.Disk("0", Hw.SerialOfDisk0, health: 2)));

        var item = reading.Of(0, Hw.SerialOfDisk0).ToItem("storage.health", new CheckSubject("disk-1"));

        var text = item + " " + item.Value + " " + item.Subject;
        Assert.DoesNotContain(Hw.SerialOfDisk0, text, StringComparison.Ordinal);
    }
}
