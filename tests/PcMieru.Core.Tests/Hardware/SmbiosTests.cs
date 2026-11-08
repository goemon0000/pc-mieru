using System.Buffers.Binary;
using PcMieru.Core.Hardware.Collectors;
using PcMieru.Core.Hardware.Model;
using PcMieru.Core.Hardware.Smbios;

namespace PcMieru.Core.Tests.Hardware;

public class SmbiosTests
{
    private static byte[] FixtureBytes() => HwFixture.Bytes("smbios-asus-x670e.bin");

    private static SmbiosTable Table()
    {
        var table = SmbiosTable.ParseRaw(FixtureBytes());
        Assert.NotNull(table);
        return table;
    }

    [Fact]
    public void RsmbSignatureIsTheDwordCodexPinnedDown()
    {
        Assert.Equal(0x52534D42u, SmbiosTable.RsmbSignature);
    }

    [Fact]
    public void ParsesRealDumpHeaderAndStructureList()
    {
        var table = Table();
        Assert.Equal(3, table.Major);
        Assert.Equal("3.6", table.VersionText);
        Assert.NotNull(table.First(0));
        Assert.NotNull(table.First(1));
        Assert.NotNull(table.First(2));
        Assert.NotNull(table.First(3));
        Assert.NotNull(table.First(4));
        Assert.NotNull(table.First(16));
        Assert.Equal(4, table.OfType(17).Count());
    }

    [Fact]
    public void BoardCollectorReadsFirmwareAndBoard()
    {
        var value = BoardCollector.Collect(Table(), null);
        Assert.Equal(ValueStatus.Ok, value.Status);
        var board = value.Data!;
        Assert.Equal("American Megatrends Inc.", board.BiosVendor.Data);
        Assert.Contains("TUF GAMING X670E", board.Product.Data);
        Assert.Contains("ASUSTeK", board.Manufacturer.Data);
        Assert.Equal(ValueStatus.Ok, board.BiosVersion.Status);
        Assert.Equal("smbios:type0", board.BiosVendor.Source);
        Assert.NotNull(board.Details?.System);
        Assert.Equal("3.6", board.Details!.SmbiosVersion.Data);
    }

    [Fact]
    public void BoardSerialIsAPlaceholderAndNotReportedAsAValue()
    {
        // The fixture is scrubbed: serial fields hold "Default string"-style text or nothing. Either way the app must never
        // show a firmware template as if it were a serial number.
        var board = BoardCollector.Collect(Table(), null).Data!;
        if (board.SerialNumber.Status == ValueStatus.Ok)
        {
            Assert.False(Probe_IsPlaceholder(board.SerialNumber.Data!));
        }
    }

    private static bool Probe_IsPlaceholder(string s) => PcMieru.Core.Hardware.Native.Probe.IsPlaceholder(s);

    [Fact]
    public void MemoryCollectorReportsFourSlotsTwoModulesAndEmptySlots()
    {
        var value = MemoryCollector.Collect(Table(), null);
        Assert.Equal(ValueStatus.Ok, value.Status);
        var memory = value.Data!;
        Assert.Equal(4, memory.Modules.Count);
        Assert.Equal(4, memory.SlotsTotal.Data);
        Assert.Equal(2, memory.SlotsUsed.Data);

        var populated = memory.Modules.Where(m => m.CapacityBytes.Data > 0).ToList();
        Assert.Equal(2, populated.Count);
        foreach (var m in populated)
        {
            Assert.Equal(32L * 1024 * 1024 * 1024, m.CapacityBytes.Data);
            Assert.Equal(MemoryType.Ddr5, m.Type.Data);
            Assert.Equal(ValueStatus.Ok, m.SpeedMtPerSec.Status);
            Assert.Contains("Corsair", m.Manufacturer.Data, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var empty in memory.Modules.Where(m => m.CapacityBytes.Data == 0))
        {
            // Empty slot: every other field says why it is missing instead of showing a fake zero.
            Assert.Equal(ValueStatus.Unsupported, empty.SpeedMtPerSec.Status);
            Assert.Equal(ValueStatus.Unsupported, empty.PartNumber.Status);
        }
    }

    [Fact]
    public void CpuStructureGivesSocketAndFirmwareClocks()
    {
        var cpu = CpuCollector.Collect(Table());
        Assert.Equal(ValueStatus.Ok, cpu.Status);
        Assert.Equal("AM5", cpu.Data!.Socket.Data);
        Assert.Equal(ValueStatus.Ok, cpu.Data.Details!.MaxClockMhz.Status);
    }

    [Fact]
    public void RawDumpWithBadLengthIsRejectedNotThrown()
    {
        Assert.Null(SmbiosTable.ParseRaw(new byte[3]));
        var truncated = FixtureBytes().AsSpan(0, 40).ToArray();
        // Header claims more table bytes than are present: parser must stop cleanly.
        var table = SmbiosTable.ParseRaw(truncated);
        Assert.True(table is null || table.Structures.Count < 3);
    }

    [Fact]
    public void Type17ExtendedSizeAndSpeedAreHonoured()
    {
        // 64 GiB module: size word 0x7FFF -> extended dword at 0x1C (MB); speed 0xFFFF -> extended word at 0x54.
        var data = new byte[0x5C];
        data[0] = 17;
        data[1] = (byte)data.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x0C), 0x7FFF);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x1C), 65536);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x15), 0xFFFF);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x54), 8400);
        var structure = new SmbiosStructure(17, 1, data, []);
        Assert.Equal(64L * 1024 * 1024 * 1024, MemoryCollector.InstalledBytes(structure));
    }

    [Fact]
    public void Type17SizeInKilobytesWhenBit15Set()
    {
        var data = new byte[0x20];
        data[0] = 17;
        data[1] = (byte)data.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x0C), 0x8000 | 512);
        var structure = new SmbiosStructure(17, 1, data, []);
        Assert.Equal(512L * 1024, MemoryCollector.InstalledBytes(structure));
    }

    [Fact]
    public void Type17ZeroSizeMeansEmptySlotAndFFFFMeansUnknown()
    {
        var empty = new byte[0x20];
        empty[0] = 17;
        empty[1] = (byte)empty.Length;
        Assert.Equal(0L, MemoryCollector.InstalledBytes(new SmbiosStructure(17, 1, empty, [])));

        var unknown = (byte[])empty.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(unknown.AsSpan(0x0C), 0xFFFF);
        Assert.Null(MemoryCollector.InstalledBytes(new SmbiosStructure(17, 1, unknown, [])));
    }

    [Fact]
    public void StringSetWithNoStringsConsumesBothTerminators()
    {
        // Structure type 127 (end) with an empty string set followed by another structure: the second must still be found.
        byte[] table =
        [
            0x08, 0x08, 0x01, 0x00, 0, 0, 0, 0, // type 8 (port connector), length 8, no strings
            0x00, 0x00,                         // empty string-set terminator
            0x01, 0x08, 0x02, 0x00, 0x01, 0x00, 0x00, 0x00, // type 1 handle 2 with one string
            (byte)'A', (byte)'B', 0x00, 0x00,
            0x7F, 0x04, 0x03, 0x00, 0x00, 0x00,
        ];
        var parsed = SmbiosTable.ParseStructures(table, 3, 4);
        Assert.Equal([8, 1, 127], parsed.Structures.Select(s => s.Type).ToArray());
        Assert.Equal("AB", parsed.First(1)!.Str(0x04));
    }
}

public class CpuTopologyTests
{
    private static byte[] Core(byte flags, byte efficiency, ulong mask)
    {
        var rec = new byte[48];
        BinaryPrimitives.WriteInt32LittleEndian(rec, 0);
        BinaryPrimitives.WriteInt32LittleEndian(rec.AsSpan(4), rec.Length);
        rec[8] = flags;
        rec[9] = efficiency;
        BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(30), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(rec.AsSpan(32), mask);
        return rec;
    }

    private static byte[] Cache(byte level, int size, int type)
    {
        var rec = new byte[56];
        BinaryPrimitives.WriteInt32LittleEndian(rec, 2);
        BinaryPrimitives.WriteInt32LittleEndian(rec.AsSpan(4), rec.Length);
        rec[8] = level;
        rec[9] = 8;
        BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(10), 64);
        BinaryPrimitives.WriteInt32LittleEndian(rec.AsSpan(12), size);
        BinaryPrimitives.WriteInt32LittleEndian(rec.AsSpan(16), type);
        return rec;
    }

    [Fact]
    public void SmtCpuIsNotHybridAndCountsThreads()
    {
        var data = new List<byte>();
        data.AddRange(Core(1, 0, 0b11));
        data.AddRange(Core(1, 0, 0b1100));
        data.AddRange(Cache(3, 32 * 1024 * 1024, 0));
        data.AddRange(Cache(2, 1024 * 1024, 0));
        data.AddRange(Cache(2, 1024 * 1024, 0));
        var t = CpuTopology.Parse(data.ToArray());
        Assert.Equal(2, t.PhysicalCores);
        Assert.Equal(4, t.LogicalProcessors);
        Assert.Equal(2, t.ThreadsPerCore);
        Assert.False(t.IsHybrid);
        Assert.Equal(32L * 1024 * 1024, t.TotalCacheBytes(3));
        Assert.Equal(2L * 1024 * 1024, t.TotalCacheBytes(2));
    }

    [Fact]
    public void HybridCpuSplitsByEfficiencyClass()
    {
        var data = new List<byte>();
        data.AddRange(Core(1, 1, 0b11));
        data.AddRange(Core(1, 1, 0b1100));
        data.AddRange(Core(0, 0, 0b10000));
        var t = CpuTopology.Parse(data.ToArray());
        Assert.True(t.IsHybrid);
        Assert.Equal(2, t.PerformanceCores);
        Assert.Equal(1, t.EfficiencyCores);
        Assert.Equal(5, t.LogicalProcessors);
    }

    [Fact]
    public void TruncatedBufferStopsWithoutThrowing()
    {
        var data = Core(1, 0, 0b11);
        var t = CpuTopology.Parse(data.AsSpan(0, 20));
        Assert.Equal(0, t.PhysicalCores);
    }
}
