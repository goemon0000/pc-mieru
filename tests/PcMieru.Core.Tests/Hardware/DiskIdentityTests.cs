using System.Buffers.Binary;
using System.Text;
using PcMieru.Core.Management;
using PcMieru.Wire;
using WireProtocol = PcMieru.Wire.StorageProtocol;

namespace PcMieru.Core.Tests.Hardware;

/// <summary>
/// The legacy-compatible identity extraction (DESIGN-A 5.2) and the NVMe Error Information log counts, in the Wire project that both the
/// Helper and Core use. Every byte image below is built by hand from the Windows structure layouts, so a change in what an extractor
/// returns (and with it a baseline key) shows here first.
/// </summary>
public class DiskIdentityTests
{
    // ---------------------------------------------------------------------------------------------------------------
    // STORAGE_DEVICE_DESCRIPTOR
    // ---------------------------------------------------------------------------------------------------------------

    private static byte[] Descriptor(string vendor, string product, string revision, string serial, uint bus)
    {
        var tail = new List<byte>();

        uint Put(string text)
        {
            if (text.Length == 0)
            {
                return 0;
            }

            var at = (uint)(36 + tail.Count);
            tail.AddRange(Encoding.ASCII.GetBytes(text));
            tail.Add(0);
            return at;
        }

        var v = Put(vendor);
        var p = Put(product);
        var r = Put(revision);
        var s = Put(serial);
        var d = new byte[36 + tail.Count];
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(4), (uint)d.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(12), v);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(16), p);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(20), r);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(24), s);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(28), bus);
        tail.CopyTo(d, 36);
        return d;
    }

    [Fact]
    public void The_descriptor_strings_are_ascii_up_to_the_first_nul_and_trimmed()
    {
        var d = Descriptor("ATA     ", "Example SSD    ", " 1B6Q", "  SN%7|A_..  ", 11);

        var fields = WireProtocol.ParseDescriptorFields(d);

        Assert.NotNull(fields);
        Assert.Equal("ATA", fields!.Value.Vendor);
        Assert.Equal("Example SSD", fields.Value.Product);
        Assert.Equal("1B6Q", fields.Value.Revision);
        Assert.Equal("SN%7|A_..", fields.Value.Serial);
        Assert.Equal(11, fields.Value.BusType);
    }

    [Fact]
    public void The_bus_is_the_raw_dword_cast_to_a_signed_int_like_the_old_enum_cast()
    {
        Assert.Equal(17, WireProtocol.ParseDescriptorFields(Descriptor("", "P", "", "", 17))!.Value.BusType);
        Assert.Equal(-1, WireProtocol.ParseDescriptorFields(Descriptor("", "P", "", "", 0xFFFFFFFF))!.Value.BusType);
        Assert.Equal(int.MinValue, WireProtocol.ParseDescriptorFields(Descriptor("", "P", "", "", 0x80000000))!.Value.BusType);
    }

    [Fact]
    public void An_answer_shorter_than_the_fixed_part_is_no_descriptor()
    {
        Assert.Null(WireProtocol.ParseDescriptorFields(new byte[35]));
        Assert.Null(WireProtocol.ParseDescriptorFields([]));
        Assert.NotNull(WireProtocol.ParseDescriptorFields(new byte[36]));
    }

    [Fact]
    public void A_string_offset_of_zero_or_outside_the_answer_is_an_empty_string()
    {
        var d = Descriptor("V", "Product", "R", "SERIAL", 11);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(16), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(24), (uint)d.Length);

        var fields = WireProtocol.ParseDescriptorFields(d)!.Value;

        Assert.Equal("V", fields.Vendor);
        Assert.Equal("", fields.Product);
        Assert.Equal("R", fields.Revision);
        Assert.Equal("", fields.Serial);
    }

    [Fact]
    public void A_string_without_a_terminator_runs_to_the_end_of_the_answer()
    {
        var d = Descriptor("V", "P", "R", "TAILSERIAL", 11);

        var fields = WireProtocol.ParseDescriptorFields(d.AsSpan(0, d.Length - 1))!.Value;

        Assert.Equal("TAILSERIAL", fields.Serial);
    }

    [Theory]
    [InlineData("ATA", "Example SSD", "ATA Example SSD")]
    [InlineData("Samsung", "Samsung SSD 980 PRO 2TB", "Samsung SSD 980 PRO 2TB")]
    [InlineData("SAMSUNG", "samsung ssd", "samsung ssd")]
    [InlineData("NVMe", "Samsung SSD 980 PRO 2TB", "NVMe Samsung SSD 980 PRO 2TB")]
    [InlineData("", "Product", "Product")]
    [InlineData("WDC", "WD40EZRZ-00GXCB0", "WDC WD40EZRZ-00GXCB0")]
    [InlineData("WDC", "WDC WD40EZRZ-00GXCB0", "WDC WD40EZRZ-00GXCB0")]
    [InlineData("", "", "")]
    public void The_model_has_its_vendor_in_front_unless_the_product_already_starts_with_it(string vendor, string product, string model) =>
        Assert.Equal(model, WireProtocol.ComposeModel(vendor, product));

    [Theory]
    [InlineData("  AB12CD  ", "AB12CD")]
    [InlineData("SN_1..", "SN_1")]
    [InlineData("S3Z9NB0K.123.", "S3Z9NB0K.123")]
    [InlineData("...", "")]
    [InlineData("", "")]
    [InlineData("SN%7|A_", "SN%7|A_")]
    public void The_serial_is_trimmed_and_loses_its_trailing_dots_only(string serial, string expected) =>
        Assert.Equal(expected, WireProtocol.ComposeSerial(serial));

    // ---------------------------------------------------------------------------------------------------------------
    // STORAGE_DEVICE_ID_DESCRIPTOR
    // ---------------------------------------------------------------------------------------------------------------

    private static byte[] Ids(params (int CodeSet, int Type, int Association, string Hex)[] ids)
    {
        var body = new List<byte>();
        foreach (var (codeSet, type, association, hex) in ids)
        {
            var value = Convert.FromHexString(hex);
            var next = (16 + value.Length + 3) & ~3;
            var e = new byte[next];
            BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(0), codeSet);
            BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(4), type);
            BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(8), (ushort)value.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(10), (ushort)next);
            BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(12), association);
            value.CopyTo(e, 16);
            body.AddRange(e);
        }

        var d = new byte[12 + body.Count];
        BinaryPrimitives.WriteInt32LittleEndian(d.AsSpan(0), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(4), (uint)d.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(8), (uint)ids.Length);
        body.CopyTo(d, 12);
        return d;
    }

    private const string Eui = "0123456789abcdef";
    private const string Naa = "50014ee2b8c0ffee";

    [Fact]
    public void An_eui_64_id_of_the_device_gives_eui_dot_lower_case_hex()
    {
        Assert.Equal("eui.0123456789abcdef", WireProtocol.ParseDeviceUniqueId(Ids((1, 2, 0, Eui))));
        Assert.Equal("eui.abcdef0123456789", WireProtocol.ParseDeviceUniqueId(Ids((1, 2, 0, "ABCDEF0123456789"))));
    }

    [Fact]
    public void A_naa_id_gives_naa_dot_hex()
    {
        Assert.Equal("naa.50014ee2b8c0ffee", WireProtocol.ParseDeviceUniqueId(Ids((1, 3, 0, Naa))));
        Assert.Equal("naa.5000c500a1b2c3d4e5f60718293a4b5c", WireProtocol.ParseDeviceUniqueId(Ids((1, 3, 0, "5000c500a1b2c3d4e5f60718293a4b5c"))));
    }

    [Fact]
    public void The_eui_id_wins_over_the_naa_id_whatever_their_order()
    {
        Assert.Equal("eui.0123456789abcdef", WireProtocol.ParseDeviceUniqueId(Ids((1, 3, 0, Naa), (1, 2, 0, Eui))));
        Assert.Equal("eui.0123456789abcdef", WireProtocol.ParseDeviceUniqueId(Ids((1, 2, 0, Eui), (1, 3, 0, Naa))));
    }

    [Fact]
    public void Eui_lengths_8_12_and_16_count_and_others_do_not()
    {
        Assert.Equal("eui.0123456789abcdef01234567", WireProtocol.ParseDeviceUniqueId(Ids((1, 2, 0, "0123456789abcdef01234567"))));
        Assert.Equal("eui.0123456789abcdef0123456789abcdef", WireProtocol.ParseDeviceUniqueId(Ids((1, 2, 0, "0123456789abcdef0123456789abcdef"))));
        Assert.Equal("", WireProtocol.ParseDeviceUniqueId(Ids((1, 2, 0, "0123456789abcdef0123"))));
        Assert.Equal("", WireProtocol.ParseDeviceUniqueId(Ids((1, 2, 0, "01234567"))));
    }

    [Fact]
    public void Naa_lengths_8_and_16_count_and_others_do_not()
    {
        Assert.Equal("", WireProtocol.ParseDeviceUniqueId(Ids((1, 3, 0, "50014ee2b8c0ffee01234567"))));
        Assert.Equal("", WireProtocol.ParseDeviceUniqueId(Ids((1, 3, 0, "50014ee2"))));
    }

    [Fact]
    public void An_all_zero_or_all_ff_value_is_no_id_and_the_next_one_is_used()
    {
        Assert.Equal("naa.50014ee2b8c0ffee", WireProtocol.ParseDeviceUniqueId(Ids((1, 2, 0, "0000000000000000"), (1, 3, 0, Naa))));
        Assert.Equal("naa.50014ee2b8c0ffee", WireProtocol.ParseDeviceUniqueId(Ids((1, 2, 0, "ffffffffffffffff"), (1, 3, 0, Naa))));
        Assert.Equal("", WireProtocol.ParseDeviceUniqueId(Ids((1, 2, 0, "0000000000000000"))));
        Assert.Equal("", WireProtocol.ParseDeviceUniqueId(Ids((1, 3, 0, "ffffffffffffffff"))));
        Assert.Equal("eui.0000000000000001", WireProtocol.ParseDeviceUniqueId(Ids((1, 2, 0, "0000000000000001"))));
    }

    [Fact]
    public void Only_binary_ids_of_the_device_itself_count()
    {
        Assert.Equal("", WireProtocol.ParseDeviceUniqueId(Ids((1, 2, 1, Eui))));   // association 1: the port
        Assert.Equal("", WireProtocol.ParseDeviceUniqueId(Ids((1, 2, 2, Eui))));   // association 2: the target
        Assert.Equal("", WireProtocol.ParseDeviceUniqueId(Ids((2, 2, 0, Eui))));   // code set 2: ASCII
        Assert.Equal("", WireProtocol.ParseDeviceUniqueId(Ids((1, 1, 0, Eui))));   // type 1: vendor id
        Assert.Equal("", WireProtocol.ParseDeviceUniqueId(Ids((1, 8, 0, Eui))));   // type 8: SCSI name string
    }

    [Fact]
    public void A_short_or_inconsistent_answer_gives_no_id()
    {
        Assert.Equal("", WireProtocol.ParseDeviceUniqueId([]));
        Assert.Equal("", WireProtocol.ParseDeviceUniqueId(new byte[11]));

        // The identifier claims more bytes than the answer holds.
        var cut = Ids((1, 2, 0, Eui));
        BinaryPrimitives.WriteUInt16LittleEndian(cut.AsSpan(12 + 8), 200);
        Assert.Equal("", WireProtocol.ParseDeviceUniqueId(cut));

        // The count says more identifiers than there are: the first one is still read and nothing beyond the data is.
        var many = Ids((1, 2, 0, Eui));
        BinaryPrimitives.WriteUInt32LittleEndian(many.AsSpan(8), 40);
        Assert.Equal("eui.0123456789abcdef", WireProtocol.ParseDeviceUniqueId(many));
    }

    [Fact]
    public void A_next_offset_below_the_fixed_part_ends_the_walk_like_the_old_code()
    {
        var d = Ids((1, 2, 0, "0000000000000000"), (1, 3, 0, Naa));
        BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(12 + 10), 0);

        Assert.Equal("", WireProtocol.ParseDeviceUniqueId(d));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // SCSI_ADDRESS and DISK_GEOMETRY_EX
    // ---------------------------------------------------------------------------------------------------------------

    private static byte[] Address(uint length, byte port, byte path, byte target, byte lun)
    {
        var a = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(a, length);
        a[4] = port;
        a[5] = path;
        a[6] = target;
        a[7] = lun;
        return a;
    }

    [Fact]
    public void The_scsi_address_is_port_path_target_lun()
    {
        Assert.Equal("1:0:3:0", WireProtocol.ParseScsiAddress(Address(8, 1, 0, 3, 0)));
        Assert.Equal("0:0:0:0", WireProtocol.ParseScsiAddress(Address(8, 0, 0, 0, 0)));
        Assert.Equal("255:2:7:1", WireProtocol.ParseScsiAddress(Address(8, 255, 2, 7, 1)));
    }

    [Fact]
    public void A_scsi_address_that_does_not_fit_is_empty()
    {
        Assert.Equal("", WireProtocol.ParseScsiAddress(Address(4, 1, 0, 3, 0)));
        Assert.Equal("", WireProtocol.ParseScsiAddress(new byte[7]));
        Assert.Equal("", WireProtocol.ParseScsiAddress([]));
    }

    [Fact]
    public void The_disk_size_is_the_int64_at_byte_24_of_the_geometry()
    {
        var g = new byte[32];
        BinaryPrimitives.WriteInt64LittleEndian(g.AsSpan(24), 1000204886016);

        Assert.Equal(1000204886016, WireProtocol.ParseGeometrySize(g));
        Assert.Equal(0, WireProtocol.ParseGeometrySize(g.AsSpan(0, 31)));
        Assert.Equal(1000204886016, WireProtocol.ParseGeometrySize([.. g, .. new byte[40]]));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // DiskIdentity.Read: the four queries, nothing else
    // ---------------------------------------------------------------------------------------------------------------

    private sealed class Device(byte[]? descriptor, byte[]? ids, byte[]? geometry, byte[]? address)
    {
        public List<(uint Code, int PropertyId, int Length)> Calls { get; } = [];

        public byte[]? Ioctl(uint code, byte[] input, int length)
        {
            Calls.Add((code, input.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(input) : -1, length));
            return code switch
            {
                WireProtocol.IoctlStorageQueryProperty when input.Length == 12 && BinaryPrimitives.ReadInt32LittleEndian(input) == WireProtocol.StorageDeviceProperty => descriptor,
                WireProtocol.IoctlStorageQueryProperty when input.Length == 12 && BinaryPrimitives.ReadInt32LittleEndian(input) == WireProtocol.StorageDeviceIdProperty => ids,
                WireProtocol.IoctlDiskGetDriveGeometryEx => geometry,
                WireProtocol.IoctlScsiGetAddress => address,
                _ => null,
            };
        }
    }

    private static byte[] Geometry(long size)
    {
        var g = new byte[32];
        BinaryPrimitives.WriteInt64LittleEndian(g.AsSpan(24), size);
        return g;
    }

    [Fact]
    public void Read_returns_the_legacy_values_of_the_four_queries_and_asks_nothing_else()
    {
        var device = new Device(Descriptor("ATA     ", "Example SSD", "1B6Q", "SN%7|A_..", 11), Ids((1, 2, 0, Eui)), Geometry(1000204886016), Address(8, 0, 0, 0, 0));

        var identity = DiskIdentity.Read(device.Ioctl, out var firmware);

        Assert.Equal(new DiskIdentity("ATA Example SSD", "SN%7|A_", "eui.0123456789abcdef", "0:0:0:0", 1000204886016, 11), identity);
        Assert.Equal("1B6Q", firmware);
        (uint, int)[] expected =
        [
            (WireProtocol.IoctlStorageQueryProperty, WireProtocol.StorageDeviceProperty),
            (WireProtocol.IoctlDiskGetDriveGeometryEx, -1),
            (WireProtocol.IoctlStorageQueryProperty, WireProtocol.StorageDeviceIdProperty),
            (WireProtocol.IoctlScsiGetAddress, -1),
        ];
        Assert.Equal(expected, device.Calls.Select(c => (c.Code, c.PropertyId)).ToArray());
    }

    [Fact]
    public void Read_without_a_descriptor_gives_no_identity_and_one_failed_query_changes_only_its_own_field()
    {
        Assert.Null(DiskIdentity.Read(new Device(null, Ids((1, 2, 0, Eui)), Geometry(5), Address(8, 0, 0, 0, 0)).Ioctl, out var none));
        Assert.Equal("", none);
        Assert.Null(DiskIdentity.Read(new Device(new byte[20], null, null, null).Ioctl, out _));

        var d = Descriptor("V", "Prod", "R", "SERIAL1", 17);
        Assert.Equal(new DiskIdentity("V Prod", "SERIAL1", "", "", 0, 17), DiskIdentity.Read(new Device(d, null, null, null).Ioctl, out _));
        Assert.Equal(new DiskIdentity("V Prod", "SERIAL1", "eui.0123456789abcdef", "", 0, 17), DiskIdentity.Read(new Device(d, Ids((1, 2, 0, Eui)), null, null).Ioctl, out _));
        Assert.Equal(new DiskIdentity("V Prod", "SERIAL1", "", "2:0:1:0", 7, 17), DiskIdentity.Read(new Device(d, null, Geometry(7), Address(8, 2, 0, 1, 0)).Ioctl, out _));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The wire keys
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_new_wire_keys_are_these_literals_and_the_old_ones_did_not_change()
    {
        Assert.Equal("identity.serial", HelperDataKeys.IdentitySerial);
        Assert.Equal("identity.uniqueId", HelperDataKeys.IdentityUniqueId);
        Assert.Equal("identity.location", HelperDataKeys.IdentityLocation);
        Assert.Equal("identity.model", HelperDataKeys.IdentityModel);
        Assert.Equal("identity.sizeBytes", HelperDataKeys.IdentitySizeBytes);
        Assert.Equal("identity.bus", HelperDataKeys.IdentityBus);
        Assert.Equal("nvme.errorlog.entries", HelperDataKeys.NvmeErrorLogEntries);
        Assert.Equal("nvme.errorlog.media", HelperDataKeys.NvmeErrorLogMedia);
        Assert.Equal("drive.3.identity.uniqueId", HelperDataKeys.ForDrive(3, HelperDataKeys.IdentityUniqueId));

        Assert.Equal("code", HelperDataKeys.DriveCode);
        Assert.Equal("smart.data", HelperDataKeys.SmartData);
        Assert.Equal("smart.thresholds", HelperDataKeys.SmartThresholds);
        Assert.Equal("smart.predict-failure", HelperDataKeys.PredictFailure);
        Assert.Equal("nvme.health", HelperDataKeys.NvmeHealth);
        Assert.Equal("nvme.errors", HelperDataKeys.NvmeErrors);
    }

    private static Dictionary<string, string> Prefixed(int drive, DiskIdentity identity)
    {
        var plain = new Dictionary<string, string>();
        identity.WriteTo(plain);
        return plain.ToDictionary(p => HelperDataKeys.ForDrive(drive, p.Key), p => p.Value);
    }

    [Fact]
    public void An_identity_survives_the_trip_through_the_data_map()
    {
        var identity = new DiskIdentity("ATA Example SSD", "SN%7|A_", "eui.0123456789abcdef", "0:0:0:0", 1000204886016, 11);

        var plain = new Dictionary<string, string>();
        identity.WriteTo(plain);

        Assert.Equal("SN%7|A_", plain["identity.serial"]);
        Assert.Equal("eui.0123456789abcdef", plain["identity.uniqueId"]);
        Assert.Equal("0:0:0:0", plain["identity.location"]);
        Assert.Equal("ATA Example SSD", plain["identity.model"]);
        Assert.Equal("1000204886016", plain["identity.sizeBytes"]);
        Assert.Equal("11", plain["identity.bus"]);
        Assert.Equal(6, plain.Count);
        Assert.Equal(identity, DiskIdentity.ReadFrom(Prefixed(3, identity), 3));
    }

    [Fact]
    public void Empty_strings_zero_size_and_a_negative_bus_survive_too()
    {
        var identity = new DiskIdentity("M", "", "", "", 0, -1);

        Assert.Equal(identity, DiskIdentity.ReadFrom(Prefixed(0, identity), 0));
    }

    [Fact]
    public void An_identity_is_read_for_its_own_drive_only()
    {
        var data = Prefixed(3, new DiskIdentity("M", "S", "", "", 1, 11));

        Assert.NotNull(DiskIdentity.ReadFrom(data, 3));
        Assert.Null(DiskIdentity.ReadFrom(data, 4));
        Assert.Null(DiskIdentity.ReadFrom(new Dictionary<string, string>(), 3));
    }

    [Theory]
    [InlineData("identity.serial")]
    [InlineData("identity.uniqueId")]
    [InlineData("identity.location")]
    [InlineData("identity.model")]
    [InlineData("identity.sizeBytes")]
    [InlineData("identity.bus")]
    public void One_missing_key_means_no_identity_never_a_partly_filled_one(string missing)
    {
        var data = Prefixed(2, new DiskIdentity("M", "S", "eui.01", "0:0:0:0", 5, 11));
        data.Remove("drive.2." + missing);

        Assert.Null(DiskIdentity.ReadFrom(data, 2));
    }

    [Theory]
    [InlineData("identity.sizeBytes", "")]
    [InlineData("identity.sizeBytes", "12abc")]
    [InlineData("identity.sizeBytes", "1.5")]
    [InlineData("identity.sizeBytes", "99999999999999999999")]
    [InlineData("identity.bus", "")]
    [InlineData("identity.bus", "Sata")]
    [InlineData("identity.bus", "4294967296")]
    public void A_number_that_is_not_a_number_means_no_identity(string key, string value)
    {
        var data = Prefixed(2, new DiskIdentity("M", "S", "eui.01", "0:0:0:0", 5, 11));
        data["drive.2." + key] = value;

        Assert.Null(DiskIdentity.ReadFrom(data, 2));
    }

    [Fact]
    public void A_sixteen_drive_batch_with_the_most_the_new_keys_can_hold_stays_far_below_the_result_file_limit()
    {
        // Every drive NVMe, full health page, full error log page, longest believable identity strings: the biggest answer one request can give.
        var longText = new string('x', 255);
        var answers = new List<(int Drive, ActionResult Result)>();
        for (var drive = 0; drive < DriveList.MaxCount; drive++)
        {
            var data = new Dictionary<string, string>
            {
                [HelperDataKeys.Bus] = "nvme",
                [HelperDataKeys.NvmeHealth] = Convert.ToBase64String(Enumerable.Range(0, 512).Select(i => (byte)(i * 7 + 251)).ToArray()),
                [HelperDataKeys.NvmeErrors] = Convert.ToBase64String(Enumerable.Range(0, 1024).Select(i => (byte)(i * 5 + 191)).ToArray()),
                [HelperDataKeys.NvmeErrorLogEntries] = "16",
                [HelperDataKeys.NvmeErrorLogMedia] = "16",
            };
            new DiskIdentity(longText, longText, "eui." + new string('a', 32), "255:255:255:255", long.MaxValue, 17).WriteTo(data);
            answers.Add((drive, ActionResult.Of(OperationCode.Ok, MessageKeys.DiskRead).WithData(data)));
        }

        var combined = DiskOperations.CombineHealth(answers);
        var result = new HelperResult("n", "disk-health-all", combined.Code, null, combined.Data is null ? null : new Dictionary<string, string>(combined.Data));
        var json = System.Text.Json.JsonSerializer.Serialize(result, WireJsonContext.Default.HelperResult);

        Assert.True(Encoding.UTF8.GetByteCount(json) < HelperClient.MaxResultBytes / 2, $"{Encoding.UTF8.GetByteCount(json)} bytes");
        Assert.Equal(combined.Data!.Count, System.Text.Json.JsonSerializer.Deserialize(json, WireJsonContext.Default.HelperResult)!.Data!.Count);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // NVMe Error Information log (0x01): counts only
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>A log page of 16 entries of 64 bytes. An entry is (error count, Status Code Type); every other field gets a recognisable non-zero value.</summary>
    private static byte[] ErrorLog(params (ulong Count, int StatusCodeType)[] entries)
    {
        var page = new byte[WireProtocol.NvmeErrorLogLength];
        for (var i = 0; i < entries.Length; i++)
        {
            var e = page.AsSpan(i * WireProtocol.NvmeErrorEntryLength, WireProtocol.NvmeErrorEntryLength);
            BinaryPrimitives.WriteUInt64LittleEndian(e, entries[i].Count);
            BinaryPrimitives.WriteUInt16LittleEndian(e[8..], 1);                                                       // submission queue id
            BinaryPrimitives.WriteUInt16LittleEndian(e[10..], 0x1234);                                                 // command id
            BinaryPrimitives.WriteUInt16LittleEndian(e[12..], (ushort)((entries[i].StatusCodeType << 9) | (0x02 << 1) | 1)); // SCT, SC, phase tag
            BinaryPrimitives.WriteUInt64LittleEndian(e[16..], 0xDEADBEEFCAFE0000UL + (ulong)i);                       // LBA
            BinaryPrimitives.WriteUInt32LittleEndian(e[24..], 1);                                                      // namespace
        }

        return page;
    }

    [Fact]
    public void Entries_with_an_error_count_are_counted_and_only_status_code_type_2_is_a_media_error()
    {
        var page = ErrorLog(
            (5, 2),   // media
            (4, 0),   // generic
            (3, 2),   // media
            (2, 1),   // command specific
            (1, 3),   // path related
            (0, 2));  // an empty slot is not an entry, whatever its status says

        var counts = WireProtocol.CountNvmeErrorLog(page);

        Assert.Equal(new WireProtocol.NvmeErrorLogCounts(5, 2), counts);
    }

    [Fact]
    public void The_status_code_type_is_bits_9_to_11_and_the_more_and_do_not_retry_bits_do_not_matter()
    {
        var page = ErrorLog((1, 2), (2, 2), (3, 6), (4, 3));
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(12), (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(12)) | 0xC000));
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(64 + 12), (ushort)(2 << 9));

        Assert.Equal(new WireProtocol.NvmeErrorLogCounts(4, 2), WireProtocol.CountNvmeErrorLog(page));
    }

    [Fact]
    public void An_empty_log_is_zero_entries_not_unknown()
    {
        Assert.Equal(new WireProtocol.NvmeErrorLogCounts(0, 0), WireProtocol.CountNvmeErrorLog(new byte[WireProtocol.NvmeErrorLogLength]));
    }

    [Fact]
    public void A_full_log_is_sixteen_entries_and_bytes_beyond_them_are_ignored()
    {
        var page = ErrorLog(Enumerable.Range(1, 16).Select(i => ((ulong)i, i % 2 == 0 ? 2 : 0)).ToArray());
        var longer = new byte[2048];
        page.CopyTo(longer, 0);
        longer.AsSpan(1024).Fill(0xFF);

        Assert.Equal(new WireProtocol.NvmeErrorLogCounts(16, 8), WireProtocol.CountNvmeErrorLog(page));
        Assert.Equal(new WireProtocol.NvmeErrorLogCounts(16, 8), WireProtocol.CountNvmeErrorLog(longer));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(64)]
    [InlineData(512)]
    [InlineData(1023)]
    public void A_page_shorter_than_sixteen_entries_gives_no_counts_at_all_never_zero(int length)
    {
        var full = ErrorLog((9, 2), (8, 2));

        Assert.Null(WireProtocol.CountNvmeErrorLog(full.AsSpan(0, length)));
    }

    [Fact]
    public void The_counts_do_not_depend_on_the_lba_the_command_id_or_the_namespace()
    {
        var a = ErrorLog((7, 2), (6, 0));
        var b = (byte[])a.Clone();
        for (var i = 0; i < 16; i++)
        {
            b.AsSpan((i * 64) + 8, 4).Fill(0xAB);          // queue id and command id
            b.AsSpan((i * 64) + 14, 2).Fill(0xCD);         // parameter error location
            b.AsSpan((i * 64) + 16, 8).Fill(0xEF);         // LBA
            b.AsSpan((i * 64) + 24, 40).Fill(0x5A);        // namespace and the rest
        }

        Assert.Equal(WireProtocol.CountNvmeErrorLog(a), WireProtocol.CountNvmeErrorLog(b));
    }

    [Fact]
    public void The_counts_type_holds_two_numbers_and_nothing_that_could_carry_an_lba_or_a_page()
    {
        string[] names = ["Entries", "MediaErrors"];
        Assert.Equal(names, typeof(WireProtocol.NvmeErrorLogCounts).GetProperties().Select(p => p.Name).Order().ToArray());
        Assert.All(typeof(WireProtocol.NvmeErrorLogCounts).GetProperties(), p => Assert.Equal(typeof(int), p.PropertyType));
    }

    [Fact]
    public void The_log_0x01_request_is_a_read_of_16_entries_and_no_other_page_than_the_two_logs_can_be_built()
    {
        var q = WireProtocol.BuildNvmeLogQuery(WireProtocol.NvmeLogErrorInfo, WireProtocol.NvmeErrorLogLength);

        Assert.Equal(WireProtocol.StorageDeviceProtocolSpecificProperty, BinaryPrimitives.ReadInt32LittleEndian(q.AsSpan(0)));
        Assert.Equal(WireProtocol.ProtocolTypeNvme, BinaryPrimitives.ReadInt32LittleEndian(q.AsSpan(8)));
        Assert.Equal(WireProtocol.NvmeDataTypeLogPage, BinaryPrimitives.ReadInt32LittleEndian(q.AsSpan(12)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(q.AsSpan(16)));
        Assert.Equal(1024u, BinaryPrimitives.ReadUInt32LittleEndian(q.AsSpan(28)));
        Assert.Equal(8 + WireProtocol.ProtocolSpecificDataSize + 1024, q.Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => WireProtocol.BuildNvmeLogQuery(0x03, 512));
        Assert.Throws<ArgumentOutOfRangeException>(() => WireProtocol.BuildNvmeLogQuery(0xC0, 512));
    }
}
