using Xunit.Abstractions;
using System.Buffers.Binary;
using PcMieru.Core.Hardware;
using PcMieru.Core.Hardware.Model;
using PcMieru.Core.Hardware.Storage;
using PcMieru.Wire;
using WireProtocol = PcMieru.Wire.StorageProtocol;

namespace PcMieru.Core.Tests.Hardware;

/// <summary>Shared byte images of the health pages and the Helper's answers, built the way the Helper builds them (with its own public pieces).</summary>
internal static class InspectionFixtures
{
    public static string B64(byte[] bytes) => Convert.ToBase64String(bytes);

    public static void Write128(byte[] page, int offset, UInt128 value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(offset), (ulong)(value & ulong.MaxValue));
        BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(offset + 8), (ulong)(value >> 64));
    }

    /// <summary>An NVMe health log page: 94,140,625 data units written, 24 error log entries, 3 unsafe shutdowns.</summary>
    public static byte[] NvmePage()
    {
        var p = new byte[512];
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(1), 309);
        p[3] = 100;
        p[4] = 10;
        p[5] = 7;
        Write128(p, 48, 94140625);
        Write128(p, 128, 8000);
        Write128(p, 144, 3);
        Write128(p, 176, 24);
        return p;
    }

    public static byte[] SmartTable()
    {
        var t = new byte[512];
        t[0] = 0x10;
        t[2] = 9;
        t[5] = 99;
        t[6] = 99;
        t[7] = 42;
        return t;
    }

    public static byte[] SmartLimits()
    {
        var t = new byte[512];
        t[2] = 9;
        t[3] = 0;
        return t;
    }

    /// <summary>A 16-entry log 0x01 page: (error count, Status Code Type) per entry, an LBA and a command id in every used entry.</summary>
    public static byte[] ErrorLog(params (ulong Count, int StatusCodeType)[] entries)
    {
        var page = new byte[WireProtocol.NvmeErrorLogLength];
        for (var i = 0; i < entries.Length; i++)
        {
            var e = page.AsSpan(i * WireProtocol.NvmeErrorEntryLength, WireProtocol.NvmeErrorEntryLength);
            BinaryPrimitives.WriteUInt64LittleEndian(e, entries[i].Count);
            BinaryPrimitives.WriteUInt16LittleEndian(e[10..], 0x1234);
            BinaryPrimitives.WriteUInt16LittleEndian(e[12..], (ushort)((entries[i].StatusCodeType << 9) | 1));
            BinaryPrimitives.WriteUInt64LittleEndian(e[16..], 0xDEADBEEFCAFE0000UL + (ulong)i);
        }

        return page;
    }

    public static readonly DiskIdentity NvmeDisk = new("Samsung SSD 980 PRO 2TB", "0025_38B5_21A0_1234", "eui.0025385b21a01234", "", 2000398934016, 17);

    public static readonly DiskIdentity SataDisk = new("ATA Example SSD", "SN%7|A_", "naa.50014ee2b8c0ffee", "1:0:3:0", 1000204886016, 11);

    /// <summary>What a NEW Helper's NVMe section holds for a drive it read: health page, the raw error page (kept for old callers), the counts and the identity.</summary>
    public static ActionResult NvmeSection(byte[] page, byte[]? errorLog, DiskIdentity? identity, bool counts = true, OperationCode code = OperationCode.Ok)
    {
        var data = new Dictionary<string, string> { [HelperDataKeys.Bus] = "nvme", [HelperDataKeys.NvmeHealth] = B64(page) };
        if (errorLog is not null)
        {
            data[HelperDataKeys.NvmeErrors] = B64(errorLog);
            if (counts && WireProtocol.CountNvmeErrorLog(errorLog) is { } c)
            {
                data[HelperDataKeys.NvmeErrorLogEntries] = c.Entries.ToString(System.Globalization.CultureInfo.InvariantCulture);
                data[HelperDataKeys.NvmeErrorLogMedia] = c.MediaErrors.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        identity?.WriteTo(data);
        return ActionResult.Of(code, code == OperationCode.Ok ? MessageKeys.DiskRead : MessageKeys.DiskDenied).WithData(data);
    }

    public static ActionResult SataSection(byte[] table, byte[]? limits, DiskIdentity? identity, string? predict = null, OperationCode code = OperationCode.Ok)
    {
        var data = new Dictionary<string, string> { [HelperDataKeys.Bus] = "ata", [HelperDataKeys.SmartData] = B64(table) };
        if (limits is not null)
        {
            data[HelperDataKeys.SmartThresholds] = B64(limits);
        }

        if (predict is not null)
        {
            data[HelperDataKeys.SmartSource] = "predict-failure";
            data[HelperDataKeys.PredictFailure] = predict;
        }

        identity?.WriteTo(data);
        return ActionResult.Of(code, code == OperationCode.Ok ? MessageKeys.DiskRead : MessageKeys.DiskDenied).WithData(data);
    }

    /// <summary>The Helper client that answers every request with one combined batch answer and records the requests.</summary>
    public sealed class BatchHelper(ActionResult combined) : IHelperClient
    {
        public List<HelperRequest> Requests { get; } = [];

        public Task<HelperOutcome> RunAsync(HelperRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            var data = combined.Data is null ? null : new Dictionary<string, string>(combined.Data);
            return Task.FromResult(new HelperOutcome(combined.Code, false, new HelperResult(request.Nonce, HelperOperations.Get(request.Op).Name, combined.Code, null, data)));
        }
    }

    /// <summary>An <see cref="IElevatedStorageReader"/> written before the evidence existed: it only knows the two raw reads.</summary>
    public sealed class OldReader(Dictionary<int, byte[]?> bytes) : IElevatedStorageReader
    {
        public Task<byte[]?> ReadRawAsync(int physicalDriveNumber, StorageHealthKind kind, CancellationToken ct) =>
            Task.FromResult(bytes.GetValueOrDefault(physicalDriveNumber));
    }
}

public class InspectionParserTests
{
    private static byte[] PageWithHigh(int offset)
    {
        var p = InspectionFixtures.NvmePage();
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(offset + 8), 1);
        return p;
    }

    [Fact]
    public void The_nvme_counters_are_read_at_full_precision_and_nothing_is_flagged_when_every_value_fits()
    {
        var counters = InspectionParsers.FromNvme(InspectionFixtures.NvmePage(), new StorageErrorLogCounts(5, 2));

        Assert.NotNull(counters);
        Assert.Equal(StorageHealthKind.Nvme, counters!.Kind);
        Assert.Equal(new U128(24UL), counters.ErrorLogEntries);
        Assert.Equal(new U128(94140625UL), counters.DataUnitsWritten);
        Assert.Equal(new StorageErrorLogCounts(5, 2), counters.RecentErrorLog);
        Assert.False(counters.Saturated);
        Assert.Empty(counters.UnrepresentableCounters);
        Assert.Null(counters.AtaPredictFailure);
        Assert.False(counters.AtaThresholdsRead);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(273)]
    [InlineData(309)]
    [InlineData(343)]
    [InlineData(65535)]
    public void The_nvme_composite_temperature_is_kept_as_the_raw_kelvin_of_bytes_1_and_2(int kelvin)
    {
        var page = InspectionFixtures.NvmePage();
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(1), (ushort)kelvin);

        var counters = InspectionParsers.FromNvme(page, null)!;

        Assert.Equal(kelvin, counters.NvmeCompositeKelvin);
    }

    [Fact]
    public void A_sata_read_has_no_nvme_temperature()
    {
        Assert.Null(InspectionParsers.ForAta(null, true).NvmeCompositeKelvin);
    }

    [Fact]
    public void A_counter_above_64_bits_keeps_its_full_value_and_is_flagged_as_one_the_old_ulong_clipped()
    {
        var page = InspectionFixtures.NvmePage();
        var big = ((UInt128)1 << 64) | 5;
        InspectionFixtures.Write128(page, 48, big);

        var counters = InspectionParsers.FromNvme(page, null)!;

        Assert.Equal(new U128(big), counters.DataUnitsWritten);
        Assert.True(counters.Saturated);
        Assert.Equal([StorageLegacyCounterKeys.DataUnitsWritten], counters.UnrepresentableCounters.ToArray());
        Assert.Equal(new U128(24UL), counters.ErrorLogEntries);
    }

    [Theory]
    [InlineData(32, null)]
    [InlineData(48, "nvme.data_units_written")]
    [InlineData(64, null)]
    [InlineData(80, null)]
    [InlineData(96, null)]
    [InlineData(112, null)]
    [InlineData(128, "nvme.power_on_hours")]
    [InlineData(144, "nvme.unsafe_shutdowns")]
    [InlineData(160, "nvme.media_errors")]
    [InlineData(176, "nvme.error_log_entries")]
    public void Each_of_the_ten_counters_the_old_code_read_into_ulong_flags_saturation_and_the_stored_five_are_named(int offset, string? key)
    {
        var counters = InspectionParsers.FromNvme(PageWithHigh(offset), null)!;

        Assert.True(counters.Saturated);
        Assert.Equal(key is null ? [] : new[] { key }, counters.UnrepresentableCounters.ToArray());
    }

    [Fact]
    public void A_high_half_outside_the_ten_counters_is_not_saturation()
    {
        Assert.False(InspectionParsers.FromNvme(PageWithHigh(200), null)!.Saturated);
        Assert.False(InspectionParsers.FromNvme(PageWithHigh(16), null)!.Saturated);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(511)]
    public void A_short_health_page_gives_no_counters_rather_than_zeros(int length) =>
        Assert.Null(InspectionParsers.FromNvme(new byte[length], null));

    [Fact]
    public void Sata_counters_say_only_whether_the_thresholds_and_the_prediction_window_were_read()
    {
        Assert.Equal(new StorageInspectionCounters(StorageHealthKind.Sata, null, null, null, false, [], null, false).Kind, InspectionParsers.ForAta(null, false).Kind);

        var one = InspectionParsers.FromBytes(StorageHealthKind.Sata, new byte[512], null, null)!;
        var two = InspectionParsers.FromBytes(StorageHealthKind.Sata, new byte[1024], null, true)!;

        Assert.False(one.AtaThresholdsRead);
        Assert.Null(one.AtaPredictFailure);
        Assert.True(two.AtaThresholdsRead);
        Assert.True(two.AtaPredictFailure);
        Assert.Null(two.ErrorLogEntries);
        Assert.Null(two.DataUnitsWritten);
        Assert.Null(two.RecentErrorLog);
    }

    [Fact]
    public void No_bytes_means_no_counters_for_either_bus()
    {
        Assert.Null(InspectionParsers.FromBytes(StorageHealthKind.Nvme, null, new StorageErrorLogCounts(1, 1), null));
        Assert.Null(InspectionParsers.FromBytes(StorageHealthKind.Sata, null, null, true));
    }

    [Fact]
    public void The_core_side_error_log_counts_agree_with_the_wire_counter_and_a_short_page_is_not_read()
    {
        var page = InspectionFixtures.ErrorLog((3, 2), (2, 0), (1, 2));

        Assert.Equal(new StorageErrorLogCounts(3, 2), InspectionParsers.ErrorLogOf(page));
        Assert.Null(InspectionParsers.ErrorLogOf(page.AsSpan(0, 1023)));
        Assert.Equal(new StorageErrorLogCounts(0, 0), InspectionParsers.ErrorLogOf(new byte[1024]));
    }

    [Fact]
    public void The_counts_record_holds_nothing_that_could_carry_an_lba_or_a_page()
    {
        string[] names = ["EntriesRead", "MediaErrors"];
        Assert.Equal(names, typeof(StorageErrorLogCounts).GetProperties().Select(p => p.Name).Order().ToArray());
        Assert.DoesNotContain(typeof(StorageInspectionCounters).GetProperties(), p => p.PropertyType == typeof(byte[]));
        Assert.DoesNotContain(typeof(StorageInspectionDrive).GetProperties(), p => p.PropertyType == typeof(byte[]));
    }

    private static Dictionary<string, string> Counts(string? entries, string? media, int? drive = null)
    {
        string Key(string name) => drive is { } n ? HelperDataKeys.ForDrive(n, name) : name;
        var d = new Dictionary<string, string>();
        if (entries is not null)
        {
            d[Key(HelperDataKeys.NvmeErrorLogEntries)] = entries;
        }

        if (media is not null)
        {
            d[Key(HelperDataKeys.NvmeErrorLogMedia)] = media;
        }

        return d;
    }

    [Theory]
    [InlineData("0", "0", 0, 0)]
    [InlineData("5", "2", 5, 2)]
    [InlineData("16", "16", 16, 16)]
    public void Both_count_keys_of_a_drive_are_read_as_two_small_numbers(string entries, string media, int e, int m) =>
        Assert.Equal(new StorageErrorLogCounts(e, m), InspectionParsers.ErrorLogFrom(Counts(entries, media, 4), 4));

    [Theory]
    [InlineData(null, "0")]
    [InlineData("0", null)]
    [InlineData("17", "0")]
    [InlineData("5", "6")]
    [InlineData("-1", "0")]
    [InlineData("0", "-1")]
    [InlineData("five", "0")]
    [InlineData("", "")]
    [InlineData(" 5", "2")]
    [InlineData("5.0", "2")]
    [InlineData("99999999999", "0")]
    public void A_missing_or_damaged_count_is_not_delivered(string? entries, string? media) =>
        Assert.Null(InspectionParsers.ErrorLogFrom(Counts(entries, media, 4), 4));

    [Fact]
    public void The_counts_of_another_drive_or_the_unprefixed_ones_are_not_taken_for_this_drive()
    {
        var data = Counts("5", "2", 4);

        Assert.Null(InspectionParsers.ErrorLogFrom(data, 5));
        Assert.Null(InspectionParsers.ErrorLogFrom(data, null));
        Assert.NotNull(InspectionParsers.ErrorLogFrom(Counts("5", "2"), null));
    }

    [Fact]
    public void The_same_identity_on_the_health_handle_is_confirmed()
    {
        Assert.Equal(StorageIdentityConfirmation.Confirmed, InspectionParsers.Confirm(InspectionFixtures.SataDisk, InspectionFixtures.SataDisk with { }));
    }

    public static TheoryData<string, DiskIdentity> OtherDisks() => new()
    {
        { "serial", InspectionFixtures.SataDisk with { Serial = "SN%7|A_X" } },
        { "uniqueId", InspectionFixtures.SataDisk with { UniqueId = "naa.50014ee2b8c0ffef" } },
        { "uniqueId removed", InspectionFixtures.SataDisk with { UniqueId = "" } },
        { "location", InspectionFixtures.SataDisk with { Location = "1:0:4:0" } },
        { "model", InspectionFixtures.SataDisk with { Model = "ATA Example SSD 2" } },
        { "size", InspectionFixtures.SataDisk with { SizeBytes = 1000204886017 } },
        { "bus", InspectionFixtures.SataDisk with { Bus = 3 } },
    };

    [Theory]
    [MemberData(nameof(OtherDisks))]
    public void Any_difference_in_any_of_the_six_fields_is_a_mismatch_so_a_swapped_disk_is_never_confirmed(string field, DiskIdentity other)
    {
        Assert.True(field.Length > 0);
        Assert.Equal(StorageIdentityConfirmation.Mismatch, InspectionParsers.Confirm(InspectionFixtures.SataDisk, other));
        Assert.Equal(StorageIdentityConfirmation.Mismatch, InspectionParsers.Confirm(other, InspectionFixtures.SataDisk));
    }

    [Fact]
    public void No_identity_on_the_health_handle_is_unconfirmed_and_never_the_same()
    {
        Assert.Equal(StorageIdentityConfirmation.Unconfirmed, InspectionParsers.Confirm(InspectionFixtures.SataDisk, null));
    }

    [Theory]
    [InlineData(StorageRecognition.Recognized, ValueStatus.Ok, StorageIdentityConfirmation.Confirmed, true)]
    [InlineData(StorageRecognition.Unreadable, ValueStatus.Ok, StorageIdentityConfirmation.Confirmed, false)]
    [InlineData(StorageRecognition.Recognized, ValueStatus.Denied, StorageIdentityConfirmation.Confirmed, false)]
    [InlineData(StorageRecognition.Recognized, ValueStatus.Ok, StorageIdentityConfirmation.Mismatch, false)]
    [InlineData(StorageRecognition.Recognized, ValueStatus.Ok, StorageIdentityConfirmation.Unconfirmed, false)]
    [InlineData(StorageRecognition.Recognized, ValueStatus.Ok, StorageIdentityConfirmation.NoHealth, false)]
    public void Health_is_comparable_only_when_recognized_read_and_confirmed(StorageRecognition recognition, ValueStatus status, StorageIdentityConfirmation confirmation, bool comparable)
    {
        var health = status == ValueStatus.Ok
            ? Value.Ok(new StorageHealth(StorageHealthKind.Sata, Value.NotCollected<double>("t"), Value.NotCollected<double>("t"), Value.NotCollected<double>("t"), Value.NotCollected<double>("t"), Value.NotCollected<int>("t"), Value.NotCollected<U128>("t"), Value.NotCollected<U128>("t"), Value.NotCollected<U128>("t"), Value.NotCollected<U128>("t"), Value.NotCollected<U128>("t"), Value.NotCollected<U128>("t"), []), "t")
            : Value.Denied<StorageHealth>("t", "needs administrator");
        var drive = new StorageInspectionDrive(0, recognition, "M", "S", "F", 11, 1, "", "", DateTimeOffset.UnixEpoch, health, confirmation, null);

        Assert.Equal(comparable, drive.HealthComparable);
    }
}

/// <summary>
/// The Helper batch answer as Core reads it, against the compatibility matrix: new Core with a new Helper, new Core with an old Helper
/// (no identity keys), and an old Core with a new Helper (extra keys it never looks at). The Helper itself is never started.
/// </summary>
public class HelperIdentityBatchTests
{
    private static readonly byte[] Page = InspectionFixtures.NvmePage();
    private static readonly byte[] Table = InspectionFixtures.SmartTable();
    private static readonly byte[] Limits = InspectionFixtures.SmartLimits();

    private static ActionResult NewHelperAnswer(byte[]? log = null) =>
        DiskOperations.CombineHealth(
        [
            (0, InspectionFixtures.NvmeSection(Page, log ?? InspectionFixtures.ErrorLog((5, 2), (4, 0), (3, 2), (2, 1), (1, 3)), InspectionFixtures.NvmeDisk)),
            (3, InspectionFixtures.SataSection(Table, Limits, InspectionFixtures.SataDisk)),
            (5, InspectionFixtures.SataSection(Table, null, InspectionFixtures.SataDisk with { Serial = "OTHER" }, predict: "true")),
        ]);

    /// <summary>What an OLD Helper answered for the same drives: the same keys, minus identity and the two counts.</summary>
    private static ActionResult OldHelperAnswer() =>
        DiskOperations.CombineHealth(
        [
            (0, InspectionFixtures.NvmeSection(Page, InspectionFixtures.ErrorLog((5, 2), (4, 0), (3, 2), (2, 1), (1, 3)), null, counts: false)),
            (3, InspectionFixtures.SataSection(Table, Limits, null)),
            (5, InspectionFixtures.SataSection(Table, null, null, predict: "true")),
        ]);

    private static readonly (int, StorageHealthKind)[] Asked = [(0, StorageHealthKind.Nvme), (3, StorageHealthKind.Sata), (5, StorageHealthKind.Sata)];

    [Fact]
    public async Task A_new_helper_batch_brings_the_identity_the_counts_and_the_prediction_answer_of_each_drive_it_read()
    {
        var client = new InspectionFixtures.BatchHelper(NewHelperAnswer());

        var read = await new HelperStorageReader(() => client).ReadManyAsync(Asked, CancellationToken.None);

        var request = Assert.Single(client.Requests);
        Assert.Equal(HelperOperation.DiskHealthAll, request.Op);
        Assert.Equal([0, 3, 5], request.Drives!.Numbers.ToArray());

        Assert.Equal(Page, read[0].Bytes);
        Assert.Equal(InspectionFixtures.NvmeDisk, read[0].Identity);
        Assert.Equal(new StorageErrorLogCounts(5, 2), read[0].ErrorLog);
        Assert.Null(read[0].PredictFailure);

        Assert.Equal(1024, read[3].Bytes!.Length);
        Assert.Equal(InspectionFixtures.SataDisk, read[3].Identity);
        Assert.Null(read[3].ErrorLog);
        Assert.Null(read[3].PredictFailure);

        Assert.Equal(512, read[5].Bytes!.Length);
        Assert.Equal(InspectionFixtures.SataDisk with { Serial = "OTHER" }, read[5].Identity);
        Assert.True(read[5].PredictFailure);
    }

    [Fact]
    public async Task A_new_core_with_an_old_helper_shows_the_health_and_cannot_compare_the_identity()
    {
        var client = new InspectionFixtures.BatchHelper(OldHelperAnswer());

        var read = await new HelperStorageReader(() => client).ReadManyAsync(Asked, CancellationToken.None);

        foreach (var number in new[] { 0, 3, 5 })
        {
            Assert.NotNull(read[number].Bytes);
            Assert.Null(read[number].Identity);
            Assert.Null(read[number].ErrorLog);
            Assert.Equal(StorageIdentityConfirmation.Unconfirmed, InspectionParsers.Confirm(InspectionFixtures.NvmeDisk, read[number].Identity));
        }

        // The old Helper's raw error page (with LBAs) is on the wire for old callers; the new reader hands out only the health page.
        Assert.Equal(512, read[0].Bytes!.Length);
        Assert.Equal(Page, read[0].Bytes);
        Assert.True(read[5].PredictFailure);
    }

    [Fact]
    public async Task The_health_of_a_drive_the_old_helper_read_is_still_shown_but_not_comparable()
    {
        var client = new InspectionFixtures.BatchHelper(OldHelperAnswer());
        var steps = new[] { new RealStorageHealthProvider.Step(0, "Samsung SSD 980 PRO 2TB", StorageHealthKind.Nvme, null) };

        var resolved = await RealStorageHealthProvider.ResolveWithEvidenceAsync(steps, null, new HelperStorageReader(() => client), CancellationToken.None);

        var only = Assert.Single(resolved);
        Assert.Equal(ValueStatus.Ok, only.Health.Health.Status);
        Assert.Equal(new U128((UInt128)94140625 * 512_000), only.Health.Health.Data!.DataWrittenBytes.Data);
        Assert.Equal(StorageIdentityConfirmation.Unconfirmed, InspectionParsers.Confirm(InspectionFixtures.NvmeDisk, only.Evidence!.Identity));
    }

    [Fact]
    public async Task An_old_core_reading_a_new_helper_answer_gets_the_same_bytes_as_from_an_old_helper_answer()
    {
        var fromNew = await new HelperStorageReader(() => new InspectionFixtures.BatchHelper(NewHelperAnswer())).ReadRawManyAsync(Asked, CancellationToken.None);
        var fromOld = await new HelperStorageReader(() => new InspectionFixtures.BatchHelper(OldHelperAnswer())).ReadRawManyAsync(Asked, CancellationToken.None);

        Assert.Equal(fromOld.Keys.Order(), fromNew.Keys.Order());
        foreach (var number in fromOld.Keys)
        {
            Assert.NotNull(fromNew[number]);
            Assert.Equal(fromOld[number], fromNew[number]);
        }
    }

    [Fact]
    public void The_new_helper_answer_is_the_old_answer_plus_new_keys_every_old_key_keeps_its_value()
    {
        var old = OldHelperAnswer().Data!;
        var current = NewHelperAnswer().Data!;

        foreach (var (key, value) in old)
        {
            // The one old key whose value differs is drive 5's: the fixture gave it another serial, which is not part of the old keys.
            Assert.True(current.TryGetValue(key, out var now), key);
            Assert.Equal(value, now);
        }

        var added = current.Keys.Except(old.Keys).ToArray();
        Assert.NotEmpty(added);
        Assert.All(added, key =>
        {
            var plain = key[(key.IndexOf('.', 6) + 1)..];
            Assert.True(plain.StartsWith("identity.", StringComparison.Ordinal) || plain.StartsWith("nvme.errorlog.", StringComparison.Ordinal), key);
        });
    }

    [Fact]
    public async Task An_old_core_reading_one_drive_at_a_time_is_not_disturbed_by_the_new_keys()
    {
        var data = InspectionFixtures.NvmeSection(Page, InspectionFixtures.ErrorLog((1, 2)), InspectionFixtures.NvmeDisk).Data!;
        var client = new InspectionFixtures.BatchHelper(ActionResult.Of(OperationCode.Ok, MessageKeys.DiskRead).WithData(data.ToDictionary(p => p.Key, p => p.Value)));

        var raw = await new HelperStorageReader(() => client).ReadRawAsync(0, StorageHealthKind.Nvme, CancellationToken.None);

        Assert.Equal(Page, raw);
        Assert.Equal(HelperOperation.NvmeHealth, Assert.Single(client.Requests).Op);
    }

    [Fact]
    public async Task An_old_reader_implementation_still_works_through_the_default_batch_member()
    {
        var reader = new InspectionFixtures.OldReader(new Dictionary<int, byte[]?> { [0] = Page });
        var steps = new[] { new RealStorageHealthProvider.Step(0, "M", StorageHealthKind.Nvme, null) };

        var resolved = await RealStorageHealthProvider.ResolveWithEvidenceAsync(steps, null, reader, CancellationToken.None);

        var only = Assert.Single(resolved);
        Assert.Equal(ValueStatus.Ok, only.Health.Health.Status);
        Assert.Equal(Page, only.Evidence!.Bytes);
        Assert.Null(only.Evidence.Identity);
        Assert.Equal(StorageIdentityConfirmation.Unconfirmed, InspectionParsers.Confirm(InspectionFixtures.NvmeDisk, only.Evidence.Identity));
    }

    [Fact]
    public async Task A_disk_swapped_between_the_two_reads_is_a_mismatch_and_its_health_is_still_shown()
    {
        // Core saw NvmeDisk before the read; the Helper's handle says it is another disk (renumbered, swapped, or a different disk at that number).
        var other = InspectionFixtures.NvmeDisk with { Serial = "9999_0000_FFFF_0001", UniqueId = "eui.00253800000000ff" };
        var client = new InspectionFixtures.BatchHelper(DiskOperations.CombineHealth([(0, InspectionFixtures.NvmeSection(Page, InspectionFixtures.ErrorLog(), other))]));
        var steps = new[] { new RealStorageHealthProvider.Step(0, "Samsung SSD 980 PRO 2TB", StorageHealthKind.Nvme, null) };

        var resolved = await RealStorageHealthProvider.ResolveWithEvidenceAsync(steps, null, new HelperStorageReader(() => client), CancellationToken.None);

        var only = Assert.Single(resolved);
        Assert.Equal(ValueStatus.Ok, only.Health.Health.Status);
        Assert.Equal(StorageIdentityConfirmation.Mismatch, InspectionParsers.Confirm(InspectionFixtures.NvmeDisk, only.Evidence!.Identity));
        Assert.Equal(StorageIdentityConfirmation.Confirmed, InspectionParsers.Confirm(other, only.Evidence.Identity));
    }

    [Fact]
    public async Task The_same_disk_on_both_reads_is_confirmed()
    {
        var client = new InspectionFixtures.BatchHelper(DiskOperations.CombineHealth([(0, InspectionFixtures.NvmeSection(Page, InspectionFixtures.ErrorLog(), InspectionFixtures.NvmeDisk))]));
        var steps = new[] { new RealStorageHealthProvider.Step(0, "M", StorageHealthKind.Nvme, null) };

        var resolved = await RealStorageHealthProvider.ResolveWithEvidenceAsync(steps, null, new HelperStorageReader(() => client), CancellationToken.None);

        Assert.Equal(StorageIdentityConfirmation.Confirmed, InspectionParsers.Confirm(InspectionFixtures.NvmeDisk, Assert.Single(resolved).Evidence!.Identity));
    }

    [Theory]
    [InlineData("identity.serial")]
    [InlineData("identity.uniqueId")]
    [InlineData("identity.location")]
    [InlineData("identity.model")]
    [InlineData("identity.sizeBytes")]
    [InlineData("identity.bus")]
    public async Task One_identity_key_missing_from_the_helper_answer_is_unconfirmed_not_a_partial_match(string missing)
    {
        var answer = NewHelperAnswer();
        var data = new Dictionary<string, string>(answer.Data!);
        data.Remove("drive.0." + missing);
        var client = new InspectionFixtures.BatchHelper(ActionResult.Of(OperationCode.Ok, MessageKeys.DiskRead).WithData(data));

        var read = await new HelperStorageReader(() => client).ReadManyAsync(Asked, CancellationToken.None);

        Assert.NotNull(read[0].Bytes);
        Assert.Null(read[0].Identity);
        Assert.NotNull(read[3].Identity);
    }

    [Fact]
    public async Task A_damaged_count_is_dropped_but_the_bytes_and_the_identity_are_kept()
    {
        var data = new Dictionary<string, string>(NewHelperAnswer().Data!)
        {
            ["drive.0.nvme.errorlog.entries"] = "99",
            ["drive.0.nvme.errorlog.media"] = "99",
        };
        var client = new InspectionFixtures.BatchHelper(ActionResult.Of(OperationCode.Ok, MessageKeys.DiskRead).WithData(data));

        var read = await new HelperStorageReader(() => client).ReadManyAsync(Asked, CancellationToken.None);

        Assert.Equal(Page, read[0].Bytes);
        Assert.Equal(InspectionFixtures.NvmeDisk, read[0].Identity);
        Assert.Null(read[0].ErrorLog);
    }

    [Fact]
    public async Task A_drive_whose_own_code_is_not_ok_has_no_bytes_and_no_identity_even_if_identity_keys_are_there()
    {
        var combined = DiskOperations.CombineHealth(
        [
            (0, InspectionFixtures.NvmeSection(Page, InspectionFixtures.ErrorLog(), InspectionFixtures.NvmeDisk)),
            (3, InspectionFixtures.SataSection(Table, Limits, InspectionFixtures.SataDisk, code: OperationCode.Denied)),
        ]);
        var data = new Dictionary<string, string>(combined.Data!);
        foreach (var (key, value) in InspectionFixtures.SataSection(Table, Limits, InspectionFixtures.SataDisk).Data!)
        {
            data["drive.3." + key] = value;
        }

        var client = new InspectionFixtures.BatchHelper(ActionResult.Of(OperationCode.PartialSuccess, MessageKeys.DiskRead).WithData(data));
        var read = await new HelperStorageReader(() => client).ReadManyAsync([(0, StorageHealthKind.Nvme), (3, StorageHealthKind.Sata)], CancellationToken.None);

        Assert.NotNull(read[0].Bytes);
        Assert.Null(read[3].Bytes);
        Assert.Null(read[3].Identity);
        Assert.Null(read[3].ErrorLog);
    }

    [Fact]
    public async Task Bytes_the_reader_refused_bring_no_identity_either()
    {
        var data = new Dictionary<string, string>(NewHelperAnswer().Data!) { ["drive.0.nvme.health"] = "AAAA" };
        var client = new InspectionFixtures.BatchHelper(ActionResult.Of(OperationCode.Ok, MessageKeys.DiskRead).WithData(data));

        var read = await new HelperStorageReader(() => client).ReadManyAsync(Asked, CancellationToken.None);

        Assert.Null(read[0].Bytes);
        Assert.Null(read[0].Identity);
        Assert.NotNull(read[3].Identity);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("True", true)]
    [InlineData("maybe", null)]
    [InlineData("", null)]
    public async Task The_prediction_answer_is_true_false_or_not_delivered(string text, bool? expected)
    {
        var combined = DiskOperations.CombineHealth([(5, InspectionFixtures.SataSection(Table, null, null, predict: text))]);
        var client = new InspectionFixtures.BatchHelper(combined);

        var read = await new HelperStorageReader(() => client).ReadManyAsync([(5, StorageHealthKind.Sata)], CancellationToken.None);

        Assert.Equal(expected, read[5].PredictFailure);
    }

    [Fact]
    public async Task The_request_stays_the_fixed_batch_of_at_most_sixteen_drives_in_the_range_0_to_255()
    {
        var drives = Enumerable.Range(0, 20).Select(i => (i, StorageHealthKind.Sata)).Append((256, StorageHealthKind.Sata)).Append((-1, StorageHealthKind.Nvme)).ToList();
        var client = new InspectionFixtures.BatchHelper(DiskOperations.CombineHealth([(0, InspectionFixtures.SataSection(Table, null, InspectionFixtures.SataDisk))]));

        var read = await new HelperStorageReader(() => client).ReadManyAsync(drives, CancellationToken.None);

        Assert.Equal(2, client.Requests.Count);
        Assert.All(client.Requests, r => Assert.Equal(HelperOperation.DiskHealthAll, r.Op));
        Assert.Equal(16, client.Requests[0].Drives!.Numbers.Count);
        Assert.Equal(4, client.Requests[1].Drives!.Numbers.Count);
        Assert.Equal(20, read.Count);
        Assert.DoesNotContain(256, read.Keys);
        Assert.DoesNotContain(-1, read.Keys);
    }

    [Fact]
    public async Task Without_a_helper_nothing_is_asked_and_every_drive_is_not_delivered()
    {
        var read = await new HelperStorageReader(() => null).ReadManyAsync(Asked, CancellationToken.None);

        Assert.Equal(3, read.Count);
        Assert.All(read.Values, r =>
        {
            Assert.Null(r.Bytes);
            Assert.Null(r.Identity);
            Assert.Null(r.ErrorLog);
            Assert.Null(r.PredictFailure);
        });
    }

    [Fact]
    public async Task A_declined_batch_leaves_every_drive_not_delivered_and_the_health_denied()
    {
        var client = new DeclinedHelper();
        var steps = new[] { new RealStorageHealthProvider.Step(0, "M", StorageHealthKind.Nvme, null) };

        var resolved = await RealStorageHealthProvider.ResolveWithEvidenceAsync(steps, null, new HelperStorageReader(() => client), CancellationToken.None);

        var only = Assert.Single(resolved);
        Assert.Equal(ValueStatus.Denied, only.Health.Health.Status);
        Assert.Null(only.Evidence);
    }

    private sealed class DeclinedHelper : IHelperClient
    {
        public Task<HelperOutcome> RunAsync(HelperRequest request, CancellationToken ct = default) =>
            Task.FromResult(new HelperOutcome(OperationCode.Cancelled, true, null));
    }

    [Fact]
    public async Task The_evidence_of_a_drive_read_in_this_process_is_handed_through_and_a_drive_without_bytes_has_none()
    {
        var ok = Value.Ok(
            new StorageHealth(StorageHealthKind.Nvme, Value.NotCollected<double>("t"), Value.NotCollected<double>("t"), Value.NotCollected<double>("t"), Value.NotCollected<double>("t"), Value.NotCollected<int>("t"), Value.NotCollected<U128>("t"), Value.NotCollected<U128>("t"), Value.NotCollected<U128>("t"), Value.NotCollected<U128>("t"), Value.NotCollected<U128>("t"), Value.NotCollected<U128>("t"), []),
            "t");
        var steps = new[]
        {
            new RealStorageHealthProvider.Step(0, "read here", StorageHealthKind.Nvme, ok),
            new RealStorageHealthProvider.Step(1, "failed here", StorageHealthKind.Nvme, Value.Denied<StorageHealth>("t", "x")),
        };
        var own = new ElevatedDriveRead(Page, InspectionFixtures.NvmeDisk, new StorageErrorLogCounts(1, 0), null);

        var resolved = await RealStorageHealthProvider.ResolveWithEvidenceAsync(steps, new Dictionary<int, ElevatedDriveRead> { [0] = own }, null, CancellationToken.None);

        Assert.Same(own, resolved[0].Evidence);
        Assert.Null(resolved[1].Evidence);
        Assert.Equal(["read here", "failed here"], resolved.Select(r => r.Health.Model).ToArray());
    }
}

/// <summary>What an "unreadable slot" is (DESIGN-A 5.2) and what the inspection says about it.</summary>
public class UnreadableSlotTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 20, 3, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void A_drive_number_nobody_has_is_absent_not_unreadable(int error) =>
        Assert.Equal(RealStorageInspectionSource.SlotOutcome.Absent, RealStorageInspectionSource.Classify(error, descriptorRead: false));

    [Theory]
    [InlineData(5)]     // access denied
    [InlineData(21)]    // device not ready
    [InlineData(32)]    // sharing violation
    [InlineData(55)]    // device no longer exists
    [InlineData(1117)]  // I/O device error
    [InlineData(1)]
    public void Any_other_open_failure_is_an_unreadable_slot(int error) =>
        Assert.Equal(RealStorageInspectionSource.SlotOutcome.Unreadable, RealStorageInspectionSource.Classify(error, descriptorRead: false));

    [Fact]
    public void A_drive_that_opens_but_gives_no_descriptor_is_unreadable_and_one_with_a_descriptor_is_readable()
    {
        Assert.Equal(RealStorageInspectionSource.SlotOutcome.Unreadable, RealStorageInspectionSource.Classify(0, descriptorRead: false));
        Assert.Equal(RealStorageInspectionSource.SlotOutcome.Readable, RealStorageInspectionSource.Classify(0, descriptorRead: true));
    }

    [Fact]
    public async Task The_inspection_carries_the_count_and_the_complete_flag_follows_it()
    {
        var drives = (await new FakeStorageInspectionSource().InspectAsync(true)).Drives;

        var complete = new StorageInspection(At, 0, drives);
        var incomplete = new StorageInspection(At, 2, drives);

        Assert.Equal(0, complete.UnreadableSlots);
        Assert.True(complete.InventoryComplete);
        Assert.Equal(2, incomplete.UnreadableSlots);
        Assert.False(incomplete.InventoryComplete);
        // The recognised drives are still all there with their own state: an unreadable slot does not blank the others.
        Assert.Equal(drives.Count, incomplete.Drives.Count);
        Assert.All(incomplete.Drives, d => Assert.Equal(StorageRecognition.Recognized, d.Recognition));
    }

    [Fact]
    public async Task The_fake_has_no_unreadable_slot()
    {
        var inspection = await new FakeStorageInspectionSource().InspectAsync(true);

        Assert.Equal(0, inspection.UnreadableSlots);
        Assert.True(inspection.InventoryComplete);
    }
}

public class FakeStorageInspectionTests
{
    private static readonly DateTime Clock = new(2026, 11, 20, 3, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task The_fake_gives_one_nvme_drive_with_counters_and_a_sata_drive_that_needs_administrator()
    {
        var inspection = await new FakeStorageInspectionSource(() => Clock).InspectAsync(allowElevation: false);

        Assert.True(inspection.InventoryComplete);
        Assert.Equal(new DateTimeOffset(Clock), inspection.SampledAt);
        Assert.Equal(2, inspection.Drives.Count);

        var nvme = inspection.Drives[0];
        Assert.Equal(StorageRecognition.Recognized, nvme.Recognition);
        Assert.Equal(17, nvme.BusType);
        Assert.Equal(StorageIdentityConfirmation.Confirmed, nvme.Confirmation);
        Assert.True(nvme.HealthComparable);
        Assert.Equal(new U128(24UL), nvme.Counters!.ErrorLogEntries);
        Assert.Equal(new U128(94140625UL), nvme.Counters.DataUnitsWritten);
        Assert.Equal(new StorageErrorLogCounts(0, 0), nvme.Counters.RecentErrorLog);

        var sata = inspection.Drives[1];
        Assert.Equal(11, sata.BusType);
        Assert.Equal(ValueStatus.Denied, sata.Health.Status);
        Assert.Equal(StorageIdentityConfirmation.NoHealth, sata.Confirmation);
        Assert.Null(sata.Counters);
        Assert.False(sata.HealthComparable);
    }

    [Fact]
    public async Task The_fake_sata_drive_is_confirmed_when_elevation_is_allowed()
    {
        var inspection = await new FakeStorageInspectionSource(() => Clock).InspectAsync(allowElevation: true);

        var sata = inspection.Drives[1];
        Assert.Equal(StorageIdentityConfirmation.Confirmed, sata.Confirmation);
        Assert.True(sata.HealthComparable);
        Assert.True(sata.Counters!.AtaThresholdsRead);
        Assert.All(inspection.Drives, d => Assert.Equal(new DateTimeOffset(Clock), d.SampledAt));
    }

    [Fact]
    public async Task The_fake_keys_are_distinct_for_distinct_drives()
    {
        var inspection = await new FakeStorageInspectionSource().InspectAsync(true);

        Assert.Equal(2, inspection.Drives.Select(d => (d.SerialNumber, d.UniqueId, d.Model)).Distinct().Count());
        Assert.Equal(2, inspection.Drives.Select(d => d.PhysicalDriveNumber).Distinct().Count());
    }
}

/// <summary>
/// A read-only look at THIS machine's disks through the real source, never elevated (no Helper is given and elevation is not allowed).
/// It writes what it saw to the test output with every serial masked. Excluded from CI with the other RealMachine tests.
/// </summary>
public sealed class RealStorageInspectionSmokeTests(ITestOutputHelper output)
{
    private static string Mask(string text) =>
        text.Length == 0 ? "(none)" : $"[{text.Length} chars, ends ..{text[^Math.Min(2, text.Length)..]}]";

    [Trait("Category", "RealMachine")]
    [Fact]
    public async Task The_real_source_reads_this_pcs_disks_without_elevation_and_a_disk_never_mismatches_itself()
    {
        var inspection = await new RealStorageInspectionSource().InspectAsync(allowElevation: false);

        output.WriteLine($"drives: {inspection.Drives.Count}, unreadable slots: {inspection.UnreadableSlots}, inventory complete: {inspection.InventoryComplete}");
        foreach (var d in inspection.Drives)
        {
            output.WriteLine(
                $"#{d.PhysicalDriveNumber} {d.Recognition} model='{d.Model}' bus={d.BusType} size={d.SizeBytes} fw='{d.Firmware}' " +
                $"serial={Mask(d.SerialNumber)} uid={(d.UniqueId.Length == 0 ? "(none)" : d.UniqueId[..4] + Mask(d.UniqueId[4..]))} loc='{d.Location}' " +
                $"health={d.Health.Status} confirmation={d.Confirmation} comparable={d.HealthComparable} " +
                $"counters={(d.Counters is null ? "none" : $"kind={d.Counters.Kind} errLogEntries={d.Counters.ErrorLogEntries} duw={d.Counters.DataUnitsWritten} recent={d.Counters.RecentErrorLog} saturated={d.Counters.Saturated} thresholds={d.Counters.AtaThresholdsRead}")}");
        }

        Assert.NotEmpty(inspection.Drives);

        // The identity read before the health read and the one read on the health handle are the same disk: a mismatch here is a bug in the
        // extraction or in the comparison, not a swapped disk.
        Assert.DoesNotContain(inspection.Drives, d => d.Confirmation == StorageIdentityConfirmation.Mismatch);
        Assert.All(inspection.Drives.Where(d => d.Recognition == StorageRecognition.Recognized), d => Assert.NotEmpty(d.Model));
        Assert.All(inspection.Drives.Where(d => d.Health.Status == ValueStatus.Ok), d => Assert.Equal(StorageIdentityConfirmation.Confirmed, d.Confirmation));
    }
}
