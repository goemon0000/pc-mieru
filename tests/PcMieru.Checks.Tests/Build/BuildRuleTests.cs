// Provenance: BuildRuleTests of PartsLife.Checks.Tests (pl-cloud 25e4571, tests/PartsLife.Checks.Tests/BuildCodeTests.cs), MIT, same owner.
// The cases and their expected values are the old ones; the names and the comments are new (English). The machines named in them are
// the model names the old tests used; no serial number, machine name or user name is in them.
using PcMieru.Checks.Build;

namespace PcMieru.Checks.Tests.Build;

public class BuildRuleTests
{
    // --- The net for serial numbers ---

    [Theory]
    [InlineData("E823_8FA6_BF53_0001_001B_448B_4A5E_2D11.")] // an NVMe serial (hex with separators)
    [InlineData("2020202057442D5743433446")] // a serial turned into hex (Win32_DiskDrive)
    [InlineData("4C4C4544-0035-4410-8044-B4C04F4E3732")] // a UUID
    [InlineData("S4EWNX0R12345678901")] // a long run of digits
    [InlineData("00000000000000000000")]
    public void Serial_like_strings_are_detected(string s) => Assert.True(BuildRules.LooksLikeSerial(s));

    [Theory]
    [InlineData("ST2000DM008-2FR102")] // a Seagate model (18 characters, no space)
    [InlineData("CMK64GX5M2B5600Z40")] // a Corsair memory model (18 characters, no space)
    [InlineData("F4-3200C16-16GVK")]
    [InlineData("Samsung SSD 980 PRO 1TB")]
    [InlineData("WDC WD20EZAZ-00GGJB0")]
    [InlineData("TOSHIBA MG04ACA200E")]
    [InlineData("S5GXNF0R123456A")] // 16 characters or fewer are not put through the net (it cannot be told from a model)
    public void Model_names_are_not_mistaken_for_serials(string s) => Assert.False(BuildRules.LooksLikeSerial(s));

    [Fact]
    public void A_part_whose_name_looks_like_a_serial_is_dropped()
    {
        var c = new BuildConfig();
        c.Set("ssd", [new BuildPart("E823_8FA6_BF53_0001_001B_448B_4A5E_2D11."), new BuildPart("Corsair MP600 PRO LPX", Cap: 2000, Type: "nvme")]);
        var n = BuildCode.Normalize(c);
        Assert.Equal("Corsair MP600 PRO LPX", Assert.Single(n["ssd"]).Name);
    }

    [Fact]
    public void Ram_with_a_serial_or_empty_part_number_gets_a_generic_name()
    {
        var parts = BuildRules.Ram(
        [
            new RawMemory("4C4C4544003544108044B4C04F4E3732", "80CE", 17179869184, 3200, 26, 0),
            new RawMemory("   ", "Unknown", 8589934592, 2666, 0, 24),
            new RawMemory("", "", 0, 0, 0, 0), // no capacity either: not listed
        ]);
        Assert.Equal(2, parts.Count);
        Assert.Equal(new BuildPart("DDR4 16GB", Cap: 16, Speed: 3200, Type: "DDR4", Mfr: "Samsung"), parts[0]);
        Assert.Equal(new BuildPart("DDR3 8GB", Cap: 8, Speed: 2666, Type: "DDR3"), parts[1]);
    }

    // --- Memory ---

    [Theory]
    [InlineData(26, 0, "DDR4")]
    [InlineData(34, 0, "DDR5")]
    [InlineData(35, 0, "LPDDR5")]
    [InlineData(30, 0, "LPDDR4")]
    [InlineData(24, 0, "DDR3")]
    [InlineData(0, 24, "DDR3")] // an old machine has no SMBIOSMemoryType: MemoryType is used
    [InlineData(2, 21, "DDR2")] // 2 = Unknown
    [InlineData(0, 0, null)]
    [InlineData(99, 0, null)]
    public void Ram_type_mapping(int smbios, int memoryType, string? expected) =>
        Assert.Equal(expected, BuildRules.RamType(smbios, memoryType));

    [Theory]
    [InlineData("Corsair", "Corsair")]
    [InlineData("80CE", "Samsung")]
    [InlineData("CE00000000000000", "Samsung")]
    [InlineData("9E02000000000000", "Corsair")]
    [InlineData("04CD", "G.Skill")]
    [InlineData("ABCD", null)] // an unknown number is not written
    [InlineData("Unknown", null)]
    [InlineData("", null)]
    public void Ram_manufacturer(string raw, string? expected) => Assert.Equal(expected, BuildRules.RamManufacturer(raw));

    [Theory]
    [InlineData(4800u, 5600u, 5600)] // running faster than rated by XMP or EXPO: the speed it runs at
    [InlineData(3200u, 0u, 3200)] // ConfiguredClockSpeed cannot be read: the rated speed
    [InlineData(3200u, 2133u, 3200)] // running slower than rated: the rated speed
    [InlineData(3200u, 3200u, 3200)]
    [InlineData(0u, 2666u, 2666)] // no rated speed reported
    [InlineData(0u, 0u, null)]
    public void Ram_speed_prefers_configured_only_when_faster(uint rated, uint configured, int? expected) =>
        Assert.Equal(expected, BuildRules.RamSpeed(rated, configured));

    [Fact]
    public void Xmp_kit_reports_its_running_speed()
    {
        // A real machine (2026-10-11): CMK64GX5M2B5600Z40 has Speed=4800 and ConfiguredClockSpeed=5600.
        var p = Assert.Single(BuildRules.Ram([new RawMemory("CMK64GX5M2B5600Z40", "Corsair", 34359738368, 4800, 34, 0, 5600)]));
        Assert.Equal(5600, p.Speed);
    }

    [Fact]
    public void Ram_module_maps_capacity_speed_type_and_maker()
    {
        var p = Assert.Single(BuildRules.Ram([new RawMemory(" CMK64GX5M2B5600Z40 ", "Corsair", 34359738368, 4800, 34, 0)]));
        Assert.Equal(new BuildPart("CMK64GX5M2B5600Z40", Cap: 32, Speed: 4800, Type: "DDR5", Mfr: "Corsair"), p);
    }

    // --- Storage ---

    [Theory]
    [InlineData(17, "nvme")]
    [InlineData(11, "sata")]
    [InlineData(3, "sata")]
    [InlineData(10, "sas")]
    [InlineData(1, null)]
    [InlineData(8, null)]
    public void Storage_interface_mapping(int bus, string? expected) => Assert.Equal(expected, BuildRules.StorageInterface(bus));

    [Theory]
    [InlineData(1024209543168UL, 1000)] // a "1 TB" as sold, 1.024 TB
    [InlineData(512110190592UL, 512)]
    [InlineData(2000398934016UL, 2000)]
    [InlineData(2000396321280UL, 2000)] // Win32_DiskDrive gives a little less
    [InlineData(250059350016UL, 250)]
    [InlineData(4000787030016UL, 4000)]
    [InlineData(180012345678UL, 180)] // far from a size that is sold: rounded as it is
    public void Storage_capacity_is_decimal_gb_snapped_to_market_sizes(ulong bytes, int gb) =>
        Assert.Equal(gb, BuildRules.StorageGb(bytes));

    [Fact]
    public void Disks_split_into_ssd_and_hdd_and_skip_usb_and_virtual()
    {
        // The MSFT_PhysicalDisk of a real machine (2026-10-11), plus a virtual disk and a storage pool.
        var (ssd, hdd) = BuildRules.Disks(
        [
            new RawDisk("PCIe SSD", 17, 4, 512110190592),
            new RawDisk("CSSD-S6L1TMGAX 1TB", 11, 4, 1024209543168),
            new RawDisk("TOSHIBA MG04ACA200E", 11, 3, 2000398934016),
            new RawDisk("SanDisk Cruzer Blade", 7, 0, 16005464064), // a USB stick
            new RawDisk("Corsair MP600 PRO LPX", 17, 4, 2000398934016),
            new RawDisk("Msft Virtual Disk", 14, 0, 137438953472),
            new RawDisk("Storage pool", 16, 0, 4000787030016),
            new RawDisk("Samsung SSD 870 EVO 1TB", 11, 0, 1000204886016), // an unknown MediaType: an SSD by the name
            new RawDisk("WDC WD40EZAZ-00SF3B0", 11, 0, 4000787030016), // unknown, and nothing in the name: an HDD
            new RawDisk("", 17, 4, 1000204886016),
        ]);
        Assert.Equal(new[] { "PCIe SSD", "CSSD-S6L1TMGAX 1TB", "Corsair MP600 PRO LPX", "Samsung SSD 870 EVO 1TB" }, ssd.Select(x => x.Name).ToArray());
        Assert.Equal(new[] { "TOSHIBA MG04ACA200E", "WDC WD40EZAZ-00SF3B0" }, hdd.Select(x => x.Name).ToArray());
        Assert.Equal(new BuildPart("CSSD-S6L1TMGAX 1TB", Cap: 1000, Type: "sata"), ssd[1]);
        Assert.Equal(new BuildPart("PCIe SSD", Cap: 512, Type: "nvme"), ssd[0]);
    }

    [Theory]
    [InlineData("IDE", @"IDE\DISKTOSHIBA_MG04ACA200E\1", "Fixed hard disk media", 11)]
    [InlineData("SCSI", @"SCSI\DISK&VEN_NVME&PROD_CORSAIR_MP600\5&1", "Fixed hard disk media", 17)]
    [InlineData("SCSI", @"SCSI\DISK&VEN_&PROD_PCIE_SSD\5&2", "Fixed hard disk media", 1)]
    [InlineData("USB", @"USBSTOR\DISK&VEN_SANDISK&PROD_CRUZER_BLADE\4C53", "Removable Media", 7)]
    [InlineData("SCSI", @"SCSI\DISK&VEN_WD&PROD_ELEMENTS\1", "External hard disk media", 7)]
    public void Win32_diskdrive_fallback_maps_to_bus_types(string iface, string pnp, string media, int bus) =>
        Assert.Equal(bus, BuildRules.BusFromWin32(iface, pnp, media));

    // --- GPU, monitor, optical drive ---

    [Fact]
    public void Virtual_and_integrated_display_adapters_are_not_gpus()
    {
        var gpus = BuildRules.Gpus(
        [
            new RawGpu("NVIDIA GeForce RTX 5070", 12_817_793_024, true),
            new RawGpu("NVIDIA GeForce RTX 3080 Ti", 12_621_709_312, true),
            new RawGpu("AMD Radeon(TM) Graphics", 536_870_912, true), // the integrated one of a Ryzen 7000
            new RawGpu("Intel(R) UHD Graphics 770", 134_217_728, true),
            new RawGpu("AMD Radeon 780M Graphics", 536_870_912, true),
            new RawGpu("Intel(R) Arc(TM) Graphics", 134_217_728, true),
            new RawGpu("Intel(R) Arc(TM) A770 Graphics", 17_045_651_456, true), // a discrete card stays
            new RawGpu("AMD Radeon RX 7900 XTX", 25_753_026_560, true),
            new RawGpu("Microsoft Basic Display Adapter", 0, true),
            new RawGpu("Microsoft Basic Render Driver", 0, false),
            new RawGpu("Meta Virtual Monitor", 0, false),
            new RawGpu("Virtual Desktop Monitor", 0, false),
            new RawGpu("Microsoft Hyper-V Video", 0, true),
            new RawGpu("Parsec Virtual Display Adapter", 0, true),
            new RawGpu("NVIDIA GeForce RTX 5070", 12_817_793_024, false), // an alias with no PCI address
        ]);
        Assert.Equal(new[] { "NVIDIA GeForce RTX 5070", "NVIDIA GeForce RTX 3080 Ti", "Intel(R) Arc(TM) A770 Graphics", "AMD Radeon RX 7900 XTX" }, gpus.Select(g => g.Name).ToArray());
        Assert.Equal(new int?[] { 12, 12, 16, 24 }, gpus.Select(g => g.Vram).ToArray());
    }

    [Fact]
    public void Gpu_without_known_vram_has_no_vram_field()
    {
        var g = Assert.Single(BuildRules.Gpus([new RawGpu("NVIDIA GeForce GTX 1060 6GB", 0, true)]));
        Assert.Null(g.Vram);
    }

    [Fact]
    public void Monitor_names_are_decoded_and_empty_inactive_or_virtual_ones_skipped()
    {
        ushort[] raw = [.. "PHL 278E1".Select(ch => (ushort)ch), 0, 0, 0];
        Assert.Equal("PHL 278E1", BuildRules.DecodeWmiString(raw));
        Assert.Equal("", BuildRules.DecodeWmiString([0, 0]));
        Assert.Equal("", BuildRules.DecodeWmiString(null));

        var m = BuildRules.Monitors(
        [
            new RawMonitor("UG27PJ", true),
            new RawMonitor("MFG27F4Q", true),
            new RawMonitor("PHL 278E1", true),
            new RawMonitor("", true),
            new RawMonitor("OLD MONITOR", false),
            new RawMonitor("Virtual Display", true),
        ]);
        Assert.Equal(new[] { "UG27PJ", "MFG27F4Q", "PHL 278E1" }, m.Select(x => x.Name).ToArray());
    }

    [Fact]
    public void Virtual_and_usb_optical_drives_are_skipped()
    {
        var o = BuildRules.Opticals(
        [
            new RawOptical("HL-DT-ST BD-RE WH16NS60", @"SCSI\CDROM&VEN_HL-DT-ST&PROD_BD-RE_WH16NS60\4&1"),
            new RawOptical("Msft Virtual DVD-ROM", @"SCSI\CDROM&VEN_MSFT&PROD_VIRTUAL_DVD-ROM\2&1"),
            new RawOptical("ASUS SDRW-08U9M-U", @"USBSTOR\CDROM&VEN_ASUS\1"),
        ]);
        Assert.Equal("HL-DT-ST BD-RE WH16NS60", Assert.Single(o).Name);
    }

    [Theory]
    [InlineData("ASUSTeK COMPUTER INC.", "TUF GAMING X670E-PLUS WIFI", "ASUSTeK COMPUTER INC. TUF GAMING X670E-PLUS WIFI", "ASUSTeK COMPUTER INC.")]
    [InlineData("Micro-Star International Co., Ltd.", "MAG B650 TOMAHAWK WIFI (MS-7D75)", "Micro-Star International Co., Ltd. MAG B650 TOMAHAWK WIFI (MS-7D75)", "Micro-Star International Co., Ltd.")]
    [InlineData("To be filled by O.E.M.", "B450M DS3H", "B450M DS3H", null)]
    public void Motherboard_name_is_maker_plus_product(string mfr, string product, string name, string? maker)
    {
        var p = Assert.Single(BuildRules.Motherboard(mfr, product));
        Assert.Equal(name, p.Name);
        Assert.Equal(maker, p.Mfr);
    }

    [Fact]
    public void Placeholder_board_product_yields_no_motherboard()
    {
        Assert.Empty(BuildRules.Motherboard("ASUSTeK COMPUTER INC.", "System Product Name"));
        Assert.Empty(BuildRules.Motherboard("Default string", "Default string"));
        Assert.Empty(BuildRules.Motherboard(null, null));
    }
}
