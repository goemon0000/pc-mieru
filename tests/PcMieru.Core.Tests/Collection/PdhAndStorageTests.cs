using System.Buffers.Binary;
using System.Text;
using PcMieru.Core.Collection;

namespace PcMieru.Core.Tests.Collection;

public sealed class PdhArrayTests
{
    private const ulong Base = 0x0000_7FF6_1234_0000;

    /// <summary>The layout PdhGetFormattedCounterArrayW writes: 24-byte items first, then the name strings, pointers absolute.</summary>
    private static byte[] Build(params (string Name, double Value, uint CStatus)[] items)
    {
        var namesStart = items.Length * PdhArray.ItemSize;
        var names = new MemoryStream();
        var pointers = new ulong[items.Length];
        for (var i = 0; i < items.Length; i++)
        {
            pointers[i] = Base + (ulong)(namesStart + names.Length);
            names.Write(Encoding.Unicode.GetBytes(items[i].Name + "\0"));
        }

        var buffer = new byte[namesStart + (int)names.Length];
        for (var i = 0; i < items.Length; i++)
        {
            var item = buffer.AsSpan(i * PdhArray.ItemSize, PdhArray.ItemSize);
            BinaryPrimitives.WriteUInt64LittleEndian(item, pointers[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(item[8..], items[i].CStatus);
            BinaryPrimitives.WriteInt64LittleEndian(item[16..], BitConverter.DoubleToInt64Bits(items[i].Value));
        }

        names.ToArray().CopyTo(buffer, namesStart);
        return buffer;
    }

    [Fact]
    public void Items_are_read_with_name_value_and_status()
    {
        var buffer = Build(("0 C:", 12.5, Pdh.CStatusValidData), ("_Total", 99.0, Pdh.CStatusNewData));

        Assert.True(PdhArray.TryGetItem(buffer, Base, 0, 2, out var name0, out var v0, out var s0));
        Assert.Equal("0 C:", name0.ToString());
        Assert.Equal(12.5, v0);
        Assert.Equal(Pdh.CStatusValidData, s0);

        Assert.True(PdhArray.TryGetItem(buffer, Base, 1, 2, out var name1, out var v1, out var s1));
        Assert.Equal("_Total", name1.ToString());
        Assert.Equal(99.0, v1);
        Assert.Equal(Pdh.CStatusNewData, s1);
    }

    [Fact]
    public void Japanese_and_empty_names_are_read_whole()
    {
        var buffer = Build(("日本語 1", 1, Pdh.CStatusValidData), (string.Empty, 2, Pdh.CStatusValidData));

        Assert.True(PdhArray.TryGetItem(buffer, Base, 0, 2, out var name0, out _, out _));
        Assert.Equal("日本語 1", name0.ToString());
        Assert.True(PdhArray.TryGetItem(buffer, Base, 1, 2, out var name1, out _, out _));
        Assert.Equal(string.Empty, name1.ToString());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(int.MaxValue)]
    public void An_index_outside_the_count_is_refused(int index)
    {
        var buffer = Build(("a", 1, Pdh.CStatusValidData), ("b", 2, Pdh.CStatusValidData));

        Assert.False(PdhArray.TryGetItem(buffer, Base, index, 2, out _, out _, out _));
    }

    [Fact]
    public void A_count_that_does_not_fit_the_buffer_is_refused()
    {
        var buffer = Build(("a", 1, Pdh.CStatusValidData));

        Assert.False(PdhArray.TryGetItem(buffer, Base, 0, 1000, out _, out _, out _));
    }

    [Theory]
    [InlineData("below")]
    [InlineData("insideItems")]
    [InlineData("pastEnd")]
    [InlineData("odd")]
    public void A_name_pointer_that_leaves_the_name_area_gives_no_name_and_never_reads_outside(string kind)
    {
        var buffer = Build(("abc", 7, Pdh.CStatusValidData));
        var pointer = kind switch
        {
            "below" => Base - 8,
            "insideItems" => Base + 4,
            "pastEnd" => Base + (ulong)buffer.Length + 64,
            _ => Base + (ulong)PdhArray.ItemSize + 1,
        };
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, pointer);

        Assert.False(PdhArray.TryGetItem(buffer, Base, 0, 1, out var name, out var value, out _));
        Assert.True(name.IsEmpty);
        Assert.Equal(7.0, value);
    }

    [Fact]
    public void A_name_with_no_terminator_before_the_end_of_the_buffer_gives_no_name()
    {
        var buffer = Build(("abc", 1, Pdh.CStatusValidData));
        // Overwrite the NUL (the last two bytes) so the string runs to the end.
        buffer[^1] = 0x41;
        buffer[^2] = 0x41;

        Assert.False(PdhArray.TryGetItem(buffer, Base, 0, 1, out _, out _, out _));
    }
}

public sealed class DiskCounterTests
{
    [Theory]
    [InlineData("0 C:", 0, "C:")]
    [InlineData("1", 1, "")]
    [InlineData("2 D: E:", 2, "D: E:")]
    [InlineData("  3 F:  ", 3, "F:")]
    [InlineData("12", 12, "")]
    public void Instance_names_give_the_disk_number_and_the_letters(string instance, int index, string letters)
    {
        Assert.True(PdhSystemSampler.TryParseInstance(instance, out var parsedIndex, out var parsedLetters));
        Assert.Equal(index, parsedIndex);
        Assert.Equal(letters, parsedLetters);
    }

    [Theory]
    [InlineData("_Total")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("C:")]
    [InlineData("-1 C:")]
    [InlineData("1.5")]
    [InlineData("99999999999 C:")]
    public void Anything_that_is_not_a_disk_instance_is_refused(string instance)
    {
        Assert.False(PdhSystemSampler.TryParseInstance(instance, out _, out _));
    }

    [Fact]
    public void A_disk_is_named_by_its_model_and_letters_and_falls_back_to_its_number()
    {
        Assert.Equal("Samsung SSD 980 PRO (C:)", PdhSystemSampler.DisplayName(3, "C:", "Samsung SSD 980 PRO"));
        Assert.Equal("Samsung SSD 980 PRO (D:,E:)", PdhSystemSampler.DisplayName(3, "D: E:", "Samsung SSD 980 PRO"));
        Assert.Equal("Disk 4 (G:)", PdhSystemSampler.DisplayName(4, "G:", null));
        Assert.Equal("Disk 5", PdhSystemSampler.DisplayName(5, string.Empty, " "));
    }

    private static PdhSystemSampler.DiskAccumulator Disk(double idle, double read, double write, double latency, uint status = Pdh.CStatusValidData)
    {
        var valid = new PdhSystemSampler.Reading(true, 0, status);
        return new PdhSystemSampler.DiskAccumulator
        {
            Seen = true,
            Idle = valid with { Value = idle },
            Read = valid with { Value = read },
            Write = valid with { Value = write },
            Latency = valid with { Value = latency },
        };
    }

    [Fact]
    public void Active_time_is_one_hundred_minus_idle_and_latency_is_in_milliseconds()
    {
        var disk = PdhSystemSampler.Build("1 D:", Disk(idle: 75, read: 1000, write: 2000, latency: 0.0042), primed: true, model: "Corsair");

        Assert.Equal("PhysicalDrive1", disk.Id);
        Assert.Equal("Corsair (D:)", disk.Name);
        Assert.Equal(25.0, disk.ActivePercent.Data, 6);
        Assert.Equal(1000.0, disk.ReadBytesPerSec.Data);
        Assert.Equal(2000.0, disk.WriteBytesPerSec.Data);
        Assert.Equal(4.2, disk.ResponseTimeMs.Data, 6);
        Assert.True(disk.ActivePercent.IsOk && disk.ResponseTimeMs.IsOk);
    }

    [Fact]
    public void Idle_time_past_one_hundred_never_gives_a_negative_active_time()
    {
        // The counter is read without the 100 percent cap, so a busy-looking "idle" of 100.3 can happen.
        var disk = PdhSystemSampler.Build("0 C:", Disk(idle: 100.3, read: 0, write: 0, latency: 0), primed: true, model: null);

        Assert.Equal(0.0, disk.ActivePercent.Data);
    }

    [Fact]
    public void Before_the_first_window_everything_is_not_collected_and_never_zero()
    {
        var disk = PdhSystemSampler.Build("0 C:", Disk(idle: 100, read: 0, write: 0, latency: 0), primed: false, model: null);

        Assert.Equal(ValueStatus.NotCollected, disk.ActivePercent.Status);
        Assert.Equal(ValueStatus.NotCollected, disk.ReadBytesPerSec.Status);
        Assert.Equal(ValueStatus.NotCollected, disk.WriteBytesPerSec.Status);
        Assert.Equal(ValueStatus.NotCollected, disk.ResponseTimeMs.Status);
    }

    [Fact]
    public void A_value_PDH_could_not_compute_is_not_collected_and_a_missing_counter_is_unsupported()
    {
        var accumulator = Disk(idle: 50, read: 1, write: 1, latency: 0);
        accumulator.Read = new PdhSystemSampler.Reading(true, 0, 0xC0000BBA);
        accumulator.Latency = default;

        var disk = PdhSystemSampler.Build("0 C:", accumulator, primed: true, model: null);

        Assert.Equal(ValueStatus.NotCollected, disk.ReadBytesPerSec.Status);
        Assert.Equal(ValueStatus.Unsupported, disk.ResponseTimeMs.Status);
        Assert.True(disk.ActivePercent.IsOk);
    }
}

public sealed class StorageModelTests
{
    /// <summary>STORAGE_DEVICE_DESCRIPTOR with the vendor and product strings placed after the 36-byte header.</summary>
    private static byte[] Descriptor(string? vendor, string? product, uint? vendorOffsetOverride = null)
    {
        var strings = new MemoryStream();
        var vendorOffset = 0u;
        var productOffset = 0u;
        if (vendor is not null)
        {
            vendorOffset = 36 + (uint)strings.Length;
            strings.Write(Encoding.ASCII.GetBytes(vendor + "\0"));
        }

        if (product is not null)
        {
            productOffset = 36 + (uint)strings.Length;
            strings.Write(Encoding.ASCII.GetBytes(product + "\0"));
        }

        var buffer = new byte[36 + (int)strings.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12), vendorOffsetOverride ?? vendorOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(16), productOffset);
        strings.ToArray().CopyTo(buffer, 36);
        return buffer;
    }

    [Fact]
    public void Vendor_and_product_are_joined()
    {
        Assert.Equal("TOSHIBA MG04ACA200E", StorageModel.Parse(Descriptor("TOSHIBA", "MG04ACA200E")));
    }

    [Theory]
    [InlineData("NVMe")]
    [InlineData("ATA")]
    [InlineData("SCSI")]
    [InlineData("USB")]
    public void A_bus_name_in_the_vendor_field_is_dropped(string bus)
    {
        Assert.Equal("Samsung SSD 980 PRO 2TB", StorageModel.Parse(Descriptor(bus, "Samsung SSD 980 PRO 2TB")));
    }

    [Fact]
    public void The_padding_drivers_put_around_the_strings_is_trimmed()
    {
        Assert.Equal("SanDisk Cruzer Blade", StorageModel.Parse(Descriptor("SanDisk ", " Cruzer Blade  ")));
    }

    [Fact]
    public void A_missing_product_leaves_just_the_vendor_and_nothing_at_all_gives_null()
    {
        Assert.Equal("Acme", StorageModel.Parse(Descriptor("Acme", null)));
        Assert.Null(StorageModel.Parse(Descriptor(null, null)));
        Assert.Null(StorageModel.Parse(Descriptor("ATA", null)));
    }

    [Theory]
    [InlineData(0u, "Y")]
    [InlineData(36u, "X Y")]
    [InlineData(5000u, "Y")]
    [InlineData(uint.MaxValue, "Y")]
    public void A_vendor_offset_outside_the_buffer_is_not_followed_and_the_product_survives(uint offset, string expected)
    {
        var buffer = Descriptor("X", "Y", vendorOffsetOverride: offset);

        Assert.Equal(expected, StorageModel.Parse(buffer));
    }

    [Fact]
    public void A_buffer_shorter_than_the_header_is_null()
    {
        Assert.Null(StorageModel.Parse(new byte[10]));
        Assert.Null(StorageModel.Parse([]));
    }

    [Fact]
    public void An_unterminated_string_at_the_end_of_the_buffer_is_still_bounded()
    {
        var buffer = Descriptor("ABC", "DEF");
        buffer[^1] = (byte)'Z';

        Assert.Equal("ABC DEFZ", StorageModel.Parse(buffer));
    }
}
