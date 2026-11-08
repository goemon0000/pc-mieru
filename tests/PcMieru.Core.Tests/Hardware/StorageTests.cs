using System.Buffers.Binary;
using System.Text;
using PcMieru.Core.Hardware.Model;
using PcMieru.Core.Hardware.Storage;

namespace PcMieru.Core.Tests.Hardware;

public class StorageProtocolTests
{
    [Fact]
    public void NvmeLogQueryIsAGetLogPageRequestOnly()
    {
        var q = StorageProtocol.BuildNvmeLogQuery(StorageProtocol.NvmeLogHealthInfo, 512);
        Assert.Equal(8 + 40 + 512, q.Length);
        Assert.Equal(50, BinaryPrimitives.ReadInt32LittleEndian(q.AsSpan(0)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(q.AsSpan(4)));
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(q.AsSpan(8)));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(q.AsSpan(12)));
        Assert.Equal(0x02u, BinaryPrimitives.ReadUInt32LittleEndian(q.AsSpan(16)));
        Assert.Equal(40u, BinaryPrimitives.ReadUInt32LittleEndian(q.AsSpan(24)));
        Assert.Equal(512u, BinaryPrimitives.ReadUInt32LittleEndian(q.AsSpan(28)));
        // Nothing past the 8 + 40 byte header is set: no payload that could be mistaken for a command.
        Assert.True(q.AsSpan(48).IndexOfAnyExcept((byte)0) < 0);
    }

    [Fact]
    public void PropertyQueryIsStandardQuery()
    {
        var q = StorageProtocol.BuildPropertyQuery(StorageProtocol.StorageDeviceSeekPenaltyProperty);
        Assert.Equal(12, q.Length);
        Assert.Equal(7, BinaryPrimitives.ReadInt32LittleEndian(q));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(q.AsSpan(4)));
    }

    private static byte[] ProtocolOutput(int dataLength, int offset = 40, uint version = 48, uint size = 48)
    {
        var buf = new byte[8 + offset + dataLength];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0), version);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), size);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8 + 16), (uint)offset);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8 + 20), (uint)dataLength);
        for (var i = 0; i < dataLength; i++)
        {
            buf[8 + offset + i] = (byte)(i % 251 + 1);
        }

        return buf;
    }

    [Fact]
    public void ExtractsTheLogPageFromTheDescriptor()
    {
        var page = StorageProtocol.ExtractProtocolData(ProtocolOutput(512), 512);
        Assert.NotNull(page);
        Assert.Equal(512, page.Length);
        Assert.Equal(1, page[0]);
        Assert.Equal(2, page[1]);
    }

    [Fact]
    public void RejectsADescriptorOfTheWrongShape()
    {
        Assert.Null(StorageProtocol.ExtractProtocolData(ProtocolOutput(512, version: 8), 512));
        Assert.Null(StorageProtocol.ExtractProtocolData(ProtocolOutput(512, size: 12), 512));
        Assert.Null(StorageProtocol.ExtractProtocolData(ProtocolOutput(256), 512));
        Assert.Null(StorageProtocol.ExtractProtocolData(ProtocolOutput(512, offset: 8), 512));
        Assert.Null(StorageProtocol.ExtractProtocolData(new byte[20], 512));
    }

    [Fact]
    public void RejectsADescriptorWhoseDataRunsPastTheBuffer()
    {
        var output = ProtocolOutput(512);
        Array.Resize(ref output, output.Length - 100);
        Assert.Null(StorageProtocol.ExtractProtocolData(output, 512));
    }

    private static byte[] DeviceDescriptorBytes(string vendor, string product, string revision, string serial, int busType, bool removable)
    {
        var strings = new List<byte>();
        int Add(string s)
        {
            if (s.Length == 0)
            {
                return 0;
            }

            var at = 40 + strings.Count;
            strings.AddRange(Encoding.ASCII.GetBytes(s));
            strings.Add(0);
            return at;
        }

        var v = Add(vendor);
        var p = Add(product);
        var r = Add(revision);
        var s = Add(serial);
        var buf = new byte[40 + strings.Count];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), (uint)buf.Length);
        buf[8] = 0;
        buf[10] = (byte)(removable ? 1 : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12), (uint)v);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(16), (uint)p);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(20), (uint)r);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(24), (uint)s);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(28), busType);
        strings.CopyTo(buf, 40);
        return buf;
    }

    [Fact]
    public void ParsesTheDeviceDescriptorStrings()
    {
        var d = StorageProtocol.ParseDeviceDescriptor(DeviceDescriptorBytes("ATA     ", "Example SSD 1TB", "FW01", "TESTSERIAL0001", 11, false));
        Assert.NotNull(d);
        Assert.Equal("ATA", d.Vendor);
        Assert.Equal("Example SSD 1TB", d.Product);
        Assert.Equal("FW01", d.Revision);
        Assert.Equal("TESTSERIAL0001", d.Serial);
        Assert.Equal(11, d.BusType);
        Assert.False(d.Removable);
        Assert.Equal("Example SSD 1TB", StorageProtocol.JoinVendorModel(d.Vendor, d.Product));
    }

    [Fact]
    public void MissingStringsAreEmptyNotGarbage()
    {
        var d = StorageProtocol.ParseDeviceDescriptor(DeviceDescriptorBytes("", "Only Product", "", "", 17, true));
        Assert.NotNull(d);
        Assert.Equal("", d.Vendor);
        Assert.Equal("", d.Serial);
        Assert.Equal(17, d.BusType);
        Assert.True(d.Removable);
    }

    [Fact]
    public void StringOffsetsOutsideTheBufferAreIgnored()
    {
        var bytes = DeviceDescriptorBytes("V", "P", "R", "S", 3, false);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 9999);
        var d = StorageProtocol.ParseDeviceDescriptor(bytes);
        Assert.NotNull(d);
        Assert.Equal("", d.Product);
    }

    [Fact]
    public void ShortDescriptorIsNull()
    {
        Assert.Null(StorageProtocol.ParseDeviceDescriptor(new byte[20]));
    }

    [Fact]
    public void VendorIsPrefixedOnlyWhenTheProductDoesNotAlreadyHaveIt()
    {
        Assert.Equal("Corsair MP600", StorageProtocol.JoinVendorModel("Corsair", "MP600"));
        Assert.Equal("TOSHIBA MG04", StorageProtocol.JoinVendorModel("TOSHIBA", "TOSHIBA MG04"));
    }

    [Fact]
    public void SerialPaddingIsStrippedButTheValueKept()
    {
        Assert.Equal("AB12 CD", StorageProtocol.CleanSerial(" AB12 CD_._."));
    }

    [Fact]
    public void SmallDescriptorsDecode()
    {
        Assert.True(StorageProtocol.ParseSeekPenalty(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 1 }));
        Assert.False(StorageProtocol.ParseTrimEnabled(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0 }));
        Assert.Null(StorageProtocol.ParseTrimEnabled(new byte[4]));

        var alignment = new byte[24];
        BinaryPrimitives.WriteInt32LittleEndian(alignment.AsSpan(16), 512);
        BinaryPrimitives.WriteInt32LittleEndian(alignment.AsSpan(20), 4096);
        Assert.Equal((512, 4096), StorageProtocol.ParseAlignment(alignment));
        Assert.Null(StorageProtocol.ParseAlignment(new byte[10]));

        var geometry = new byte[40];
        BinaryPrimitives.WriteInt64LittleEndian(geometry.AsSpan(24), 2_000_398_934_016);
        Assert.Equal(2_000_398_934_016, StorageProtocol.ParseGeometrySize(geometry));
    }

    [Fact]
    public void VolumeExtentsListEveryDisk()
    {
        var buf = new byte[8 + 24 * 2];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8), 3);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(16), 1_048_576);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(24), 500_000);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(32), 4);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(40), 2_000_000);
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(48), 700_000);
        var extents = StorageProtocol.ParseVolumeExtents(buf);
        Assert.Equal(2, extents.Count);
        Assert.Equal((3, 1_048_576L, 500_000L), extents[0]);
        Assert.Equal((4, 2_000_000L, 700_000L), extents[1]);
    }

    [Fact]
    public void VolumeExtentCountLargerThanTheBufferDoesNotOverread()
    {
        var buf = new byte[8 + 24];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, 40);
        Assert.Single(StorageProtocol.ParseVolumeExtents(buf));
        Assert.Empty(StorageProtocol.ParseVolumeExtents(new byte[4]));
    }
}

public class DriveLayoutTests
{
    private static void Entry(Span<byte> buf, int index, int style, long start, long length, int number, Guid? gptType, byte mbrType)
    {
        var e = buf.Slice(48 + (index * 144), 144);
        BinaryPrimitives.WriteInt32LittleEndian(e, style);
        BinaryPrimitives.WriteInt64LittleEndian(e[8..], start);
        BinaryPrimitives.WriteInt64LittleEndian(e[16..], length);
        BinaryPrimitives.WriteUInt32LittleEndian(e[24..], (uint)number);
        if (gptType is { } g)
        {
            g.TryWriteBytes(e[32..]);
            Guid.NewGuid().TryWriteBytes(e[48..]);
        }
        else
        {
            e[32] = mbrType;
        }
    }

    [Fact]
    public void ParsesGptEntriesAndSkipsEmptySlots()
    {
        var buf = new byte[48 + (144 * 3)];
        BinaryPrimitives.WriteInt32LittleEndian(buf, 1);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4), 3);
        Entry(buf, 0, 1, 1_048_576, 104_857_600, 1, Guid.Parse("c12a7328-f81f-11d2-ba4b-00a0c93ec93b"), 0);
        Entry(buf, 1, 1, 0, 0, 0, Guid.Empty, 0);
        Entry(buf, 2, 1, 105_906_176, 1_998_624_653_312, 3, Guid.Parse("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7"), 0);

        var layout = DriveLayout.Parse(buf);
        Assert.NotNull(layout);
        Assert.Equal("GPT", layout.Value.Style);
        Assert.Equal(2, layout.Value.Partitions.Count);
        Assert.Equal("EFI System", layout.Value.Partitions[0].Type);
        Assert.Equal(1, layout.Value.Partitions[0].Number);
        Assert.Equal("Basic data", layout.Value.Partitions[1].Type);
        Assert.Equal(1_998_624_653_312, layout.Value.Partitions[1].LengthBytes);
    }

    [Fact]
    public void ParsesMbrTypes()
    {
        var buf = new byte[48 + 144];
        BinaryPrimitives.WriteInt32LittleEndian(buf, 0);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4), 1);
        Entry(buf, 0, 0, 1_048_576, 1_000_000_000, 1, null, 0x07);
        var layout = DriveLayout.Parse(buf);
        Assert.NotNull(layout);
        Assert.Equal("MBR", layout.Value.Style);
        Assert.Equal("NTFS/exFAT", layout.Value.Partitions[0].Type);
    }

    [Fact]
    public void CountLargerThanTheBufferStopsAtTheBuffer()
    {
        var buf = new byte[48 + 144];
        BinaryPrimitives.WriteInt32LittleEndian(buf, 1);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4), 50);
        Entry(buf, 0, 1, 0, 4096, 1, Guid.Parse("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7"), 0);
        Assert.Single(DriveLayout.Parse(buf)!.Value.Partitions);
        Assert.Null(DriveLayout.Parse(new byte[10]));
    }

    [Fact]
    public void UnknownGptTypeIsTheGuidItself()
    {
        var unknown = Guid.Parse("11111111-2222-3333-4444-555555555555");
        Assert.Equal(unknown.ToString(), DriveLayout.GptTypeName(unknown));
    }

    [Fact]
    public void PartitionGuidsAreNotCopiedIntoTheResult()
    {
        var buf = new byte[48 + 144];
        BinaryPrimitives.WriteInt32LittleEndian(buf, 1);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4), 1);
        Entry(buf, 0, 1, 0, 4096, 1, Guid.Parse("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7"), 0);
        var partitionId = new Guid(buf.AsSpan(48 + 48, 16));
        var json = PcMieruJson.Serialize(DriveLayout.Parse(buf)!.Value.Partitions, false);
        Assert.DoesNotContain(partitionId.ToString(), json, StringComparison.OrdinalIgnoreCase);
    }
}

public class NvmeHealthParserTests
{
    private static byte[] Page()
    {
        var p = new byte[512];
        p[0] = 0x00;
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(1), 309); // 35.85 C
        p[3] = 100;
        p[4] = 10;
        p[5] = 7;
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(32), 1_000_000);   // data units read
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(48), 2_000_000);   // data units written
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(64), 123_456_789);
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(80), 987_654_321);
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(96), 55);
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(112), 300);
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(128), 8000);
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(144), 12);
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(160), 3);
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(176), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(192), 9);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(196), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(200), 311);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(204), 305);
        return p;
    }

    [Fact]
    public void ReadsEveryStandardField()
    {
        var v = StorageHealthParsers.FromNvmeLogPage(Page());
        Assert.Equal(ValueStatus.Ok, v.Status);
        var h = v.Data!;
        Assert.Equal(StorageHealthKind.Nvme, h.Kind);
        Assert.Equal(35.85, h.TemperatureC.Data);
        Assert.Equal(7, h.PercentageUsed.Data);
        Assert.Equal(100, h.AvailableSparePercent.Data);
        Assert.Equal(10, h.AvailableSpareThresholdPercent.Data);
        Assert.Equal(0, h.CriticalWarning.Data);
        Assert.Equal(new U128(1_000_000UL * 512_000UL), h.DataReadBytes.Data);
        Assert.Equal(new U128(2_000_000UL * 512_000UL), h.DataWrittenBytes.Data);
        Assert.Equal(new U128(8000), h.PowerOnHours.Data);
        Assert.Equal(new U128(300), h.PowerCycles.Data);
        Assert.Equal(new U128(12), h.UnsafeShutdowns.Data);
        Assert.Equal(new U128(3), h.MediaErrors.Data);
        Assert.Equal(new U128(4), h.Nvme!.ErrorLogEntries.Data);
        Assert.Equal(new U128(123_456_789), h.Nvme.HostReadCommands.Data);
        Assert.Equal(new U128(987_654_321), h.Nvme.HostWriteCommands.Data);
        Assert.Equal(9, h.Nvme.WarningCompositeTempMinutes.Data);
        Assert.Equal(2, h.Nvme.CriticalCompositeTempMinutes.Data);
        Assert.Equal(new[] { 37.85, 31.85 }, h.Nvme.TemperatureSensorsC.Data!.ToArray());
        Assert.Empty(h.Nvme.CriticalWarningFlags.Data!);
    }

    [Fact]
    public void TemperatureHasNoFloatingPointNoise()
    {
        var p = Page();
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(1), 309);
        Assert.Equal("35.85", StorageHealthParsers.FromNvmeLogPage(p).Data!.TemperatureC.Data.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Counters128BitAreNotClampedToUInt64()
    {
        var p = Page();
        // power-on hours = 2^64 + 5: low qword 5, high qword 1
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(128), 5);
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(136), 1);
        var v = StorageHealthParsers.FromNvmeLogPage(p);
        var expected = (UInt128)ulong.MaxValue + 6;
        Assert.Equal(new U128(expected), v.Data!.PowerOnHours.Data);
        Assert.Equal("18446744073709551621", v.Data.PowerOnHours.Data.Value.ToString());
    }

    [Fact]
    public void LargeDataUnitsMultiplyWithoutOverflowingUInt64()
    {
        var p = Page();
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(48), ulong.MaxValue);
        var v = StorageHealthParsers.FromNvmeLogPage(p);
        Assert.Equal(ValueStatus.Ok, v.Data!.DataWrittenBytes.Status);
        Assert.Equal((UInt128)ulong.MaxValue * 512_000, v.Data.DataWrittenBytes.Data.Value);
    }

    [Fact]
    public void ByteCountThatCannotFitIn128BitsIsAnErrorNotAWrap()
    {
        var p = Page();
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(48), ulong.MaxValue);
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(56), ulong.MaxValue);
        var v = StorageHealthParsers.FromNvmeLogPage(p);
        Assert.Equal(ValueStatus.Error, v.Data!.DataWrittenBytes.Status);
    }

    [Fact]
    public void PercentageUsedAbove100IsKept()
    {
        var p = Page();
        p[5] = 120;
        Assert.Equal(120, StorageHealthParsers.FromNvmeLogPage(p).Data!.PercentageUsed.Data);
    }

    [Fact]
    public void CriticalWarningBitsAreNamed()
    {
        var p = Page();
        p[0] = 0x01 | 0x08 | 0x80;
        var h = StorageHealthParsers.FromNvmeLogPage(p).Data!;
        Assert.Equal(0x89, h.CriticalWarning.Data);
        Assert.Equal(new[] { "availableSpareLow", "readOnly", "reservedBits" }, h.Nvme!.CriticalWarningFlags.Data!.ToArray());
    }

    [Fact]
    public void ZeroKelvinIsNotATemperature()
    {
        var p = Page();
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(1), 0);
        var t = StorageHealthParsers.FromNvmeLogPage(p).Data!.TemperatureC;
        Assert.Equal(ValueStatus.Unsupported, t.Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(511)]
    public void ShortBufferIsAnErrorNeverZeros(int length)
    {
        var v = StorageHealthParsers.FromNvmeLogPage(new byte[length]);
        Assert.Equal(ValueStatus.Error, v.Status);
        Assert.Null(v.Data);
        Assert.Contains("512", v.Detail);
    }

    [Fact]
    public void ADrivesRealZeroCountersAreStillOk()
    {
        var p = new byte[512];
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(1), 300);
        p[3] = 100;
        var h = StorageHealthParsers.FromNvmeLogPage(p).Data!;
        Assert.Equal(ValueStatus.Ok, h.MediaErrors.Status);
        Assert.Equal(new U128(0), h.MediaErrors.Data);
    }

    [Fact]
    public void JsonCarries128BitCountersAsDecimalStrings()
    {
        var p = Page();
        BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(136), 1);
        var json = PcMieruJson.Serialize(StorageHealthParsers.FromNvmeLogPage(p), false);
        Assert.Contains("\"18446744073709559616\"", json);
    }
}

public class SmartParserTests
{
    private static byte[] Table(params (int Id, int Current, int Worst, ulong Raw)[] attrs)
    {
        var t = new byte[512];
        t[0] = 0x10;
        for (var i = 0; i < attrs.Length; i++)
        {
            var a = t.AsSpan(2 + (i * 12), 12);
            a[0] = (byte)attrs[i].Id;
            a[3] = (byte)attrs[i].Current;
            a[4] = (byte)attrs[i].Worst;
            for (var b = 0; b < 6; b++)
            {
                a[5 + b] = (byte)(attrs[i].Raw >> (8 * b));
            }
        }

        return t;
    }

    private static byte[] Thresholds(params (int Id, int Threshold)[] limits)
    {
        var t = new byte[512];
        for (var i = 0; i < limits.Length; i++)
        {
            t[2 + (i * 12)] = (byte)limits[i].Id;
            t[2 + (i * 12) + 1] = (byte)limits[i].Threshold;
        }

        return t;
    }

    [Fact]
    public void ReadsAttributesAndThresholds()
    {
        var data = Table((5, 100, 100, 0), (9, 98, 98, 0x1_0000_0000UL + 14638), (194, 69, 50, 0x0014_001F));
        var attrs = StorageHealthParsers.ParseSmartAttributes(data, Thresholds((5, 10), (194, 0)));
        Assert.Equal(3, attrs.Count);
        Assert.Equal(5, attrs[0].Id);
        Assert.Equal("Reallocated sectors", attrs[0].Name);
        Assert.Equal(10, attrs[0].Threshold);
        Assert.True(attrs[0].UnitKnown);
        Assert.False(attrs[1].UnitKnown);
        Assert.Equal(98, attrs[1].Current);
    }

    [Fact]
    public void TemperatureAndHoursUseOnlyTheBytesThatMeanSomething()
    {
        var data = Table((9, 98, 98, 0x1_0000_0000UL + 14638), (194, 69, 50, 0x0014_001FUL), (12, 100, 100, 404), (187, 100, 100, 0), (174, 100, 100, 60));
        var v = StorageHealthParsers.FromSmartTables(data, default);
        Assert.Equal(ValueStatus.Ok, v.Status);
        var h = v.Data!;
        Assert.Equal(StorageHealthKind.Sata, h.Kind);
        Assert.Equal(31, h.TemperatureC.Data);
        Assert.Equal(new U128(14638), h.PowerOnHours.Data);
        Assert.Equal(new U128(404), h.PowerCycles.Data);
        Assert.Equal(new U128(60), h.UnsafeShutdowns.Data);
        Assert.Equal(new U128(0), h.MediaErrors.Data);
    }

    [Fact]
    public void AttributesTheDriveDoesNotHaveAreUnsupportedNotZero()
    {
        var v = StorageHealthParsers.FromSmartTables(Table((1, 100, 100, 0)), default);
        var h = v.Data!;
        Assert.Equal(ValueStatus.Unsupported, h.TemperatureC.Status);
        Assert.Equal(ValueStatus.Unsupported, h.PowerOnHours.Status);
        Assert.Equal(ValueStatus.Unsupported, h.MediaErrors.Status);
        Assert.Equal(ValueStatus.Unsupported, h.PercentageUsed.Status);
        Assert.Equal(ValueStatus.Unsupported, h.DataWrittenBytes.Status);
        Assert.Equal(ValueStatus.Unsupported, h.CriticalWarning.Status);
    }

    [Fact]
    public void AllZeroTableIsAnErrorNotAHealthyDrive()
    {
        var v = StorageHealthParsers.FromSmartTables(new byte[512], default);
        Assert.Equal(ValueStatus.Error, v.Status);
        Assert.Null(v.Data);
    }

    [Fact]
    public void ShortTableIsAnError()
    {
        Assert.Equal(ValueStatus.Error, StorageHealthParsers.FromSmartTables(new byte[100], default).Status);
        Assert.Equal(ValueStatus.Error, StorageHealthParsers.FromSmartBlob(new byte[0]).Status);
    }

    [Fact]
    public void HelperBlobCanCarryDataAndThresholds()
    {
        var blob = new byte[1024];
        Table((5, 100, 100, 0), (194, 70, 50, 30)).CopyTo(blob, 0);
        Thresholds((5, 36)).CopyTo(blob, 512);
        var v = StorageHealthParsers.FromSmartBlob(blob);
        Assert.Equal(ValueStatus.Ok, v.Status);
        Assert.Equal(36, v.Data!.Attributes.Single(a => a.Id == 5).Threshold);
        Assert.Equal(30, v.Data.TemperatureC.Data);

        var dataOnly = StorageHealthParsers.FromSmartBlob(blob.AsSpan(0, 512));
        Assert.Equal(0, dataOnly.Data!.Attributes.Single(a => a.Id == 5).Threshold);
    }

    [Fact]
    public void RawValueKeepsAll48Bits()
    {
        var attrs = StorageHealthParsers.ParseSmartAttributes(Table((241, 100, 100, 0xFFFF_FFFF_FFFFUL)), default);
        Assert.Equal(new U128(0xFFFF_FFFF_FFFFUL), attrs.Single().Raw);
    }
}
